using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskInspector.App.Controls;
using DiskInspector.App.Services;
using DiskInspector.Core.Analysis;
using DiskInspector.Core.Model;
using DiskInspector.Core.Scanning;

namespace DiskInspector.App.ViewModels;

/// <summary>Everything computed once after a scan, off the UI thread.</summary>
public sealed class Analysis
{
    public required ScanResult Result { get; init; }
    public required CategoryBreakdown Colors { get; init; }
    public required IReadOnlyList<ExtensionStat> Extensions { get; init; }
    public required IReadOnlyList<CategoryStat> Categories { get; init; }
    public required IReadOnlyList<LocationStat> Locations { get; init; }
    public required IReadOnlyList<AgeBucket> Ages { get; init; }

    public ScanTree Tree => Result.Tree;

    /// <summary>Document properties read so far, by node (shared by every work-files table of this scan).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<int, DocumentInfo> Authors { get; } = new();

    /// <summary>Breakdowns for the whole scan.</summary>
    public ScopeStats Everything => new(ScanTree.RootId, Extensions, Categories, Ages);

    /// <summary>Breakdowns for the files below one folder.</summary>
    public ScopeStats ComputeScope(int root, CancellationToken ct)
    {
        if (root <= ScanTree.RootId)
            return Everything;
        var ext = FileTypeAnalysis.ByExtension(Tree, Colors.Categorizer, root, ct);
        return new ScopeStats(root, ext, FileTypeAnalysis.ByCategory(ext, Colors.Categorizer), AgeAnalysis.Compute(Tree, DateTime.UtcNow, root));
    }

    public static Analysis Run(ScanResult result, Categorizer categorizer)
    {
        var ext = FileTypeAnalysis.ByExtension(result.Tree, categorizer);
        return new Analysis
        {
            Result = result,
            Colors = new CategoryBreakdown(result.Tree, categorizer),
            Extensions = ext,
            Categories = FileTypeAnalysis.ByCategory(ext, categorizer),
            Locations = LocationAnalysis.Compute(result),
            Ages = AgeAnalysis.Compute(result.Tree, DateTime.UtcNow),
        };
    }
}

/// <summary>File-type, category and age breakdowns for the current scope (everything, or one folder).</summary>
public sealed record ScopeStats(int Root, IReadOnlyList<ExtensionStat> Extensions, IReadOnlyList<CategoryStat> Categories, IReadOnlyList<AgeBucket> Ages);

/// <summary>A view that can save what it shows as a CSV snapshot.</summary>
public interface ISnapshotSource
{
    TableSnapshot? Snapshot();
}

/// <summary>A view model that sorts its own rows (faster than a WPF collection view for big tables).</summary>
public interface ISortableTable
{
    void SortBy(string property, System.ComponentModel.ListSortDirection direction);
}

public static class SortHelper
{
    /// <summary>Rows sorted by a property (numbers and dates by value, text without case).</summary>
    public static IReadOnlyList<T> Sort<T>(IReadOnlyList<T> rows, string property, System.ComponentModel.ListSortDirection direction)
    {
        var prop = typeof(T).GetProperty(property);
        if (prop is null || rows.Count < 2)
            return rows;
        var keys = rows.Select(r => prop.GetValue(r)).ToArray();
        var items = rows.ToArray();
        IComparer<object?> cmp = Comparer<object?>.Create((a, b) =>
            a is string sa && b is string sb ? StringComparer.CurrentCultureIgnoreCase.Compare(sa, sb) : Comparer<object?>.Default.Compare(a, b));
        if (direction == System.ComponentModel.ListSortDirection.Descending)
        {
            var asc = cmp;
            cmp = Comparer<object?>.Create((a, b) => asc.Compare(b, a));
        }
        Array.Sort(keys, items, cmp);
        return items;
    }

    public static string ScopeLabel(MainViewModel main) =>
        main.Tree is { } t && main.ScopeRoot > 0 && main.ScopeRoot < t.Count ? t.Name(main.ScopeRoot) : "";
}

