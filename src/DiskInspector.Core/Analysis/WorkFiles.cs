using System.IO.Enumeration;
using System.Text.Json;
using DiskInspector.Core.Model;

namespace DiskInspector.Core.Analysis;

/// <summary>
/// A family of work files shown as one table, e.g. "Word documents". A file belongs to the group when its extension is in
/// <see cref="Extensions"/>, its name matches one of <see cref="FileNames"/>, or it is inside one of <see cref="Folders"/>.
/// </summary>
public sealed record WorkFileGroup(string Key, string Name, IReadOnlyList<string> Extensions)
{
    /// <summary>File names, case-insensitive; may use * and ? (for example "CLAUDE.md" or "ChatGPT*").</summary>
    public IReadOnlyList<string> FileNames { get; init; } = [];

    /// <summary>
    /// Folders whose whole contents belong to the group: a folder name found anywhere (".claude"), or a full path that may use
    /// environment variables and a * in its last part (<c>%LOCALAPPDATA%\Packages\OpenAI.*</c>).
    /// </summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    public bool IsAll => Key == WorkFiles.AllKey;
    public bool IsCustom => Key == WorkFiles.CustomKey;

    /// <summary>True when the group matches by file name or folder, not only by extension.</summary>
    public bool HasRules => FileNames.Count > 0 || Folders.Count > 0;

    /// <summary>Short description of what the group matches, for summaries.</summary>
    public string Describe() => string.Join("; ", new[]
    {
        string.Join(" ", Extensions.Select(e => "." + e)),
        FileNames.Count > 0 ? "files " + string.Join(", ", FileNames) : "",
        Folders.Count > 0 ? "folders " + string.Join(", ", Folders) : "",
    }.Where(x => x.Length > 0));
}

