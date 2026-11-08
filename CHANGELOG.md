# Changelog

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