/// <summary>Folder tree shown as a flat, virtualized list with expand/collapse.</summary>
public sealed partial class FolderTreeViewModel(MainViewModel main) : ObservableObject, ISnapshotSource
{
    const int MaxChildrenShown = 1000;
    Analysis? _a;
    bool _syncing;

    public MainViewModel Main { get; } = main;
    public BulkObservableCollection<FolderRow> Rows { get; } = [];

    [ObservableProperty]
    private FolderRow? _selectedRow;

    public void Load(Analysis a)
    {
        _a = a;
        var root = new FolderRow(a.Tree, ScanTree.RootId, 0, Main.Metric, a.Colors) { IsExpanded = true };
        Rows.ReplaceAll([root, .. ChildRows(ScanTree.RootId, 1)]);
    }

    public void Clear()
    {
        _a = null;
        Rows.ReplaceAll([]);
    }

    /// <summary>Rebuilds rows (e.g. after switching between size and size on disk), keeping folders open.</summary>
    public void Refresh()
    {
        if (_a is null)
            return;
        var open = Rows.Where(r => r.IsExpanded && !r.IsMore).Select(r => r.Node).ToHashSet();
        int selected = SelectedRow?.Node ?? -1;
        var list = new List<FolderRow>();
        void Add(int node, int level)
        {
            var row = new FolderRow(_a.Tree, node, level, Main.Metric, _a.Colors) { IsExpanded = open.Contains(node) };
            list.Add(row);
            if (row.IsExpanded)
                foreach (var c in ChildRows(node, level + 1))
                {
                    if (!c.IsMore && open.Contains(c.Node))
                        Add(c.Node, level + 1);
                    else
                        list.Add(c);
                }
        }
        Add(ScanTree.RootId, 0);
        Rows.ReplaceAll(list);
        SelectWithoutSync(Rows.FirstOrDefault(r => r.Node == selected && !r.IsMore));
    }

    List<FolderRow> ChildRows(int node, int level)
    {
        var tree = _a!.Tree;
        var kids = tree.GetChildrenSorted(node, Main.Metric);
        var rows = new List<FolderRow>(Math.Min(kids.Length, MaxChildrenShown + 1));
        for (int i = 0; i < kids.Length && i < MaxChildrenShown; i++)
            rows.Add(new FolderRow(tree, kids[i], level, Main.Metric, _a.Colors));
        if (kids.Length > MaxChildrenShown)
        {
            long rest = 0;
            for (int i = MaxChildrenShown; i < kids.Length; i++)
                rest += tree.GetSize(kids[i], Main.Metric);
            rows.Add(new FolderRow(node, level, kids.Length - MaxChildrenShown, rest));
        }
        return rows;
    }

    [RelayCommand]
    public void Toggle(FolderRow? row)
    {
        if (_a is null || row is null || row.IsMore || !row.HasChildren)
            return;
        int index = Rows.IndexOf(row);
        if (index < 0)
            return;
        var keep = SelectedRow;
        if (row.IsExpanded)
        {
            int end = index + 1;
            while (end < Rows.Count && Rows[end].Level > row.Level)
                end++;
            row.IsExpanded = false;
            Rows.RemoveRange(index + 1, end - index - 1);
        }
        else
        {
            row.IsExpanded = true;
            Rows.InsertRange(index + 1, ChildRows(row.Node, row.Level + 1));
        }
        SelectWithoutSync(keep is not null && Rows.Contains(keep) ? keep : row);
    }

    /// <summary>Opens the folders leading to a node and selects it.</summary>
    public void Reveal(int node)
    {
        if (_a is null || node < 0 || _syncing)
            return;
        if (SelectedRow is { IsMore: false } cur && cur.Node == node)
            return;
        var path = _a.Tree.PathFromRoot(node);
        int from = 0;
        FolderRow? target = null;
        foreach (int id in path)
        {
            int idx = -1;
            for (int i = from; i < Rows.Count; i++)
                if (Rows[i].Node == id && !Rows[i].IsMore)
                {
                    idx = i;
                    break;
                }
            if (idx < 0)
                break;   // beyond the "N more items" cut-off; select the deepest visible folder
            target = Rows[idx];
            from = idx + 1;
            if (id != node && !target.IsExpanded)
            {
                target.IsExpanded = true;
                Rows.InsertRange(idx + 1, ChildRows(id, target.Level + 1));
            }
        }
        SelectWithoutSync(target);
    }

