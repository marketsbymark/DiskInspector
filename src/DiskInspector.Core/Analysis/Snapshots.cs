using System.Text;

namespace DiskInspector.Core.Analysis;

/// <summary>A table to save: what a view is showing right now.</summary>
public sealed record TableSnapshot(string View, string Detail, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Writes "CSV screenshots" of views into the snapshot folder, named so they sort by time.</summary>
public static class Snapshots
{
    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DiskInspector Snapshots");

    /// <summary>Writes the table as UTF-8 CSV (Excel opens it directly) and returns the file path.</summary>
    public static string Write(string folder, TableSnapshot table, DateTime now)
    {
        Directory.CreateDirectory(folder);
        string name = $"{now:yyyy-MM-dd HHmmss} - {SafeName(table.View)}" + (string.IsNullOrWhiteSpace(table.Detail) ? "" : $" - {SafeName(table.Detail)}");
        string path = Unique(Path.Combine(folder, name), ".csv");
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine(string.Join(',', table.Headers.Select(CsvExport.Escape)));
        foreach (var row in table.Rows)
            w.WriteLine(string.Join(',', row.Select(CsvExport.Escape)));
        return path;
    }

    /// <summary>A new timestamped subfolder for a set of related snapshots.</summary>
    public static string NewBatchFolder(string folder, string title, DateTime now)
    {
        string path = Unique(Path.Combine(folder, $"{now:yyyy-MM-dd HHmmss} - {SafeName(title)}"), "");
        Directory.CreateDirectory(path);
        return path;
    }

    static string Unique(string pathWithoutExtension, string extension)
    {
        string candidate = pathWithoutExtension + extension;
        for (int i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = $"{pathWithoutExtension} ({i}){extension}";
        return candidate;
    }

    /// <summary>Makes text safe for a file name and keeps it short.</summary>
    public static string SafeName(string text)
    {
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
        string s = sb.ToString().Trim().TrimEnd('.');
        return s.Length > 80 ? s[..80].TrimEnd() : s.Length == 0 ? "snapshot" : s;
    }

    /// <summary>Local date-time text used in snapshot columns.</summary>
    public static string Date(DateTime utc) =>
        utc == DateTime.MinValue ? "" : utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
