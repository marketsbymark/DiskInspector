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

        // Summary
        rows.Add(Row(tree, SummaryGroup, "Everything scanned", ScanTree.RootId, 0));
        foreach (var vol in result.Volumes)
        {
            // Compare with what Windows reports, for each whole drive the scan covered.
            int node = tree.IsComputerRoot ? tree.FindByPath(vol.RootPath) : result.IsWholeVolume ? ScanTree.RootId : -1;
            if (node < 0)
                continue;
            string drive = tree.IsComputerRoot ? $" ({vol.RootPath.TrimEnd('\\')})" : "";
            rows.Add(new LocationStat(SummaryGroup, $"Drive used space{drive} (Windows)", vol.RootPath, 0, -1, vol.UsedBytes, vol.UsedBytes, 0));
            long gap = vol.UsedBytes - tree.Allocated(node);
            rows.Add(gap >= 0
                ? new LocationStat(SummaryGroup, $"Not seen by the scan{drive}", "", 0, -1, gap, gap, 0,
                    result.Mode == ScanMode.Mft
                        ? "NTFS metadata, shadow copies and alternate data streams"
                        : "Protected folders, shadow copies, NTFS metadata and small files stored in the file table")
                : new LocationStat(SummaryGroup, $"Counted more than once{drive}", "", 0, -1, -gap, -gap, 0,
                    "Hard-linked files (for example in WinSxS) are counted once per link by the normal scan"));
            rows.Add(new LocationStat(SummaryGroup, $"Free space{drive}", vol.RootPath, 0, -1, vol.FreeBytes, vol.FreeBytes, 0));
        }

        long cloudFiles = 0, cloudBytes = 0;
        for (int i = 1; i < tree.Count; i++)
            if ((tree.Flags(i) & NodeFlags.CloudPlaceholder) != 0 && !tree.IsDirectory(i))
            {
                cloudFiles++;
                cloudBytes += tree.Size(i);
            }
        if (cloudFiles > 0)
            rows.Add(new LocationStat(SummaryGroup, "Online-only cloud files", "", 0, -1, cloudBytes, 0, cloudFiles,
                "OneDrive (or other cloud) files not stored on this PC: they use no local space until opened"));

        int denied = result.Issues.Count;
        if (denied > 0)
            rows.Add(new LocationStat(SummaryGroup, $"Folders that could not be read: {denied:N0}", "", 0, -1, 0, 0, 0,
                "See the Inaccessible list in the status bar"));

        // A folder scan has one root; an all-drives scan has one per drive.
        List<(string Path, string Suffix)> roots = tree.IsComputerRoot
            ? tree.Children(ScanTree.RootId).ToArray().Select(d => (Path: tree.Name(d), Suffix: $" ({tree.Name(d)})")).ToList()
            : [(Path: tree.RootPath.TrimEnd('\\'), Suffix: "")];
        foreach (var (root, suffix) in roots)
            AddWellKnown(rows, tree, root, suffix);

        // Every top-level folder, so any drive gets a useful breakdown.
        string top = tree.IsComputerRoot ? "Drives" : "Top-level folders";
        foreach (int c in tree.Children(ScanTree.RootId))
            rows.Add(Row(tree, top, tree.Name(c), c, 0));

        return rows;
    }

    static void AddWellKnown(List<LocationStat> rows, ScanTree tree, string root, string suffix)
    {
        string sys = "Windows & system" + suffix;
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

        string programs = "Programs" + suffix;
        AddPath(rows, tree, programs, "Program Files", root + @"\Program Files", 0);
        AddPath(rows, tree, programs, "Program Files (x86)", root + @"\Program Files (x86)", 0);
        AddPath(rows, tree, programs, "ProgramData", root + @"\ProgramData", 0);
        AddPath(rows, tree, programs, "Package cache", root + @"\ProgramData\Package Cache", 1);
        AddPath(rows, tree, programs, "Store apps (WindowsApps)", root + @"\Program Files\WindowsApps", 1);

        int users = tree.FindByPath(root + @"\Users");
        if (users < 0)
            return;
        string me = Environment.UserName;
        foreach (int u in tree.Children(users))
        {
            if (!tree.IsDirectory(u) || (tree.Flags(u) & NodeFlags.ReparsePoint) != 0 || tree.Size(u) == 0)
                continue;
            string name = tree.Name(u);
            string group = (string.Equals(name, me, StringComparison.OrdinalIgnoreCase) ? $"User: {name} (you)" : $"User: {name}") + suffix;
            string home = tree.GetFullPath(u);
            rows.Add(Row(tree, group, "Whole profile", u, 0));
            foreach (var (label, rel, level) in UserFolders)
                AddPath(rows, tree, group, label, home + "\\" + rel, level);
            // OneDrive for work or school lives in "OneDrive - <Organization>".
            foreach (int c in tree.Children(u))
                if (tree.IsDirectory(c) && tree.Name(c).StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase))
                    rows.Add(Row(tree, group, tree.Name(c), c, 1));
        }
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

/// <summary>Finds the user's OneDrive folders (personal and work/school) from the environment and registry.</summary>
public static class CloudFolders
{
    public sealed record CloudFolder(string Name, string Path);

    public static IReadOnlyList<CloudFolder> FindOneDrive()
    {
        var found = new List<CloudFolder>();
        void Add(string? name, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;
            string full = Path.GetFullPath(path).TrimEnd('\\');
            if (found.Any(f => f.Path.Equals(full, StringComparison.OrdinalIgnoreCase)))
                return;
            found.Add(new CloudFolder(string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileName(full) : name, full));
        }

        try
        {
            using var accounts = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (accounts is not null)
                foreach (var sub in accounts.GetSubKeyNames())
                {
                    using var key = accounts.OpenSubKey(sub);
                    string? folder = key?.GetValue("UserFolder") as string;
                    string? display = key?.GetValue("DisplayName") as string;
                    string label = sub.Equals("Personal", StringComparison.OrdinalIgnoreCase) ? "OneDrive (personal)"
                        : string.IsNullOrWhiteSpace(display) ? $"OneDrive ({sub})" : $"OneDrive - {display}";
                    Add(label, folder);
                }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        Add("OneDrive (personal)", Environment.GetEnvironmentVariable("OneDriveConsumer"));
        Add("OneDrive (work or school)", Environment.GetEnvironmentVariable("OneDriveCommercial"));
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDrive"));
        return found;
    }
}