    public TableSnapshot? Snapshot()
    {
        if (_a is null)
            return null;
        var t = _a.Tree;
        var rows = Rows.Select(r => (IReadOnlyList<string>)(r.IsMore
            ? [new string(' ', r.Level * 2) + r.Name, "", "", r.Size.ToString(), r.SizeText, "", "", "", "", "", "", ""]
            : [new string(' ', r.Level * 2) + r.Name, t.GetFullPath(r.Node), r.IsDirectory ? "Folder" : "File", r.Size.ToString(), r.SizeText,
                r.Allocated.ToString(), r.PercentText, r.IsDirectory ? r.Files.ToString() : "", r.IsDirectory ? r.Folders.ToString() : "",
                Snapshots.Date(t.CreatedUtc(r.Node)), Snapshots.Date(r.Modified), r.Badge])).ToList();
        return new TableSnapshot("Folders", "", ["Name", "Path", "Kind", "Size (bytes)", "Size", "On disk (bytes)", "% of parent", "Files", "Folders",
            "Created", "Modified", "Note"], rows);
    }

    void SelectWithoutSync(FolderRow? row)
    {
        _syncing = true;
        try { SelectedRow = row; }
        finally { _syncing = false; }
    }

    partial void OnSelectedRowChanged(FolderRow? value)
    {
        if (_syncing || value is null)
            return;
        _syncing = true;
        try { Main.SelectedNode = value.Node; }
        finally { _syncing = false; }
    }
}

public sealed partial class TypesViewModel(MainViewModel main) : ObservableObject, ISnapshotSource, ISortableTable
{
    public MainViewModel Main { get; } = main;

    [ObservableProperty]
    private IReadOnlyList<ExtensionRow> _rows = [];

    [ObservableProperty]
    private IReadOnlyList<DonutSlice> _slices = [];

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private ExtensionRow? _selectedRow;

    public void Load(Analysis? a)
    {
        if (a is null)
        {
            Rows = [];
            Slices = [];
            return;
        }
        var metric = Main.Metric;
        var scope = Main.Scope ?? a.Everything;
        long total = Math.Max(1, a.Tree.GetSize(scope.Root, metric));
        Rows = scope.Extensions.Select(e => new ExtensionRow(e, (double)(metric == SizeMetric.Logical ? e.Size : e.Allocated) / total))
            .OrderByDescending(r => metric == SizeMetric.Logical ? r.Size : r.Allocated).ToList();
        Slices = scope.Categories.Select(c => new DonutSlice(c.Category.Name, metric == SizeMetric.Logical ? c.Size : c.Allocated, BrushCache.Get(c.Category.Color))).ToList();
        Summary = $"{scope.Extensions.Count:N0}";
    }

    public void SortBy(string property, System.ComponentModel.ListSortDirection direction) => Rows = SortHelper.Sort(Rows, property, direction);

    public TableSnapshot? Snapshot() => Rows.Count == 0 ? null : new TableSnapshot("File types", SortHelper.ScopeLabel(Main),
        ["Type", "Category", "Files", "Size (bytes)", "Size", "On disk (bytes)", "Share", "Average (bytes)", "Largest file"],
        Rows.Select(r => (IReadOnlyList<string>)[r.Extension, r.Category, r.Count.ToString(), r.Size.ToString(), r.SizeText, r.Allocated.ToString(),
            r.PercentText, r.Average.ToString(), r.Stat.LargestNode >= 0 && Main.Tree is { } t ? t.GetFullPath(r.Stat.LargestNode) : ""]).ToList());

    partial void OnSelectedRowChanged(ExtensionRow? value)
    {
        if (value is not null && value.Stat.LargestNode >= 0)
            Main.SelectedNode = value.Stat.LargestNode;
    }
}

