using System.Security.Principal;
using DiskInspector.Core.Model;

namespace DiskInspector.Core.Scanning;

public enum ScanMode
{
    Standard,
    Mft,
}

/// <summary>Live progress. <see cref="Fraction"/> is between 0 and 1, or -1 when the total is unknown.</summary>
public readonly record struct ScanProgress(long Files, long Directories, long Bytes, string CurrentPath, TimeSpan Elapsed, double Fraction);

public sealed record ScanIssue(string Path, string Message);

public sealed class ScanResult
{
    public required ScanTree Tree { get; init; }
    public required ScanMode Mode { get; init; }
    public required TimeSpan Duration { get; init; }
    public IReadOnlyList<ScanIssue> Issues { get; init; } = [];
    public VolumeInfo? Volume { get; init; }

    /// <summary>Message for the user, e.g. that the fast scan fell back to the normal one.</summary>
    public string? Notice { get; set; }

    /// <summary>True when the scan covered a whole drive, so volume totals can be compared with it.</summary>
    public bool IsWholeVolume => Volume is not null &&
        string.Equals(Volume.RootPath.TrimEnd('\\'), Tree.RootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}

public interface IScanner
{
    Task<ScanResult> ScanAsync(string path, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record VolumeInfo(string RootPath, string Label, string Format, long TotalBytes, long FreeBytes, int ClusterSize)
{
    public long UsedBytes => TotalBytes - FreeBytes;
    public bool IsNtfs => string.Equals(Format, "NTFS", StringComparison.OrdinalIgnoreCase);

    public static VolumeInfo? TryGet(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return null;
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return null;
            int cluster = 4096;
            if (Native.GetDiskFreeSpace(root, out uint spc, out uint bps, out _, out _) && spc > 0 && bps > 0)
                cluster = (int)(spc * bps);
            return new VolumeInfo(root, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.TotalFreeSpace, cluster);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

public static class ScanPaths
{
    /// <summary>Full path with no trailing backslash, except for a drive root ("C:\").</summary>
    public static string Normalize(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 2 && path[1] == ':')
            path += "\\";
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (root is not null && full.Length <= root.Length)
            return root.Length == 3 && root[1] == ':' ? root.ToUpperInvariant() : root;
        return full.TrimEnd('\\');
    }

    public static bool IsDriveRoot(string path)
    {
        string n = Normalize(path);
        return n.Length == 3 && n[1] == ':' && n[2] == '\\';
    }
}

public static class Elevation
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