/// <summary>Finds documents, spreadsheets, email, notes, code and AI-assistant files in a scan.</summary>
public static class WorkFiles
{
    public const string AllKey = "all";
    public const string CustomKey = "custom";

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
        // AI assistants: instructions, settings, skills, session logs and app data, found by file name and folder.
        new("claude", "Claude", [])
        {
            FileNames = ["CLAUDE.md", "CLAUDE.local.md", ".claude.json", ".claude.json.backup*", "claude_desktop_config.json", ".mcp.json", "Claude*"],
            Folders = [".claude", @"%APPDATA%\Claude", @"%LOCALAPPDATA%\AnthropicClaude", @"%LOCALAPPDATA%\Packages\Claude_*", @"%LOCALAPPDATA%\Programs\claude*"],
        },
        new("codex", "ChatGPT & Codex", [])
        {
            FileNames = ["AGENTS.md", "AGENTS.override.md", "ChatGPT*"],
            Folders = [".codex", ".agents", @"%LOCALAPPDATA%\Packages\OpenAI.*", @"%APPDATA%\ChatGPT", @"%LOCALAPPDATA%\ChatGPT",
                @"%LOCALAPPDATA%\Programs\ChatGPT", @"%USERPROFILE%\Documents\Codex"],
        },
        new("gemini", "Gemini & Antigravity", [])
        {
            FileNames = ["GEMINI.md", ".geminiignore", "Gemini*", "Antigravity*"],
            Folders = [".gemini", ".antigravity", ".agent", @"%APPDATA%\Antigravity", @"%LOCALAPPDATA%\Programs\Antigravity"],
        },
    ];

    public static string UserFilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskInspector", "work-file-groups.json");

    /// <summary>Why the user groups file was ignored, if it was.</summary>
    public static string? LoadError { get; private set; }

    /// <summary>
    /// The groups: "All work files" first (every extension-based group together), then each group, then "Any types or all files"
    /// (extensions typed in the app, or every file). A user file can change a group (same key) or add groups:
    /// <c>{ "groups": [ { "key": "dbc", "extensions": ["df2", "ddl"] }, { "key": "cursor", "name": "Cursor", "fileNames": [".cursorrules"], "folders": [".cursor"] } ] }</c>
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
                    if (string.IsNullOrWhiteSpace(g.Key) || g.Key == AllKey || g.Key == CustomKey)
                        continue;
                    var exts = ParseExtensions(string.Join(",", g.Extensions ?? []));
                    var names = Clean(g.FileNames);
                    var folders = Clean(g.Folders);
                    if (exts.Count == 0 && names.Count == 0 && folders.Count == 0)
                        continue;
                    int i = groups.FindIndex(x => x.Key.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
                    var group = new WorkFileGroup(g.Key, string.IsNullOrWhiteSpace(g.Name) ? (i >= 0 ? groups[i].Name : g.Key) : g.Name, exts)
                    {
                        FileNames = names,
                        Folders = folders,
                    };
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
        var all = new WorkFileGroup(AllKey, "All work files", groups.Where(g => !g.HasRules).SelectMany(g => g.Extensions).Distinct().ToList());
        var custom = new WorkFileGroup(CustomKey, "Any types or all files", []);
        return [all, .. groups, custom];
    }

    static List<string> Clean(IEnumerable<string>? items) =>
        (items ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Extensions from text such as "json, .toml *.sql" (commas, semicolons or spaces between them).
    /// Returns an empty list for "", "*" or "*.*", which mean every file.
    /// </summary>
    public static List<string> ParseExtensions(string? text) =>
        (text ?? "").Split([',', ';', ' ', '\t', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0 && e != "*")
            .Distinct()
            .ToList();

    // Folders full of program, package and cache files rather than the user's own work.
    static readonly HashSet<string> SystemFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "$Recycle.Bin",
        "System Volume Information", "$WINDOWS.~BT", "$WinREAgent", "$SysReset", "Recovery",
        "node_modules", "site-packages", "dist-packages", ".venv", "venv", "__pycache__", ".git", ".svn", ".hg",
        ".cache", ".nuget", ".conda", "anaconda3", "miniconda3", ".vscode-server", ".cargo", ".rustup", "bower_components",
    };

    /// <summary>True for folders that hold programs, packages or caches, which "only my files" skips (along with system files).</summary>
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
        CancellationToken ct = default) =>
        Find(tree, FileMatcher.ForMask(tree, mask), root, onlyUserFolders, filter, ct);

    /// <summary>Files below <paramref name="root"/> that <paramref name="matcher"/> accepts.</summary>
    public static List<int> Find(ScanTree tree, FileMatcher matcher, int root, bool onlyUserFolders, Func<int, bool>? filter = null,
        CancellationToken ct = default)
    {
        var result = new List<int>();
        Walk(tree, matcher, root, onlyUserFolders, i =>
        {
            if (filter is null || filter(i))
                result.Add(i);
        }, ct);
        return result;
    }

    /// <summary>Number of files per extension id below <paramref name="root"/>, for the group counts.</summary>
    public static long[] CountByExtension(ScanTree tree, int root, bool onlyUserFolders, CancellationToken ct = default) =>
        CountByExtension(tree, FileMatcher.AllFiles(tree), root, onlyUserFolders, ct);

    /// <summary>Number of matching files per extension id below <paramref name="root"/>.</summary>
    public static long[] CountByExtension(ScanTree tree, FileMatcher matcher, int root, bool onlyUserFolders, CancellationToken ct = default)
    {
        var counts = new long[tree.Extensions.Count];
        Walk(tree, matcher, root, onlyUserFolders, i => counts[tree.ExtensionId(i)]++, ct);
        return counts;
    }

    /// <summary>Calls <paramref name="file"/> for each matching file below <paramref name="root"/>.</summary>
    static void Walk(ScanTree tree, FileMatcher matcher, int root, bool onlyUserFolders, Action<int> file, CancellationToken ct)
    {
        if (root < 0 || root >= tree.Count)
            return;
        if (!tree.IsDirectory(root))
        {
            if (matcher.Matches(root, matcher.IsInsideMatchedFolder(tree.Parent(root))))
                file(root);
            return;
        }
        var stack = new Stack<(int Node, bool Inside)>();
        stack.Push((root, matcher.IsInsideMatchedFolder(root)));
        int n = 0;
        while (stack.Count > 0)
        {
            var (node, inside) = stack.Pop();
            foreach (int c in tree.Children(node))
            {
                if ((++n & 0xFFFF) == 0)
                    ct.ThrowIfCancellationRequested();
                if (!tree.IsDirectory(c))
                {
                    // "Only my files" also leaves out files Windows marks as system files (pagefile.sys, desktop.ini, ...).
                    if ((!onlyUserFolders || (tree.Flags(c) & (NodeFlags.System | NodeFlags.Metadata)) == 0) && matcher.Matches(c, inside))
                        file(c);
                }
                else if (!onlyUserFolders || !IsSystemFolder(tree.Name(c)))
                {
                    stack.Push((c, inside || matcher.IsMatchedFolder(c)));
                }
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
        public List<string>? Extensions { get; set; }
        public List<string>? FileNames { get; set; }
        public List<string>? Folders { get; set; }
    }
}

/// <summary>Decides which files of one scan belong to a group: by extension, file name or containing folder.</summary>
public sealed class FileMatcher
{
    readonly ScanTree _tree;
    readonly bool[]? _extensions;          // null: every file
    readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _namePatterns = [];
    readonly HashSet<string> _folderNames = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<int> _folderNodes = [];
    readonly bool _hasRules;

    FileMatcher(ScanTree tree, bool[]? extensions)
    {
        _tree = tree;
        _extensions = extensions;
    }

    FileMatcher(ScanTree tree, bool[] extensions, IEnumerable<string> names, IEnumerable<string> folders) : this(tree, extensions)
    {
        foreach (var n in names)
        {
            if (n.IndexOfAny(['*', '?']) >= 0)
                _namePatterns.Add(n);
            else
                _names.Add(n);
        }
        foreach (var f in folders)
        {
            if (f.Contains('\\') || f.Contains('%'))
                _folderNodes.UnionWith(ResolveFolder(tree, f));
            else
                _folderNames.Add(f);
        }
        _hasRules = true;
    }

    /// <summary>Every file.</summary>
    public static FileMatcher AllFiles(ScanTree tree) => new(tree, null);

    /// <summary>Files whose extension id is set in <paramref name="mask"/>.</summary>
    public static FileMatcher ForMask(ScanTree tree, bool[] mask) => new(tree, mask);

    /// <summary>Files with one of <paramref name="extensions"/>; every file when the list is empty.</summary>
    public static FileMatcher ForExtensions(ScanTree tree, IReadOnlyCollection<string> extensions) =>
        extensions.Count == 0 ? AllFiles(tree) : new(tree, WorkFiles.ExtensionMask(tree, extensions));

    /// <summary>The files of <paramref name="group"/> in this scan. Full-path folders that weren't scanned are ignored.</summary>
    public static FileMatcher ForGroup(ScanTree tree, WorkFileGroup group) =>
        group.HasRules
            ? new FileMatcher(tree, WorkFiles.ExtensionMask(tree, group.Extensions), group.FileNames, group.Folders)
            : ForExtensions(tree, group.Extensions);

    /// <summary>Folder nodes for a full path such as <c>%LOCALAPPDATA%\Packages\OpenAI.*</c> (a * only in the last part).</summary>
    static List<int> ResolveFolder(ScanTree tree, string pattern)
    {
        var found = new List<int>();
        string path = Environment.ExpandEnvironmentVariables(pattern).TrimEnd('\\');
        if (path.Contains('%'))
            return found;        // a variable that isn't set
        int cut = path.LastIndexOf('\\');
        string last = cut >= 0 ? path[(cut + 1)..] : path;
        if (last.IndexOfAny(['*', '?']) < 0)
        {
            int id = tree.FindByPath(path);
            if (id >= 0 && tree.IsDirectory(id))
                found.Add(id);
            return found;
        }
        int parent = cut > 0 ? tree.FindByPath(path[..cut]) : -1;
        if (parent < 0 || !tree.IsDirectory(parent))
            return found;
        foreach (int c in tree.Children(parent))
            if (tree.IsDirectory(c) && FileSystemName.MatchesSimpleExpression(last, tree.Name(c)))
                found.Add(c);
        return found;
    }

    /// <summary>True when every file matches (no extension list and no rules).</summary>
    public bool MatchesEverything => _extensions is null;

    /// <summary>True when <paramref name="folder"/> itself is one of the group's folders.</summary>
    public bool IsMatchedFolder(int folder) =>
        _hasRules && (_folderNodes.Contains(folder) || (_folderNames.Count > 0 && _folderNames.Contains(_tree.Name(folder))));

    /// <summary>True when <paramref name="folder"/> or a folder above it is one of the group's folders.</summary>
    public bool IsInsideMatchedFolder(int folder)
    {
        if (!_hasRules)
            return false;
        for (int p = folder; p >= 0; p = _tree.Parent(p))
            if (IsMatchedFolder(p))
                return true;
        return false;
    }

    /// <summary>True when <paramref name="file"/> belongs to the group; <paramref name="insideMatchedFolder"/> comes from the walk.</summary>
    public bool Matches(int file, bool insideMatchedFolder)
    {
        if (_extensions is null || insideMatchedFolder || _extensions[_tree.ExtensionId(file)])
            return true;
        if (!_hasRules)
            return false;
        string name = _tree.Name(file);
        if (_names.Contains(name))
            return true;
        foreach (var p in _namePatterns)
            if (FileSystemName.MatchesSimpleExpression(p, name))
                return true;
        return false;
    }
}
