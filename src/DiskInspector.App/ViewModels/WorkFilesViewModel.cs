using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskInspector.App.Services;
using DiskInspector.Core.Analysis;
using DiskInspector.Core.Model;

namespace DiskInspector.App.ViewModels;

/// <summary>
/// Tables of work files (Excel, Word, PowerPoint, PDF, email, Markdown, notebooks, Python, DBC Finance, AI assistants...)
/// with location, dates and author, for everything scanned or one folder. "Any types or all files" lists the extensions the
/// user types, or every file.
/// </summary>
public sealed partial class WorkFilesViewModel : ObservableObject, ISnapshotSource, ISortableTable
{
    /// <summary>More rows than this are not shown at once (the largest files are kept; snapshots still include every file).</summary>
    public const int MaxRows = 100_000;

    /// <summary>Most extensions offered in the Types list for a group that can hold any type.</summary>
    const int MaxTypeOptions = 300;

    static readonly string[] Headers =
    [
        "Name", "Type", "Group", "Folder", "Path", "Drive", "Size (bytes)", "Size", "Created", "Last saved (modified)", "Last accessed",
        "Author", "Last saved by", "Title", "Availability",
    ];

    readonly DispatcherTimer _debounce;
    Analysis? _a;
    CancellationTokenSource? _queryCts;
    CancellationTokenSource? _authorCts;
    bool _loading;
    long[]? _allCounts;                                   // every file in scope, by extension id
    Dictionary<string, long[]> _ruleCounts = [];          // matching files by extension id, for name/folder groups
    QueryResult? _last;

