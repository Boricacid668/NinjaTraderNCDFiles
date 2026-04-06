using System.Globalization;
using RespondClient.DomiKnow.NinjaTrader;

internal sealed record Options(
    string InputDir,
    string OutputDir,
    string Mode,
    double RangeSize,
    int? MaxFilesPerContract,
    HashSet<string>? Contracts,
    bool FailOnFileErrors
);

internal sealed record ContractExportResult(string ContractName, int FileCount, long TickCount, long BarCount, long FileErrors);

internal sealed record VolumeRangeBar(DateTime Timestamp, double Open, double High, double Low, double Close, long Volume, long BarIndex);

internal sealed class VolumeRangeBarBuilder
{
    private readonly double _rangeSize;
    private double? _barOpen;
    private double _barHigh;
    private double _barLow;
    private DateTime _barTimestamp;
    private double? _lastPrice;
    private long _barVolume;
    private long _barIndex;

    public VolumeRangeBarBuilder(double rangeSize)
    {
        _rangeSize = rangeSize;
        _barVolume = 0;
    }

    public IEnumerable<VolumeRangeBar> AddTick(DateTime timestamp, double price, long tickVolume)
    {
        if (_lastPrice.HasValue && price == _lastPrice.Value)
        {
            _barVolume += tickVolume;
            yield break;
        }

        _lastPrice = price;

        if (!_barOpen.HasValue)
        {
            _barOpen = price;
            _barHigh = price;
            _barLow = price;
            _barTimestamp = timestamp;
            _barVolume += tickVolume;
            yield break;
        }

        _barVolume += tickVolume;

        while (true)
        {
            var moved = false;
            if (price > _barHigh)
            {
                _barHigh = price;
                moved = true;
            }
            else if (price < _barLow)
            {
                _barLow = price;
                moved = true;
            }

            if (!moved)
            {
                break;
            }

            var barRange = _barHigh - _barLow;
            if (barRange < _rangeSize)
            {
                break;
            }

            var open = _barOpen.Value;
            var close = price >= open ? _barLow + _rangeSize : _barHigh - _rangeSize;

            yield return new VolumeRangeBar(_barTimestamp, open, _barHigh, _barLow, close, _barVolume, _barIndex);
            _barIndex++;

            _barOpen = close;
            _barHigh = close;
            _barLow = close;
            _barTimestamp = timestamp;
            _barVolume = 0; // Reset for the next bar
        }
    }
}

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var opts = ParseArgs(args);
            Run(opts);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static void Run(Options opts)
    {
        ValidateOptions(opts);
        Directory.CreateDirectory(opts.OutputDir);

        var contractDirs = Directory.GetDirectories(opts.InputDir)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (opts.Contracts is not null)
        {
            var availableContracts = contractDirs
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingContracts = opts.Contracts
                .Where(contract => !availableContracts.Contains(contract))
                .OrderBy(contract => contract, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (missingContracts.Count > 0)
            {
                throw new DirectoryNotFoundException($"Requested contracts were not found under input directory: {string.Join(", ", missingContracts)}");
            }

            contractDirs = contractDirs
                .Where(d => opts.Contracts.Contains(Path.GetFileName(d)))
                .ToList();
        }

        if (contractDirs.Count == 0)
        {
            throw new InvalidOperationException("No contract folders found after filtering.");
        }

        Console.WriteLine("NCD BRIDGE EXPORT");
        Console.WriteLine($"Input:  {opts.InputDir}");
        Console.WriteLine($"Output: {opts.OutputDir}");
        Console.WriteLine($"Mode:   {opts.Mode}");
        Console.WriteLine($"Strict: {opts.FailOnFileErrors}");
        Console.WriteLine();

        long totalTicks = 0;
        long totalBars = 0;
        long totalFileErrors = 0;

        foreach (var contractDir in contractDirs)
        {
            var result = ExportContract(contractDir, opts);
            totalTicks += result.TickCount;
            totalBars += result.BarCount;
            totalFileErrors += result.FileErrors;
        }

        Console.WriteLine();
        Console.WriteLine($"SUMMARY: contracts={contractDirs.Count}, ticks={totalTicks:N0}, bars={totalBars:N0}, file_errors={totalFileErrors}");
    }

    private static ContractExportResult ExportContract(string contractDir, Options opts)
    {
        var contractName = Path.GetFileName(contractDir);
        var contractSlug = contractName.Replace(' ', '_');

        var files = Directory.GetFiles(contractDir, "*.ncd")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (opts.MaxFilesPerContract.HasValue)
        {
            files = files.Take(opts.MaxFilesPerContract.Value).ToList();
        }

        if (files.Count == 0)
        {
            Console.WriteLine($"- {contractName}: no .ncd files, skipped");
            return new ContractExportResult(contractName, 0, 0, 0, 0);
        }

        var writeTicks = opts.Mode is "ticks" or "both";
        var writeBars = opts.Mode is "bars" or "both";

        var tickPath = Path.Combine(opts.OutputDir, $"{contractSlug}_ticks.csv");
        var barPath = Path.Combine(opts.OutputDir, $"{contractSlug}_rangebars_{opts.RangeSize.ToString("0.####", CultureInfo.InvariantCulture)}.csv");
        var tickTempPath = writeTicks ? tickPath + ".tmp" : null;
        var barTempPath = writeBars ? barPath + ".tmp" : null;

        StreamWriter? tickWriter = null;
        StreamWriter? barWriter = null;

        CleanupTempFile(tickTempPath);
        CleanupTempFile(barTempPath);

        try
        {
            tickWriter = writeTicks ? new StreamWriter(tickTempPath!, false) : null;
            barWriter = writeBars ? new StreamWriter(barTempPath!, false) : null;

            if (tickWriter is not null)
            {
                tickWriter.WriteLine("timestamp,price,bid,ask,volume,source_file");
            }

            if (barWriter is not null)
            {
                barWriter.WriteLine("timestamp,open,high,low,close,volume,bar_index");
            }

            var rangeBuilder = new VolumeRangeBarBuilder(opts.RangeSize);

            long tickCount = 0;
            long barCount = 0;
            long fileErrors = 0;

            foreach (var file in files)
            {
                try
                {
                    var ncdFile = new NCDTickFile(file);
                    while (!ncdFile.EndOfFile)
                    {
                        var tick = (TickRecord)ncdFile.ReadNextRecord();

                        if (tickWriter is not null)
                        {
                            tickWriter.Write(tick.DateTime.ToString("O", CultureInfo.InvariantCulture));
                            tickWriter.Write(',');
                            tickWriter.Write(tick.Price.ToString("0.########", CultureInfo.InvariantCulture));
                            tickWriter.Write(',');
                            tickWriter.Write(tick.Bid.ToString("0.########", CultureInfo.InvariantCulture));
                            tickWriter.Write(',');
                            tickWriter.Write(tick.Ask.ToString("0.########", CultureInfo.InvariantCulture));
                            tickWriter.Write(',');
                            tickWriter.Write(tick.Volume.ToString(CultureInfo.InvariantCulture));
                            tickWriter.Write(',');
                            tickWriter.WriteLine(Path.GetFileName(file));
                        }

                        if (barWriter is not null)
                        {
                            foreach (var bar in rangeBuilder.AddTick(tick.DateTime, tick.Price, tick.Volume))
                            {
                                barWriter.Write(bar.Timestamp.ToString("O", CultureInfo.InvariantCulture));
                                barWriter.Write(',');
                                barWriter.Write(bar.Open.ToString("0.########", CultureInfo.InvariantCulture));
                                barWriter.Write(',');
                                barWriter.Write(bar.High.ToString("0.########", CultureInfo.InvariantCulture));
                                barWriter.Write(',');
                                barWriter.Write(bar.Low.ToString("0.########", CultureInfo.InvariantCulture));
                                barWriter.Write(',');
                                barWriter.Write(bar.Close.ToString("0.########", CultureInfo.InvariantCulture));
                                barWriter.Write(',');
                                barWriter.Write(bar.Volume.ToString(CultureInfo.InvariantCulture));
                                barWriter.Write(',');
                                barWriter.WriteLine(bar.BarIndex.ToString(CultureInfo.InvariantCulture));
                                barCount++;
                            }
                        }

                        tickCount++;
                    }
                }
                catch (Exception ex)
                {
                    fileErrors++;
                    Console.WriteLine($"  ! Parse error in {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            tickWriter?.Flush();
            barWriter?.Flush();
            tickWriter?.Dispose();
            barWriter?.Dispose();

            if (opts.FailOnFileErrors && fileErrors > 0)
            {
                CleanupTempFile(tickTempPath);
                CleanupTempFile(barTempPath);
                throw new InvalidOperationException($"Strict mode aborted contract '{contractName}' because {fileErrors} file(s) failed to parse.");
            }

            PromoteTempFile(tickTempPath, tickPath);
            PromoteTempFile(barTempPath, barPath);

            Console.WriteLine($"- {contractName}: files={files.Count}, ticks={tickCount:N0}, bars={barCount:N0}, file_errors={fileErrors}");
            if (writeTicks)
            {
                Console.WriteLine($"  ticks -> {tickPath}");
            }

            if (writeBars)
            {
                Console.WriteLine($"  bars  -> {barPath}");
            }

            return new ContractExportResult(contractName, files.Count, tickCount, barCount, fileErrors);
        }
        finally
        {
            tickWriter?.Dispose();
            barWriter?.Dispose();
        }
    }

    private static void ValidateOptions(Options opts)
    {
        if (!Directory.Exists(opts.InputDir))
        {
            throw new DirectoryNotFoundException($"Input directory does not exist: {opts.InputDir}");
        }

        if (File.Exists(opts.OutputDir))
        {
            throw new IOException($"Output path points to a file, not a directory: {opts.OutputDir}");
        }

        if (double.IsNaN(opts.RangeSize) || double.IsInfinity(opts.RangeSize) || opts.RangeSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(opts.RangeSize), "--range-size must be a positive finite number.");
        }

        if (opts.MaxFilesPerContract.HasValue && opts.MaxFilesPerContract.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(opts.MaxFilesPerContract), "--max-files-per-contract must be greater than zero.");
        }
    }

    private static void PromoteTempFile(string? tempPath, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(tempPath) || !File.Exists(tempPath))
        {
            return;
        }

        File.Move(tempPath, destinationPath, true);
    }

    private static void CleanupTempFile(string? tempPath)
    {
        if (string.IsNullOrWhiteSpace(tempPath) || !File.Exists(tempPath))
        {
            return;
        }

        File.Delete(tempPath);
    }

    private static Options ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map[key] = "true";
            }
            else
            {
                map[key] = args[++i];
            }
        }

        var inputDir = Require(map, "--input-dir");
        var outputDir = Require(map, "--out-dir");
        var mode = map.TryGetValue("--mode", out var modeArg) ? modeArg.ToLowerInvariant() : "both";

        if (mode is not ("ticks" or "bars" or "both"))
        {
            throw new ArgumentException("--mode must be one of: ticks, bars, both");
        }

        var rangeSize = map.TryGetValue("--range-size", out var rs)
            ? ParseDouble(rs, "--range-size")
            : 10.0;

        int? maxFiles = null;
        if (map.TryGetValue("--max-files-per-contract", out var mf))
        {
            maxFiles = ParseInt(mf, "--max-files-per-contract");
        }

        HashSet<string>? contracts = null;
        if (map.TryGetValue("--contracts", out var contractsArg) && !string.IsNullOrWhiteSpace(contractsArg))
        {
            contracts = contractsArg
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var failOnFileErrors = map.TryGetValue("--fail-on-file-errors", out var failOnErrorsArg)
            && ParseBool(failOnErrorsArg, "--fail-on-file-errors");

        return new Options(
            InputDir: Path.GetFullPath(inputDir),
            OutputDir: Path.GetFullPath(outputDir),
            Mode: mode,
            RangeSize: rangeSize,
            MaxFilesPerContract: maxFiles,
            Contracts: contracts,
            FailOnFileErrors: failOnFileErrors
        );
    }

    private static string Require(IDictionary<string, string> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing required argument: {key}");
        }

        return value;
    }

    private static int ParseInt(string value, string argumentName)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ArgumentException($"{argumentName} must be a valid integer.");
        }

        return parsed;
    }

    private static double ParseDouble(string value, string argumentName)
    {
        if (!double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ArgumentException($"{argumentName} must be a valid number.");
        }

        return parsed;
    }

    private static bool ParseBool(string value, string argumentName)
    {
        if (!bool.TryParse(value, out var parsed))
        {
            throw new ArgumentException($"{argumentName} must be either true or false.");
        }

        return parsed;
    }
}