public sealed partial class CategoriesViewModel(MainViewModel main) : ObservableObject, ISnapshotSource
{
    Analysis? _a;
    CancellationTokenSource? _cts;

    public MainViewModel Main { get; } = main;

    [ObservableProperty]
    private IReadOnlyList<CategoryRow> _rows = [];

    [ObservableProperty]
    private IReadOnlyList<DonutSlice> _slices = [];

    [ObservableProperty]
    private CategoryRow? _selectedCategory;

    [ObservableProperty]
    private IReadOnlyList<ExtensionRow> _extensions = [];

    [ObservableProperty]
    private ExtensionRow? _selectedExtension;

    [ObservableProperty]
    private IReadOnlyList<FileRow> _files = [];

    [ObservableProperty]
    private FileRow? _selectedFile;

    [ObservableProperty]
    private string _filesTitle = "Largest files";

    public void Load(Analysis? a)
    {
        _a = a;
        if (a is null)
        {
            Rows = [];
            Slices = [];
            Extensions = [];
            Files = [];
            return;
        }
        var metric = Main.Metric;
        string? keep = SelectedCategory?.Name;
        var scope = Main.Scope ?? a.Everything;
        long total = Math.Max(1, a.Tree.GetSize(scope.Root, metric));
        Rows = scope.Categories.Where(c => c.Count > 0)
            .Select(c => new CategoryRow(c, (double)(metric == SizeMetric.Logical ? c.Size : c.Allocated) / total, metric))
            .OrderByDescending(r => r.Size).ToList();
        Slices = Rows.Select(r => new DonutSlice(r.Name, r.Size, r.Brush)).ToList();
        SelectedCategory = Rows.FirstOrDefault(r => r.Name == keep) ?? Rows.FirstOrDefault();
    }

    partial void OnSelectedCategoryChanged(CategoryRow? value)
    {
        if (_a is null || value is null)
        {
            Extensions = [];
            Files = [];
            return;
        }
        long total = Math.Max(1, value.Size);
        var metric = Main.Metric;
        Extensions = value.Stat.Extensions.Select(e => new ExtensionRow(e, (double)(metric == SizeMetric.Logical ? e.Size : e.Allocated) / total))
            .OrderByDescending(e => metric == SizeMetric.Logical ? e.Size : e.Allocated).ToList();
        SelectedExtension = null;
        LoadFiles($"Largest {value.Name.ToLowerInvariant()} files", value.Stat.Category.Id, null);
    }

    partial void OnSelectedExtensionChanged(ExtensionRow? value)
    {
        if (value is not null)
            LoadFiles($"Largest {value.Extension} files", null, value.Stat.ExtensionId);
    }

    partial void OnSelectedFileChanged(FileRow? value)
    {
        if (value is not null)
            Main.SelectedNode = value.Node;
    }

    public TableSnapshot? Snapshot()
    {
        if (Rows.Count == 0)
            return null;
        var rows = new List<IReadOnlyList<string>>();
        foreach (var c in Rows)
        {
            rows.Add([c.Name, "", c.Count.ToString(), c.Size.ToString(), c.SizeText, c.PercentText]);
            long total = Math.Max(1, c.Size);
            foreach (var e in c.Stat.Extensions.OrderByDescending(e => e.Size))
                rows.Add([c.Name, e.DisplayName, e.Count.ToString(), e.Size.ToString(), Format.Bytes(e.Size), Format.Percent((double)e.Size / total)]);
        }
        return new TableSnapshot("Categories", SortHelper.ScopeLabel(Main), ["Category", "Type", "Files", "Size (bytes)", "Size", "Share"], rows);
    }

