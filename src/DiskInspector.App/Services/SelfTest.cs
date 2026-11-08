using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiskInspector.App.ViewModels;
using DiskInspector.Core.Model;

namespace DiskInspector.App.Services;

/// <summary>
/// "--selftest &lt;outDir&gt; [--target X] [--fast]": scans, visits every view, exercises the main interactions,
/// saves a PNG of each screen and a report, then exits. Used to verify builds end to end.
/// </summary>
public static class SelfTest
{
    public static bool TryStart(string[] args, MainViewModel vm, Window window)
    {
        int i = Array.FindIndex(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length)
            return false;
        string outDir = args[i + 1];
        Directory.CreateDirectory(outDir);
        NoShots = args.Contains("--no-shots", StringComparer.OrdinalIgnoreCase);
        // Report any UI-thread operation that blocks for a noticeable time.
        var started = new Dictionary<DispatcherOperation, long>();
        var nameProp = typeof(DispatcherOperation).GetProperty("Name", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        window.Dispatcher.Hooks.OperationStarted += (_, e) => started[e.Operation] = Stopwatch.GetTimestamp();
        window.Dispatcher.Hooks.OperationCompleted += (_, e) =>
        {
            if (started.Remove(e.Operation, out long t0))
            {
                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (ms > 100)
                    Log.Info($"selftest: slow UI operation {ms:0} ms: {nameProp?.GetValue(e.Operation)} ({e.Operation.Priority})");
            }
        };
        _ = RunAsync(outDir, args, vm, window);
        return true;
    }

    static async Task RunAsync(string outDir, string[] args, MainViewModel vm, Window window)
    {
        var report = new List<string>();
        var clock = Stopwatch.StartNew();
        void Note(string s)
        {
            report.Add($"{clock.Elapsed.TotalSeconds,7:0.0}s  {s}");
            Log.Info("selftest: " + s);
        }

        try
        {
            await Idle(800);
            Shot(window, outDir, "00-start");
            int t = Array.FindIndex(args, a => a.Equals("--target", StringComparison.OrdinalIgnoreCase));
            if (t >= 0 && t + 1 < args.Length)
                vm.TargetPath = args[t + 1];
            bool fast = args.Contains("--fast", StringComparer.OrdinalIgnoreCase);
            Note($"scan {vm.TargetPath} fast={fast} elevated={vm.IsElevated}");

            var scan = fast ? vm.FastScanCommand.ExecuteAsync(null) : vm.ScanCommand.ExecuteAsync(null);
            await Idle(1500);
            Shot(window, outDir, "01-scanning");
            Note($"during scan: scan enabled={vm.ScanCommand.CanExecute(null)}, fast enabled={vm.FastScanCommand.CanExecute(null)}, " +
                 $"cancel enabled={vm.CancelCommand.CanExecute(null)}, new-scan enabled={vm.NewScanCommand.CanExecute(null)}");
            await scan;
            Note($"state={vm.State} status={vm.StatusText}");
            if (vm.Notice is not null)
                Note("notice: " + vm.Notice);
            if (vm.State != AppState.Done || vm.Tree is null)
                throw new InvalidOperationException("Scan did not complete");

            ScanTree? tree = vm.Tree;
            var names = vm.Views.Select(v => v.Title).ToArray();
            for (int v = 0; v < names.Length; v++)
            {
                vm.SelectedViewIndex = v;
                await Idle(v < 2 ? 2500 : 900);
                Shot(window, outDir, $"{v + 2:00}-{names[v].Replace(' ', '-').ToLowerInvariant()}");
                Note($"view {names[v]} ok");
            }

            // Interactions: select the biggest file, zoom the treemap into the biggest folder, switch metric, expand folders.
            int biggestDir = tree.Children(ScanTree.RootId).ToArray().FirstOrDefault(c => tree.IsDirectory(c), -1);
            int biggestFile = Core.Analysis.FileQueries.Largest(tree, 1, SizeMetric.Logical).FirstOrDefault(-1);
            if (biggestFile >= 0)
            {
                vm.SelectedNode = biggestFile;
                Note($"selected {tree.GetFullPath(biggestFile)}; details={vm.Details?.SizeText}");
            }
            if (biggestDir >= 0)
            {
                vm.ShowView("treemap");
                vm.TreemapRoot = biggestDir;
                await Idle(2500);
                Shot(window, outDir, "10-treemap-zoomed");
                Note($"treemap zoomed to {tree.Name(biggestDir)}; crumbs={vm.TreemapCrumbs.Count}");
                vm.SunburstRoot = biggestDir;
                vm.ShowView("sunburst");
                await Idle(2000);
                Shot(window, outDir, "11-sunburst-zoomed");
            }
            vm.UseAllocated = true;
            vm.ShowView("folders");
            await Idle(1200);
            Shot(window, outDir, "12-folders-on-disk");
            Note($"metric switched; folder rows={vm.Folders.Rows.Count}");
            vm.UseAllocated = false;

            vm.Largest.NameFilter = "log";
            vm.ShowView("largest");
            await Idle(2000);
            Shot(window, outDir, "13-largest-filtered");
            Note($"largest filter 'log': {vm.Largest.Summary}");

            vm.Categories.SelectedCategory = vm.Categories.Rows.FirstOrDefault(r => r.Name == "Video") ?? vm.Categories.Rows.LastOrDefault();
            vm.ShowView("categories");
            await Idle(1500);
            Shot(window, outDir, "14-categories-drill");
            Note($"category drill: {vm.Categories.FilesTitle} ({vm.Categories.Files.Count} files)");

            // Work files: each group, one type on its own, authors, snapshots.
            string snapDir = Path.Combine(outDir, "snapshots");
            vm.Settings.SnapshotFolder = snapDir;   // not saved: the test never touches the user's settings file
            vm.ShowView("work");
            await WaitFor(() => vm.WorkFiles.Groups.Count > 0 && !vm.WorkFiles.IsBusy, 20000);
            foreach (var g in vm.WorkFiles.Groups)
            {
                vm.WorkFiles.SelectedGroup = g;
                await WaitFor(() => !vm.WorkFiles.IsBusy, 20000);
                Note($"work files {g.Label}: {vm.WorkFiles.Summary}");
            }
            // Typed extensions, then every file, in "Any types or all files".
            vm.WorkFiles.SelectedGroup = vm.WorkFiles.Groups.First(g => g.Group.IsCustom);
            vm.WorkFiles.CustomTypes = "json, *.toml .sql";
            await Idle(800);
            await WaitFor(() => !vm.WorkFiles.IsBusy, 20000);
            Note($"work files typed json/toml/sql: {vm.WorkFiles.Summary}; types {string.Join(" ", vm.WorkFiles.Types.Skip(1).Select(t => t.Label))}");
            vm.WorkFiles.CustomTypes = "";
            await Idle(800);
            await WaitFor(() => !vm.WorkFiles.IsBusy, 30000);
            Note($"work files all files: {vm.WorkFiles.Summary}; {vm.WorkFiles.Types.Count - 1} types offered");
            Shot(window, outDir, "15a-work-files-any-type");
            if (vm.WorkFiles.FoundCount > vm.WorkFiles.Rows.Count)
            {
                // More files than the table shows: the snapshot must still hold every one.
                vm.Settings.SnapshotFolder = snapDir;
                await vm.SnapshotCommand.ExecuteAsync(null);
                int allLines = vm.LastSnapshot is { } big && File.Exists(big) ? File.ReadLines(big).Count() - 1 : -1;
                Note($"all-files snapshot rows {allLines:N0} of {vm.WorkFiles.FoundCount:N0} found (table {vm.WorkFiles.Rows.Count:N0}): {vm.StatusText}");
                if (allLines != vm.WorkFiles.FoundCount)
                    throw new InvalidOperationException("All-files snapshot is missing rows");
            }
            vm.WorkFiles.SelectedGroup = vm.WorkFiles.Groups.First(g => g.Group.Key == "excel");
            await WaitFor(() => !vm.WorkFiles.IsBusy, 20000);
            var xlsx = vm.WorkFiles.Types.FirstOrDefault(t => t.Extension == "xlsx" && !t.IsAll);
            if (xlsx is not null)
            {
                vm.WorkFiles.SelectedType = xlsx;
                await WaitFor(() => !vm.WorkFiles.IsBusy, 20000);
            }
            await WaitFor(() => !vm.WorkFiles.IsReadingAuthors, 60000);
            int withAuthor = vm.WorkFiles.Rows.Count(r => r.Author.Length > 0);
            Note($"work files .xlsx: {vm.WorkFiles.Summary}; with author {withAuthor:N0}; {vm.WorkFiles.AuthorStatus}");
            vm.WorkFiles.SortBy("Size", System.ComponentModel.ListSortDirection.Descending);
            await Idle(1500);
            Shot(window, outDir, "15-work-files");
            await vm.SnapshotCommand.ExecuteAsync(null);
            Note($"snapshot: {vm.StatusText}");
            if (vm.LastSnapshot is null || !File.Exists(vm.LastSnapshot))
                throw new InvalidOperationException("Snapshot file was not written");
            int lines = File.ReadAllLines(vm.LastSnapshot).Length;
            Note($"snapshot rows {lines - 1:N0} (table rows {vm.WorkFiles.Rows.Count:N0})");
            if (lines - 1 != vm.WorkFiles.Rows.Count)
                throw new InvalidOperationException("Snapshot row count does not match the table");

            // Every view can be snapshotted.
            foreach (var view in vm.Views)
            {
                vm.ShowView(view.Key);
                await Idle(400);
                await vm.SnapshotCommand.ExecuteAsync(null);
                Note($"snapshot {view.Title}: {(vm.StatusText.StartsWith("Snapshot saved") ? "ok" : vm.StatusText)}");
            }

            // Scope to the user profile, then snapshot every work-file group.
            int profile = tree.FindByPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (profile > 0)
            {
                vm.SelectedNode = profile;
                vm.ScopeToSelectionCommand.Execute(null);
                await WaitFor(() => vm.Scope?.Root == profile && !vm.WorkFiles.IsBusy, 20000);
                vm.ShowView("types");
                await Idle(1500);
                Shot(window, outDir, "16-types-scoped");
                Note($"scoped to {vm.ScopeText}: {vm.Types.Rows.Count:N0} types, largest files {vm.Largest.Summary}");
            }
            await vm.SnapshotAllWorkFilesCommand.ExecuteAsync(null);
            Note($"snapshot all groups: {vm.StatusText}");
            if (vm.LastSnapshot is null || !Directory.Exists(vm.LastSnapshot) || Directory.GetFiles(vm.LastSnapshot, "*.csv").Length != vm.WorkFiles.GroupDefinitions.Count(g => !g.IsCustom) + 1)
                throw new InvalidOperationException("Snapshot of all groups is incomplete");
            vm.ScopeToEverythingCommand.Execute(null);
            await WaitFor(() => vm.Scope?.Root == 0, 20000);

            if (!NoShots)
                window.WindowState = WindowState.Maximized;
            vm.ShowView("treemap");
            await Idle(2500);
            Shot(window, outDir, "17-treemap-maximized");
            if (!NoShots)
                window.WindowState = WindowState.Normal;
            window.Width = 980;
            window.Height = 640;
            await Idle(2000);
            Shot(window, outDir, "18-treemap-small");

            Note($"working set {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB, live managed heap {GC.GetTotalMemory(true) / (1024 * 1024)} MB");

            tree = null;   // don't keep the old scan alive while checking that rescans free it

            // Cancel a scan part-way, then scan a single folder.
            string target = vm.TargetPath;
            var again = vm.ScanCommand.ExecuteAsync(null);
            await Task.Delay(700);
            vm.CancelCommand.Execute(null);
            await again;
            Note($"after cancel: state={vm.State} status={vm.StatusText}");
            if (vm.State != AppState.Start)
                throw new InvalidOperationException("Cancel did not return to the start page");

            vm.TargetPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\System32\drivers";
            Note($"folder target: fast enabled={vm.FastScanCommand.CanExecute(null)} (expected False)");
            await vm.ScanCommand.ExecuteAsync(null);
            Note($"folder scan: state={vm.State} root={vm.Tree?.RootPath} files={vm.Tree?.FileCount(0)}");
            for (int v = 0; v < vm.Views.Count; v++)
            {
                var sw = Stopwatch.StartNew();
                vm.SelectedViewIndex = v;
                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Note($"after folder scan, switch to {vm.Views[v].Title}: {sw.ElapsedMilliseconds} ms");
                await Idle(300);
            }
            vm.ShowView("locations");
            await Idle(800);
            Shot(window, outDir, "19-folder-scan-locations");
            if (vm.State != AppState.Done)
                throw new InvalidOperationException("Folder scan failed");
            vm.TargetPath = target;
            Note($"working set after rescans {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB, live managed heap {GC.GetTotalMemory(true) / (1024 * 1024)} MB");
            Note("PASS");
        }
        catch (Exception ex)
        {
            Note("FAIL: " + ex);
        }
        finally
        {
            File.WriteAllLines(Path.Combine(outDir, "report.txt"), report);
            Application.Current.Shutdown();
        }
    }

    static async Task WaitFor(Func<bool> condition, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        await Idle(200);
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Timed out waiting for the UI");
            await Idle(200);
        }
    }

    static async Task Idle(int ms)
    {
        await Task.Delay(ms);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetForegroundWindow(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern nint GetDC(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int ReleaseDC(nint hwnd, nint hdc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern nint CreateCompatibleDC(nint hdc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern nint SelectObject(nint hdc, nint obj);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int rop);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern bool DeleteObject(nint obj);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern bool DeleteDC(nint hdc);

    struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);

    static bool NoShots;

    /// <summary>
    /// Saves the app window to a PNG. PrintWindow renders only this window, so whatever else is on the
    /// screen (or covering the window) is never captured.
    /// </summary>
    static void Shot(Window window, string outDir, string name)
    {
        if (NoShots)
            return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (!GetWindowRect(hwnd, out var r))
            return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        nint screen = GetDC(0), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, h);
        try
        {
            var old = SelectObject(mem, bmp);
            PrintWindow(hwnd, mem, 2 /* PW_RENDERFULLCONTENT */);
            SelectObject(mem, old);
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bmp, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var fs = File.Create(Path.Combine(outDir, name + ".png"));
            enc.Save(fs);
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }
}
