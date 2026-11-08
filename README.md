# DiskInspector

A Windows 11 desktop app that shows what is using space on your drives, and where your documents, spreadsheets, email and code files are.

## Run

This repository contains the source, not a checked-in executable. Build and publish it using the instructions below, then launch `publish\DiskInspector.exe`. The published app is self-contained, so the target computer does not need a separate .NET runtime.

Pick something to scan from the start page or the drop-down at the top left:

- **A drive** (for example `C:`).
- **All drives (This PC)**: every local drive in one scan, for a system-wide view.
- **OneDrive**: your OneDrive folders (personal and work or school) are found automatically. Online-only files are listed with their size but use no local space, and are never downloaded.
- **Any folder**: type a path or use the folder button.

Then choose how to scan:

- **Scan**: works without admin rights. Scan time depends on the drive, file count, permissions and Windows filesystem caches. Protected folders are skipped and listed under **Inaccessible**.
- **Fast scan**: reads the NTFS file table directly for a whole NTFS drive (not an individual folder). It sees protected folders and counts hard-linked files once. Windows asks for administrator permission. If it can't run, the normal scan is used and a notice explains why.

Scanning is read-only: the app does not delete, move or change the files it scans.

It does write a few files of its own:

- its logs
- its settings
- any CSV snapshots and exports you ask for

The file actions are Open (turned off for programs and scripts), Show in File Explorer, Copy path, and "File types in this folder".

## Views

| View | Shows |
|---|---|
| Treemap | Every file as a shaded box sized by space and colored by category. Double-click to zoom, Backspace to go up |
| Sunburst | The current folder in the middle, its contents in rings. Double-click a folder to center it |
| Folders | Expandable folder tree sorted by size, with % of parent, file counts and dates |
| File types | Every extension: total, share, size on disk, count, average, plus a category donut |
| Categories | Category → extensions → largest files |
| **Work files** | Tables of documents, spreadsheets, email, code, AI assistant files or any types you choose, with path, dates and author (see below) |
| Locations | Windows, WinSxS, page/hibernation files, Program Files, each user's Downloads/AppData/OneDrive, online-only cloud files, and what the scan couldn't see |
| Largest files | Top 1,000 files, filtered by name, category and age, with created/modified/accessed dates |
| Age | Space by when files last changed |

**Size on disk** switches every view between file size and space actually used. The details panel on the right shows the selected item's size, contents, created, modified and accessed dates.

### Scope: everything, or one folder

File types, Categories, Work files, Largest files and Age have a **Showing:** bar. Select a folder in any view and click **Use selected folder** to limit those views to it. Click **Everything** to go back. You can also right-click a folder and choose **File types in this folder**.

### Work files

One sortable table per group (click any column header to sort):

| Group | Types |
|---|---|
| All work files | Everything below together |
| Excel & spreadsheets | .xlsx .xlsm .xlsb .xls .xltx .xltm .xlt .xlam .xla .csv .tsv .ods |
| CSV only | .csv |
| Word documents | .docx .docm .doc .dotx .dotm .dot .rtf .odt |
| PowerPoint | .pptx .pptm .ppt .ppsx .ppsm .pps .potx .potm .pot .odp |
| PDF | .pdf |
| Outlook & email | .msg .eml .pst .ost .olm .oft |
| Markdown | .md .markdown .mdx |
| Jupyter notebooks | .ipynb |
| Python | .py .pyw .pyi .pyx |
| DBC Finance | .df2 .ddl (pick .ddl in Types to see only DDL import files) |
| Help files | .chm .hlp |
| Claude | `.claude` folders, `CLAUDE.md`, `CLAUDE.local.md`, `.claude.json`, `.mcp.json`, `claude_desktop_config.json` and `Claude*` files; with **Only my files** off, also `%APPDATA%\Claude` and the Claude app package |
| ChatGPT & Codex | `.codex` and `.agents` folders, `AGENTS.md`, `ChatGPT*` files and `Documents\Codex`; with **Only my files** off, also the ChatGPT and Codex app packages (`%LOCALAPPDATA%\Packages\OpenAI.*`) |
| Gemini & Antigravity | `.gemini`, `.antigravity` and `.agent` folders, `GEMINI.md`, and `Gemini*` and `Antigravity*` files; with **Only my files** off, also `%APPDATA%\Antigravity` and its install folder |
| Any types or all files | The extensions you type, or every file |

- **Types**: show the whole group, or one type on its own (for example only .xlsb).
- **Only my files** skips Windows, Program Files, ProgramData, AppData, package or cache folders (node_modules, site-packages, .venv, .git, ...), and files Windows marks as system files. Turn it off to see everything.
- The AI groups find files by name and folder, so they include settings, instructions, skills, session logs and downloaded installers. Their Types list shows each extension found.

#### Listing every file, or chosen types, in one folder

