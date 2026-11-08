using System.Text.Json;
using DiskInspector.Core.Model;

namespace DiskInspector.Core.Analysis;

/// <summary>A family of work files shown as one table, e.g. "Word documents".</summary>
public sealed record WorkFileGroup(string Key, string Name, IReadOnlyList<string> Extensions)
{
    public bool IsAll => Key == WorkFiles.AllKey;
}

/// <summary>Finds documents, spreadsheets, email, notes and code files in a scan.</summary>
public static class WorkFiles
{
    public const string AllKey = "all";

    static readonly WorkFileGroup[] Defaults =
    [
        new("excel", "Excel & spreadsheets", ["xlsx", "xlsm", "xlsb", "xls", "xltx", "xltm", "xlt", "xlam", "xla", "csv", "tsv", "ods"]),
        new("csv", "CSV only", ["csv"]),
        new("word", "Word documents", ["docx", "docm", "doc", "dotx", "dotm", "dot", "rtf", "odt"]),
        new("powerpoint", "PowerPoint", ["pptx", "pptm", "ppt", "ppsx", "ppsm", "pps", "potx", "potm", "pot", "odp"]),
        new("pdf", "PDF", ["pdf"]),
        new("outlook", "Outlook & email", ["msg", "eml", "pst", "ost", "olm", "oft"]),
        new("markdown", "Markdown", ["md", "markdown", "mdx"]),
        new("notebooks", "Jupyter notebooks", ["ipynb"]),
        new("python", "Python", ["py", "pyw", "pyi", "pyx"]),
        new("dbc", "DBC Finance", ["df2", "ddl"]),
        new("help", "Help files", ["chm", "hlp"]),
    ];

    public static string UserFilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskInspector", "work-file-groups.json");

    /// <summary>Why the user groups file was ignored, if it was.</summary>
    public static string? LoadError { get; private set; }

    /// <summary>
    /// The groups, "All work files" first. A user file can change a group's extensions (same key) or add groups:
    /// <c>{ "groups": [ { "key": "dbc", "name": "DBC Finance", "extensions": ["df2", "ddl"] } ] }</c>
    /// </summary>
    public static IReadOnlyList<WorkFileGroup> LoadGroups(bool includeUserFile = true, string? path = null)
    {
        var groups = Defaults.ToList();
        path ??= UserFilePath;
        if (includeUserFile && File.Exists(path))
        {
            try
            {
                var file = JsonSerializer.Deserialize<GroupFile>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var g in file?.Groups ?? [])
                {
                    if (string.IsNullOrWhiteSpace(g.Key) || g.Key == AllKey || g.Extensions.Count == 0)
                        continue;
                    var exts = g.Extensions.Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).Distinct().ToList();
                    int i = groups.FindIndex(x => x.Key.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
                    var group = new WorkFileGroup(g.Key, string.IsNullOrWhiteSpace(g.Name) ? (i >= 0 ? groups[i].Name : g.Key) : g.Name, exts);
                    if (i >= 0)
                        groups[i] = group;
                    else
                        groups.Add(group);
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                LoadError = $"{path}: {e.Message}";
            }
        }
        var all = new WorkFileGroup(AllKey, "All work files", groups.SelectMany(g => g.Extensions).Distinct().ToList());
        return [all, .. groups];
    }

    // Folders full of program, package and cache files rather than the user's own work.
    static readonly HashSet<string> SystemFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "$Recycle.Bin",
        "System Volume Information", "$WINDOWS.~BT", "$WinREAgent", "$SysReset", "Recovery",
        "node_modules", "site-packages", "dist-packages", ".venv", "venv", "__pycache__", ".git", ".svn", ".hg",
        ".cache", ".nuget", ".conda", "anaconda3", "miniconda3", ".vscode-server", ".cargo", ".rustup", "bower_components",
    };

    /// <summary>True for folders that hold programs, packages or caches, which "only my files" skips.</summary>
    public static bool IsSystemFolder(string name) => SystemFolderNames.Contains(name) || name.StartsWith("$", StringComparison.Ordinal);

    /// <summary>Extension ids of <paramref name="extensions"/> as a lookup indexed by extension id.</summary>
    public static bool[] ExtensionMask(ScanTree tree, IEnumerable<string> extensions)
    {
        var mask = new bool[tree.Extensions.Count];
        foreach (var e in extensions)
            if (tree.Extensions.TryGetId(e, out ushort id))
                mask[id] = true;
        return mask;
    }

    /// <summary>
    /// Files below <paramref name="root"/> whose extension is in <paramref name="mask"/>, optionally skipping
    /// program, package and cache folders.
    /// </summary>
    public static List<int> Find(ScanTree tree, bool[] mask, int root, bool onlyUserFolders, Func<int, bool>? filter = null,
        CancellationToken ct = default)
    {
        var result = new List<int>();
        Walk(tree, root, onlyUserFolders, i =>
        {
            if (mask[tree.ExtensionId(i)] && (filter is null || filter(i)))
                result.Add(i);
        }, ct);
        return result;
    }

    /// <summary>Number of files per extension id below <paramref name="root"/>, for the group counts.</summary>
    public static long[] CountByExtension(ScanTree tree, int root, bool onlyUserFolders, CancellationToken ct = default)
    {
        var counts = new long[tree.Extensions.Count];
        Walk(tree, root, onlyUserFolders, i => counts[tree.ExtensionId(i)]++, ct);
        return counts;
    }

    /// <summary>Calls <paramref name="file"/> for each file below <paramref name="root"/>.</summary>
    static void Walk(ScanTree tree, int root, bool onlyUserFolders, Action<int> file, CancellationToken ct)
    {
        if (!onlyUserFolders)
        {
            tree.VisitDescendants(root, i =>
            {
                if (!tree.IsDirectory(i))
                    file(i);
            }, ct);
            return;
        }
        var stack = new Stack<int>();
        stack.Push(root);
        int n = 0;
        while (stack.Count > 0)
        {
            foreach (int c in tree.Children(stack.Pop()))
            {
                if ((++n & 0xFFFF) == 0)
                    ct.ThrowIfCancellationRequested();
                if (!tree.IsDirectory(c))
                    file(c);
                else if (!IsSystemFolder(tree.Name(c)) || c == root)
                    stack.Push(c);
            }
        }
    }

    sealed class GroupFile
    {
        public List<GroupDef> Groups { get; set; } = [];
    }

    sealed class GroupDef
    {
        public string Key { get; set; } = "";
        public string? Name { get; set; }
        public List<string> Extensions { get; set; } = [];
    }
}
