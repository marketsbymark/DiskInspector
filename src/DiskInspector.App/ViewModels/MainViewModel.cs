using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskInspector.App.Services;
using DiskInspector.App.Views;
using DiskInspector.Core.Analysis;
using DiskInspector.Core.Model;
using DiskInspector.Core.Scanning;
using Microsoft.Win32;

namespace DiskInspector.App.ViewModels;

public enum AppState
{
    Start,
    Scanning,
    Done,
}

public sealed partial class MainViewModel : ObservableObject
{
    CancellationTokenSource? _cts;
    Analysis? _analysis;

    public MainViewModel()
    {
        Categorizer = Categorizer.Load(includeUserOverride: true);
        Settings = AppSettings.Load();
        Folders = new FolderTreeViewModel(this);
        Types = new TypesViewModel(this);
        Categories = new CategoriesViewModel(this);
        Largest = new LargestFilesViewModel(this);
        Locations = new LocationsViewModel(this);
        Ages = new AgeViewModel(this);
        WorkFiles = new WorkFilesViewModel(this);
        RefreshDrives();
    }

    public MainViewModel Main => this;
    public Categorizer Categorizer { get; }
    public FolderTreeViewModel Folders { get; }
    public TypesViewModel Types { get; }
    public CategoriesViewModel Categories { get; }
    public LargestFilesViewModel Largest { get; }
    public LocationsViewModel Locations { get; }
    public AgeViewModel Ages { get; }
    public WorkFilesViewModel WorkFiles { get; }
    public AppSettings Settings { get; private set; }
    public bool IsElevated { get; } = Elevation.IsElevated();
    public string FastScanLabel => "Fast scan";
    public string WindowTitle => IsElevated ? "DiskInspector (Administrator)" : "DiskInspector";

    public ObservableCollection<DriveItem> Drives { get; } = [];

    public IReadOnlyList<NavItem> Views { get; } =
    [
        new("treemap", "Treemap", "\uF0E2", "Every file as a box sized by space"),
        new("sunburst", "Sunburst", "\uEB05", "Folders as rings around the center"),
        new("folders", "Folders", "\uE8B7", "Folder tree sorted by size"),
        new("types", "File types", "\uE8A5", "Space by file extension"),
        new("categories", "Categories", "\uE8FD", "Space by kind of file"),
        new("work", "Work files", "\uE8F1", "Excel, Word, PowerPoint, PDF, email, Markdown, notebooks, Python, DBC, help files, AI assistant files, or any types you choose, with dates and authors"),
        new("locations", "Locations", "\uE707", "Known places: Windows, programs, users, OneDrive"),
        new("largest", "Largest files", "\uE8CB", "Biggest single files, with filters"),
        new("age", "Age", "\uE823", "Space by when files last changed"),
    ];

    public string CurrentViewKey => SelectedViewIndex >= 0 && SelectedViewIndex < Views.Count ? Views[SelectedViewIndex].Key : "treemap";

    /// <summary>Switches to a view by its key ("treemap", "work", ...).</summary>
    public void ShowView(string key)
    {
        int i = Views.ToList().FindIndex(v => v.Key == key);
        if (i >= 0)
            SelectedViewIndex = i;
    }

