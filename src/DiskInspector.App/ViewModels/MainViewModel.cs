using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskInspector.App.Services;
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
        Folders = new FolderTreeViewModel(this);
        Types = new TypesViewModel(this);
        Categories = new CategoriesViewModel(this);
        Largest = new LargestFilesViewModel(this);
        Locations = new LocationsViewModel(this);
        Ages = new AgeViewModel(this);
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
    public bool IsElevated { get; } = Elevation.IsElevated();
    public string FastScanLabel => "Fast scan";
    public string WindowTitle => IsElevated ? "DiskInspector (Administrator)" : "DiskInspector";

    public ObservableCollection<DriveItem> Drives { get; } = [];

    public IReadOnlyList<NavItem> Views { get; } =
    [
        new("Treemap", "\uF0E2", "Every file as a box sized by space"),
        new("Sunburst", "\uEB05", "Folders as rings around the center"),
        new("Folders", "\uE8B7", "Folder tree sorted by size"),
        new("File types", "\uE8A5", "Space by file extension"),
        new("Categories", "\uE8FD", "Space by kind of file"),
        new("Locations", "\uE707", "Known places: Windows, programs, users"),
        new("Largest files", "\uE8CB", "Biggest single files, with filters"),
        new("Age", "\uE823", "Space by when files last changed"),
    ];

    // ---------- state ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStart), nameof(IsScanning), nameof(IsDone))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(FastScanCommand), nameof(CancelCommand), nameof(ExportCommand), nameof(NewScanCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand), nameof(RevealSelectedCommand), nameof(CopySelectedPathCommand), nameof(ShowInTreemapCommand))]
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
            if (!Directory.Exists(target))
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
        ProgressHeadline = fast ? $"Reading the file table of {target}" : $"Scanning {target}";
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
    }

    void ApplyResults(Analysis a)
    {
        _analysis = a;
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
        Notice = r.Notice ?? (Categorizer.LoadError is { } err ? $"Your categories file was ignored: {err}" : null);

        Timed("folders", () => Folders.Load(a));
        Timed("types", () => Types.Load(a));
        Timed("categories", () => Categories.Load(a));
        Timed("largest", () => Largest.Load(a));
        Timed("locations", () => Locations.Load(a));
        Timed("ages", () => Ages.Load(a));
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

    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    void OpenSelected() => Try("open", () => Shell.Open(Tree!.GetFullPath(SelectedNode)));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    void RevealSelected() => Try("show in Explorer", () => Shell.Reveal(Tree!.GetFullPath(SelectedNode)));

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
        SelectedViewIndex = 0;
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
