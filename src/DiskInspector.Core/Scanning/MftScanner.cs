using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskInspector.Core.Model;
using DiskInspector.Core.Scanning.Ntfs;
using Microsoft.Win32.SafeHandles;

namespace DiskInspector.Core.Scanning;

/// <summary>
/// Reads the NTFS Master File Table directly. Much faster than walking folders and sees
/// protected folders too, but needs administrator rights and only works for whole NTFS drives.
/// Any problem is thrown as an exception so the caller can fall back to <see cref="StandardScanner"/>.
/// </summary>
public sealed class MftScanner : IScanner
{
    const int RootRecord = 5;
    const int FirstUserRecord = 16;
    const int ChunkBytes = 4 * 1024 * 1024;

    public Task<ScanResult> ScanAsync(string path, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(path, progress, cancellationToken), cancellationToken);

    ScanResult Scan(string path, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        string root = ScanPaths.Normalize(path);
        if (!ScanPaths.IsDriveRoot(root))
            throw new NotSupportedException("The fast scan works on whole drives only.");
        var volume = VolumeInfo.TryGet(root) ?? throw new IOException($"Drive {root} is not ready.");
        if (!volume.IsNtfs)
            throw new NotSupportedException($"The fast scan needs an NTFS drive; {root} is {volume.Format}.");

        using var handle = OpenVolume(root);
        var buffer = AlignedBuffer.Rent(ChunkBytes + 65536);
        try
        {
            var boot = ReadBoot(handle, buffer);
            var (runs, mftBytes) = ReadMftLayout(handle, boot, buffer);
            long records = mftBytes / boot.RecordSize;
            if (records < FirstUserRecord || records > int.MaxValue / 2)
                throw new InvalidDataException($"Unexpected file table size ({records:N0} records).");

            var table = new RecordTable((int)records);
            ReadAllRecords(handle, boot, runs, table, buffer, progress, clock, ct);
            ct.ThrowIfCancellationRequested();
            var (tree, orphans) = BuildTree(root, table, ct);

            var issues = new List<ScanIssue>();
            if (table.BadRecords > 0)
                issues.Add(new ScanIssue(root, $"{table.BadRecords:N0} damaged file-table records were skipped."));
            if (orphans > 0)
                issues.Add(new ScanIssue(root, $"{orphans:N0} entries were not linked to any folder and were skipped."));

            progress?.Report(new ScanProgress(tree.FileCount(0), tree.DirectoryCount(0), tree.Size(0), root, clock.Elapsed, 1));
            return new ScanResult { Tree = tree, Mode = ScanMode.Mft, Duration = clock.Elapsed, Issues = issues, Volume = volume };
        }
        finally
        {
            buffer.Dispose();
        }
    }

    static SafeFileHandle OpenVolume(string root)
    {
        const uint GenericRead = 0x80000000, ShareAll = 0x7, OpenExisting = 3;
        var h = Native.CreateFile(@"\\.\" + root.TrimEnd('\\'), GenericRead, ShareAll, 0, OpenExisting, 0, 0);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            if (err == 5)
                throw new UnauthorizedAccessException("The fast scan needs administrator rights.");
            throw new Win32Exception(err, $"Could not open drive {root} for reading.");
        }
        return h;
    }

    static NtfsBootSector ReadBoot(SafeFileHandle h, AlignedBuffer buffer)
    {
        var span = buffer.Span[..4096];
        Native.ReadAt(h, 0, span);
        return NtfsBootSector.Parse(span);
    }

    /// <summary>Finds where $MFT itself is stored, following its attribute list if it is very fragmented.</summary>
    static (List<DataRun> Runs, long Bytes) ReadMftLayout(SafeFileHandle h, NtfsBootSector boot, AlignedBuffer buffer)
    {
        var rec0 = ReadRecordAt(h, boot, boot.MftLcn * boot.ClusterSize, buffer);
        var extents = NtfsFormat.ReadDataExtents(rec0);
        long bytes = extents.FirstOrDefault(e => e.StartVcn == 0).DataSize;
        var runs = Flatten(extents);
        if (bytes <= 0 || runs.Count == 0)
            throw new InvalidDataException("Could not find the file table's own data.");

        long clustersNeeded = (bytes + boot.ClusterSize - 1) / boot.ClusterSize;
        if (runs.Sum(r => r.Length) < clustersNeeded)
        {
            // $MFT is split across several records: gather the other extents from its attribute list.
            foreach (long recNo in NtfsFormat.ReadAttributeListDataRecords(rec0).Distinct())
            {
                if (recNo == 0)
                    continue;
                long offset = VcnToOffset(runs, boot, recNo * boot.RecordSize)
                    ?? throw new InvalidDataException("File table extension record is out of reach.");
                var ext = ReadRecordAt(h, boot, offset, buffer);
                extents.AddRange(NtfsFormat.ReadDataExtents(ext));
                runs = Flatten(extents);
            }
            if (runs.Sum(r => r.Length) < clustersNeeded)
                throw new InvalidDataException("The file table layout is incomplete.");
        }
        return (runs, bytes);
    }

