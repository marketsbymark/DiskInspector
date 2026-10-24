namespace DiskInspector.Core.Model;

/// <summary>
/// Maps lower-case file extensions to compact ids. Id 0 means "no extension".
/// Not thread safe; callers serialize access (the scanners add under the tree lock).
/// </summary>
public sealed class ExtensionTable
{
    public const int MaxExtensionLength = 16;
    public const ushort None = 0;

    readonly Dictionary<string, ushort> _ids = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ushort>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    readonly List<string> _names = [""];

    public ExtensionTable() => _lookup = _ids.GetAlternateLookup<ReadOnlySpan<char>>();

    public int Count => _names.Count;

    public string this[ushort id] => _names[id];

    /// <summary>Extension of <paramref name="fileName"/> without the dot, or empty when it has none we can use.</summary>
    public static ReadOnlySpan<char> GetExtension(ReadOnlySpan<char> fileName)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
            return default;
        var ext = fileName[(dot + 1)..];
        if (ext.Length > MaxExtensionLength || ext.Contains(' '))
            return default;
        // "claude.2.1.50" or "lib.so.1" end in a version number, not a type. Keep split-archive parts (".001").
        if (!ext.ContainsAnyExcept("0123456789") && !(ext.Length == 3 && ext[0] == '0'))
            return default;
        return ext;
    }

    public ushort GetOrAdd(ReadOnlySpan<char> fileName)
    {
        var ext = GetExtension(fileName);
        if (ext.IsEmpty)
            return None;
        if (_lookup.TryGetValue(ext, out ushort id))
            return id;
        if (_names.Count >= ushort.MaxValue)
            return None;
        string name = ext.ToString().ToLowerInvariant();
        id = (ushort)_names.Count;
        _names.Add(name);
        _ids[name] = id;
        return id;
    }

    public bool TryGetId(string extension, out ushort id) => _ids.TryGetValue(extension, out id);
}
