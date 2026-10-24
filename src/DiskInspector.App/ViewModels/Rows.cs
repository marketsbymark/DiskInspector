using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskInspector.App.Controls;
using DiskInspector.Core.Analysis;
using DiskInspector.Core.Model;
using DiskInspector.Core.Scanning;

namespace DiskInspector.App.ViewModels;

public sealed record DriveItem(string RootPath, string Label, string Format, long Total, long Free)
{
    public long Used => Total - Free;
    public double UsedFraction => Total > 0 ? (double)Used / Total : 0;
    public bool IsNtfs => string.Equals(Format, "NTFS", StringComparison.OrdinalIgnoreCase);
    public string Title => string.IsNullOrWhiteSpace(Label) ? $"Local Disk ({RootPath.TrimEnd('\\')})" : $"{Label} ({RootPath.TrimEnd('\\')})";
    public string UsageText => $"{Core.Analysis.Format.Bytes(Free)} free of {Core.Analysis.Format.Bytes(Total)}";
    public string Short => RootPath;

    public static IEnumerable<DriveItem> Enumerate()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            DriveItem? item = null;
            try
            {
                if (d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                    item = new DriveItem(d.RootDirectory.FullName, d.VolumeLabel, d.DriveFormat, d.TotalSize, d.TotalFreeSpace);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            if (item is not null)
                yield return item;
        }
    }
}

public sealed record Crumb(int Node, string Name);

/// <summary>One file in a list (largest files, category drill-down).</summary>
public sealed class FileRow
{
    public FileRow(ScanTree tree, int node, CategoryBreakdown colors, long total)
    {
        Node = node;
        Name = tree.Name(node);
        Size = tree.Size(node);
        Allocated = tree.Allocated(node);
        Modified = tree.ModifiedUtc(node);
        int parent = tree.Parent(node);
        Folder = parent >= 0 ? tree.GetFullPath(parent) : "";
        var cat = colors.Categorizer.Categories[colors.CategoryOf(node)];
        Category = cat.Name;
        CategoryBrush = BrushCache.Get(cat.Color);
        Fraction = total > 0 ? (double)Size / total : 0;
    }

    public int Node { get; }
    public string Name { get; }
    public string Folder { get; }
    public long Size { get; }
    public long Allocated { get; }
    public DateTime Modified { get; }
    public string Category { get; }
    public Brush CategoryBrush { get; }
    public double Fraction { get; }
    public string SizeText => Format.Bytes(Size);
    public string AllocatedText => Format.Bytes(Allocated);
    public string ModifiedText => Format.Date(Modified);
    public string PercentText => Format.Percent(Fraction);
}

public sealed class ExtensionRow(ExtensionStat stat, double fraction)
{
    public ExtensionStat Stat { get; } = stat;
    public string Extension => Stat.DisplayName;
    public string Category => Stat.Category.Name;
    public Brush CategoryBrush { get; } = BrushCache.Get(stat.Category.Color);
    public long Size => Stat.Size;
    public long Allocated => Stat.Allocated;
    public long Count => Stat.Count;
    public long Average => Stat.Count > 0 ? Stat.Size / Stat.Count : 0;
    public double Fraction { get; } = fraction;
    public string SizeText => Format.Bytes(Size);
    public string AllocatedText => Format.Bytes(Allocated);
    public string CountText => Format.Count(Count);
    public string AverageText => Format.Bytes(Average);
    public string PercentText => Format.Percent(Fraction);
}

public sealed class CategoryRow(CategoryStat stat, double fraction, SizeMetric metric)
{
    public CategoryStat Stat { get; } = stat;
    public string Name => Stat.Category.Name;
    public Brush Brush { get; } = BrushCache.Get(stat.Category.Color);
    public long Size => metric == SizeMetric.Logical ? Stat.Size : Stat.Allocated;
    public long Count => Stat.Count;
    public double Fraction { get; } = fraction;
    public string SizeText => Format.Bytes(Size);
    public string CountText => $"{Format.Count(Count)} files";
    public string PercentText => Format.Percent(Fraction);
    public string ExtensionsText => Stat.Extensions.Count == 0 ? "" :
        string.Join("  ", Stat.Extensions.Take(6).Select(e => e.DisplayName)) + (Stat.Extensions.Count > 6 ? $"  +{Stat.Extensions.Count - 6}" : "");
}