    static List<DataRun> Flatten(List<(long StartVcn, List<DataRun> Runs, long DataSize)> extents) =>
        extents.GroupBy(e => e.StartVcn).Select(g => g.First()).OrderBy(e => e.StartVcn).SelectMany(e => e.Runs).ToList();

    static long? VcnToOffset(List<DataRun> runs, NtfsBootSector boot, long byteOffset)
    {
        long vcn = byteOffset / boot.ClusterSize, within = byteOffset % boot.ClusterSize;
        foreach (var r in runs)
        {
            if (vcn < r.Length)
                return r.IsSparse ? null : (r.Lcn + vcn) * boot.ClusterSize + within;
            vcn -= r.Length;
        }
        return null;
    }

    static byte[] ReadRecordAt(SafeFileHandle h, NtfsBootSector boot, long offset, AlignedBuffer buffer)
    {
        long aligned = offset / boot.BytesPerSector * boot.BytesPerSector;
        int skip = (int)(offset - aligned);
        int len = (skip + boot.RecordSize + boot.BytesPerSector - 1) / boot.BytesPerSector * boot.BytesPerSector;
        var span = buffer.Span[..len];
        Native.ReadAt(h, aligned, span);
        var rec = span.Slice(skip, boot.RecordSize).ToArray();
        if (!NtfsFormat.HasFileSignature(rec) || !NtfsFormat.ApplyFixups(rec))
            throw new InvalidDataException("A file table record is damaged.");
        return rec;
    }

    static void ReadAllRecords(SafeFileHandle h, NtfsBootSector boot, List<DataRun> runs, RecordTable table, AlignedBuffer buffer,
        IProgress<ScanProgress>? progress, Stopwatch clock, CancellationToken ct)
    {
        int recSize = boot.RecordSize;
        long totalBytes = (long)table.Count * recSize;
        long streamPos = 0;      // bytes of $MFT handed to the parser so far
        int filled = 0;          // bytes waiting in the buffer
        long nextRecord = 0;
        var span = buffer.Span;
        int chunk = (int)(ChunkBytes / boot.ClusterSize * boot.ClusterSize);
        if (chunk <= 0)
            chunk = (int)boot.ClusterSize;
        long lastReport = 0;

        foreach (var run in runs)
        {
            long runBytes = run.Length * boot.ClusterSize;
            long runPos = 0;
            while (runPos < runBytes && streamPos + filled < totalBytes)
            {
                ct.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(Math.Max(chunk - filled, boot.BytesPerSector), runBytes - runPos);
                toRead = Math.Min(toRead, span.Length - filled);
                toRead = (int)(toRead / boot.BytesPerSector * boot.BytesPerSector);
                if (toRead <= 0)
                    throw new InvalidDataException("File table read alignment problem.");
                var target = span.Slice(filled, toRead);
                if (run.IsSparse)
                    target.Clear();
                else
                    Native.ReadAt(h, run.Lcn * boot.ClusterSize + runPos, target);
                runPos += toRead;
                filled += toRead;

                int whole = filled / recSize;
                for (int k = 0; k < whole && nextRecord < table.Count; k++)
                    table.Load((int)nextRecord++, span.Slice(k * recSize, recSize));
                int consumed = whole * recSize;
                if (consumed < filled)
                    span[consumed..filled].CopyTo(span);
                streamPos += consumed;
                filled -= consumed;

                if (progress is not null && clock.ElapsedMilliseconds - lastReport >= 100)
                {
                    lastReport = clock.ElapsedMilliseconds;
                    progress.Report(new ScanProgress(table.Files, table.Directories, table.Bytes, $"File table record {nextRecord:N0} of {table.Count:N0}",
                        clock.Elapsed, (double)nextRecord / table.Count));
                }
            }
        }
        if (nextRecord < FirstUserRecord)
            throw new InvalidDataException("The file table could not be read.");
    }

