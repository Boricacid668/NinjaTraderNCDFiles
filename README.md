# NinjaTraderNCDFiles
Classes to read NinjaTrader proprietary NCD Files.

## How to use the classes...

### 1) Set the static value for the Directory containing the NinjaTrader data.

This path designation tells the helper classes which directories in which to search for the NinjaTrader files.

As of the publish date NT8 files are found by default in 
{driveletter}:\Users\{yourusername}\Documents\NinjaTrader 8\db

```csharp
NinjaTrader.GlobalOptions.HistoricalDataPath = $"{driveletter}:\Users\{yourusername}\Documents\NinjaTrader 8\db";
```

### 2) Create the Helperobject...

```csharp
DateTime startDateTime = new DateTime.ParseExact("092719", "MMddyy", CultureInfo.InvariantCulture);
int numberDaysForward = 7;
int numberDaysBack = 7;
NCDFiles myFiles = new NCDFiles(NCDFileType.Minute, "AAPL", startDateTime, numberDaysForward, numberDaysBack);
```

### 3) Process the files...

```csharp
while (!myFiles.EndOfData)
{
  MinuteRecord record = myFiles.ReadNextRecord();
  // do whatever you want with it
}
```

This example is for Minute files.  If you are processing Tick files then simply make the appropriate changes in the constructor and reader...

```csharp
NCDFiles myFiles = new NCDFiles(NCDFileType.Minute, "AAPL", startDateTime, numberDaysForward, numberDaysBack);
while (!myFiles.EndOfData)
{
  TickRecord record = myFiles.ReadNextRecord();
}
```

Feel free to reach out to me directly if you have any questions or comments or would like any custom libraries developed that utilize the files.

Enjoy!

jrstokka gmail or skype jrstokka

## VolumeBarBridge CLI

The repo also includes a CLI bridge for exporting tick and range-bar CSVs from a directory of `.ncd` files.

Example:

```powershell
dotnet run --project .\VolumeBarBridge\VolumeBarBridge.csproj -- --input-dir .\Data_Lake\RAW --out-dir .\Data_Lake\validation\junction_smoke --mode both --range-size 10 --max-files-per-contract 1 --contracts "MNQ 03-25" --fail-on-file-errors
```

Useful flags:

- `--mode ticks|bars|both`
- `--range-size <positive number>`
- `--max-files-per-contract <positive int>`
- `--contracts "CONTRACT_A,CONTRACT_B"`
- `--fail-on-file-errors` to abort a run if any `.ncd` file fails to parse

## Smoke Test

To validate the bridge against the local `Data_Lake` junction before running full canonical exports:

```powershell
.\scripts\bridge-smoke-test.ps1
```

The script auto-selects the first contract under the input root that contains `.ncd` files, runs a one-file export, and verifies that both CSV outputs are created and non-empty.