public sealed class LocationRow(LocationStat stat, double fraction, SizeMetric metric)
{
    public LocationStat Stat { get; } = stat;
    public string Group => Stat.Group;
    public string Name => Stat.Name;
    public int Level => Stat.Level;
    public int Node => Stat.Node;
    public string Path => Stat.Path;
    public string? Note => Stat.Note;
    public long Size => metric == SizeMetric.Logical ? Stat.Size : Stat.Allocated;
    public double Fraction { get; } = stat.Node >= 0 ? fraction : 0;
    public string SizeText => Stat.Node < 0 && Size == 0 ? "" : Format.Bytes(Size);
    public string FilesText => Stat.Files > 0 ? Format.Count(Stat.Files) : "";
    public string PercentText => Stat.Node >= 0 ? Format.Percent(Fraction) : "";
}

public sealed class AgeRow(AgeBucket bucket, double fraction, SizeMetric metric)
{
    public string Label => bucket.Label;
    public long Size => metric == SizeMetric.Logical ? bucket.Size : bucket.Allocated;
    public double Fraction { get; } = fraction;
    public string SizeText => Format.Bytes(Size);
    public string CountText => $"{Format.Count(bucket.Count)} files";
    public string PercentText => Format.Percent(Fraction);
}

public sealed partial class FolderRow : ObservableObject
{
    public FolderRow(ScanTree tree, int node, int level, SizeMetric metric, CategoryBreakdown colors)
    {
        Node = node;
        Level = level;
        IsDirectory = tree.IsDirectory(node);
        HasChildren = tree.ChildCount(node) > 0;
        Name = node == ScanTree.RootId ? tree.RootPath : tree.Name(node);
        Size = tree.Size(node);
        Allocated = tree.Allocated(node);
        Files = IsDirectory ? tree.FileCount(node) : 0;
        Folders = IsDirectory ? tree.DirectoryCount(node) : 0;
        Modified = tree.ModifiedUtc(node);
        int parent = tree.Parent(node);
        long parentSize = parent >= 0 ? tree.GetSize(parent, metric) : tree.GetSize(node, metric);
        Fraction = parentSize > 0 ? (double)tree.GetSize(node, metric) / parentSize : 0;
        Brush = BrushCache.Get(colors.ColorOf(node));
        var flags = tree.Flags(node);
        Badge = (flags & NodeFlags.AccessDenied) != 0 ? "Access denied"
            : (flags & NodeFlags.ReparsePoint) != 0 ? "Link (not followed)"
            : (flags & NodeFlags.CloudPlaceholder) != 0 ? "Online only"
            : (flags & NodeFlags.Error) != 0 ? "Could not read"
            : "";
    }

    /// <summary>"N more items" placeholder for very large folders.</summary>
    public FolderRow(int parentNode, int level, int hiddenCount, long hiddenSize)
    {
        Node = parentNode;
        Level = level;
        IsMore = true;
        Name = $"{hiddenCount:N0} smaller items";
        Size = hiddenSize;
        Brush = Brushes.Gray;
        Badge = "";
    }

    public int Node { get; }
    public int Level { get; }
    public bool IsDirectory { get; }
    public bool HasChildren { get; }
    public bool IsMore { get; }
    public string Name { get; }
    public long Size { get; }
    public long Allocated { get; }
    public int Files { get; }
    public int Folders { get; }
    public DateTime Modified { get; }
    public double Fraction { get; }
    public Brush Brush { get; }
    public string Badge { get; }

    [ObservableProperty]
    private bool _isExpanded;

    public string Glyph => IsMore ? "" : IsDirectory ? (IsExpanded ? "" : "") : "";
    public string ExpanderGlyph => !HasChildren ? "" : IsExpanded ? "" : "";
    public string SizeText => Format.Bytes(Size);
    public string AllocatedText => IsMore ? "" : Format.Bytes(Allocated);
    public string PercentText => IsMore ? "" : Format.Percent(Fraction);
    public string FilesText => IsDirectory ? Format.Count(Files) : "";
    public string FoldersText => IsDirectory ? Format.Count(Folders) : "";
    public string ModifiedText => IsMore ? "" : Format.Date(Modified);

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(ExpanderGlyph));
    }
}