    // ---------- state ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStart), nameof(IsScanning), nameof(IsDone))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(FastScanCommand), nameof(CancelCommand), nameof(ExportCommand), nameof(NewScanCommand),
        nameof(SnapshotCommand), nameof(SnapshotAllWorkFilesCommand))]
    private AppState _state = AppState.Start;

    public bool IsStart => State == AppState.Start;
    public bool IsScanning => State == AppState.Scanning;
    public bool IsDone => State == AppState.Done;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FastScanCommand))]
    private string _targetPath = @"C:\";

    [ObservableProperty]
    private DriveItem? _selectedDrive;

    partial void OnSelectedDriveChanged(DriveItem? value)
    {
        if (value is not null)
            TargetPath = value.RootPath;
    }

    // ---------- progress ----------

    [ObservableProperty]
    private string _progressHeadline = "";

    [ObservableProperty]
    private string _progressDetail = "";

    [ObservableProperty]
    private string _progressPath = "";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _progressIndeterminate = true;

    // ---------- results ----------

    [ObservableProperty]
    private ScanResult? _result;

    [ObservableProperty]
    private ScanTree? _tree;

    [ObservableProperty]
    private CategoryBreakdown? _colors;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Metric))]
    private bool _useAllocated;

    public SizeMetric Metric => UseAllocated ? SizeMetric.Allocated : SizeMetric.Logical;

    partial void OnUseAllocatedChanged(bool value)
    {
        if (_analysis is null)
            return;
        Folders.Refresh();
        Types.Load(_analysis);
        Categories.Load(_analysis);
        Largest.Load(_analysis);
        Locations.Load(_analysis);
        Ages.Load(_analysis);
        UpdateDetails();
        UpdateHover();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand), nameof(RevealSelectedCommand), nameof(CopySelectedPathCommand), nameof(ShowInTreemapCommand),
        nameof(ScopeToSelectionCommand), nameof(AnalyzeSelectedFolderCommand))]
    private int _selectedNode = -1;

    partial void OnSelectedNodeChanged(int value)
    {
        UpdateDetails();
        Folders.Reveal(value);
    }

    [ObservableProperty]
    private int _hoveredNode = -1;

    partial void OnHoveredNodeChanged(int value) => UpdateHover();

    [ObservableProperty]
    private string _hoverText = "Hover over the map to see what each box is. Double-click to zoom in.";

    [ObservableProperty]
    private NodeDetails? _details;

    [ObservableProperty]
    private int _selectedViewIndex;

    [ObservableProperty]
    private string _statusText = "Choose a drive or folder, then Scan.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    [ObservableProperty]
    private IReadOnlyList<ScanIssue> _issues = [];

    [ObservableProperty]
    private IReadOnlyList<Category> _legend = [];

    // ---------- scope: everything, or one folder ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScoped), nameof(ScopeText))]
    [NotifyCanExecuteChangedFor(nameof(ScopeToEverythingCommand))]
    private int _scopeRoot;

    /// <summary>Breakdowns for the current scope (null until a scan finishes).</summary>
    public ScopeStats? Scope { get; private set; }

    public bool IsScoped => ScopeRoot > 0;

    public string ScopeText => Tree is { } t && ScopeRoot > 0 && ScopeRoot < t.Count ? t.GetFullPath(ScopeRoot) : "Everything scanned";

    CancellationTokenSource? _scopeCts;

    partial void OnScopeRootChanged(int value) => _ = ReloadScopeAsync();

    async Task ReloadScopeAsync()
    {
        var a = _analysis;
        if (a is null)
            return;
        _scopeCts?.Cancel();
        var cts = _scopeCts = new CancellationTokenSource();
        int root = ScopeRoot;
        try
        {
            var stats = await Task.Run(() => a.ComputeScope(root, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || _analysis != a)
                return;
            Scope = stats;
            Types.Load(a);
            Categories.Load(a);
            Largest.Recompute();
            Ages.Load(a);
            WorkFiles.RefreshGroups(keepSelection: true);
            StatusText = root > 0 ? $"Showing file types, categories, largest files, age and work files for {ScopeText}" : StatusText;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The folder for scoped views: the selection if it is a folder, else the folder holding it.</summary>
    int SelectedFolder()
    {
        if (Tree is not { } t || SelectedNode < 0 || SelectedNode >= t.Count)
            return -1;
        return t.IsDirectory(SelectedNode) ? SelectedNode : t.Parent(SelectedNode);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    void ScopeToSelection()
    {
        int folder = SelectedFolder();
        if (folder >= 0)
            ScopeRoot = folder;
    }

    [RelayCommand(CanExecute = nameof(IsScoped))]
    void ScopeToEverything() => ScopeRoot = 0;

    /// <summary>From the right-click menu: show the file types of the selected folder.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    void AnalyzeSelectedFolder()
    {
        ScopeToSelection();
        ShowView("types");
    }

    // ---------- snapshots ----------

    [ObservableProperty]
    private string? _lastSnapshot;

    /// <summary>The table the current view is showing, or null.</summary>
    TableSnapshot? CurrentSnapshot()
    {
        switch (CurrentViewKey)
        {
            case "treemap": return ChildrenSnapshot("Treemap", TreemapRoot);
            case "sunburst": return ChildrenSnapshot("Sunburst", SunburstRoot);
            case "folders": return Folders.Snapshot();
            case "types": return Types.Snapshot();
            case "categories": return Categories.Snapshot();
            case "work": return WorkFiles.Snapshot();
            case "locations": return Locations.Snapshot();
            case "largest": return Largest.Snapshot();
            case "age": return Ages.Snapshot();
            default: return null;
        }
    }

    /// <summary>What the treemap or sunburst shows: the items in its current folder.</summary>
    TableSnapshot? ChildrenSnapshot(string view, int root)
    {
        var t = Tree;
        var colors = Colors;
        if (t is null || colors is null || root < 0 || root >= t.Count)
            return null;
        long total = Math.Max(1, t.GetSize(root, Metric));
        var rows = t.GetChildrenSorted(root, Metric).Select(c => (IReadOnlyList<string>)
        [
            t.IsDirectory(c) ? t.Name(c) + "\\" : t.Name(c), t.GetFullPath(c), t.IsDirectory(c) ? "Folder" : "File",
            colors.Categorizer.Categories[colors.CategoryOf(c)].Name, t.Size(c).ToString(), Format.Bytes(t.Size(c)), t.Allocated(c).ToString(),
            Format.Percent((double)t.GetSize(c, Metric) / total), t.IsDirectory(c) ? t.FileCount(c).ToString() : "",
            Snapshots.Date(t.CreatedUtc(c)), Snapshots.Date(t.ModifiedUtc(c)), Snapshots.Date(t.AccessedUtc(c)),
        ]).ToList();
        return new TableSnapshot(view, root == 0 ? "" : t.Name(root),
            ["Name", "Path", "Kind", "Category (folders: mostly)", "Size (bytes)", "Size", "On disk (bytes)", "Share", "Files", "Created", "Modified", "Accessed"], rows);
    }

    /// <summary>Saves the current view's table. Big tables are written on a background thread.</summary>
    [RelayCommand(CanExecute = nameof(CanExport), AllowConcurrentExecutions = false)]
    async Task Snapshot()
    {
        try
        {
            var table = CurrentSnapshot();
            if (table is null)
            {
                StatusText = "Nothing to snapshot in this view yet.";
                return;
            }
            string folder = Settings.SnapshotFolder;
            var now = DateTime.Now;
            if (table.Rows.Count > 5_000)
            {
                StatusText = $"Saving snapshot ({table.Rows.Count:N0} rows)…";
                LastSnapshot = await Task.Run(() => Snapshots.Write(folder, table, now));
            }
            else
            {
                LastSnapshot = Snapshots.Write(folder, table, now);
            }
            StatusText = $"Snapshot saved ({table.Rows.Count:N0} rows): {LastSnapshot}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error("Snapshot failed", ex);
            StatusText = $"Snapshot failed: {ex.Message}. Check the snapshot folder in Settings.";
        }
    }

    CancellationTokenSource? _batchCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnapshotAllWorkFilesCommand))]
    private bool _isBatchRunning;

    bool CanSnapshotAll() => State == AppState.Done && !IsBatchRunning;

    /// <summary>One CSV per work-file group (current scope) plus a summary, in a new timestamped folder.</summary>
    [RelayCommand(CanExecute = nameof(CanSnapshotAll))]
    async Task SnapshotAllWorkFiles()
    {
        var cts = _batchCts = new CancellationTokenSource();
        IsBatchRunning = true;
        try
        {
            var progress = new Progress<string>(m => StatusText = m);
            string? folder = await WorkFiles.SnapshotAllGroupsAsync(progress, cts.Token);
            if (folder is not null)
            {
                LastSnapshot = folder;
                StatusText = $"Saved a snapshot of every work-file group to {folder}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Snapshot of all work-file groups was cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error("Snapshot of all groups failed", ex);
            StatusText = $"Snapshot failed: {ex.Message}. Check the snapshot folder in Settings.";
        }
        finally
        {
            IsBatchRunning = false;
        }
    }

    [RelayCommand]
    void OpenSnapshotFolder()
    {
        Try("open the snapshot folder", () =>
        {
            Directory.CreateDirectory(Settings.SnapshotFolder);
            if (LastSnapshot is { } last && File.Exists(last))
                Shell.Reveal(last);
            else
                Shell.Open(Settings.SnapshotFolder);
        });
    }

    [RelayCommand]
    void OpenSettings()
    {
        var copy = Settings.Clone();
        var window = new SettingsWindow { Owner = Application.Current.MainWindow, DataContext = copy };
        if (window.ShowDialog() != true)
            return;
        string? error = copy.Save();
        bool authorsTurnedOn = copy.ReadAuthors && !Settings.ReadAuthors;
        Settings = copy;
        OnPropertyChanged(nameof(Settings));
        StatusText = error is null ? $"Settings saved. Snapshots go to {copy.SnapshotFolder}" : $"Settings could not be saved: {error}";
        if (authorsTurnedOn && _analysis is not null)
            WorkFiles.RefreshGroups(keepSelection: true);
    }

    // ---------- treemap / sunburst navigation ----------

    [ObservableProperty]
    private int _treemapRoot;

    [ObservableProperty]
    private int _sunburstRoot;

    public ObservableCollection<Crumb> TreemapCrumbs { get; } = [];
    public ObservableCollection<Crumb> SunburstCrumbs { get; } = [];

    partial void OnTreemapRootChanged(int value)
    {
        FillCrumbs(TreemapCrumbs, value);
        TreemapUpCommand.NotifyCanExecuteChanged();
    }

    partial void OnSunburstRootChanged(int value)
    {
        FillCrumbs(SunburstCrumbs, value);
        SunburstUpCommand.NotifyCanExecuteChanged();
    }

    void FillCrumbs(ObservableCollection<Crumb> crumbs, int root)
    {
        crumbs.Clear();
        if (Tree is null || root < 0 || root >= Tree.Count)
            return;
        foreach (int n in Tree.PathFromRoot(root))
            crumbs.Add(new Crumb(n, n == ScanTree.RootId ? Tree.RootPath.TrimEnd('\\') : Tree.Name(n)));
    }

    [RelayCommand]
    void GoToTreemapRoot(int node) => TreemapRoot = node;

    [RelayCommand(CanExecute = nameof(CanTreemapUp))]
    void TreemapUp()
    {
        if (Tree is not null && TreemapRoot > 0)
            TreemapRoot = Tree.Parent(TreemapRoot);
    }

    bool CanTreemapUp() => TreemapRoot > 0;

    [RelayCommand]
    void GoToSunburstRoot(int node) => SunburstRoot = node;

    [RelayCommand(CanExecute = nameof(CanSunburstUp))]
    void SunburstUp()
    {
        if (Tree is not null && SunburstRoot > 0)
            SunburstRoot = Tree.Parent(SunburstRoot);
    }

    bool CanSunburstUp() => SunburstRoot > 0;

    // ---------- startup ----------

    public void RefreshDrives()
    {
        Drives.Clear();
        foreach (var d in DriveItem.Enumerate())
            Drives.Add(d);
        SelectedDrive = Drives.FirstOrDefault(d => d.RootPath.StartsWith("C", StringComparison.OrdinalIgnoreCase)) ?? Drives.FirstOrDefault();
    }

    /// <summary>Handles "--fast --target X" from an elevated restart, and a plain path argument.</summary>
    public void ApplyStartupArguments(string[] args)
    {
        int t = Array.FindIndex(args, a => a.Equals("--target", StringComparison.OrdinalIgnoreCase));
        string? target = t >= 0 && t + 1 < args.Length ? args[t + 1] : args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(target))
        {
            try { TargetPath = ScanPaths.Normalize(target); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        bool fast = args.Any(a => a.Equals("--fast", StringComparison.OrdinalIgnoreCase));
        if (fast && IsElevated)
            _ = RunScanAsync(fast: true);
        else if (target is not null)
            _ = RunScanAsync(fast: false);
    }

    // ---------- scanning ----------

    bool CanScan() => State != AppState.Scanning;

    [RelayCommand(CanExecute = nameof(CanScan))]
    Task Scan() => RunScanAsync(fast: false);

    [RelayCommand]
    Task ScanDrive(DriveItem? drive)
    {
        if (drive is null)
            return Task.CompletedTask;
        SelectedDrive = drive;
        TargetPath = drive.RootPath;
        return RunScanAsync(fast: false);
    }

    bool CanFastScan()
    {
        if (State == AppState.Scanning)
            return false;
        try
        {
            return ScanPaths.IsDriveRoot(TargetPath) && VolumeInfo.TryGet(TargetPath) is { IsNtfs: true };
        }
        catch (Exception)
        {
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanFastScan))]
    async Task FastScan()
    {
        if (IsElevated)
        {
            await RunScanAsync(fast: true);
            return;
        }
        var answer = MessageBox.Show(
            "The fast scan reads the drive's NTFS file table directly. It takes seconds instead of minutes and also sees protected folders, " +
            "but Windows only allows it for administrators.\n\nRestart DiskInspector as administrator and run the fast scan?",
            "Fast scan", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK)
            return;
        if (Shell.RestartElevated(ScanPaths.Normalize(TargetPath)))
            Application.Current.Shutdown();
        else
            StatusText = "The administrator prompt was cancelled. You can still use the normal scan.";
    }

    [RelayCommand(CanExecute = nameof(IsScanning))]
    void Cancel()
    {
        _cts?.Cancel();
        ProgressHeadline = "Stopping…";
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    void NewScan()
    {
        ClearResults();
        RefreshDrives();
        State = AppState.Start;
        StatusText = "Choose a drive or folder, then Scan.";
    }

    [RelayCommand]
    void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to scan", Multiselect = false };
        try
        {
            if (Directory.Exists(TargetPath))
                dialog.InitialDirectory = TargetPath;
        }
        catch (Exception) { }
        if (dialog.ShowDialog() == true)
            TargetPath = dialog.FolderName;
    }

    async Task RunScanAsync(bool fast)
    {
        if (State == AppState.Scanning)
            return;

        string target;
        try
        {
            target = ScanPaths.Normalize(TargetPath);
            if (!ScanPaths.Exists(target))
            {
                MessageBox.Show($"The folder \"{TargetPath}\" was not found.", "DiskInspector", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            MessageBox.Show($"\"{TargetPath}\" is not a valid folder path.", "DiskInspector", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var previous = State;
        ClearResults();
        ReleaseMemory();
        State = AppState.Scanning;
        Notice = null;
        ProgressHeadline = fast ? $"Reading the file table of {target}" : target == ScanTree.ComputerRoot ? "Scanning all drives" : $"Scanning {target}";
        ProgressDetail = "Starting…";
        ProgressPath = "";
        ProgressIndeterminate = !fast;
        ProgressValue = 0;
        StatusText = "Scanning…";
        var cts = _cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(OnProgress);

        try
        {
            var result = await ScanService.RunAsync(target, fast, progress, cts.Token);
            ProgressHeadline = "Analyzing…";
            ProgressDetail = $"{result.Tree.FileCount(0):N0} files found";
            ProgressPath = "";
            ProgressIndeterminate = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var analysis = await Task.Run(() => Analysis.Run(result, Categorizer), cts.Token);
            Log.Info($"Analysis took {sw.ElapsedMilliseconds} ms");
            sw.Restart();
            ApplyResults(analysis);
            State = AppState.Done;
            Log.Info($"Showing results took {sw.ElapsedMilliseconds} ms");
            var idle = System.Diagnostics.Stopwatch.StartNew();
            _ = Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Log.Info($"UI ready {idle.ElapsedMilliseconds} ms after results");
                // The scan leaves a lot of short-lived garbage behind; give that memory back to Windows once.
                ReleaseMemory();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch (OperationCanceledException)
        {
            State = previous == AppState.Done ? AppState.Start : previous;
            if (State == AppState.Scanning)
                State = AppState.Start;
            StatusText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error("Scan failed", ex);
            State = AppState.Start;
            StatusText = "The scan failed.";
            MessageBox.Show($"The scan could not finish:\n\n{ex.Message}\n\nDetails were written to {Log.CurrentFile}.",
                "DiskInspector", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            cts.Dispose();
            if (_cts == cts)
                _cts = null;
        }
    }

    void OnProgress(ScanProgress p)
    {
        if (State != AppState.Scanning || ProgressHeadline == "Stopping…")
            return;
        ProgressDetail = $"{p.Files:N0} files · {p.Directories:N0} folders · {Format.Bytes(p.Bytes)} · {Format.Duration(p.Elapsed)}";
        ProgressPath = p.CurrentPath;
        if (p.Fraction >= 0)
        {
            ProgressIndeterminate = false;
            ProgressValue = p.Fraction * 100;
        }
    }

    void ClearResults()
    {
        _analysis = null;
        _scopeCts?.Cancel();
        Scope = null;
        ScopeRoot = 0;
        SelectedNode = -1;
        HoveredNode = -1;
        TreemapRoot = 0;
        SunburstRoot = 0;
        Colors = null;
        Tree = null;
        Result = null;
        Details = null;
        Issues = [];
        Folders.Clear();
        Types.Load(null);
        Categories.Load(null);
        Largest.Load(null);
        Locations.Load(null);
        Ages.Load(null);
        WorkFiles.Load(null);
    }

    void ApplyResults(Analysis a)
    {
        _analysis = a;
        Scope = a.Everything;
        var r = a.Result;
        Result = r;
        Colors = a.Colors;
        Tree = r.Tree;
        TreemapRoot = 0;
        SunburstRoot = 0;
        FillCrumbs(TreemapCrumbs, 0);
        FillCrumbs(SunburstCrumbs, 0);
        Legend = a.Categories.Where(c => c.Size > 0).Select(c => c.Category).ToList();
        Issues = r.Issues;
        Notice = r.Notice
                 ?? (Categorizer.LoadError is { } err ? $"Your categories file was ignored: {err}" : null)
                 ?? (Core.Analysis.WorkFiles.LoadError is { } werr ? $"Your work-file groups file was ignored: {werr}" : null);

        Timed("folders", () => Folders.Load(a));
        Timed("types", () => Types.Load(a));
        Timed("categories", () => Categories.Load(a));
        Timed("largest", () => Largest.Load(a));
        Timed("locations", () => Locations.Load(a));
        Timed("ages", () => Ages.Load(a));
        Timed("work files", () => WorkFiles.Load(a));
        Timed("select", () => SelectedNode = ScanTree.RootId);

        var t = r.Tree;
        string mode = r.Mode == ScanMode.Mft ? "Fast scan (NTFS file table)" : "Normal scan";
        string volume = r.IsWholeVolume && r.Volume is { } v
            ? $" · Windows reports {Format.Bytes(v.UsedBytes)} used of {Format.Bytes(v.TotalBytes)}"
            : "";
        string unreadable = r.Issues.Count > 0 ? $" · {r.Issues.Count:N0} folders could not be read" : "";
        StatusText = $"{mode} of {t.RootPath} in {Format.Duration(r.Duration)} · {t.FileCount(0):N0} files, {t.DirectoryCount(0):N0} folders · " +
                     $"{Format.Bytes(t.Size(0))} ({Format.Bytes(t.Allocated(0))} on disk){volume}{unreadable}";
    }

    static void ReleaseMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    static void Timed(string what, Action action)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        action();
        if (sw.ElapsedMilliseconds > 50)
            Log.Info($"Slow step: {what} took {sw.ElapsedMilliseconds} ms");
    }

    void UpdateDetails()
    {
        if (_analysis is null || SelectedNode < 0 || SelectedNode >= _analysis.Tree.Count)
        {
            Details = null;
            return;
        }
        Details = NodeDetails.Create(_analysis.Result, _analysis.Colors, SelectedNode, Metric);
    }

    void UpdateHover()
    {
        var tree = Tree;
        if (tree is null || HoveredNode < 0 || HoveredNode >= tree.Count)
            return;
        long size = tree.GetSize(HoveredNode, Metric);
        long total = tree.GetSize(0, Metric);
        HoverText = $"{tree.GetFullPath(HoveredNode)}   ·   {Format.Bytes(size)}   ·   {Format.Percent(total > 0 ? (double)size / total : 0)} of everything" +
                    (tree.IsDirectory(HoveredNode) ? $"   ·   {tree.FileCount(HoveredNode):N0} files" : "");
    }

    // ---------- actions on the selection ----------

    bool HasSelection() => Tree is not null && SelectedNode >= 0 && SelectedNode < Tree.Count;

    bool CanOpenSelected() => HasSelection() && Shell.CanOpen(Tree!, SelectedNode);

    bool IsComputerRootSelected => Tree is { IsComputerRoot: true } && SelectedNode == ScanTree.RootId;

    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    void OpenSelected() => Try("open", () =>
    {
        if (IsComputerRootSelected)
            Shell.OpenThisPc();
        else
            Shell.Open(Tree!.GetFullPath(SelectedNode));
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    void RevealSelected() => Try("show in Explorer", () =>
    {
        if (IsComputerRootSelected)
            Shell.OpenThisPc();
        else
            Shell.Reveal(Tree!.GetFullPath(SelectedNode));
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    void CopySelectedPath()
    {
        string path = Tree!.GetFullPath(SelectedNode);
        StatusText = Shell.CopyText(path) ? $"Copied: {path}" : "Could not copy: the clipboard is busy. Try again.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    void ShowInTreemap()
    {
        var tree = Tree!;
        int node = SelectedNode;
        TreemapRoot = tree.IsDirectory(node) && tree.ChildCount(node) > 0 ? node : Math.Max(0, tree.Parent(node));
        ShowView("treemap");
    }

    void Try(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not {what}", ex);
            StatusText = $"Could not {what}: {ex.Message}";
        }
    }

    // ---------- export ----------

    bool CanExport() => State == AppState.Done;

    [RelayCommand(CanExecute = nameof(CanExport))]
    async Task Export(string kind)
    {
        var a = _analysis;
        if (a is null)
            return;
        string root = new string(a.Tree.RootPath.Where(char.IsLetterOrDigit).ToArray());
        var dialog = new SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"DiskInspector-{root}-{kind}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Title = "Export to CSV",
        };
        if (dialog.ShowDialog() != true)
            return;
        string file = dialog.FileName;
        StatusText = "Exporting…";
        try
        {
            var nodes = Largest.CurrentNodes;
            await Task.Run(() =>
            {
                switch (kind)
                {
                    case "folders": CsvExport.WriteFolders(a.Tree, file); break;
                    case "types": CsvExport.WriteTypes(a.Extensions, file); break;
                    default: CsvExport.WriteFiles(a.Tree, nodes, file); break;
                }
            });
            StatusText = $"Exported to {file}";
        }
        catch (Exception ex)
        {
            Log.Error("Export failed", ex);
            StatusText = $"Export failed: {ex.Message}";
        }
    }
}
