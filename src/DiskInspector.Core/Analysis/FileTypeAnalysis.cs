using DiskInspector.Core.Model;

namespace DiskInspector.Core.Analysis;

public sealed record ExtensionStat(ushort ExtensionId, string Extension, Category Category, long Count, long Size, long Allocated, int LargestNode)
{
    public string DisplayName => Extension.Length == 0 ? "(no extension)" : "." + Extension;
}

public sealed record CategoryStat(Category Category, long Count, long Size, long Allocated, IReadOnlyList<ExtensionStat> Extensions);

public static class FileTypeAnalysis
{
    /// <summary>One row per extension, largest total size first.</summary>
    public static IReadOnlyList<ExtensionStat> ByExtension(ScanTree tree, Categorizer categorizer)
    {
        int n = tree.Extensions.Count;
        var count = new long[n];
        var size = new long[n];
        var alloc = new long[n];
        var largest = new int[n];
        Array.Fill(largest, -1);

        for (int i = 1; i < tree.Count; i++)
        {
            if (tree.IsDirectory(i))
                continue;
            int e = tree.ExtensionId(i);
            count[e]++;
            long s = tree.Size(i);
            size[e] += s;
            alloc[e] += tree.Allocated(i);
            if (largest[e] < 0 || s > tree.Size(largest[e]))
                largest[e] = i;
        }

        var map = categorizer.BuildExtensionMap(tree.Extensions);
        var list = new List<ExtensionStat>();
        for (int e = 0; e < n; e++)
            if (count[e] > 0)
                list.Add(new ExtensionStat((ushort)e, tree.Extensions[(ushort)e], categorizer.Categories[map[e]], count[e], size[e], alloc[e], largest[e]));
        list.Sort((a, b) => b.Size.CompareTo(a.Size));
        return list;
    }

    /// <summary>One row per category (including empty ones), largest first.</summary>
    public static IReadOnlyList<CategoryStat> ByCategory(IReadOnlyList<ExtensionStat> extensions, Categorizer categorizer)
    {
        var groups = categorizer.Categories.ToDictionary(c => c.Id, _ => new List<ExtensionStat>());
        foreach (var e in extensions)
            groups[e.Category.Id].Add(e);
        return categorizer.Categories
            .Select(c => new CategoryStat(c, groups[c.Id].Sum(e => e.Count), groups[c.Id].Sum(e => e.Size), groups[c.Id].Sum(e => e.Allocated), groups[c.Id]))
            .OrderByDescending(c => c.Size)
            .ToList();
    }
}

/// <summary>
/// Logical bytes per category for every folder, so folders can be colored by what they mostly hold
/// and the details panel can show what a folder is made of.
/// </summary>
public sealed class CategoryBreakdown
{
    readonly int[] _dirIndex;
    readonly long[] _sums;
    readonly byte[] _dominant;
    readonly byte[] _extToCategory;
    readonly ScanTree _tree;

    public CategoryBreakdown(ScanTree tree, Categorizer categorizer)
    {
        _tree = tree;
        Categorizer = categorizer;
        CategoryCount = categorizer.Categories.Count;
        _extToCategory = categorizer.BuildExtensionMap(tree.Extensions);

        _dirIndex = new int[tree.Count];
        int dirs = 0;
        for (int i = 0; i < tree.Count; i++)
            _dirIndex[i] = tree.IsDirectory(i) ? dirs++ : -1;

        int k = CategoryCount;
        _sums = new long[(long)dirs * k];
        for (int i = tree.Count - 1; i > 0; i--)
        {
            int p = _dirIndex[tree.Parent(i)];
            if (_dirIndex[i] < 0)
            {
                _sums[(long)p * k + _extToCategory[tree.ExtensionId(i)]] += tree.Size(i);
            }
            else
            {
                long src = (long)_dirIndex[i] * k, dst = (long)p * k;
                for (int c = 0; c < k; c++)
                    _sums[dst + c] += _sums[src + c];
            }
        }

        _dominant = new byte[dirs];
        for (int d = 0; d < dirs; d++)
        {
            long best = -1;
            byte bestId = categorizer.Other.Id;
            for (int c = 0; c < k; c++)
                if (_sums[(long)d * k + c] > best)
                {
                    best = _sums[(long)d * k + c];
                    bestId = (byte)c;
                }
            _dominant[d] = best > 0 ? bestId : categorizer.Other.Id;
        }
    }

    public Categorizer Categorizer { get; }
    public int CategoryCount { get; }

    public byte CategoryOf(int node) =>
        _dirIndex[node] >= 0 ? _dominant[_dirIndex[node]] : _extToCategory[_tree.ExtensionId(node)];

    public uint ColorOf(int node) => Categorizer.Categories[CategoryOf(node)].Color;

    /// <summary>Bytes per category below a folder (or for a single file), largest first.</summary>
    public IReadOnlyList<(Category Category, long Bytes)> For(int node)
    {
        var list = new List<(Category, long)>();
        if (_dirIndex[node] < 0)
        {
            list.Add((Categorizer.Categories[CategoryOf(node)], _tree.Size(node)));
            return list;
        }
        long start = (long)_dirIndex[node] * CategoryCount;
        for (int c = 0; c < CategoryCount; c++)
            if (_sums[start + c] > 0)
                list.Add((Categorizer.Categories[c], _sums[start + c]));
        list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return list;
    }
}
