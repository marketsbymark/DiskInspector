using DiskInspector.Core.Model;

namespace DiskInspector.Core.Analysis;

public static class FileQueries
{
    /// <summary>The <paramref name="count"/> largest files matching <paramref name="filter"/>, largest first.</summary>
    public static int[] Largest(ScanTree tree, int count, SizeMetric metric, Func<int, bool>? filter = null, CancellationToken ct = default)
    {
        var heap = new PriorityQueue<int, long>(count + 1);
        for (int i = 1; i < tree.Count; i++)
        {
            if ((i & 0xFFFF) == 0)
                ct.ThrowIfCancellationRequested();
            if (tree.IsDirectory(i))
                continue;
            long s = tree.GetSize(i, metric);
            if (heap.Count == count && heap.TryPeek(out _, out long min) && s <= min)
                continue;
            if (filter is not null && !filter(i))
                continue;
            heap.Enqueue(i, s);
            if (heap.Count > count)
                heap.Dequeue();
        }
        var result = new int[heap.Count];
        for (int k = result.Length - 1; k >= 0; k--)
            result[k] = heap.Dequeue();
        return result;
    }
}

public sealed record AgeBucket(string Label, long Count, long Size, long Allocated);

public static class AgeAnalysis
{
    static readonly (string Label, double MaxDays)[] Ranges =
    [
        ("Last 30 days", 30),
        ("1 to 6 months", 182),
        ("6 to 12 months", 365),
        ("1 to 3 years", 365 * 3),
        ("3 to 5 years", 365 * 5),
        ("More than 5 years", double.MaxValue),
    ];

    public static IReadOnlyList<string> Labels { get; } = [.. Ranges.Select(r => r.Label), "Unknown date"];

    /// <summary>Bucket index for a file's last-modified time (the last bucket is "unknown").</summary>
    public static int BucketOf(long modifiedTicks, DateTime nowUtc)
    {
        if (modifiedTicks <= 0)
            return Ranges.Length;
        double days = (nowUtc.Ticks - modifiedTicks) / (double)TimeSpan.TicksPerDay;
        if (days < -1)
            return Ranges.Length;   // future date: clock problem or bad metadata
        for (int b = 0; b < Ranges.Length; b++)
            if (days < Ranges[b].MaxDays)
                return b;
        return Ranges.Length - 1;
    }

    public static IReadOnlyList<AgeBucket> Compute(ScanTree tree, DateTime nowUtc)
    {
        int n = Labels.Count;
        var count = new long[n];
        var size = new long[n];
        var alloc = new long[n];
        for (int i = 1; i < tree.Count; i++)
        {
            if (tree.IsDirectory(i))
                continue;
            int b = BucketOf(tree.ModifiedTicks(i), nowUtc);
            count[b]++;
            size[b] += tree.Size(i);
            alloc[b] += tree.Allocated(i);
        }
        return [.. Enumerable.Range(0, n).Select(b => new AgeBucket(Labels[b], count[b], size[b], alloc[b]))];
    }
}
