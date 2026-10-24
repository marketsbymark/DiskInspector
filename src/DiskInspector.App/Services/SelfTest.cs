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
                vm.SelectedViewIndex = 0;
                vm.TreemapRoot = biggestDir;
                await Idle(2500);
                Shot(window, outDir, "10-treemap-zoomed");
                Note($"treemap zoomed to {tree.Name(biggestDir)}; crumbs={vm.TreemapCrumbs.Count}");
                vm.SunburstRoot = biggestDir;
                vm.SelectedViewIndex = 1;
                await Idle(2000);
                Shot(window, outDir, "11-sunburst-zoomed");
            }
            vm.UseAllocated = true;
            vm.SelectedViewIndex = 2;
            await Idle(1200);
            Shot(window, outDir, "12-folders-on-disk");
            Note($"metric switched; folder rows={vm.Folders.Rows.Count}");
            vm.UseAllocated = false;

            vm.Largest.NameFilter = "log";
            vm.SelectedViewIndex = 6;
            await Idle(2000);
            Shot(window, outDir, "13-largest-filtered");
            Note($"largest filter 'log': {vm.Largest.Summary}");

            vm.Categories.SelectedCategory = vm.Categories.Rows.FirstOrDefault(r => r.Name == "Video") ?? vm.Categories.Rows.LastOrDefault();
            vm.SelectedViewIndex = 4;
            await Idle(1500);
            Shot(window, outDir, "14-categories-drill");
            Note($"category drill: {vm.Categories.FilesTitle} ({vm.Categories.Files.Count} files)");

            if (!NoShots)
                window.WindowState = WindowState.Maximized;
            vm.SelectedViewIndex = 0;
            await Idle(2500);
            Shot(window, outDir, "15-treemap-maximized");
            if (!NoShots)
                window.WindowState = WindowState.Normal;
            window.Width = 980;
            window.Height = 640;
            await Idle(2000);
            Shot(window, outDir, "16-treemap-small");

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
            vm.SelectedViewIndex = 5;
            await Idle(800);
            Shot(window, outDir, "17-folder-scan-locations");
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

    /// <summary>Copies the window as it appears on screen (including the Mica backdrop) to a PNG.</summary>
    static bool NoShots;

    static void Shot(Window window, string outDir, string name)
    {
        if (NoShots)
            return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        window.Activate();
        SetForegroundWindow(hwnd);
        if (!GetWindowRect(hwnd, out var r))
            return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        nint screen = GetDC(0), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, h);
        try
        {
            var old = SelectObject(mem, bmp);
            BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020 /* SRCCOPY */);
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
