using DiskInspector.Core.Analysis;
using DiskInspector.Core.Model;

namespace DiskInspector.Core.Tests;

public class ModelTests
{
    internal static ScanTree Sample()
    {
        var t = new ScanTree(@"C:\");
        int win = t.Add(0, "Windows", NodeFlags.Directory, 0, 0, 0);
        int sys = t.Add(win, "System32", NodeFlags.Directory, 0, 0, 0);
        t.Add(sys, "kernel32.dll", NodeFlags.None, 1000, 4096, 0);
        t.Add(sys, "ntoskrnl.exe", NodeFlags.None, 5000, 8192, 0);
        int users = t.Add(0, "Users", NodeFlags.Directory, 0, 0, 0);
        int me = t.Add(users, "me", NodeFlags.Directory, 0, 0, 0);
        t.Add(me, "movie.MP4", NodeFlags.None, 20000, 20480, 0);
        t.Add(me, "notes.txt", NodeFlags.None, 10, 4096, 0);
        t.Add(0, "pagefile.sys", NodeFlags.None, 3000, 4096, 0);
        t.Add(users, "Empty", NodeFlags.Directory, 0, 0, 0);
        t.Complete();
        return t;
    }

    [Fact]
    public void Complete_rolls_sizes_and_counts_up()
    {
        var t = Sample();
        Assert.Equal(29010, t.Size(ScanTree.RootId));
        Assert.Equal(4096 + 8192 + 20480 + 4096 + 4096, t.Allocated(ScanTree.RootId));
        Assert.Equal(5, t.FileCount(ScanTree.RootId));
        Assert.Equal(5, t.DirectoryCount(ScanTree.RootId));
        int users = t.FindByPath(@"C:\Users");
        Assert.Equal(20010, t.Size(users));
        Assert.Equal(2, t.FileCount(users));
        Assert.Equal(2, t.DirectoryCount(users));
    }

    [Fact]
    public void Children_are_sorted_largest_first()
    {
        var t = Sample();
        var names = t.Children(ScanTree.RootId).ToArray().Select(t.Name).ToArray();
        Assert.Equal(["Users", "Windows", "pagefile.sys"], names);
        var byAlloc = t.GetChildrenSorted(ScanTree.RootId, SizeMetric.Allocated).Select(t.Name).ToArray();
        Assert.Equal("Users", byAlloc[0]);
    }

    [Fact]
    public void Paths_round_trip()
    {
        var t = Sample();
        int id = t.FindByPath(@"c:\users\ME\movie.mp4");
        Assert.True(id > 0);
        Assert.Equal(@"C:\Users\me\movie.MP4", t.GetFullPath(id));
        Assert.Equal(ScanTree.RootId, t.FindByPath(@"C:\"));
        Assert.Equal(-1, t.FindByPath(@"C:\Nope"));
        Assert.Equal(-1, t.FindByPath(@"D:\Users"));
        Assert.Equal(3, t.Depth(id));
        Assert.Equal(4, t.PathFromRoot(id).Length);
    }

    [Theory]
    [InlineData("photo.JPG", "jpg")]
    [InlineData("archive.tar.gz", "gz")]
    [InlineData(".gitignore", "gitignore")]
    [InlineData("README", "")]
    [InlineData("trailing.", "")]
    [InlineData("weird.this is not an ext", "")]
    [InlineData("long.abcdefghijklmnopq", "")]
    [InlineData("claude.2.1.50", "")]
    [InlineData("backup.zip.001", "001")]
    [InlineData("video.mp4", "mp4")]
    public void Extension_parsing(string name, string expected) =>
        Assert.Equal(expected, ExtensionTable.GetExtension(name).ToString().ToLowerInvariant());

    [Fact]
    public void Extension_ids_are_case_insensitive()
    {
        var t = new ExtensionTable();
        Assert.Equal(t.GetOrAdd("a.PNG"), t.GetOrAdd("b.png"));
        Assert.Equal("png", t[t.GetOrAdd("c.Png")]);
        Assert.Equal(ExtensionTable.None, t.GetOrAdd("noext"));
    }

    [Fact]
    public void Add_rejects_bad_parents()
    {
        var t = new ScanTree(@"C:\");
        int f = t.Add(0, "file.txt", NodeFlags.None, 1, 1, 0);
        Assert.Throws<ArgumentException>(() => t.Add(f, "child", NodeFlags.None, 1, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.Add(99, "x", NodeFlags.None, 1, 1, 0));
    }

    [Fact]
    public void Tree_grows_past_initial_capacity()
    {
        var t = new ScanTree(@"C:\", capacity: 16);
        int dir = t.Add(0, "d", NodeFlags.Directory, 0, 0, 0);
        for (int i = 0; i < 10_000; i++)
            t.Add(dir, $"f{i}.bin", NodeFlags.None, i, i, 0);
        t.Complete();
        Assert.Equal(10_000L * 9_999 / 2, t.Size(0));
        Assert.Equal(10_000, t.FileCount(0));
    }

    [Fact]
    public void Categorizer_maps_common_types()
    {
        var c = Categorizer.Default;
        Assert.Equal("Video", c.ForExtension("mp4").Name);
        Assert.Equal("Video", c.ForExtension("MKV").Name);
        Assert.Equal("Images", c.ForExtension("jpg").Name);
        Assert.Equal("Documents", c.ForExtension("pdf").Name);
        Assert.Equal("Programs & Libraries", c.ForExtension("dll").Name);
        Assert.Equal("System", c.ForExtension("sys").Name);
        Assert.Equal("AI & ML models", c.ForExtension("safetensors").Name);
        Assert.Equal("Archives", c.ForExtension("001").Name);
        Assert.Equal("Other", c.ForExtension("").Name);
        Assert.Equal("Other", c.ForExtension("zzzunknown").Name);
        Assert.Equal(c.Other, c.Categories[^1]);
    }

    [Fact]
    public void Categorizer_user_override_moves_extensions()
    {
        string file = Path.Combine(Path.GetTempPath(), $"di-cat-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """
            { "categories": [ { "name": "Documents", "extensions": ["sys"] }, { "name": "Games", "color": "#112233", "extensions": ["pak", "unity3d"] } ] }
            """);
        try
        {
            var c = Categorizer.Load(includeUserOverride: true, overridePath: file);
            Assert.Equal("Documents", c.ForExtension("sys").Name);
            Assert.Equal("Games", c.ForExtension("pak").Name);
            Assert.Equal(0x112233u, c.ForExtension("unity3d").Color);
            Assert.Equal("Other", c.Categories[^1].Name);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Broken_user_override_is_ignored()
    {
        string file = Path.Combine(Path.GetTempPath(), $"di-cat-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{ not json");
        try
        {
            var c = Categorizer.Load(includeUserOverride: true, overridePath: file);
            Assert.Equal("Video", c.ForExtension("mp4").Name);
            Assert.NotNull(Categorizer.LoadError);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void File_type_and_category_stats()
    {
        var t = Sample();
        var c = Categorizer.Default;
        var ext = FileTypeAnalysis.ByExtension(t, c);
        Assert.Equal("mp4", ext[0].Extension);
        Assert.Equal(20000, ext[0].Size);
        Assert.Equal(t.FindByPath(@"C:\Users\me\movie.MP4"), ext[0].LargestNode);
        Assert.Equal(t.Size(0), ext.Sum(e => e.Size));

        var cats = FileTypeAnalysis.ByCategory(ext, c);
        Assert.Equal("Video", cats[0].Category.Name);
        Assert.Equal(t.Size(0), cats.Sum(x => x.Size));
        Assert.Equal(c.Categories.Count, cats.Count);
    }

    [Fact]
    public void Category_breakdown_per_folder()
    {
        var t = Sample();
        var b = new CategoryBreakdown(t, Categorizer.Default);
        int win = t.FindByPath(@"C:\Windows");
        Assert.Equal("Programs & Libraries", Categorizer.Default.Categories[b.CategoryOf(win)].Name);
        Assert.Equal("Video", Categorizer.Default.Categories[b.CategoryOf(0)].Name);
        Assert.Equal(t.Size(0), b.For(0).Sum(x => x.Bytes));
        int empty = t.FindByPath(@"C:\Users\Empty");
        Assert.Equal(Categorizer.Default.Other.Id, b.CategoryOf(empty));
    }

    [Fact]
    public void Largest_files_query()
    {
        var t = Sample();
        var top = FileQueries.Largest(t, 3, SizeMetric.Logical);
        Assert.Equal(["movie.MP4", "ntoskrnl.exe", "pagefile.sys"], top.Select(t.Name).ToArray());
        var filtered = FileQueries.Largest(t, 10, SizeMetric.Logical, i => t.Extension(i) == "txt");
        Assert.Single(filtered);
        Assert.Empty(FileQueries.Largest(t, 0, SizeMetric.Logical));
    }

    [Fact]
    public void Age_buckets()
    {
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0, AgeAnalysis.BucketOf(now.AddDays(-2).Ticks, now));
        Assert.Equal(1, AgeAnalysis.BucketOf(now.AddDays(-60).Ticks, now));
        Assert.Equal(3, AgeAnalysis.BucketOf(now.AddYears(-2).Ticks, now));
        Assert.Equal(5, AgeAnalysis.BucketOf(now.AddYears(-20).Ticks, now));
        Assert.Equal(6, AgeAnalysis.BucketOf(0, now));
        Assert.Equal(6, AgeAnalysis.BucketOf(now.AddDays(30).Ticks, now));

        var t = Sample();
        var buckets = AgeAnalysis.Compute(t, now);
        Assert.Equal(t.FileCount(0), buckets.Sum(b => b.Count));
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 byte")]
    [InlineData(1023, "1,023 bytes")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536 * 1024, "1.50 MB")]
    [InlineData(250L * 1024 * 1024 * 1024, "250 GB")]
    public void Byte_formatting(long bytes, string expected)
    {
        var old = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        try { Assert.Equal(expected, Format.Bytes(bytes)); }
        finally { Thread.CurrentThread.CurrentCulture = old; }
    }

    [Fact]
    public void Csv_escaping()
    {
        Assert.Equal("plain", CsvExport.Escape("plain"));
        Assert.Equal("\"a,b\"", CsvExport.Escape("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", CsvExport.Escape("say \"hi\""));
        Assert.Equal("'=cmd()", CsvExport.Escape("=cmd()"));
    }
}
