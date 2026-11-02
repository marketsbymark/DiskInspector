# DiskInspector

A Windows 11 desktop app that shows what is using space on a drive or folder.

## Run

This repository contains the source, not a checked-in executable. Build and
publish it using the instructions below, then launch `publish\DiskInspector.exe`.
The published app is self-contained, so the target computer does not need a
separate .NET runtime. Pick a drive or folder and press **Scan**.

- **Scan**: works without admin rights. Scan time depends on the drive, file count, permissions and Windows filesystem caches. Protected folders are skipped and listed under **Inaccessible**.
- **Fast scan**: reads the NTFS file table directly for a whole NTFS drive (not an individual folder). It sees protected folders and counts hard-linked files once. Windows asks for administrator permission. If it can't run, the normal scan is used and a notice explains why.

Scanning is read-only: the app does not delete or move the files it scans. It does
write its own logs and any CSV exports you request. The file actions are Open
(turned off for programs and scripts), Show in File Explorer and Copy path.

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

Logs are written under `%LOCALAPPDATA%\DiskInspector\logs\`. UI errors may also
show a dialog; unhandled process-level errors can terminate the application.

## Build and test

Use Windows with the **.NET 10 SDK** installed: the app, core library and tests
all target `net10.0-windows`, and the desktop UI uses WPF. Run the commands from
the repository root. Package restore requires access to the configured NuGet
sources on the first build.

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
