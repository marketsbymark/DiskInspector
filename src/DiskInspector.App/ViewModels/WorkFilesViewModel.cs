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
/// Tables of work files (Excel, Word, PowerPoint, PDF, email, Markdown, notebooks, Python, DBC Finance...)
/// with location, dates and author, for everything scanned or one folder.
/// </summary>
public sealed partial class WorkFilesViewModel : ObservableObject, ISnapshotSource, ISortableTable
{
    /// <summary>More rows than this are not shown at once (the largest files are kept).</summary>
    public const int MaxRows = 100_000;

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

    public WorkFilesViewModel(MainViewModel main)
    {
        Main = main;
        GroupDefinitions = WorkFiles.LoadGroups();
        _onlyUserFolders = main.Settings.OnlyUserFolders;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Requery();
        };
    }

    public MainViewModel Main { get; }
    public IReadOnlyList<WorkFileGroup> GroupDefinitions { get; }

    [ObservableProperty]
    private IReadOnlyList<WorkGroupOption> _groups = [];

    [ObservableProperty]
    private WorkGroupOption? _selectedGroup;

    [ObservableProperty]
    private IReadOnlyList<TypeOption> _types = [];

    [ObservableProperty]
    private TypeOption? _selectedType;

    [ObservableProperty]
    private string _nameFilter = "";

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
        IsBusy = true;
        Task.Run(() => WorkFiles.CountByExtension(a.Tree, root, only)).ContinueWith(t =>
        {
            if (_a != a || t.Status != TaskStatus.RanToCompletion)
                return;
            var counts = t.Result;
            long Count(WorkFileGroup g) => g.Extensions.Sum(e => a.Tree.Extensions.TryGetId(e, out ushort id) ? counts[id] : 0);
            _loading = true;
            Groups = GroupDefinitions.Select(g => new WorkGroupOption(g, Count(g))).ToList();
            SelectedGroup = Groups.FirstOrDefault(g => g.Group.Key == keepGroup) ?? Groups.FirstOrDefault(g => g.Group.Key == "excel") ?? Groups.FirstOrDefault();
            _loading = false;
            RebuildTypes(counts);
            Requery();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    long[]? _lastCounts;

    void RebuildTypes(long[]? counts = null)
    {
        counts ??= _lastCounts;
        _lastCounts = counts;
        var a = _a;
        if (a is null || SelectedGroup is null || counts is null)
            return;
        string? keep = SelectedType?.Extension;
        _loading = true;
        var options = new List<TypeOption> { new("All types in group", "") };
        foreach (var e in SelectedGroup.Group.Extensions)
            if (a.Tree.Extensions.TryGetId(e, out ushort id) && counts[id] > 0)
                options.Add(new TypeOption($".{e} ({counts[id]:N0})", e));
        Types = options;
        SelectedType = options.FirstOrDefault(o => o.Extension == keep) ?? options[0];
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

    partial void OnOnlyUserFoldersChanged(bool value) => RefreshGroups(keepSelection: true);

    partial void OnSelectedRowChanged(WorkFileRow? value)
    {
        if (value is not null)
            Main.SelectedNode = value.Node;
    }

    /// <summary>Extensions the table currently shows.</summary>
    IReadOnlyList<string> CurrentExtensions() =>
        SelectedType is { Extension.Length: > 0 } t ? [t.Extension] : SelectedGroup?.Group.Extensions ?? [];

    void Requery()
    {
        var a = _a;
        if (a is null || SelectedGroup is null)
            return;
        _queryCts?.Cancel();
        _authorCts?.Cancel();
        var cts = _queryCts = new CancellationTokenSource();
        var exts = CurrentExtensions();
        int root = Main.Scope?.Root ?? ScanTree.RootId;
        bool only = OnlyUserFolders;
        string name = NameFilter.Trim();
        IsBusy = true;
        Task.Run(() => BuildRows(a, exts, root, only, name, cts.Token), cts.Token).ContinueWith(t =>
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
            var (rows, found, bytes) = t.Result;
            Rows = rows;
            int online = rows.Count(r => r.IsOnlineOnly);
            Summary = found == 0 ? "No matching files"
                : $"{found:N0} files, {Format.Bytes(bytes)}" + (online > 0 ? $" · {online:N0} online-only" : "")
                  + (found > rows.Count ? $" · showing the largest {rows.Count:N0}" : "");
            StartAutoAuthors();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    static (List<WorkFileRow> Rows, int Found, long Bytes) BuildRows(Analysis a, IReadOnlyList<string> exts, int root, bool only, string name, CancellationToken ct)
    {
        var tree = a.Tree;
        var mask = WorkFiles.ExtensionMask(tree, exts);
        var nodes = WorkFiles.Find(tree, mask, root, only,
            name.Length == 0 ? null : i => tree.Name(i).Contains(name, StringComparison.OrdinalIgnoreCase), ct);
        long bytes = nodes.Sum(tree.Size);
        int found = nodes.Count;
        if (nodes.Count > MaxRows)
            nodes = nodes.OrderByDescending(tree.Size).Take(MaxRows).ToList();
        else
            nodes.Sort((x, y) => tree.ModifiedTicks(y).CompareTo(tree.ModifiedTicks(x)));   // newest first
        var rows = new List<WorkFileRow>(nodes.Count);
        foreach (int n in nodes)
        {
            var row = new WorkFileRow(tree, n);
            if (a.Authors.TryGetValue(n, out var info))
                row.Apply(info);
            rows.Add(row);
        }
        return (rows, found, bytes);
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

    public TableSnapshot? Snapshot()
    {
        if (Rows.Count == 0 || SelectedGroup is null)
            return null;
        string group = SelectedGroup.Group.Name;
        string detail = string.Join(", ", new[]
        {
            SelectedType is { Extension.Length: > 0 } t ? "." + t.Extension : group,
            SortHelper.ScopeLabel(Main),
            NameFilter.Trim(),
        }.Where(x => x.Length > 0));
        return new TableSnapshot("Work files", detail, Headers, Rows.Select(r => RowCells(r, group)).ToList());
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
            foreach (var g in GroupDefinitions)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report($"Collecting {g.Name}…");
                var (rows, found, bytes) = BuildRows(a, g.Extensions, root, only, "", ct);
                if (readAuthors)
                {
                    var pending = rows.Where(r => r.CanReadProperties && !r.IsOnlineOnly && !a.Authors.ContainsKey(r.Node)).ToList();
                    int done = 0;
                    Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, r =>
                    {
                        a.Authors[r.Node] = DocumentProperties.TryRead(r.Path) ?? DocumentInfo.Empty;
                        int n = Interlocked.Increment(ref done);
                        if (n % 100 == 0)
                            progress.Report($"Reading authors for {g.Name}: {n:N0} of {pending.Count:N0}");
                    });
                    foreach (var r in rows)
                        if (a.Authors.TryGetValue(r.Node, out var info))
                            r.Apply(info);
                }
                Snapshots.Write(batch, new TableSnapshot(g.Name, "", Headers, rows.Select(r => RowCells(r, g.Name)).ToList()), now);
                summary.Add([g.Name, string.Join(" ", g.Extensions.Select(e => "." + e)), found.ToString(), bytes.ToString(), Format.Bytes(bytes),
                    rows.Count(r => r.IsOnlineOnly).ToString()]);
            }
            Snapshots.Write(batch, new TableSnapshot("Summary", "", ["Group", "Types", "Files", "Size (bytes)", "Size", "Online-only files"], summary), now);
            return batch;
        }, ct);
    }
}
