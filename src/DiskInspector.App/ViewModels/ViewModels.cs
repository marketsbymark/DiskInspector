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

/// <summary>Folder tree shown as a flat, virtualized list with expand/collapse.</summary>
public sealed partial class FolderTreeViewModel(MainViewModel main) : ObservableObject
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

public sealed partial class TypesViewModel(MainViewModel main) : ObservableObject
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
        long total = Math.Max(1, a.Tree.GetSize(0, metric));
        Rows = a.Extensions.Select(e => new ExtensionRow(e, (double)(metric == SizeMetric.Logical ? e.Size : e.Allocated) / total))
            .OrderByDescending(r => metric == SizeMetric.Logical ? r.Size : r.Allocated).ToList();
        Slices = a.Categories.Select(c => new DonutSlice(c.Category.Name, metric == SizeMetric.Logical ? c.Size : c.Allocated, BrushCache.Get(c.Category.Color))).ToList();
        Summary = $"{a.Extensions.Count:N0}";
    }

    partial void OnSelectedRowChanged(ExtensionRow? value)
    {
        if (value is not null && value.Stat.LargestNode >= 0)
            Main.SelectedNode = value.Stat.LargestNode;
    }
}

public sealed partial class CategoriesViewModel(MainViewModel main) : ObservableObject
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
        long total = Math.Max(1, a.Tree.GetSize(0, metric));
        Rows = a.Categories.Where(c => c.Count > 0)
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

    void LoadFiles(string title, byte? category, ushort? extension)
    {
        var a = _a!;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var metric = Main.Metric;
        FilesTitle = title;
        Task.Run(() =>
        {
            var map = a.Colors.Categorizer.BuildExtensionMap(a.Tree.Extensions);
            Func<int, bool> filter = extension is { } e
                ? i => a.Tree.ExtensionId(i) == e
                : i => map[a.Tree.ExtensionId(i)] == category;
            var top = FileQueries.Largest(a.Tree, 300, metric, filter, cts.Token);
            long total = a.Tree.GetSize(0, metric);
            return top.Select(n => new FileRow(a.Tree, n, a.Colors, total)).ToList();
        }, cts.Token).ContinueWith(t =>
        {
            if (!cts.IsCancellationRequested && t.Status == TaskStatus.RanToCompletion)
                Files = t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}

public sealed record FilterOption(string Label, int Value);

public sealed partial class LargestFilesViewModel : ObservableObject
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
            var top = FileQueries.Largest(a.Tree, 1000, metric, filter, cts.Token);
            long total = a.Tree.GetSize(0, metric);
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

public sealed partial class LocationsViewModel(MainViewModel main) : ObservableObject
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
}

public sealed partial class AgeViewModel(MainViewModel main) : ObservableObject
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
        long total = Math.Max(1, a.Ages.Sum(b => metric == SizeMetric.Logical ? b.Size : b.Allocated));
        Rows = a.Ages.Select(b => new AgeRow(b, (double)(metric == SizeMetric.Logical ? b.Size : b.Allocated) / total, metric)).ToList();
    }
}

public sealed record NavItem(string Title, string Glyph, string Hint);
