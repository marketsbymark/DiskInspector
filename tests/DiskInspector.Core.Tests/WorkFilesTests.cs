using System.IO.Compression;
using System.Text;
using DiskInspector.Core.Analysis;
using DiskInspector.Core.Model;
using DiskInspector.Core.Scanning;

namespace DiskInspector.Core.Tests;

public class ScopeAndTimesTests
{
    [Fact]
    public void Tree_keeps_created_and_accessed_times()
    {
        var t = new ScanTree(@"C:\");
        var created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var accessed = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        int f = t.Add(0, "a.txt", NodeFlags.None, 1, 1, accessed.Ticks, created.Ticks, accessed.Ticks);
        t.Complete();
        Assert.Equal(created, t.CreatedUtc(f));
        Assert.Equal(accessed, t.AccessedUtc(f));
        Assert.Equal(DateTime.MinValue, t.CreatedUtc(0));
    }

    [Fact]
    public void Computer_root_paths()
    {
        var t = new ScanTree(ScanTree.ComputerRoot);
        int c = t.Add(0, "C:", NodeFlags.Directory, 0, 0, 0);
        int d = t.Add(0, "D:", NodeFlags.Directory, 0, 0, 0);
        int users = t.Add(c, "Users", NodeFlags.Directory, 0, 0, 0);
        int f = t.Add(users, "x.pdf", NodeFlags.None, 10, 10, 0);
        t.Add(d, "y.pdf", NodeFlags.None, 5, 5, 0);
        t.Complete();
        Assert.True(t.IsComputerRoot);
        Assert.Equal(@"C:\", t.GetFullPath(c));
        Assert.Equal(@"C:\Users\x.pdf", t.GetFullPath(f));
        Assert.Equal(f, t.FindByPath(@"c:\users\X.PDF"));
        Assert.Equal(c, t.FindByPath(@"C:\"));
        Assert.Equal(ScanTree.RootId, t.FindByPath(ScanTree.ComputerRoot));
        Assert.Equal(15, t.Size(0));
    }

    [Fact]
    public void Visit_descendants_of_a_subtree()
    {
        var t = ModelTests.Sample();
        int users = t.FindByPath(@"C:\Users");
        var seen = new List<string>();
        t.VisitDescendants(users, i => seen.Add(t.Name(i)));
        Assert.Equal(["Empty", "me", "movie.MP4", "notes.txt"], seen.Order().ToArray());
        int all = 0;
        t.VisitDescendants(ScanTree.RootId, _ => all++);
        Assert.Equal(t.Count - 1, all);
    }

    [Fact]
    public void Scoped_breakdowns_only_count_the_folder()
    {
        var t = ModelTests.Sample();
        int users = t.FindByPath(@"C:\Users");
        var ext = FileTypeAnalysis.ByExtension(t, Categorizer.Default, users);
        Assert.Equal(["mp4", "txt"], ext.Select(e => e.Extension).ToArray());
        Assert.Equal(t.Size(users), ext.Sum(e => e.Size));
        Assert.Equal(2, AgeAnalysis.Compute(t, DateTime.UtcNow, users).Sum(b => b.Count));
        var top = FileQueries.Largest(t, 10, SizeMetric.Logical, root: users);
        Assert.Equal(2, top.Length);
        Assert.All(top, n => Assert.True(t.IsAncestorOrSelf(users, n)));
    }

    [Fact]
    public async Task Standard_scan_records_created_and_accessed()
    {
        using var tmp = new TempTree();
        string f = tmp.File("doc.txt", 10);
        var created = new DateTime(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var accessed = new DateTime(2023, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(f, created);
        File.SetLastAccessTimeUtc(f, accessed);
        var result = await new StandardScanner().ScanAsync(tmp.Root);
        int id = result.Tree.FindByPath(f);
        Assert.Equal(created, result.Tree.CreatedUtc(id));
        Assert.Equal(accessed, result.Tree.AccessedUtc(id));
    }

    [Fact]
    public async Task All_drives_scan_puts_each_drive_under_this_pc()
    {
        using var a = new TempTree();
        using var b = new TempTree();
        a.File(@"x\one.bin", 100);
        b.File("two.bin", 50);
        var scanner = new StandardScanner { DrivesOverride = () => [a.Root + "\\", b.Root + "\\"] };
        var result = await scanner.ScanAsync(ScanTree.ComputerRoot);
        var t = result.Tree;
        Assert.True(t.IsComputerRoot);
        Assert.Equal(2, t.ChildCount(0));
        Assert.Equal(150, t.Size(0));
        // The stand-in "drives" are named after their folders, so walk down instead of looking up by path.
        int driveA = t.FindChild(0, a.Root);
        int one = t.FindChild(t.FindChild(driveA, "x"), "one.bin");
        Assert.True(one > 0);
        Assert.Equal(Path.Combine(a.Root, @"x\one.bin"), t.GetFullPath(one));
        Assert.Equal(2, result.Volumes.Count);
    }

    [Fact]
    public void This_pc_target_is_recognised()
    {
        Assert.Equal(ScanTree.ComputerRoot, ScanPaths.Normalize("this pc"));
        Assert.True(ScanPaths.Exists(ScanTree.ComputerRoot));
        Assert.False(ScanPaths.IsDriveRoot(ScanTree.ComputerRoot));
    }

    [Fact]
    public void Locations_report_online_only_files()
    {
        var t = new ScanTree(@"C:\");
        int od = t.Add(0, "OneDrive", NodeFlags.Directory, 0, 0, 0);
        t.Add(od, "cloud.pdf", NodeFlags.CloudPlaceholder, 5000, 0, 0);
        t.Add(od, "local.pdf", NodeFlags.None, 100, 4096, 0);
        t.Complete();
        var rows = LocationAnalysis.Compute(new ScanResult { Tree = t, Mode = ScanMode.Standard, Duration = TimeSpan.Zero });
        var cloud = Assert.Single(rows, r => r.Name == "Online-only cloud files");
        Assert.Equal(5000, cloud.Size);
        Assert.Equal(1, cloud.Files);
    }
}

public class WorkFilesTests
{
    static ScanTree Tree()
    {
        var t = new ScanTree(@"C:\");
        int users = t.Add(0, "Users", NodeFlags.Directory, 0, 0, 0);
        int me = t.Add(users, "me", NodeFlags.Directory, 0, 0, 0);
        int docs = t.Add(me, "Documents", NodeFlags.Directory, 0, 0, 0);
        t.Add(docs, "budget.xlsx", NodeFlags.None, 10, 10, 0);
        t.Add(docs, "data.csv", NodeFlags.None, 20, 20, 0);
        t.Add(docs, "memo.docx", NodeFlags.None, 30, 30, 0);
        t.Add(docs, "deal.df2", NodeFlags.None, 40, 40, 0);
        t.Add(docs, "notes.md", NodeFlags.None, 1, 1, 0);
        t.Add(docs, "model.ipynb", NodeFlags.None, 2, 2, 0);
        t.Add(docs, "script.py", NodeFlags.None, 3, 3, 0);
        int appdata = t.Add(me, "AppData", NodeFlags.Directory, 0, 0, 0);
        t.Add(appdata, "cache.py", NodeFlags.None, 5, 5, 0);
        int venv = t.Add(docs, ".venv", NodeFlags.Directory, 0, 0, 0);
        t.Add(venv, "lib.py", NodeFlags.None, 6, 6, 0);
        int win = t.Add(0, "Windows", NodeFlags.Directory, 0, 0, 0);
        t.Add(win, "setup.py", NodeFlags.None, 7, 7, 0);
        t.Complete();
        return t;
    }

    [Fact]
    public void Default_groups_cover_the_requested_types()
    {
        var groups = WorkFiles.LoadGroups(includeUserFile: false);
        Assert.Equal(WorkFiles.AllKey, groups[0].Key);
        string Of(string key) => string.Join(",", groups.Single(g => g.Key == key).Extensions);
        Assert.Contains("xlsb", Of("excel"));
        Assert.Contains("csv", Of("excel"));
        Assert.Equal("csv", Of("csv"));
        Assert.Contains("docx", Of("word"));
        Assert.Contains("pptx", Of("powerpoint"));
        Assert.Equal("pdf", Of("pdf"));
        Assert.Contains("msg", Of("outlook"));
        Assert.Contains("md", Of("markdown"));
        Assert.Equal("ipynb", Of("notebooks"));
        Assert.Contains("py", Of("python"));
        Assert.Equal("df2", Of("dbc"));
        Assert.All(groups.Skip(1).SelectMany(g => g.Extensions), e => Assert.Contains(e, groups[0].Extensions));
    }

    [Fact]
    public void User_file_changes_and_adds_groups()
    {
        string file = Path.Combine(Path.GetTempPath(), $"di-groups-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "groups": [ { "key": "dbc", "extensions": ["df2", ".DDL"] }, { "key": "sql", "name": "SQL", "extensions": ["sql"] } ] }""");
        try
        {
            var groups = WorkFiles.LoadGroups(true, file);
            Assert.Equal(["df2", "ddl"], groups.Single(g => g.Key == "dbc").Extensions);
            Assert.Equal("DBC Finance", groups.Single(g => g.Key == "dbc").Name);
            Assert.Equal("SQL", groups.Single(g => g.Key == "sql").Name);
            Assert.Contains("sql", groups[0].Extensions);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Find_skips_program_and_package_folders_when_asked()
    {
        var t = Tree();
        var mask = WorkFiles.ExtensionMask(t, ["py"]);
        var mine = WorkFiles.Find(t, mask, ScanTree.RootId, onlyUserFolders: true).Select(t.Name).ToArray();
        Assert.Equal(["script.py"], mine);
        var all = WorkFiles.Find(t, mask, ScanTree.RootId, onlyUserFolders: false).Select(t.Name).Order().ToArray();
        Assert.Equal(["cache.py", "lib.py", "script.py", "setup.py"], all);
        // Scoping into a skipped folder still lists its own files.
        int appdata = t.FindByPath(@"C:\Users\me\AppData");
        Assert.Equal(["cache.py"], WorkFiles.Find(t, mask, appdata, onlyUserFolders: true).Select(t.Name).ToArray());
    }

    [Fact]
    public void Counts_by_extension_for_group_totals()
    {
        var t = Tree();
        var counts = WorkFiles.CountByExtension(t, ScanTree.RootId, onlyUserFolders: true);
        Assert.True(t.Extensions.TryGetId("py", out ushort py));
        Assert.Equal(1, counts[py]);
        Assert.True(t.Extensions.TryGetId("df2", out ushort df2));
        Assert.Equal(1, counts[df2]);
    }
}

public class DocumentPropertiesTests
{
    static string MakeOpenXml(string ext, string coreXml)
    {
        string path = Path.Combine(Path.GetTempPath(), $"di-doc-{Guid.NewGuid():N}.{ext}");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open()))
                w.Write("<Types/>");
            using (var w = new StreamWriter(zip.CreateEntry("docProps/core.xml").Open()))
                w.Write(coreXml);
        }
        return path;
    }

    const string Core = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/">
        <dc:title></dc:title><dc:creator>Ada Lovelace</dc:creator><cp:lastModifiedBy>Charles Babbage</cp:lastModifiedBy></cp:coreProperties>
        """;

    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    public void Reads_open_xml_author_even_after_an_empty_title(string ext)
    {
        string path = MakeOpenXml(ext, Core);
        try
        {
            var info = DocumentProperties.TryRead(path);
            Assert.NotNull(info);
            Assert.Equal("Ada Lovelace", info!.Author);
            Assert.Equal("Charles Babbage", info.LastSavedBy);
            Assert.Null(info.Title);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Reading_does_not_change_the_last_access_date()
    {
        string path = MakeOpenXml("docx", Core);
        try
        {
            var old = new DateTime(2015, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            File.SetLastAccessTimeUtc(path, old);
            var write = File.GetLastWriteTimeUtc(path);
            Assert.Equal("Ada Lovelace", DocumentProperties.TryRead(path)?.Author);
            Assert.Equal(old, File.GetLastAccessTimeUtc(path));
            Assert.Equal(write, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Broken_files_return_null_instead_of_throwing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"di-bad-{Guid.NewGuid():N}.xlsx");
        File.WriteAllText(path, "<!DOCTYPE html><html>not a workbook</html>");
        try
        {
            Assert.Null(DocumentProperties.TryRead(path));
            Assert.Null(DocumentProperties.TryRead(path + ".missing"));
            Assert.Null(DocumentProperties.TryRead(Path.ChangeExtension(path, ".py")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parses_ole_summary_property_set()
    {
        // Property set with one section: codepage 1252, author (4) as VT_LPSTR, last author (8) as VT_LPWSTR.
        var section = new List<byte>();
        void U32(List<byte> b, uint v) => b.AddRange(BitConverter.GetBytes(v));
        var values = new List<byte>();
        var offsets = new List<(uint Id, int Off)>();
        int headerLen = 8 + 3 * 8;
        offsets.Add((1, headerLen + values.Count)); values.AddRange([2, 0, 0, 0, 0xE4, 0x04, 0, 0]);          // VT_I2 1252
        offsets.Add((4, headerLen + values.Count)); values.AddRange([0x1E, 0, 0, 0]); U32(values, 6); values.AddRange(Encoding.Latin1.GetBytes("Zoë A\0")); values.AddRange([0, 0]);
        offsets.Add((8, headerLen + values.Count)); values.AddRange([0x1F, 0, 0, 0]); U32(values, 4); values.AddRange(Encoding.Unicode.GetBytes("Bob\0"));
        U32(section, (uint)(headerLen + values.Count));
        U32(section, 3);
        foreach (var (id, off) in offsets) { U32(section, id); U32(section, (uint)off); }
        section.AddRange(values);

        var data = new List<byte> { 0xFE, 0xFF, 0, 0 };
        U32(data, 0); data.AddRange(new byte[16]); U32(data, 1); data.AddRange(new byte[16]); U32(data, 48);
        data.AddRange(section);

        var props = DocumentProperties.ReadPropertySet([.. data]);
        Assert.Equal("Zoë A", props[4]);
        Assert.Equal("Bob", props[8]);
    }

    [Fact]
    public void Reads_pdf_info_strings()
    {
        string path = Path.Combine(Path.GetTempPath(), $"di-{Guid.NewGuid():N}.pdf");
        var pdf = "%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\n" +
                  "9 0 obj << /Title (Old \\(draft\\)) /Author (Old Name) >> endobj\n" +
                  "10 0 obj << /Title (Q3 Report) /Author <FEFF00C5006E006E0061> /Creator (Word) >> endobj\n" +
                  "trailer << /Info 10 0 R >>\n%%EOF";
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(pdf));
        try
        {
            var info = DocumentProperties.TryRead(path);
            Assert.Equal("Ånna", info?.Author);
            Assert.Equal("Q3 Report", info?.Title);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Encrypted_pdf_strings_are_not_reported()
    {
        string path = Path.Combine(Path.GetTempPath(), $"di-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes("%PDF-1.6\n1 0 obj << /Author (x#@!) >> endobj\ntrailer << /Encrypt 5 0 R /Info 1 0 R >>"));
        try
        {
            Assert.Null(DocumentProperties.TryRead(path)?.Author);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Reads_eml_from_and_subject_with_encoded_words()
    {
        string path = Path.Combine(Path.GetTempPath(), $"di-{Guid.NewGuid():N}.eml");
        File.WriteAllText(path, "Received: x\r\nFrom: =?utf-8?B?SsO8cmdlbg==?= <j@example.com>\r\nSubject: =?iso-8859-1?Q?Caf=E9_plans?=\r\n\r\nFrom: not a header\r\n");
        try
        {
            var info = DocumentProperties.TryRead(path);
            Assert.Equal("Jürgen <j@example.com>", info?.Author);
            Assert.Equal("Café plans", info?.Title);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class SnapshotTests
{
    [Fact]
    public void Writes_csv_named_by_time_and_view()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"di-snap-{Guid.NewGuid():N}");
        try
        {
            var now = new DateTime(2026, 11, 7, 9, 5, 3);
            var table = new TableSnapshot("Work files", "Excel: C:\\data?", ["Name", "Note"], [["a,b.xlsx", "say \"hi\""], ["=x", ""]]);
            string path = Snapshots.Write(dir, table, now);
            Assert.Equal("2026-11-07 090503 - Work files - Excel_ C__data_.csv", Path.GetFileName(path));
            var lines = File.ReadAllLines(path);
            Assert.Equal(["Name,Note", "\"a,b.xlsx\",\"say \"\"hi\"\"\"", "'=x,"], lines);
            string again = Snapshots.Write(dir, table, now);
            Assert.NotEqual(path, again);
            Assert.EndsWith("(2).csv", again);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Batch_folders_are_unique()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"di-snap-{Guid.NewGuid():N}");
        try
        {
            var now = DateTime.Now;
            string a = Snapshots.NewBatchFolder(dir, "Work files", now);
            string b = Snapshots.NewBatchFolder(dir, "Work files", now);
            Assert.True(Directory.Exists(a) && Directory.Exists(b));
            Assert.NotEqual(a, b);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
