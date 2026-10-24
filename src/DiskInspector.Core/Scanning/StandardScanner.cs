using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Threading.Channels;
using DiskInspector.Core.Model;

namespace DiskInspector.Core.Scanning;

/// <summary>
/// Walks folders with the normal Windows directory APIs. Needs no admin rights, never opens
/// file contents (so cloud files are not downloaded) and keeps going past folders it cannot read.
/// </summary>
public sealed class StandardScanner : IScanner
{
    static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        BufferSize = 16 * 1024,
    };

    const FileAttributes CloudAttributes = (FileAttributes)(Native.FileAttributeOffline |
        Native.FileAttributeRecallOnOpen | Native.FileAttributeRecallOnDataAccess);

    readonly int _workerCount;

    public StandardScanner(int workerCount = 0) =>
        _workerCount = workerCount > 0 ? workerCount : Math.Clamp(Environment.ProcessorCount, 2, 16);

    /// <summary>Test hook, called just before a folder is listed.</summary>
    internal Action<string>? BeforeDirectory { get; set; }

    public Task<ScanResult> ScanAsync(string path, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(path, progress, cancellationToken), cancellationToken);

    ScanResult Scan(string path, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        string root = ScanPaths.Normalize(path);
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists)
            throw new DirectoryNotFoundException($"Folder not found: {root}");

        long rootTicks = 0;
        try { rootTicks = rootInfo.LastWriteTimeUtc.Ticks; } catch (IOException) { }

        var volume = VolumeInfo.TryGet(root);
        var ctx = new Context(new ScanTree(root, rootTicks, 1 << 16), volume?.ClusterSize ?? 4096, ct);

        using (progress is null ? null : new Timer(_ => progress.Report(ctx.Snapshot(clock.Elapsed)), null, 100, 100))
            RunWorkers(ctx);

        ct.ThrowIfCancellationRequested();
        ctx.Tree.Complete();
        progress?.Report(ctx.Snapshot(clock.Elapsed));

        return new ScanResult
        {
            Tree = ctx.Tree,
            Mode = ScanMode.Standard,
            Duration = clock.Elapsed,
            Issues = [.. ctx.Issues],
            Volume = volume,
        };
    }

    void RunWorkers(Context ctx)
    {
        ctx.Pending = 1;
        ctx.Channel.Writer.TryWrite(new DirWork(ScanTree.RootId, ctx.Tree.RootPath));

        var workers = new Task[_workerCount];
        for (int i = 0; i < workers.Length; i++)
            workers[i] = Task.Factory.StartNew(() => Worker(ctx), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            Task.WaitAll(workers);
        }
        catch (AggregateException ae) when (ae.Flatten().InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(ctx.Token);
        }
    }

    void Worker(Context ctx)
    {
        var reader = ctx.Channel.Reader;
        var batch = new List<RawEntry>(256);
        var prepared = new List<Prepared>(256);
        var subdirs = new List<DirWork>(64);
        while (true)
        {
            if (!reader.TryRead(out var work))
            {
                if (!reader.WaitToReadAsync(ctx.Token).AsTask().GetAwaiter().GetResult())
                    return;
                continue;
            }
            try
            {
                ProcessDirectory(ctx, work, batch, prepared, subdirs);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Never let one odd folder stop the whole scan.
                ctx.Issues.Enqueue(new ScanIssue(work.Path, e.Message));
                lock (ctx.Tree)
                    ctx.Tree.AddFlags(work.Id, NodeFlags.Error);
            }
            finally
            {
                if (Interlocked.Decrement(ref ctx.Pending) == 0)
                    ctx.Channel.Writer.TryComplete();
            }
        }
    }

    void ProcessDirectory(Context ctx, DirWork work, List<RawEntry> batch, List<Prepared> prepared, List<DirWork> subdirs)
    {
        ctx.Token.ThrowIfCancellationRequested();
        ctx.CurrentPath = work.Path;
        BeforeDirectory?.Invoke(work.Path);

        batch.Clear();
        NodeFlags dirFlags = NodeFlags.None;
        try
        {
            var entries = new FileSystemEnumerable<RawEntry>(work.Path,
                static (ref FileSystemEntry e) => new RawEntry(e.FileName.ToString(), e.Attributes, e.Length, e.LastWriteTimeUtc.UtcTicks),
                Options);
            foreach (var entry in entries)
                batch.Add(entry);
        }
        catch (UnauthorizedAccessException)
        {
            dirFlags = NodeFlags.AccessDenied;
            ctx.Issues.Enqueue(new ScanIssue(work.Path, "Access denied"));
        }
        catch (DirectoryNotFoundException)
        {
            dirFlags = NodeFlags.Error;
            ctx.Issues.Enqueue(new ScanIssue(work.Path, "Folder was removed during the scan"));
        }
        catch (IOException e)
        {
            dirFlags = NodeFlags.Error;
            ctx.Issues.Enqueue(new ScanIssue(work.Path, e.Message));
        }

        prepared.Clear();
        long bytes = 0;
        int files = 0, dirs = 0;
        foreach (var e in batch)
        {
            var attrs = e.Attributes;
            var flags = MapFlags(attrs);
            bool descend = false;
            long alloc = 0;

            if ((attrs & FileAttributes.Directory) != 0)
            {
                flags |= NodeFlags.Directory;
                dirs++;
                if ((attrs & FileAttributes.ReparsePoint) != 0 && !Native.IsCloudTag(Native.GetReparseTag(Path.Join(work.Path, e.Name))))
                    flags |= NodeFlags.ReparsePoint;   // junction / symlink / mount point: not followed
                else
                    descend = true;
            }
            else
            {
                files++;
                bytes += e.Length;
                if ((attrs & CloudAttributes) != 0)
                {
                    flags |= NodeFlags.CloudPlaceholder;
                }
                else if ((attrs & (FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.ReparsePoint)) != 0)
                {
                    alloc = Native.GetCompressedFileSize(Path.Join(work.Path, e.Name));
                    if (alloc < 0)
                        alloc = RoundUp(e.Length, ctx.ClusterSize);
                    else
                        alloc = RoundUp(alloc, ctx.ClusterSize);
                }
                else
                {
                    alloc = RoundUp(e.Length, ctx.ClusterSize);
                }
            }
            prepared.Add(new Prepared(e.Name, flags, e.Length, alloc, e.ModifiedTicks, descend));
        }

        subdirs.Clear();
        lock (ctx.Tree)
        {
            if (dirFlags != NodeFlags.None)
                ctx.Tree.AddFlags(work.Id, dirFlags);
            foreach (var p in prepared)
            {
                int id = ctx.Tree.Add(work.Id, p.Name, p.Flags, p.Size, p.Allocated, p.ModifiedTicks);
                if (p.Descend)
                    subdirs.Add(new DirWork(id, p.Name));
            }
        }

        Interlocked.Add(ref ctx.Files, files);
        Interlocked.Add(ref ctx.Directories, dirs);
        Interlocked.Add(ref ctx.Bytes, bytes);

        foreach (var sub in subdirs)
        {
            Interlocked.Increment(ref ctx.Pending);
            ctx.Channel.Writer.TryWrite(new DirWork(sub.Id, Path.Join(work.Path, sub.Path)));
        }
    }

    static NodeFlags MapFlags(FileAttributes a)
    {
        var f = NodeFlags.None;
        if ((a & FileAttributes.Hidden) != 0) f |= NodeFlags.Hidden;
        if ((a & FileAttributes.System) != 0) f |= NodeFlags.System;
        if ((a & FileAttributes.Compressed) != 0) f |= NodeFlags.Compressed;
        if ((a & FileAttributes.SparseFile) != 0) f |= NodeFlags.Sparse;
        return f;
    }

    internal static long RoundUp(long size, long cluster) =>
        size <= 0 ? 0 : cluster <= 1 ? size : (size + cluster - 1) / cluster * cluster;

    readonly record struct RawEntry(string Name, FileAttributes Attributes, long Length, long ModifiedTicks);

    readonly record struct Prepared(string Name, NodeFlags Flags, long Size, long Allocated, long ModifiedTicks, bool Descend);

    readonly record struct DirWork(int Id, string Path);

    sealed class Context(ScanTree tree, int clusterSize, CancellationToken token)
    {
        public readonly ScanTree Tree = tree;
        public readonly int ClusterSize = clusterSize;
        public readonly CancellationToken Token = token;
        public readonly Channel<DirWork> Channel = System.Threading.Channels.Channel.CreateUnbounded<DirWork>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        public readonly ConcurrentQueue<ScanIssue> Issues = new();
        public int Pending;
        public long Files, Directories, Bytes;
        public volatile string CurrentPath = "";

        public ScanProgress Snapshot(TimeSpan elapsed) =>
            new(Interlocked.Read(ref Files), Interlocked.Read(ref Directories), Interlocked.Read(ref Bytes), CurrentPath, elapsed, -1);
    }
}
