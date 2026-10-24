# DiskInspector

A Windows 11 desktop app that shows what is using space on a drive or folder.

## Run

`publish\DiskInspector.exe` is a single self-contained file (no .NET install needed). Pick a drive and press **Scan**.

- **Scan**: works without admin rights. A full C:\ takes a few seconds once Windows has the folder listings cached; the first scan after a reboot takes longer. Protected folders are skipped and listed under **Inaccessible**.
- **Fast scan**: reads the NTFS file table directly. It sees protected folders and counts hard-linked files once. Windows asks for administrator permission. If it can't run, the normal scan is used and a notice explains why.

The app only reads the disk. The actions are Open (turned off for programs and scripts), Show in File Explorer and Copy path.

## Views

| View | Shows |
|---|---|
| Treemap | Every file as a shaded box sized by space and colored by category. Double-click to zoom, Backspace to go up |
| Sunburst | The current folder in the middle, its contents in rings. Double-click a folder to center it |
| Folders | Expandable folder tree sorted by size, with % of parent, file counts and dates |
| File types | Every extension: total, share, size on disk, count, average, plus a category donut |
| Categories | Category → extensions → largest files |
| Locations | Windows, WinSxS, page/hibernation files, Program Files, each user's Downloads/AppData/etc., plus what the scan couldn't see |
| Largest files | Top 1,000 files, filtered by name, category and age |
| Age | Space by when files last changed |

**Size on disk** switches every view between file size and space actually used. **Export** writes the folder list, file types or largest files to CSV.

## Custom categories

Create `%APPDATA%\DiskInspector\categories.json` to move extensions or add categories:

```json
{ "categories": [ { "name": "Games", "color": "#2E86AB", "extensions": ["pak", "unity3d"] } ] }
```

If the file is broken, the app ignores it and shows a notice.

## Logs

`%LOCALAPPDATA%\DiskInspector\logs\`. Unexpected errors are logged and shown in a dialog. The app keeps running.

## Build and test

```bash
dotnet test tests/DiskInspector.Core.Tests
```

```bash
dotnet publish src/DiskInspector.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

- `tools/DiskInspector.Cli`: `DiskInspector.Cli.exe C:\ [--fast] [--compare <folder>]` prints timings, memory and totals for a real drive.
- `DiskInspector.exe --selftest <outDir> [--target C:\] [--fast] [--no-shots]`: scans, opens every view, tries the main interactions, saves screenshots and a `report.txt`, then exits.
- Running the tests as administrator also cross-checks the fast scan against the normal scan.

## Layout

- `src/DiskInspector.Core`: scanners (`StandardScanner`, `MftScanner`), the compact `ScanTree`, analysis and treemap/sunburst layout
- `src/DiskInspector.App`: WPF UI (Fluent theme, follows Windows light/dark)
- `tests/DiskInspector.Core.Tests`: xUnit tests