For example, every `.json`, `.toml` and `.sql` file under `C:\Dev`:

1. Type `C:\Dev` in the box at the top left and click **Scan**. If you have already scanned the drive, select `Dev` in Folders and click **Use selected folder** instead.
2. In **Work files**, pick **Any types or all files** and type `json, toml, sql` in **Extensions**. Leave it empty to list every file.
3. Turn **Only my files** off if you also want `.venv`, `node_modules`, `.git` and similar folders.
4. Click **Snapshot table**. The CSV has every matching file, even when the table shows only the largest 100,000.
- **Columns**: name, type, size, last saved, created, last accessed, author, last saved by, availability (on this PC or online only), folder, and title/subject.
- **Authors** are read from inside the files: Office (new and old formats), PDF, Outlook .msg (sender) and .eml (From).
  - Files are opened read-only, Windows is told not to update their last-accessed date, and online-only files are never opened.
  - This happens automatically for tables with up to 2,000 readable files; for bigger tables click **Read authors**.
  - Markdown, Python, notebooks, CSV, DBC and help files don't record an author.

## Snapshots ("CSV screenshots")

- **Snapshot** (toolbar, or **Snapshot table** in Work files) saves exactly what the current view shows (same scope, filters and sort order) as a CSV file that opens in Excel. In Work files it saves every matching file, even past the 100,000 the table shows.
- **Snapshot all work-file groups** (in the Snapshot menu or the Work files view) creates a new folder with one CSV per group plus `Summary.csv`, reading authors first if that is turned on.
- Files are named `yyyy-MM-dd HHmmss - View - Detail.csv` and go to the folder set in **Settings** (gear button). The default is `Documents\DiskInspector Snapshots`.
- The Snapshot menu also has **Open snapshot folder** and the full exports (every folder, file types, largest files).

## Settings

Saved in `%APPDATA%\DiskInspector\settings.json`:

- the snapshot folder
- whether to read authors
- the automatic author-reading limit
- whether **Only my files** starts on

## Customizing

Create `%APPDATA%\DiskInspector\categories.json` to move extensions or add categories:

```json
{ "categories": [ { "name": "Games", "color": "#2E86AB", "extensions": ["pak", "unity3d"] } ] }
```

Create `%APPDATA%\DiskInspector\work-file-groups.json` to change a group's types (same key) or add groups:

```json
{ "groups": [
  { "key": "dbc", "extensions": ["df2"] },
  { "key": "sql", "name": "SQL scripts", "extensions": ["sql"] },
  { "key": "cursor", "name": "Cursor", "fileNames": [".cursorrules"], "folders": [".cursor", "%APPDATA%\\Cursor"] }
] }
```

- `fileNames` match whole file names and may use `*` and `?`.
- `folders` are folder names found anywhere (`.cursor`), or full paths that may use environment variables and a `*` in the last part.

Built-in keys: `excel`, `csv`, `word`, `powerpoint`, `pdf`, `outlook`, `markdown`, `notebooks`, `python`, `dbc`, `help`, `claude`, `codex`, `gemini`. If a file is broken, the app ignores it and shows a notice.

## Logs

Logs are written under `%LOCALAPPDATA%\DiskInspector\logs\`. UI errors may also show a dialog; unhandled process-level errors can terminate the application.

## Build and test

Use Windows with the **.NET 10 SDK** installed: the app, core library and tests all target `net10.0-windows`, and the desktop UI uses WPF. Run the commands from the repository root. Package restore requires access to the configured NuGet sources on the first build.

```bash
dotnet test tests/DiskInspector.Core.Tests
```

```bash
dotnet publish src/DiskInspector.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

- `tools/DiskInspector.Cli`:
  - `DiskInspector.Cli.exe C:\ [--fast] [--compare <folder>]` prints timings, memory and totals for a real drive.
  - `DiskInspector.Cli.exe --authors <folder> [max]` prints the author, last saved by and title the document reader finds.
  - `DiskInspector.Cli.exe --groups <path> [--all-folders]` prints each work-file group's file count, size and timing.
- `DiskInspector.exe --selftest <outDir> [--target C:\] [--fast] [--no-shots]`: scans, opens every view, tries the main interactions (including work files, scope and snapshots), saves window screenshots and a `report.txt`, then exits.
- Running the tests as administrator also cross-checks the fast scan against the normal scan.

## Layout

- `src/DiskInspector.Core`: scanners (`StandardScanner`, `MftScanner`), the compact `ScanTree`, analysis (file types, locations, work files, document properties, snapshots) and treemap/sunburst layout
- `src/DiskInspector.App`: WPF UI (Fluent theme, follows Windows light/dark)
- `tests/DiskInspector.Core.Tests`: xUnit tests

See [CHANGELOG.md](CHANGELOG.md) for what changed in each version.