    static (ScanTree Tree, int Orphans) BuildTree(string root, RecordTable t, CancellationToken ct)
    {
        int n = t.Count;
        if (!t.IsLiveDirectory(RootRecord))
            throw new InvalidDataException("The root folder record is missing.");

        // One entry per name: most files have one, hard-linked files have several (one per folder they appear in).
        // A hard-linked file's size is counted once, on the link outside WinSxS when there is one, because that
        // is where people look for it (System32, Program Files); its other names show with no size.
        int winSxs = t.FindWinSxS();
        int capacity = t.Files + t.Directories;
        var entParent = new List<int>(capacity);
        var entRecord = new List<int>(capacity);
        var entName = new List<string?>(capacity);   // null = the record's main name
        var entSized = new List<bool>(capacity);
        for (int i = 0; i < n; i++)
        {
            if (i == RootRecord || !t.IsLive(i))
                continue;
            if (t.Links.TryGetValue(i, out var links))
            {
                var valid = links.Where(l => (uint)l.Parent < (uint)n && t.IsLiveDirectory(l.Parent)).ToList();
                if (valid.Count == 0)
                    continue;
                int sized = valid.FindIndex(l => !t.IsUnder(l.Parent, winSxs));
                if (sized < 0)
                    sized = 0;
                for (int k = 0; k < valid.Count; k++)
                {
                    entParent.Add(valid[k].Parent);
                    entRecord.Add(i);
                    entName.Add(valid[k].Name);
                    entSized.Add(k == sized);
                }
            }
            else if (t.IsLinkable(i))
            {
                entParent.Add(t.Parent[i]);
                entRecord.Add(i);
                entName.Add(null);
                entSized.Add(true);
            }
        }

        // Group entries by parent folder (CSR).
        var start = new int[n + 1];
        foreach (int p in entParent)
            start[p + 1]++;
        for (int i = 0; i < n; i++)
            start[i + 1] += start[i];
        var order = new int[entParent.Count];
        var cursor = (int[])start.Clone();
        for (int e = 0; e < entParent.Count; e++)
            order[cursor[entParent[e]]++] = e;

        var tree = new ScanTree(root, FileTimeToTicks(t.Modified[RootRecord]), Math.Max(1024, entParent.Count + 16));
        var seen = new bool[n];
        seen[RootRecord] = true;
        var queue = new Queue<(int Record, int Node)>();
        queue.Enqueue((RootRecord, ScanTree.RootId));
        int added = 0;
        while (queue.Count > 0)
        {
            var (rec, node) = queue.Dequeue();
            for (int k = start[rec]; k < start[rec + 1]; k++)
            {
                if ((++added & 0xFFFF) == 0)
                    ct.ThrowIfCancellationRequested();
                int e = order[k];
                int c = entRecord[e];
                bool dir = t.IsDirectory(c);
                if (dir && seen[c])
                    continue;   // a folder has only one parent; never walk a subtree twice
                seen[c] = true;
                string name = entName[e] ?? t.Names[c]!;
                var flags = MapFlags(t.Attributes[c], dir);
                if (c < FirstUserRecord || (rec == RootRecord && name.StartsWith('$') && c < 64))
                    flags |= NodeFlags.Metadata | NodeFlags.System;
                if (t.Links.ContainsKey(c))
                    flags |= NodeFlags.HardLink;
                bool sized = entSized[e];
                int id = tree.Add(node, name, flags, sized ? t.Size[c] : 0, sized ? t.Alloc[c] : 0, FileTimeToTicks(t.Modified[c]));
                if (dir)
                    queue.Enqueue((c, id));
            }
        }

        int orphans = 0;
        for (int i = 0; i < n; i++)
            if (!seen[i] && t.IsLive(i) && t.Names[i] is not null)
                orphans++;
        tree.Complete();
        return (tree, orphans);
    }

    static NodeFlags MapFlags(uint a, bool dir)
    {
        var f = dir ? NodeFlags.Directory : NodeFlags.None;
        if ((a & 0x2) != 0) f |= NodeFlags.Hidden;
        if ((a & 0x4) != 0) f |= NodeFlags.System;
        if ((a & 0x800) != 0) f |= NodeFlags.Compressed;
        if ((a & 0x200) != 0) f |= NodeFlags.Sparse;
        if (dir && (a & Native.FileAttributeReparsePoint) != 0) f |= NodeFlags.ReparsePoint;
        if (!dir && (a & (Native.FileAttributeOffline | Native.FileAttributeRecallOnOpen | Native.FileAttributeRecallOnDataAccess)) != 0)
            f |= NodeFlags.CloudPlaceholder;
        return f;
    }

    internal static long FileTimeToTicks(long fileTime)
    {
        const long Epoch = 504911232000000000;   // 1601-01-01 in DateTime ticks
        return fileTime <= 0 || fileTime > DateTime.MaxValue.Ticks - Epoch ? 0 : fileTime + Epoch;
    }

