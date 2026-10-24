using System.Globalization;
using System.Text;
using DiskInspector.Core.Model;

namespace DiskInspector.Core.Analysis;

public static class CsvExport
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void WriteFolders(ScanTree tree, string file, CancellationToken ct = default)
    {
        using var w = new StreamWriter(file, false, new UTF8Encoding(true));
        w.WriteLine("Path,Size (bytes),Size on disk (bytes),Files,Folders,Modified (UTC),Flags");
        for (int i = 0; i < tree.Count; i++)
        {
            if (!tree.IsDirectory(i))
                continue;
            if ((i & 0x3FFF) == 0)
                ct.ThrowIfCancellationRequested();
            w.WriteLine(string.Join(',', Escape(tree.GetFullPath(i)), tree.Size(i).ToString(Inv), tree.Allocated(i).ToString(Inv),
                tree.FileCount(i).ToString(Inv), tree.DirectoryCount(i).ToString(Inv), Date(tree.ModifiedUtc(i)), Escape(tree.Flags(i).ToString())));
        }
    }

    public static void WriteTypes(IEnumerable<ExtensionStat> stats, string file)
    {
        using var w = new StreamWriter(file, false, new UTF8Encoding(true));
        w.WriteLine("Extension,Category,Files,Size (bytes),Size on disk (bytes)");
        foreach (var s in stats)
            w.WriteLine(string.Join(',', Escape(s.DisplayName), Escape(s.Category.Name), s.Count.ToString(Inv), s.Size.ToString(Inv), s.Allocated.ToString(Inv)));
    }

    public static void WriteFiles(ScanTree tree, IEnumerable<int> nodes, string file)
    {
        using var w = new StreamWriter(file, false, new UTF8Encoding(true));
        w.WriteLine("Path,Size (bytes),Size on disk (bytes),Modified (UTC)");
        foreach (int i in nodes)
            w.WriteLine(string.Join(',', Escape(tree.GetFullPath(i)), tree.Size(i).ToString(Inv), tree.Allocated(i).ToString(Inv), Date(tree.ModifiedUtc(i))));
    }

    static string Date(DateTime d) => d == DateTime.MinValue ? "" : d.ToString("yyyy-MM-dd HH:mm:ss", Inv);

    public static string Escape(string s)
    {
        // Leading = + - @ would make spreadsheet apps run the cell as a formula.
        if (s.Length > 0 && "=+-@".Contains(s[0]))
            s = "'" + s;
        return s.AsSpan().IndexOfAny("\",\r\n") >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

public static class Format
{
    static readonly string[] Units = ["KB", "MB", "GB", "TB", "PB", "EB"];

    /// <summary>Size like Explorer shows it (1 KB = 1024 bytes).</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0)
            return "-" + Bytes(bytes == long.MinValue ? long.MaxValue : -bytes);
        if (bytes < 1024)
            return bytes == 1 ? "1 byte" : $"{bytes:N0} bytes";
        double v = bytes / 1024.0;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return v >= 100 ? $"{v:N0} {Units[u]}" : v >= 10 ? $"{v:N1} {Units[u]}" : $"{v:N2} {Units[u]}";
    }

    public static string Percent(double fraction) =>
        double.IsNaN(fraction) || double.IsInfinity(fraction) ? "" : fraction >= 0.9995 ? "100%" : $"{fraction * 100:0.0}%";

    public static string Count(long n) => n.ToString("N0");

    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.0} s" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{(int)t.TotalHours} h {t.Minutes} min";

    public static string Date(DateTime utc) =>
        utc == DateTime.MinValue ? "" : utc.ToLocalTime().ToString("g");
}
