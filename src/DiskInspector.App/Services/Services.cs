using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using DiskInspector.Core.Model;
using DiskInspector.Core.Scanning;

namespace DiskInspector.App.Services;

/// <summary>Writes to %LOCALAPPDATA%\DiskInspector\logs. Never throws.</summary>
public static class Log
{
    static readonly object Gate = new();

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskInspector", "logs");

    public static string CurrentFile => Path.Combine(Folder, $"app-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(CurrentFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never take the app down.
        }
    }
}

public static class Shell
{
    // Opening these would run a program or script, so "Open" is disabled for them.
    static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "com", "bat", "cmd", "msi", "msp", "msu", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "scr", "pif",
        "lnk", "url", "reg", "hta", "cpl", "msc", "appref-ms", "application", "jar", "py", "pyw", "appx", "msix", "appxbundle",
        "msixbundle", "inf", "sys", "dll", "ocx", "drv", "efi", "library-ms", "settingcontent-ms", "diagcab", "theme", "themepack",
    };

    public static bool CanOpen(ScanTree tree, int node) =>
        tree.IsDirectory(node) || !Runnable.Contains(tree.Extension(node));

    public static void Open(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    public static void OpenThisPc() =>
        Process.Start(new ProcessStartInfo("explorer.exe", "shell:MyComputerFolder") { UseShellExecute = true })?.Dispose();

    public static void Reveal(string path)
    {
        if (ScanPaths.IsDriveRoot(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        else
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Copies text, retrying because another app may briefly hold the clipboard.</summary>
    public static bool CopyText(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }
        return false;
    }

    /// <summary>Starts a new elevated copy that runs the fast scan. False if the user declined the UAC prompt.</summary>
    public static bool RestartElevated(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--fast --target \"{target.TrimEnd('\\')}\"",
            })?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;   // UAC prompt cancelled
        }
    }
}

public static class ScanService
{
    /// <summary>Runs the fast scan if asked and possible, otherwise (or if it fails) the normal one.</summary>
    public static async Task<ScanResult> RunAsync(string target, bool fast, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        Log.Info($"Scan started: {target} (fast={fast}, elevated={Elevation.IsElevated()})");
        if (fast)
        {
            try
            {
                var r = await new MftScanner().ScanAsync(target, progress, ct);
                Log.Info($"Fast scan finished: {r.Tree.Count:N0} items in {r.Duration}");
                return r;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("Fast scan failed; falling back to the normal scan.", ex);
                var r = await new StandardScanner().ScanAsync(target, progress, ct);
                r.Notice = $"The fast scan could not run ({ex.Message}), so the normal scan was used instead.";
                return r;
            }
        }
        var result = await new StandardScanner().ScanAsync(target, progress, ct);
        Log.Info($"Scan finished: {result.Tree.Count:N0} items in {result.Duration}, {result.Issues.Count} unreadable folders");
        return result;
    }
}