public sealed record BreakdownItem(string Name, Brush Brush, double Fraction, string SizeText);

public sealed class NodeDetails
{
    public required int Node { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public required string SizeText { get; init; }
    public required string BytesText { get; init; }
    public required string AllocatedText { get; init; }
    public required string ShareText { get; init; }
    public required string ContentsText { get; init; }
    public required string ModifiedText { get; init; }
    public required string Notes { get; init; }
    public required Brush Brush { get; init; }
    public required IReadOnlyList<BreakdownItem> Breakdown { get; init; }
    public required bool CanOpen { get; init; }
    public bool HasNotes => Notes.Length > 0;
    public bool HasBreakdown => Breakdown.Count > 1;

    public static NodeDetails Create(ScanResult result, CategoryBreakdown colors, int node, SizeMetric metric)
    {
        var tree = result.Tree;
        bool dir = tree.IsDirectory(node);
        var flags = tree.Flags(node);
        var cat = colors.Categorizer.Categories[colors.CategoryOf(node)];
        var notes = new List<string>();
        if ((flags & NodeFlags.AccessDenied) != 0) notes.Add("Windows would not let the scan look inside this folder, so its real size may be larger.");
        if ((flags & NodeFlags.ReparsePoint) != 0) notes.Add("This is a link (junction or symbolic link) to another place. It is not followed, so nothing is counted twice.");
        if ((flags & NodeFlags.CloudPlaceholder) != 0) notes.Add("Online-only cloud file: it uses no local space until opened.");
        if ((flags & NodeFlags.HardLink) != 0)
            notes.Add(result.Mode == ScanMode.Mft
                ? (tree.Size(node) == 0
                    ? "This file has more than one name (hard link). Its space is counted once, under another of its names."
                    : "This file has more than one name (hard link). Its space is counted here once; its other names show 0 bytes.")
                : "This file has more than one name (hard link).");
        if ((flags & NodeFlags.Compressed) != 0) notes.Add("Compressed by NTFS.");
        if ((flags & NodeFlags.Sparse) != 0) notes.Add("Sparse file: parts of it take no space on disk.");
        if ((flags & NodeFlags.Metadata) != 0) notes.Add("NTFS system file.");
        if ((flags & NodeFlags.Error) != 0) notes.Add("Part of this folder could not be read (it may have changed during the scan).");
        if ((flags & NodeFlags.System) != 0 && (flags & NodeFlags.Metadata) == 0) notes.Add("Marked as a Windows system item.");

        long size = tree.GetSize(node, metric);
        long root = tree.GetSize(ScanTree.RootId, metric);
        long parent = tree.Parent(node) >= 0 ? tree.GetSize(tree.Parent(node), metric) : size;
        string share = node == ScanTree.RootId ? "" :
            $"{Format.Percent(parent > 0 ? (double)size / parent : 0)} of its folder · {Format.Percent(root > 0 ? (double)size / root : 0)} of everything scanned";

        long total = tree.Size(node);
        var breakdown = dir
            ? colors.For(node).Take(8).Select(x => new BreakdownItem(x.Category.Name, BrushCache.Get(x.Category.Color),
                total > 0 ? (double)x.Bytes / total : 0, Format.Bytes(x.Bytes))).ToList()
            : [];

        return new NodeDetails
        {
            Node = node,
            Name = node == ScanTree.RootId ? tree.RootPath : tree.Name(node),
            Path = tree.GetFullPath(node),
            Kind = dir ? $"Folder · mostly {cat.Name}" : $"{(tree.Extension(node).Length > 0 ? "." + tree.Extension(node) : "File")} · {cat.Name}",
            SizeText = Format.Bytes(tree.Size(node)),
            BytesText = tree.Size(node) >= 1024 ? $"{tree.Size(node):N0} bytes" : "",
            AllocatedText = Format.Bytes(tree.Allocated(node)),
            ShareText = share,
            ContentsText = dir ? $"{tree.FileCount(node):N0} files in {tree.DirectoryCount(node):N0} folders" : "",
            ModifiedText = Format.Date(tree.ModifiedUtc(node)),
            Notes = string.Join("\n", notes),
            Brush = BrushCache.Get(cat.Color),
            Breakdown = breakdown,
            CanOpen = Services.Shell.CanOpen(tree, node),
        };
    }
}
