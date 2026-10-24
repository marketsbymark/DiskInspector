using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using DiskInspector.Core.Analysis;
using DiskInspector.Core.Model;
using DiskInspector.Core.Scanning;
using Xunit.Abstractions;

namespace DiskInspector.Core.Tests;

public sealed class TempTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "di-test-" + Guid.NewGuid().ToString("N")[..8]);

    public TempTree() => Directory.CreateDirectory(Root);

    public string File(string relative, int bytes)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public string Dir(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

    public void Dispose()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
                if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(d);
        }
        catch (Exception) { }
        try { Directory.Delete(Root, recursive: true); } catch (Exception) { }
    }
}

public class ScannerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Scans_known_tree_exactly()
    {
        using var tmp = new TempTree();
        tmp.File(@"a\one.txt", 100);
        tmp.File(@"a\two.jpg", 2000);
        tmp.File(@"a\b\three.mp4", 30000);
        tmp.File(@"a\b\c\four", 1);
        tmp.File("top.pdf", 4096);
        tmp.Dir(@"empty\nested");

        var result = await new StandardScanner().ScanAsync(tmp.Root);
        var t = result.Tree;
        Assert.Equal(ScanMode.Standard, result.Mode);
        Assert.Empty(result.Issues);
        Assert.Equal(100 + 2000 + 30000 + 1 + 4096, t.Size(0));
        Assert.Equal(5, t.FileCount(0));
        Assert.Equal(5, t.DirectoryCount(0));
        int b = t.FindByPath(Path.Combine(tmp.Root, "a", "b"));
        Assert.Equal(30001, t.Size(b));
        Assert.True(t.Allocated(0) >= t.Size(0));
        int mp4 = t.FindByPath(Path.Combine(tmp.Root, @"a\b\three.mp4"));
        Assert.Equal("mp4", t.Extension(mp4));
        Assert.True(t.ModifiedUtc(mp4) > DateTime.UtcNow.AddHours(-1));
    }

    [Fact]
    public async Task Junction_loops_are_not_followed()
    {
        using var tmp = new TempTree();
        tmp.File(@"real\data.bin", 5000);
        string link = Path.Combine(tmp.Root, @"real\loop");
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{tmp.Root}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "could not create junction");

        var result = await new StandardScanner().ScanAsync(tmp.Root).WaitAsync(TimeSpan.FromSeconds(30));
        var t = result.Tree;
        Assert.Equal(5000, t.Size(0));
        int loop = t.FindByPath(link);
        Assert.True(loop > 0);
        Assert.True((t.Flags(loop) & NodeFlags.ReparsePoint) != 0);
        Assert.Equal(0, t.ChildCount(loop));
    }

    [Fact]
    public async Task Paths_longer_than_260_characters_are_scanned()
    {
        using var tmp = new TempTree();
        string rel = string.Join('\\', Enumerable.Repeat("a_fairly_long_folder_name_for_testing", 9));
        string deep = Path.Combine(tmp.Root, rel, "deep.txt");
        Assert.True(deep.Length > 300);
        Directory.CreateDirectory(Path.GetDirectoryName(deep)!);
        File.WriteAllBytes(deep, new byte[777]);

        var result = await new StandardScanner().ScanAsync(tmp.Root);
        Assert.Equal(777, result.Tree.Size(0));
        int id = result.Tree.FindByPath(deep);
        Assert.True(id > 0);
        Assert.Equal(deep, result.Tree.GetFullPath(id));
    }

    [Fact]
    public async Task Access_denied_folder_is_flagged_and_scan_continues()
    {
        using var tmp = new TempTree();
        tmp.File(@"open\ok.txt", 10);
        string locked = tmp.Dir("locked");
        tmp.File(@"locked\secret.txt", 999);
        var info = new DirectoryInfo(locked);
        var sid = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(sid, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            var result = await new StandardScanner().ScanAsync(tmp.Root);
            var t = result.Tree;
            int id = t.FindByPath(locked);
            Assert.True(id > 0);
            if (Elevation.IsElevated() && (t.Flags(id) & NodeFlags.AccessDenied) == 0)
                return;   // admins may still be able to list it
            Assert.True((t.Flags(id) & NodeFlags.AccessDenied) != 0);
            Assert.Contains(result.Issues, i => i.Path == locked);
            Assert.Equal(10, t.Size(0));
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
        }
    }

    [Fact]
    public async Task Folder_removed_during_scan_is_reported()
    {
        using var tmp = new TempTree();
        tmp.File(@"keep\a.txt", 50);
        string doomed = tmp.Dir("doomed");
        tmp.File(@"doomed\b.txt", 70);
        var scanner = new StandardScanner(workerCount: 1)
        {
            BeforeDirectory = p => { if (p == doomed) Directory.Delete(doomed, true); },
        };
        var result = await scanner.ScanAsync(tmp.Root);
        Assert.Equal(50, result.Tree.Size(0));
        Assert.Contains(result.Issues, i => i.Path == doomed);
        Assert.True((result.Tree.Flags(result.Tree.FindByPath(doomed)) & NodeFlags.Error) != 0);
    }

    [Fact]
    public async Task Cancel_stops_the_scan()
    {
        using var tmp = new TempTree();
        for (int i = 0; i < 50; i++)
            tmp.File($@"d{i}\f.txt", 1);
        using var cts = new CancellationTokenSource();
        var scanner = new StandardScanner(workerCount: 2) { BeforeDirectory = _ => cts.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(tmp.Root, null, cts.Token));
    }

    [Fact]
    public async Task Missing_folder_throws_clear_error()
    {
        var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new StandardScanner().ScanAsync(@"C:\definitely\not\here\" + Guid.NewGuid()));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task Progress_is_reported()
    {
        using var tmp = new TempTree();
        tmp.File("x.txt", 5);
        var reports = new List<ScanProgress>();
        var progress = new SyncProgress<ScanProgress>(reports.Add);
        await new StandardScanner().ScanAsync(tmp.Root, progress);
        Assert.NotEmpty(reports);
        Assert.Equal(1, reports[^1].Files);
    }

    [Theory]
    [InlineData("c:", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\Windows\", @"C:\Windows")]
    [InlineData("\"C:\\Users\"", @"C:\Users")]
    public void Normalizes_paths(string input, string expected) => Assert.Equal(expected, ScanPaths.Normalize(input));

    [Fact]
    public void Round_up_to_clusters()
    {
        Assert.Equal(0, StandardScanner.RoundUp(0, 4096));
        Assert.Equal(4096, StandardScanner.RoundUp(1, 4096));
        Assert.Equal(8192, StandardScanner.RoundUp(4097, 4096));
    }

    [Fact]
    public async Task Fast_scan_without_admin_fails_cleanly()
    {
        if (Elevation.IsElevated())
            return;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new MftScanner().ScanAsync(@"C:\"));
    }

    [Fact]
    public async Task Fast_scan_rejects_folders()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => new MftScanner().ScanAsync(Path.GetTempPath()));
    }

    /// <summary>Compares the two scanners on real folders. Only runs when the tests are elevated.</summary>
    [Fact]
    public async Task Fast_scan_matches_standard_scan_when_elevated()
    {
        if (!Elevation.IsElevated())
        {
            output.WriteLine("Skipped: run the tests as administrator to cross-check the MFT scanner.");
            return;
        }
        var fast = await new MftScanner().ScanAsync(@"C:\");
        Assert.Equal(ScanMode.Mft, fast.Mode);
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Documents" })
        {
            if (!Directory.Exists(folder))
                continue;
            var normal = await new StandardScanner().ScanAsync(folder);
            int id = fast.Tree.FindByPath(folder);
            Assert.True(id >= 0, folder);
            long a = normal.Tree.Size(0), b = fast.Tree.Size(id);
            int fa = normal.Tree.FileCount(0), fb = fast.Tree.FileCount(id);
            int links = 0;
            for (int i = 0; i < fast.Tree.Count; i++)
                if ((fast.Tree.Flags(i) & NodeFlags.HardLink) != 0 && fast.Tree.IsAncestorOrSelf(id, i))
                    links++;
            output.WriteLine($"{folder}: standard {a:N0} bytes / {fa:N0} files, fast {b:N0} bytes / {fb:N0} files, {links:N0} hard-linked names");
            // Every name is listed by both scans.
            Assert.InRange(fb, fa * 0.99 - 50, fa * 1.01 + 50);
            // The fast scan counts a hard-linked file's space once; the normal scan once per name.
            Assert.True(b <= a * 1.01 + (1 << 20), "fast scan should never report more than the normal scan");
            if (links == 0)
                Assert.InRange(b, a * 0.99 - (1 << 20), a * 1.01 + (1 << 20));
        }
    }

    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        readonly object _gate = new();
        public void Report(T value) { lock (_gate) report(value); }
    }
}

public class NtfsFormatTests
{
    [Fact]
    public void Decodes_data_runs_with_negative_offsets_and_sparse_runs()
    {
        // 0x21: 1-byte length, 2-byte offset. Run 1: 0x18 clusters at LCN 0x5634.
        // 0x11: 1-byte length, 1-byte offset (-0x10).  0x01: sparse run of 5.
        byte[] runs = [0x21, 0x18, 0x34, 0x56, 0x11, 0x08, 0xF0, 0x01, 0x05, 0x00];
        var list = Scanning.Ntfs.NtfsFormat.DecodeRuns(runs);
        Assert.Equal(3, list.Count);
        Assert.Equal((0x5634L, 0x18L), (list[0].Lcn, list[0].Length));
        Assert.Equal((0x5634L - 0x10, 8L), (list[1].Lcn, list[1].Length));
        Assert.True(list[2].IsSparse);
        Assert.Equal(5, list[2].Length);
    }

    [Fact]
    public void Corrupt_runs_throw()
    {
        Assert.Throws<InvalidDataException>(() => Scanning.Ntfs.NtfsFormat.DecodeRuns([0x21, 0x18]));
        Assert.Throws<InvalidDataException>(() => Scanning.Ntfs.NtfsFormat.DecodeRuns([0x90, 0x01]));
    }

    static byte[] BuildRecord(string name, long parent, long size, bool dir)
    {
        var rec = new byte[1024];
        "FILE"u8.CopyTo(rec);
        BitConverter.TryWriteBytes(rec.AsSpan(4), (ushort)0x30);      // update sequence offset
        BitConverter.TryWriteBytes(rec.AsSpan(6), (ushort)3);         // usn + 2 sectors
        BitConverter.TryWriteBytes(rec.AsSpan(0x14), (ushort)0x38);   // first attribute
        BitConverter.TryWriteBytes(rec.AsSpan(0x16), (ushort)(dir ? 3 : 1));
        int off = 0x38;

        // $STANDARD_INFORMATION (resident, 0x48 value)
        off = Attr(rec, off, 0x10, 0x48, v =>
        {
            BitConverter.TryWriteBytes(v[0x08..], 133_000_000_000_000_000L);
            BitConverter.TryWriteBytes(v[0x20..], 0x20u);
        });
        // $FILE_NAME (Win32 namespace)
        off = Attr(rec, off, 0x30, 0x42 + name.Length * 2, v =>
        {
            BitConverter.TryWriteBytes(v, parent | (1L << 48));
            v[0x40] = (byte)name.Length;
            v[0x41] = 1;
            System.Text.Encoding.Unicode.GetBytes(name).CopyTo(v[0x42..]);
        });
        if (!dir)
        {
            // Non-resident unnamed $DATA
            int len = 0x48;
            BitConverter.TryWriteBytes(rec.AsSpan(off), 0x80u);
            BitConverter.TryWriteBytes(rec.AsSpan(off + 4), (uint)len);
            rec[off + 8] = 1;
            BitConverter.TryWriteBytes(rec.AsSpan(off + 0x20), (ushort)0x40);
            BitConverter.TryWriteBytes(rec.AsSpan(off + 0x28), 8192L);
            BitConverter.TryWriteBytes(rec.AsSpan(off + 0x30), size);
            rec[off + 0x40] = 0;   // empty run list
            off += len;
        }
        BitConverter.TryWriteBytes(rec.AsSpan(off), 0xFFFFFFFFu);
        BitConverter.TryWriteBytes(rec.AsSpan(0x18), (uint)(off + 8));

        // Fixups: usn 0x0102 at the end of each 512-byte sector, originals saved in the array.
        BitConverter.TryWriteBytes(rec.AsSpan(0x30), (ushort)0x0102);
        for (int i = 1; i <= 2; i++)
        {
            int pos = i * 512 - 2;
            rec[0x30 + i * 2] = rec[pos];
            rec[0x30 + i * 2 + 1] = rec[pos + 1];
            rec[pos] = 0x02;
            rec[pos + 1] = 0x01;
        }
        return rec;
    }

    static int Attr(byte[] rec, int off, uint type, int valueLength, SpanAction body)
    {
        int len = (0x18 + valueLength + 7) & ~7;
        BitConverter.TryWriteBytes(rec.AsSpan(off), type);
        BitConverter.TryWriteBytes(rec.AsSpan(off + 4), (uint)len);
        BitConverter.TryWriteBytes(rec.AsSpan(off + 0x10), (uint)valueLength);
        BitConverter.TryWriteBytes(rec.AsSpan(off + 0x14), (ushort)0x18);
        body(rec.AsSpan(off + 0x18, valueLength));
        return off + len;
    }

    delegate void SpanAction(Span<byte> value);

    [Fact]
    public void Parses_a_file_record()
    {
        var rec = BuildRecord("report.docx", 42, 123_456, dir: false);
        Assert.True(Scanning.Ntfs.NtfsFormat.ApplyFixups(rec));
        var names = new List<Scanning.Ntfs.FileNameEntry>();
        Assert.True(Scanning.Ntfs.NtfsFormat.Parse(rec, out var info, names));
        Assert.True(info.InUse);
        Assert.False(info.IsDirectory);
        var name = Assert.Single(names);
        Assert.Equal("report.docx", name.Name);
        Assert.Equal(42, name.ParentRecord);
        Assert.False(name.IsDosOnly);
        Assert.Equal(123_456, info.DataSize);
        Assert.Equal(8192, info.DataAllocated);
        Assert.Equal(0x20u, info.Attributes);
    }

    [Fact]
    public void Torn_record_fails_fixups()
    {
        var rec = BuildRecord("x", 5, 1, dir: false);
        rec[1022] = 0x99;
        Assert.False(Scanning.Ntfs.NtfsFormat.ApplyFixups(rec));
    }

    [Fact]
    public void Record_table_tracks_names_and_hard_links()
    {
        var table = new MftScanner.RecordTable(64);
        table.Load(20, BuildRecord("folder", 5, 0, dir: true));
        table.Load(21, BuildRecord("big.iso", 20, 5_000_000_000, dir: false));
        Assert.True(table.IsLiveDirectory(20));
        Assert.True(table.IsLinkable(21));
        Assert.Equal("big.iso", table.Names[21]);
        Assert.Equal(5_000_000_000, table.Size[21]);
        Assert.Equal(20, table.Parent[21]);
        Assert.Empty(table.Links);

        // An extension record for 21 carrying a second name in another folder: a hard link.
        var ext = BuildRecord("copy.iso", 22, 0, dir: false);
        BitConverter.TryWriteBytes(ext.AsSpan(0x20), 21L);   // base record reference (fixups untouched: outside sector ends)
        table.Load(40, ext);
        Assert.True(table.Links.TryGetValue(21, out var links));
        Assert.Equal([("big.iso", 20), ("copy.iso", 22)], links);
        Assert.Equal("big.iso", table.Names[21]);
    }

    [Fact]
    public void File_time_conversion()
    {
        Assert.Equal(0, MftScanner.FileTimeToTicks(0));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
            MftScanner.FileTimeToTicks(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()));
    }
}
