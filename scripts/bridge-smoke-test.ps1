param(
    [string]$InputDir = ".\Data_Lake\RAW",
    [string]$OutputDir = ".\Data_Lake\validation\junction_smoke",
    [string]$Contract,
    [int]$MaxFilesPerContract = 1,
    [double]$RangeSize = 10.0
)

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not (Test-Path $InputDir -PathType Container)) {
    throw "Input directory was not found: $InputDir"
}

if ($MaxFilesPerContract -le 0) {
    throw "MaxFilesPerContract must be greater than zero."
}

if ($RangeSize -le 0) {
    throw "RangeSize must be greater than zero."
}

if ([string]::IsNullOrWhiteSpace($Contract)) {
    $contractDir = Get-ChildItem $InputDir -Directory |
        Where-Object { Get-ChildItem $_.FullName -Filter *.ncd -File | Select-Object -First 1 } |
        Select-Object -First 1

    if ($null -eq $contractDir) {
        throw "No contract directory containing .ncd files was found under $InputDir"
    }

    $Contract = $contractDir.Name
}

if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}

dotnet run --project ".\VolumeBarBridge\VolumeBarBridge.csproj" -- --input-dir $InputDir --out-dir $OutputDir --mode both --range-size $RangeSize --max-files-per-contract $MaxFilesPerContract --contracts $Contract --fail-on-file-errors

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$contractSlug = $Contract.Replace(' ', '_')
$rangeToken = $RangeSize.ToString("0.####", [System.Globalization.CultureInfo]::InvariantCulture)
$tickPath = Join-Path $OutputDir ("{0}_ticks.csv" -f $contractSlug)
$barPath = Join-Path $OutputDir ("{0}_rangebars_{1}.csv" -f $contractSlug, $rangeToken)

foreach ($path in @($tickPath, $barPath)) {
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Expected output file was not created: $path"
    }

    if ((Get-Item $path).Length -le 0) {
        throw "Output file is empty: $path"
    }
}

Write-Host "Smoke test passed for contract '$Contract'."
Write-Host "Tick output: $tickPath"
Write-Host "Bar output:  $barPath"