    void LoadFiles(string title, byte? category, ushort? extension)
    {
        var a = _a!;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var metric = Main.Metric;
        int root = Main.Scope?.Root ?? ScanTree.RootId;
        FilesTitle = title;
        Task.Run(() =>
        {
            var map = a.Colors.Categorizer.BuildExtensionMap(a.Tree.Extensions);
            Func<int, bool> filter = extension is { } e
                ? i => a.Tree.ExtensionId(i) == e
                : i => map[a.Tree.ExtensionId(i)] == category;
            var top = FileQueries.Largest(a.Tree, 300, metric, filter, cts.Token, root);
            long total = a.Tree.GetSize(root, metric);
            return top.Select(n => new FileRow(a.Tree, n, a.Colors, total)).ToList();
        }, cts.Token).ContinueWith(t =>
        {
            if (!cts.IsCancellationRequested && t.Status == TaskStatus.RanToCompletion)
                Files = t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}

public sealed record FilterOption(string Label, int Value);

public sealed partial class LargestFilesViewModel : ObservableObject, ISnapshotSource, ISortableTable
{
    readonly DispatcherTimer _debounce;
    Analysis? _a;
    CancellationTokenSource? _cts;

    public LargestFilesViewModel(MainViewModel main)
    {
        Main = main;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Recompute();
        };
    }

    public MainViewModel Main { get; }

    public static IReadOnlyList<FilterOption> AgeOptions { get; } =
    [
        new("Any age", 0), new("Not changed in 6 months", 182), new("Not changed in 1 year", 365),
        new("Not changed in 3 years", 365 * 3), new("Not changed in 5 years", 365 * 5),
    ];

    [ObservableProperty]
    private IReadOnlyList<FilterOption> _categoryOptions = [new("All categories", -1)];

    [ObservableProperty]
    private FilterOption _categoryFilter = new("All categories", -1);

    [ObservableProperty]
    private FilterOption _ageFilter = AgeOptions[0];

    [ObservableProperty]
    private string _nameFilter = "";

    [ObservableProperty]
    private IReadOnlyList<FileRow> _rows = [];

    [ObservableProperty]
    private FileRow? _selectedRow;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private bool _isBusy;

    public IReadOnlyList<int> CurrentNodes => Rows.Select(r => r.Node).ToList();

    public void SortBy(string property, System.ComponentModel.ListSortDirection direction) => Rows = SortHelper.Sort(Rows, property, direction);

    public TableSnapshot? Snapshot()
    {
        if (Rows.Count == 0)
            return null;
        string detail = string.Join(", ", new[] { SortHelper.ScopeLabel(Main), NameFilter.Trim(), CategoryFilter.Value >= 0 ? CategoryFilter.Label : "",
            AgeFilter.Value > 0 ? AgeFilter.Label : "" }.Where(x => x.Length > 0));
        return new TableSnapshot("Largest files", detail,
            ["Name", "Path", "Folder", "Size (bytes)", "Size", "On disk (bytes)", "Category", "Created", "Modified", "Accessed"],
            Rows.Select(r => (IReadOnlyList<string>)[r.Name, Path.Combine(r.Folder, r.Name), r.Folder, r.Size.ToString(), r.SizeText, r.Allocated.ToString(),
                r.Category, Snapshots.Date(r.Created), Snapshots.Date(r.Modified), Snapshots.Date(r.Accessed)]).ToList());
    }

    public void Load(Analysis? a)
    {
        _a = a;
        if (a is null)
        {
            Rows = [];
            return;
        }
        int keep = CategoryFilter.Value;
        CategoryOptions = [new("All categories", -1), .. a.Colors.Categorizer.Categories.Select(c => new FilterOption(c.Name, c.Id))];
        CategoryFilter = CategoryOptions.FirstOrDefault(o => o.Value == keep) ?? CategoryOptions[0];
        Recompute();
    }

    partial void OnNameFilterChanged(string value) => Restart();
    partial void OnCategoryFilterChanged(FilterOption value) => Restart();
    partial void OnAgeFilterChanged(FilterOption value) => Restart();

    partial void OnSelectedRowChanged(FileRow? value)
    {
        if (value is not null)
            Main.SelectedNode = value.Node;
    }

    void Restart()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void Recompute()
    {
        var a = _a;
        if (a is null)
            return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var metric = Main.Metric;
        string name = NameFilter.Trim();
        int category = CategoryFilter?.Value ?? -1;
        int days = AgeFilter?.Value ?? 0;
        int root = Main.Scope?.Root ?? ScanTree.RootId;
        IsBusy = true;
        Task.Run(() =>
        {
            var map = a.Colors.Categorizer.BuildExtensionMap(a.Tree.Extensions);
            long cutoff = days > 0 ? DateTime.UtcNow.AddDays(-days).Ticks : long.MaxValue;
            bool any = name.Length > 0 || category >= 0 || days > 0;
            Func<int, bool>? filter = !any ? null : i =>
                (category < 0 || map[a.Tree.ExtensionId(i)] == category)
                && (days <= 0 || (a.Tree.ModifiedTicks(i) > 0 && a.Tree.ModifiedTicks(i) < cutoff))
                && (name.Length == 0 || a.Tree.Name(i).Contains(name, StringComparison.OrdinalIgnoreCase));
            var top = FileQueries.Largest(a.Tree, 1000, metric, filter, cts.Token, root);
            long total = a.Tree.GetSize(root, metric);
            return (top.Select(n => new FileRow(a.Tree, n, a.Colors, total)).ToList(), top.Sum(n => a.Tree.GetSize(n, metric)));
        }, cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested)
                return;
            IsBusy = false;
            if (t.Status != TaskStatus.RanToCompletion)
            {
                if (t.Exception is not null)
                    Log.Error("Largest-files query failed", t.Exception);
                return;
            }
            var (rows, sum) = t.Result;
            Rows = rows;
            Summary = rows.Count == 0 ? "No files match" : $"Top {rows.Count:N0} files, together {Format.Bytes(sum)}";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}

public sealed partial class LocationsViewModel(MainViewModel main) : ObservableObject, ISnapshotSource
{
    public MainViewModel Main { get; } = main;

    [ObservableProperty]
    private IReadOnlyList<LocationRow> _rows = [];

    [ObservableProperty]
    private LocationRow? _selectedRow;

    public void Load(Analysis? a)
    {
        if (a is null)
        {
            Rows = [];
            return;
        }
        var metric = Main.Metric;
        long total = Math.Max(1, a.Tree.GetSize(0, metric));
        Rows = a.Locations.Select(l => new LocationRow(l, (double)(metric == SizeMetric.Logical ? l.Size : l.Allocated) / total, metric)).ToList();
    }

    partial void OnSelectedRowChanged(LocationRow? value)
    {
        if (value is { Node: >= 0 })
            Main.SelectedNode = value.Node;
    }

    public TableSnapshot? Snapshot() => Rows.Count == 0 ? null : new TableSnapshot("Locations", "",
        ["Group", "Location", "Path", "Size (bytes)", "Size", "Share of scan", "Files", "Note"],
        Rows.Select(r => (IReadOnlyList<string>)[r.Group, new string(' ', r.Level * 2) + r.Name, r.Path, r.Size.ToString(), r.SizeText, r.PercentText,
            r.FilesText.Replace(",", ""), r.Note ?? ""]).ToList());
}

public sealed partial class AgeViewModel(MainViewModel main) : ObservableObject, ISnapshotSource
{
    public MainViewModel Main { get; } = main;

    [ObservableProperty]
    private IReadOnlyList<AgeRow> _rows = [];

    public void Load(Analysis? a)
    {
        if (a is null)
        {
            Rows = [];
            return;
        }
        var metric = Main.Metric;
        var ages = (Main.Scope ?? a.Everything).Ages;
        long total = Math.Max(1, ages.Sum(b => metric == SizeMetric.Logical ? b.Size : b.Allocated));
        Rows = ages.Select(b => new AgeRow(b, (double)(metric == SizeMetric.Logical ? b.Size : b.Allocated) / total, metric)).ToList();
    }

    public TableSnapshot? Snapshot() => Rows.Count == 0 ? null : new TableSnapshot("Age", SortHelper.ScopeLabel(Main),
        ["Last changed", "Files", "Size (bytes)", "Size", "Share"],
        Rows.Select(r => (IReadOnlyList<string>)[r.Label, r.Count.ToString(), r.Size.ToString(), r.SizeText, r.PercentText]).ToList());
}

public sealed record NavItem(string Key, string Title, string Glyph, string Hint);
