# Changelog

## 1.2.0

### Added

- **Any types or all files** (in Work files): type extensions such as `json, toml, sql`, or leave the box empty for every file. It covers everything scanned or one folder and below: scan a folder such as `C:\Dev`, or use **Use selected folder**.
- **AI assistant groups** in Work files. They match by file name and folder, not only extension:
  - **Claude**: `.claude` folders, `CLAUDE.md`, `.claude.json`, `.mcp.json`, `claude_desktop_config.json` and `Claude*` files. With "Only my files" off, also Claude Desktop's app data.
  - **ChatGPT & Codex**: `.codex` and `.agents` folders, `AGENTS.md`, `ChatGPT*` files and `Documents\Codex`. With "Only my files" off, also the ChatGPT and Codex app packages.
  - **Gemini & Antigravity**: `.gemini`, `.antigravity` and `.agent` folders, `GEMINI.md`, and `Gemini*` and `Antigravity*` files. With "Only my files" off, also Antigravity's app data and install folder.
- `work-file-groups.json` groups can use `fileNames` (wildcards allowed) and `folders` (folder names, or full paths with environment variables).
- `DiskInspector.Cli --groups <path> [--all-folders]` prints each group's file count, size and timing.

### Changed

- **Snapshot table** now saves every matching file, including the ones the table leaves out when it shows only the largest 100,000. Big snapshots are written in the background.
- "Only my files" also leaves out files Windows marks as system files, such as `pagefile.sys`, `hiberfil.sys` and `desktop.ini`.
- In "Any types" and the AI groups, the Types list offers each extension found, including files with no extension.

## 1.1.1

- Work files: the DBC Finance group now includes DDL import files (.ddl) along with .df2. Pick .ddl in Types to see only DDL files.
- Work files: new **Help files** group for compiled help (.chm) and legacy WinHelp (.hlp).
- .chm, .hlp, .df2 and .ddl now count as Documents in the category views.

## 1.1.0

### Added

- **Work files view**: sortable tables for Excel and spreadsheets, CSV only, Word, PowerPoint, PDF, Outlook and email, Markdown, Jupyter notebooks, Python and DBC Finance (.df2) files.
  - You can show the whole group or one type on its own.
  - Each table has a name filter and an "Only my files" option.
  - Columns include created, last saved and last accessed dates, author, last saved by, title or subject, and whether the file is on this PC or online only.
- **Author reading** for Office (new and old formats), PDF, Outlook .msg and .eml files.
  - Files are opened read-only and their last-accessed date is kept.
  - Online-only cloud files are never opened.
- **Scope**: File types, Categories, Work files, Largest files and Age can show everything scanned or one folder ("Use selected folder", or "File types in this folder" on the right-click menu).
- **Snapshots**: save what the current view shows as a CSV file in a folder you choose. "Snapshot all work-file groups" writes one CSV per group plus a summary.
- **Settings** window: snapshot folder, author reading and its automatic limit, and whether "Only my files" starts on.
- **OneDrive targets**: personal and work or school OneDrive folders are detected and offered on the start page. Locations shows how much is online-only.
- **All drives (This PC)** scan, for a system-wide view across every local drive.
- Created and accessed dates are recorded by both scanners and shown in the details panel, Largest files and snapshots.
- `work-file-groups.json` to change or add work-file groups.
- `DiskInspector.Cli --authors` to check the document reader on real files.

### Changed

- The Export button is now a Snapshot button with a menu that also holds the full CSV exports.
- Big tables (File types, Largest files, Work files) sort in the view model, so sorting is fast and snapshots keep the order you see.

## 1.0.0

- First version: normal and fast (NTFS) scans, with Treemap, Sunburst, Folders, File types, Categories, Locations, Largest files and Age views, a details panel and CSV export.