    /// <summary>Per-record facts gathered while streaming the file table, indexed by record number.</summary>
    internal sealed class RecordTable(int count)
    {
        const byte Live = 1, Dir = 2;
        public readonly int Count = count;
        public readonly byte[] State = new byte[count];
        public readonly int[] Parent = new int[count];
        public readonly string?[] Names = new string?[count];
        public readonly byte[] NameRank = new byte[count];
        public readonly long[] Size = new long[count];
        public readonly long[] Alloc = new long[count];
        public readonly long[] Modified = new long[count];
        public readonly uint[] Attributes = new uint[count];

        /// <summary>Every name of the files that have more than one (hard links).</summary>
        public readonly Dictionary<int, List<(string Name, int Parent)>> Links = [];

        public int BadRecords, Files, Directories;
        public long Bytes;
        readonly List<FileNameEntry> _names = new(4);

        public bool IsLive(int i) => (State[i] & Live) != 0;
        public bool IsDirectory(int i) => (State[i] & Dir) != 0;
        public bool IsLiveDirectory(int i) => (State[i] & (Live | Dir)) == (Live | Dir);
        public bool IsLinkable(int i) => IsLive(i) && Names[i] is not null && (uint)Parent[i] < (uint)Count && IsLiveDirectory(Parent[i]);

        /// <summary>Record number of the drive's Windows\WinSxS folder, or -1.</summary>
        public int FindWinSxS()
        {
            for (int i = 0; i < Count; i++)
                if (Names[i] is { Length: 6 } name && name.Equals("WinSxS", StringComparison.OrdinalIgnoreCase) && IsLiveDirectory(i)
                    && (uint)Parent[i] < (uint)Count && Parent[Parent[i]] == RootRecord
                    && string.Equals(Names[Parent[i]], "Windows", StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        /// <summary>True when <paramref name="folder"/> is <paramref name="ancestor"/> or a few levels inside it.</summary>
        public bool IsUnder(int folder, int ancestor)
        {
            if (ancestor < 0)
                return false;
            for (int d = 0, f = folder; d < 6 && (uint)f < (uint)Count && f != RootRecord; d++, f = Parent[f])
                if (f == ancestor)
                    return true;
            return false;
        }

        static int Rank(byte nameSpace) => nameSpace switch
        {
            1 or 3 => 3,   // Win32, Win32 + DOS
            0 => 2,        // POSIX
            _ => 1,        // DOS 8.3 only
        };

        public void Load(int index, Span<byte> rec)
        {
            if (!NtfsFormat.HasFileSignature(rec))
                return;   // never used, or zeroed
            if (!NtfsFormat.ApplyFixups(rec) || !NtfsFormat.Parse(rec, out var info, _names))
            {
                BadRecords++;
                return;
            }
            if (!info.InUse)
                return;

            long target = info.BaseRecord == 0 ? index : info.BaseRecord;
            if (target < 0 || target >= Count)
                return;
            int t = (int)target;

            if (info.BaseRecord == 0)
            {
                State[t] |= Live;
                if (info.IsDirectory)
                {
                    State[t] |= Dir;
                    Directories++;
                }
                else
                {
                    Files++;
                }
            }
            if (info.HasStandardInfo)
            {
                Modified[t] = info.ModifiedFileTime;
                Attributes[t] = info.Attributes;
            }
            foreach (var fn in _names)
            {
                int parent = fn.ParentRecord > int.MaxValue ? -1 : (int)fn.ParentRecord;
                if (!fn.IsDosOnly)
                {
                    if (Links.TryGetValue(t, out var list))
                        list.Add((fn.Name, parent));
                    else if (NameRank[t] >= 2)
                        Links[t] = [(Names[t]!, Parent[t]), (fn.Name, parent)];
                }
                int rank = Rank(fn.NameSpace);
                if (rank > NameRank[t])
                {
                    NameRank[t] = (byte)rank;
                    Names[t] = fn.Name;
                    Parent[t] = parent;
                }
            }
            if (info.HasData)
            {
                Size[t] = info.DataSize;
                Alloc[t] = info.DataAllocated;
                Bytes += info.DataSize;
            }
        }
    }

    /// <summary>Sector-aligned native buffer (volume reads require aligned offsets and lengths).</summary>
    sealed unsafe class AlignedBuffer : IDisposable
    {
        void* _ptr;
        readonly int _length;

        AlignedBuffer(int length)
        {
            _length = length;
            _ptr = NativeMemory.AlignedAlloc((nuint)length, 4096);
        }

        public static AlignedBuffer Rent(int length) => new(length);

        public Span<byte> Span => _ptr is null ? throw new ObjectDisposedException(nameof(AlignedBuffer)) : new Span<byte>(_ptr, _length);

        public void Dispose()
        {
            if (_ptr is not null)
            {
                NativeMemory.AlignedFree(_ptr);
                _ptr = null;
            }
        }
    }
}