    public WorkFilesViewModel(MainViewModel main)
    {
        Main = main;
        GroupDefinitions = WorkFiles.LoadGroups();
        _onlyUserFolders = main.Settings.OnlyUserFolders;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (_customChanged)
            {
                _customChanged = false;
                RebuildTypes();
            }
            Requery();
        };
    }

    public MainViewModel Main { get; }
    public IReadOnlyList<WorkFileGroup> GroupDefinitions { get; }

    [ObservableProperty]
    private IReadOnlyList<WorkGroupOption> _groups = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomGroup))]
    private WorkGroupOption? _selectedGroup;

    [ObservableProperty]
    private IReadOnlyList<TypeOption> _types = [];

    [ObservableProperty]
    private TypeOption? _selectedType;

    [ObservableProperty]
    private string _nameFilter = "";

    /// <summary>Extensions for "Any types or all files", e.g. "json, toml, sql". Empty or * means every file.</summary>
    [ObservableProperty]
    private string _customTypes = "";

    bool _customChanged;

    public bool IsCustomGroup => SelectedGroup?.Group.IsCustom == true;

    /// <summary>How many files match, including any the table leaves out because there are too many to show.</summary>
    public int FoundCount => _last?.Found ?? 0;

    [ObservableProperty]
    private bool _onlyUserFolders;

    [ObservableProperty]
    private IReadOnlyList<WorkFileRow> _rows = [];

    [ObservableProperty]
    private WorkFileRow? _selectedRow;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadAuthorsCommand), nameof(StopAuthorsCommand))]
    private bool _isReadingAuthors;

    [ObservableProperty]
    private string _authorStatus = "";

    public void Load(Analysis? a)
    {
        _a = a;
        _authorCts?.Cancel();
        _last = null;
        if (a is null)
        {
            Groups = [];
            Rows = [];
            Summary = "";
            AuthorStatus = "";
            return;
        }
        RefreshGroups(keepSelection: true);
    }

    /// <summary>Recounts the groups for the current scope and reloads the table.</summary>
    public void RefreshGroups(bool keepSelection)
    {
        var a = _a;
        if (a is null)
            return;
        string? keepGroup = keepSelection ? SelectedGroup?.Group.Key : null;
        int root = Main.Scope?.Root ?? ScanTree.RootId;
        bool only = OnlyUserFolders;
        var ruleGroups = GroupDefinitions.Where(g => g.HasRules).ToList();
        IsBusy = true;
        Task.Run(() =>
        {
            var all = WorkFiles.CountByExtension(a.Tree, root, only);
            var rules = new Dictionary<string, long[]>();
            foreach (var g in ruleGroups)
                rules[g.Key] = WorkFiles.CountByExtension(a.Tree, FileMatcher.ForGroup(a.Tree, g), root, only);
            return (all, rules);
        }).ContinueWith(t =>
        {
            if (_a != a || t.Status != TaskStatus.RanToCompletion)
                return;
            (_allCounts, _ruleCounts) = t.Result;
            _loading = true;
            Groups = GroupDefinitions.Select(g => new WorkGroupOption(g, g.IsCustom ? -1 : CountsFor(g).Sum())).ToList();
            SelectedGroup = Groups.FirstOrDefault(g => g.Group.Key == keepGroup) ?? Groups.FirstOrDefault(g => g.Group.Key == "excel") ?? Groups.FirstOrDefault();
            _loading = false;
            RebuildTypes();
            Requery();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Matching files by extension id for <paramref name="g"/> in the current scope.</summary>
    long[] CountsFor(WorkFileGroup g)
    {
        var all = _allCounts ?? [];
        if (g.HasRules)
            return _ruleCounts.TryGetValue(g.Key, out var c) ? c : new long[all.Length];
        var exts = g.IsCustom ? WorkFiles.ParseExtensions(CustomTypes) : g.Extensions;
        if (g.IsCustom && exts.Count == 0)
            return all;
        var result = new long[all.Length];
        if (_a is { } a)
            foreach (var e in exts)
                if (a.Tree.Extensions.TryGetId(e, out ushort id) && id < all.Length)
                    result[id] = all[id];
        return result;
    }

    void RebuildTypes()
    {
        var a = _a;
        if (a is null || SelectedGroup is null || _allCounts is null)
            return;
        var group = SelectedGroup.Group;
        var counts = CountsFor(group);
        TypeOption? keep = SelectedType;
        _loading = true;
        var options = new List<TypeOption> { TypeOption.All(group.IsCustom ? "All matching types" : "All types in group") };
        if (group.HasRules || group.IsCustom)
        {
            // Any extension can turn up: offer the ones found, most common first.
            options.AddRange(Enumerable.Range(0, counts.Length).Where(i => counts[i] > 0).OrderByDescending(i => counts[i]).Take(MaxTypeOptions)
                .Select(i => TypeOption.For((ushort)i, a.Tree.Extensions[(ushort)i], counts[i])));
        }
        else
        {
            foreach (var e in group.Extensions)
                if (a.Tree.Extensions.TryGetId(e, out ushort id) && counts[id] > 0)
                    options.Add(TypeOption.For(id, e, counts[id]));
        }
        Types = options;
        SelectedType = options.FirstOrDefault(o => keep is not null && o.ExtensionId == keep.ExtensionId) ?? options[0];
        _loading = false;
    }

    partial void OnSelectedGroupChanged(WorkGroupOption? value)
    {
        if (_loading)
            return;
        RebuildTypes();
        Requery();
    }

    partial void OnSelectedTypeChanged(TypeOption? value)
    {
        if (!_loading)
            Requery();
    }

    partial void OnNameFilterChanged(string value)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    partial void OnCustomTypesChanged(string value)
    {
        if (!IsCustomGroup)
            return;
        _customChanged = true;
        _debounce.Stop();
        _debounce.Start();
    }

    partial void OnOnlyUserFoldersChanged(bool value) => RefreshGroups(keepSelection: true);

    partial void OnSelectedRowChanged(WorkFileRow? value)
    {
        if (value is not null)
            Main.SelectedNode = value.Node;
    }

    /// <summary>Which files the table currently shows.</summary>
    FileMatcher CurrentMatcher(ScanTree tree)
    {
        var group = SelectedGroup!.Group;
        return group.IsCustom ? FileMatcher.ForExtensions(tree, WorkFiles.ParseExtensions(CustomTypes)) : FileMatcher.ForGroup(tree, group);
    }

    void Requery()
    {
        var a = _a;
        if (a is null || SelectedGroup is null)
            return;
        _queryCts?.Cancel();
        _authorCts?.Cancel();
        var cts = _queryCts = new CancellationTokenSource();
        var matcher = CurrentMatcher(a.Tree);
        int type = SelectedType?.ExtensionId ?? -1;
        int root = Main.Scope?.Root ?? ScanTree.RootId;
        bool only = OnlyUserFolders;
        string name = NameFilter.Trim();
        IsBusy = true;
        Task.Run(() => BuildRows(a, matcher, type, root, only, name, cts.Token), cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || _a != a)
                return;
            IsBusy = false;
            if (t.Status != TaskStatus.RanToCompletion)
            {
                if (t.Exception is not null)
                    Log.Error("Work files query failed", t.Exception);
                return;
            }
            var r = _last = t.Result;
            Rows = r.Rows;
            int online = r.Rows.Count(x => x.IsOnlineOnly);
            Summary = r.Found == 0 ? "No matching files"
                : $"{r.Found:N0} files, {Format.Bytes(r.Bytes)}" + (online > 0 ? $" · {online:N0} online-only" : "")
                  + (r.Found > r.Rows.Count ? $" · showing the largest {r.Rows.Count:N0} (Snapshot table saves all {r.Found:N0})" : "");
            StartAutoAuthors();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The rows to show plus, when there are too many to show, the rest (smaller files) for snapshots.</summary>
    sealed record QueryResult(List<WorkFileRow> Rows, int[] Hidden, int Found, long Bytes);

    static QueryResult BuildRows(Analysis a, FileMatcher matcher, int extensionId, int root, bool only, string name, CancellationToken ct)
    {
        var tree = a.Tree;
        Func<int, bool>? filter = (extensionId, name.Length) switch
        {
            ( < 0, 0) => null,
            ( < 0, _) => i => tree.Name(i).Contains(name, StringComparison.OrdinalIgnoreCase),
            (_, 0) => i => tree.ExtensionId(i) == extensionId,
            _ => i => tree.ExtensionId(i) == extensionId && tree.Name(i).Contains(name, StringComparison.OrdinalIgnoreCase),
        };
        var nodes = WorkFiles.Find(tree, matcher, root, only, filter, ct);
        long bytes = 0;
        foreach (int n in nodes)
            bytes += tree.Size(n);
        int found = nodes.Count;
        int[] hidden = [];
        if (nodes.Count > MaxRows)
        {
            nodes.Sort((x, y) => tree.Size(y).CompareTo(tree.Size(x)));     // largest first
            hidden = nodes.GetRange(MaxRows, nodes.Count - MaxRows).ToArray();
            nodes.RemoveRange(MaxRows, nodes.Count - MaxRows);
        }
        else
        {
            nodes.Sort((x, y) => tree.ModifiedTicks(y).CompareTo(tree.ModifiedTicks(x)));   // newest first
        }
        ct.ThrowIfCancellationRequested();
        var rows = new List<WorkFileRow>(nodes.Count);
        foreach (int n in nodes)
            rows.Add(MakeRow(a, n));
        return new QueryResult(rows, hidden, found, bytes);
    }

    static WorkFileRow MakeRow(Analysis a, int node)
    {
        var row = new WorkFileRow(a.Tree, node);
        if (a.Authors.TryGetValue(node, out var info))
            row.Apply(info);
        return row;
    }

    public void SortBy(string property, ListSortDirection direction) => Rows = SortHelper.Sort(Rows, property, direction);

    // ---------- authors ----------

    IReadOnlyList<WorkFileRow> PendingAuthorRows() =>
        _a is null ? [] : Rows.Where(r => r.CanReadProperties && !r.IsOnlineOnly && !_a.Authors.ContainsKey(r.Node)).ToList();

    void StartAutoAuthors()
    {
        var pending = PendingAuthorRows();
        if (pending.Count == 0)
        {
            AuthorStatus = Rows.Any(r => r.CanReadProperties) ? "" : "Authors are not recorded in these file types.";
            return;
        }
        if (Main.Settings.ReadAuthors && pending.Count <= Main.Settings.AuthorAutoLimit)
            _ = ReadAuthorsAsync(pending);
        else
            AuthorStatus = $"Authors not read yet for {pending.Count:N0} files. Click Read authors.";
    }

    bool CanReadAuthors() => !IsReadingAuthors;

    [RelayCommand(CanExecute = nameof(CanReadAuthors))]
    Task ReadAuthors() => ReadAuthorsAsync(PendingAuthorRows());

    [RelayCommand(CanExecute = nameof(IsReadingAuthors))]
    void StopAuthors() => _authorCts?.Cancel();

    async Task ReadAuthorsAsync(IReadOnlyList<WorkFileRow> rows)
    {
        var a = _a;
        if (a is null || rows.Count == 0)
            return;
        _authorCts?.Cancel();
        var cts = _authorCts = new CancellationTokenSource();
        IsReadingAuthors = true;
        int done = 0;
        try
        {
            var dispatcher = Application.Current.Dispatcher;
            await Task.Run(() => Parallel.ForEach(rows, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cts.Token }, row =>
            {
                var info = DocumentProperties.TryRead(row.Path) ?? DocumentInfo.Empty;
                a.Authors[row.Node] = info;
                dispatcher.BeginInvoke(() => row.Apply(info), DispatcherPriority.Background);
                int n = Interlocked.Increment(ref done);
                if (n % 50 == 0)
                    dispatcher.BeginInvoke(() => AuthorStatus = $"Reading authors… {n:N0} of {rows.Count:N0}", DispatcherPriority.Background);
            }), cts.Token);
            AuthorStatus = $"Authors read for {rows.Count:N0} files (read-only; last-accessed dates left unchanged).";
        }
        catch (OperationCanceledException)
        {
            AuthorStatus = $"Stopped after {done:N0} of {rows.Count:N0} files.";
        }
        finally
        {
            if (_authorCts == cts)
                IsReadingAuthors = false;
        }
    }

    // ---------- snapshots ----------

    static IReadOnlyList<string> RowCells(WorkFileRow r, string group) =>
    [
        r.Name, r.Type, group, r.Folder, r.Path, r.Path.Length >= 2 && r.Path[1] == ':' ? r.Path[..2] : "", r.Size.ToString(), r.SizeText,
        Snapshots.Date(r.Created), Snapshots.Date(r.Modified), Snapshots.Date(r.Accessed), r.Author, r.LastSavedBy, r.Title, r.Availability,
    ];

    /// <summary>Label of what the table holds, e.g. ".xlsx" or "Any types (json, toml)".</summary>
    string TableLabel()
    {
        var group = SelectedGroup!.Group;
        if (SelectedType is { IsAll: false } t)
            return t.DisplayType;
        if (!group.IsCustom)
            return group.Name;
        var exts = WorkFiles.ParseExtensions(CustomTypes);
        return exts.Count == 0 ? "All files" : string.Join(" ", exts.Select(e => "." + e));
    }

    /// <summary>
    /// The table in its current sort order. When there were too many files to show, the files that weren't shown follow,
    /// largest first, so the CSV holds every matching file. Rows are made as the file is written.
    /// </summary>
    public TableSnapshot? Snapshot()
    {
        var a = _a;
        if (a is null || Rows.Count == 0 || SelectedGroup is null)
            return null;
        string group = SelectedGroup.Group.IsCustom ? TableLabel() : SelectedGroup.Group.Name;
        string detail = string.Join(", ", new[]
        {
            TableLabel(),
            SortHelper.ScopeLabel(Main),
            NameFilter.Trim(),
        }.Where(x => x.Length > 0));
        var shown = Rows;
        int[] hidden = _last?.Hidden ?? [];     // set together with Rows; sorting only reorders the same rows
        var cells = new ProjectedList<IReadOnlyList<string>>(shown.Count + hidden.Length,
            i => RowCells(i < shown.Count ? shown[i] : MakeRow(a, hidden[i - shown.Count]), group));
        return new TableSnapshot("Work files", detail, Headers, cells);
    }

    /// <summary>
    /// Writes one CSV per group (for the current scope) plus a summary into a new timestamped folder.
    /// Reads authors first when that is turned on in Settings.
    /// </summary>
    public async Task<string?> SnapshotAllGroupsAsync(IProgress<string> progress, CancellationToken ct)
    {
        var a = _a;
        if (a is null)
            return null;
        int root = Main.Scope?.Root ?? ScanTree.RootId;
        bool only = OnlyUserFolders;
        bool readAuthors = Main.Settings.ReadAuthors;
        string folder = Main.Settings.SnapshotFolder;
        string scope = SortHelper.ScopeLabel(Main);
        var now = DateTime.Now;
        return await Task.Run(() =>
        {
            string batch = Snapshots.NewBatchFolder(folder, "Work files" + (scope.Length > 0 ? $" - {scope}" : ""), now);
            var summary = new List<IReadOnlyList<string>>();
            foreach (var g in GroupDefinitions.Where(g => !g.IsCustom))
            {
                ct.ThrowIfCancellationRequested();
                progress.Report($"Collecting {g.Name}…");
                var r = BuildRows(a, FileMatcher.ForGroup(a.Tree, g), -1, root, only, "", ct);
                if (readAuthors)
                {
                    var pending = r.Rows.Where(x => x.CanReadProperties && !x.IsOnlineOnly && !a.Authors.ContainsKey(x.Node)).ToList();
                    int done = 0;
                    Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, x =>
                    {
                        a.Authors[x.Node] = DocumentProperties.TryRead(x.Path) ?? DocumentInfo.Empty;
                        int n = Interlocked.Increment(ref done);
                        if (n % 100 == 0)
                            progress.Report($"Reading authors for {g.Name}: {n:N0} of {pending.Count:N0}");
                    });
                    foreach (var x in r.Rows)
                        if (a.Authors.TryGetValue(x.Node, out var info))
                            x.Apply(info);
                }
                var cells = new ProjectedList<IReadOnlyList<string>>(r.Rows.Count + r.Hidden.Length,
                    i => RowCells(i < r.Rows.Count ? r.Rows[i] : MakeRow(a, r.Hidden[i - r.Rows.Count]), g.Name));
                Snapshots.Write(batch, new TableSnapshot(g.Name, "", Headers, cells), now);
                int online = r.Rows.Count(x => x.IsOnlineOnly) + r.Hidden.Count(n => (a.Tree.Flags(n) & NodeFlags.CloudPlaceholder) != 0);
                summary.Add([g.Name, g.Describe(), r.Found.ToString(), r.Bytes.ToString(), Format.Bytes(r.Bytes), online.ToString()]);
            }
            Snapshots.Write(batch, new TableSnapshot("Summary", "", ["Group", "Matches", "Files", "Size (bytes)", "Size", "Online-only files"], summary), now);
            return batch;
        }, ct);
    }
}

/// <summary>A read-only list whose items are made when they are read, so a big snapshot doesn't hold every row in memory.</summary>
public sealed class ProjectedList<T>(int count, Func<int, T> item) : IReadOnlyList<T>
{
    public int Count => count;
    public T this[int index] => (uint)index < (uint)count ? item(index) : throw new ArgumentOutOfRangeException(nameof(index));

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < count; i++)
            yield return item(i);
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
