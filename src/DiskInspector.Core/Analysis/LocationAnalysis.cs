using DiskInspector.Core.Model;
using DiskInspector.Core.Scanning;

namespace DiskInspector.Core.Analysis;

/// <summary>A well-known place on the drive. <see cref="Node"/> is -1 for summary rows that are not a folder.</summary>
public sealed record LocationStat(string Group, string Name, string Path, int Level, int Node, long Size, long Allocated, long Files, string? Note = null);

public static class LocationAnalysis
{
    public const string SummaryGroup = "Summary";

    public static IReadOnlyList<LocationStat> Compute(ScanResult result)
    {
        var tree = result.Tree;
        var rows = new List<LocationStat>();
        string root = tree.RootPath.TrimEnd('\\');

        // Summary
        rows.Add(Row(tree, SummaryGroup, "Everything scanned", ScanTree.RootId, 0));
        if (result.IsWholeVolume && result.Volume is { } vol)
        {
            rows.Add(new LocationStat(SummaryGroup, "Drive used space (Windows)", vol.RootPath, 0, -1, vol.UsedBytes, vol.UsedBytes, 0));
            long gap = vol.UsedBytes - tree.Allocated(ScanTree.RootId);
            rows.Add(gap >= 0
                ? new LocationStat(SummaryGroup, "Not seen by the scan", "", 0, -1, gap, gap, 0,
                    result.Mode == ScanMode.Mft
                        ? "NTFS metadata, shadow copies and alternate data streams"
                        : "Protected folders, shadow copies, NTFS metadata and small files stored in the file table")
                : new LocationStat(SummaryGroup, "Counted more than once", "", 0, -1, -gap, -gap, 0,
                    "Hard-linked files (for example in WinSxS) are counted once per link by the normal scan"));
            rows.Add(new LocationStat(SummaryGroup, "Free space", vol.RootPath, 0, -1, vol.FreeBytes, vol.FreeBytes, 0));
        }
        int denied = result.Issues.Count;
        if (denied > 0)
            rows.Add(new LocationStat(SummaryGroup, $"Folders that could not be read: {denied:N0}", "", 0, -1, 0, 0, 0,
                "See the Inaccessible list in the status bar"));

        // System
        const string sys = "Windows & system";
        AddPath(rows, tree, sys, "Windows", root + @"\Windows", 0);
        AddPath(rows, tree, sys, "Component store (WinSxS)", root + @"\Windows\WinSxS", 1);
        AddPath(rows, tree, sys, "Installer cache", root + @"\Windows\Installer", 1);
        AddPath(rows, tree, sys, "Windows Update downloads", root + @"\Windows\SoftwareDistribution", 1);
        AddPath(rows, tree, sys, "Windows temp", root + @"\Windows\Temp", 1);
        AddPath(rows, tree, sys, "Drivers", root + @"\Windows\System32\DriverStore", 1);
        AddPath(rows, tree, sys, "Page file", root + @"\pagefile.sys", 0);
        AddPath(rows, tree, sys, "Hibernation file", root + @"\hiberfil.sys", 0);
        AddPath(rows, tree, sys, "Swap file", root + @"\swapfile.sys", 0);
        AddPath(rows, tree, sys, "Recycle Bin", root + @"\$Recycle.Bin", 0);
        AddPath(rows, tree, sys, "System Volume Information", root + @"\System Volume Information", 0);
        AddPath(rows, tree, sys, "Previous Windows (Windows.old)", root + @"\Windows.old", 0);
        AddPath(rows, tree, sys, "Upgrade files ($WINDOWS.~BT)", root + @"\$WINDOWS.~BT", 0);
        AddPath(rows, tree, sys, "Upgrade files ($WinREAgent)", root + @"\$WinREAgent", 0);

        // Programs
        const string programs = "Programs";
        AddPath(rows, tree, programs, "Program Files", root + @"\Program Files", 0);
        AddPath(rows, tree, programs, "Program Files (x86)", root + @"\Program Files (x86)", 0);
        AddPath(rows, tree, programs, "ProgramData", root + @"\ProgramData", 0);
        AddPath(rows, tree, programs, "Package cache", root + @"\ProgramData\Package Cache", 1);
        AddPath(rows, tree, programs, "Store apps (WindowsApps)", root + @"\Program Files\WindowsApps", 1);

        // Users
        int users = tree.FindByPath(root + @"\Users");
        if (users >= 0)
        {
            string me = Environment.UserName;
            foreach (int u in tree.Children(users))
            {
                if (!tree.IsDirectory(u) || (tree.Flags(u) & NodeFlags.ReparsePoint) != 0 || tree.Size(u) == 0)
                    continue;
                string name = tree.Name(u);
                string group = string.Equals(name, me, StringComparison.OrdinalIgnoreCase) ? $"User: {name} (you)" : $"User: {name}";
                string home = tree.GetFullPath(u);
                rows.Add(Row(tree, group, "Whole profile", u, 0));
                foreach (var (label, rel, level) in UserFolders)
                    AddPath(rows, tree, group, label, home + "\\" + rel, level);
            }
        }

        // Every top-level folder, so any drive gets a useful breakdown.
        const string top = "Top-level folders";
        foreach (int c in tree.Children(ScanTree.RootId))
            rows.Add(Row(tree, top, tree.Name(c), c, 0));

        return rows;
    }

    static readonly (string Label, string Relative, int Level)[] UserFolders =
    [
        ("Desktop", "Desktop", 1),
        ("Documents", "Documents", 1),
        ("Downloads", "Downloads", 1),
        ("Pictures", "Pictures", 1),
        ("Videos", "Videos", 1),
        ("Music", "Music", 1),
        ("OneDrive", "OneDrive", 1),
        ("AppData\\Local", @"AppData\Local", 1),
        ("Temp", @"AppData\Local\Temp", 2),
        ("Packages (Store app data)", @"AppData\Local\Packages", 2),
        ("AppData\\Roaming", @"AppData\Roaming", 1),
        ("AppData\\LocalLow", @"AppData\LocalLow", 1),
    ];

    static void AddPath(List<LocationStat> rows, ScanTree tree, string group, string name, string path, int level)
    {
        int id = tree.FindByPath(path);
        if (id >= 0)
            rows.Add(Row(tree, group, name, id, level));
    }

    static LocationStat Row(ScanTree tree, string group, string name, int id, int level) =>
        new(group, name, tree.GetFullPath(id), level, id, tree.Size(id), tree.Allocated(id), tree.FileCount(id),
            (tree.Flags(id) & NodeFlags.AccessDenied) != 0 ? "Access denied: size may be larger" : null);
}
