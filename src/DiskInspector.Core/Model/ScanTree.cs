using System.Text;

namespace DiskInspector.Core.Model;

/// <summary>
/// Compact store for every file and folder found by a scan. Nodes live in parallel arrays
/// (no object per file) so a full C:\ drive with millions of entries stays small.
/// Nodes must be added after their parent, which lets <see cref="Complete"/> roll sizes
/// up in a single backwards pass.
/// </summary>
public sealed class ScanTree
{
    public const int RootId = 0;

    /// <summary>Root name of an "all drives" scan; its children are the drives ("C:", "D:").</summary>
    public const string ComputerRoot = "This PC";

    int _count;
    int[] _parent;
    string[] _name;
    long[] _size;
    long[] _alloc;
    long[] _mtime;
    long[] _ctime;
    long[] _atime;
    ushort[] _ext;
    NodeFlags[] _flags;

    // Filled by Complete().
    int[] _fileCount = [];
    int[] _dirCount = [];
    int[] _childStart = [];
    int[] _childCount = [];
    int[] _children = [];

    public ScanTree(string rootPath, long rootModifiedTicks = 0, int capacity = 1 << 14)
    {
        capacity = Math.Max(capacity, 16);
        _parent = new int[capacity];
        _name = new string[capacity];
        _size = new long[capacity];
        _alloc = new long[capacity];
        _mtime = new long[capacity];
        _ctime = new long[capacity];
        _atime = new long[capacity];
        _ext = new ushort[capacity];
        _flags = new NodeFlags[capacity];

        RootPath = rootPath;
        _parent[0] = -1;
        _name[0] = rootPath;
        _flags[0] = NodeFlags.Directory;
        _mtime[0] = rootModifiedTicks;
        _count = 1;
    }

    public string RootPath { get; }

    /// <summary>True for an "all drives" scan, where the root is <see cref="ComputerRoot"/> and its children are drives.</summary>
    public bool IsComputerRoot => RootPath == ComputerRoot;
    public ExtensionTable Extensions { get; } = new();
    public int Count => _count;
    public bool IsComplete { get; private set; }

    /// <summary>Adds a node. Directory sizes are ignored; they are rolled up by <see cref="Complete"/>.</summary>
    public int Add(int parent, string name, NodeFlags flags, long size, long allocated, long modifiedTicks,
        long createdTicks = 0, long accessedTicks = 0)
    {
        if (IsComplete)
            throw new InvalidOperationException("The tree is already complete.");
        if ((uint)parent >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(parent));
        if ((_flags[parent] & NodeFlags.Directory) == 0)
            throw new ArgumentException("Parent must be a directory.", nameof(parent));
        if (_count == _parent.Length)
            Grow();

        int id = _count++;
        bool dir = (flags & NodeFlags.Directory) != 0;
        _parent[id] = parent;
        _name[id] = name;
        _flags[id] = flags;
        _size[id] = dir ? 0 : Math.Max(0, size);
        _alloc[id] = dir ? 0 : Math.Max(0, allocated);
        _mtime[id] = modifiedTicks;
        _ctime[id] = createdTicks;
        _atime[id] = accessedTicks;
        _ext[id] = dir ? ExtensionTable.None : Extensions.GetOrAdd(name);
        return id;
    }

    public void AddFlags(int id, NodeFlags flags) => _flags[id] |= flags;

    void Grow()
    {
        int n = _parent.Length * 2;
        Array.Resize(ref _parent, n);
        Array.Resize(ref _name, n);
        Array.Resize(ref _size, n);
        Array.Resize(ref _alloc, n);
        Array.Resize(ref _mtime, n);
        Array.Resize(ref _ctime, n);
        Array.Resize(ref _atime, n);
        Array.Resize(ref _ext, n);
        Array.Resize(ref _flags, n);
    }

    /// <summary>Rolls folder totals up and builds the child index (children sorted by size, largest first).</summary>
    public void Complete()
    {
        if (IsComplete)
            return;
        int n = _count;
        Array.Resize(ref _parent, n);
        Array.Resize(ref _name, n);
        Array.Resize(ref _size, n);
        Array.Resize(ref _alloc, n);
        Array.Resize(ref _mtime, n);
        Array.Resize(ref _ctime, n);
        Array.Resize(ref _atime, n);
        Array.Resize(ref _ext, n);
        Array.Resize(ref _flags, n);

        _fileCount = new int[n];
        _dirCount = new int[n];
        _childCount = new int[n];
        for (int i = 0; i < n; i++)
            if ((_flags[i] & NodeFlags.Directory) == 0)
                _fileCount[i] = 1;

        for (int i = n - 1; i > 0; i--)
        {
            int p = _parent[i];
            _size[p] += _size[i];
            _alloc[p] += _alloc[i];
            _fileCount[p] += _fileCount[i];
            _dirCount[p] += _dirCount[i] + ((_flags[i] & NodeFlags.Directory) != 0 ? 1 : 0);
            _childCount[p]++;
        }

        _childStart = new int[n];
        int running = 0;
        for (int i = 0; i < n; i++)
        {
            _childStart[i] = running;
            running += _childCount[i];
        }

        _children = new int[Math.Max(0, n - 1)];
        var cursor = (int[])_childStart.Clone();
        for (int i = 1; i < n; i++)
            _children[cursor[_parent[i]]++] = i;

        var size = _size;
        Comparison<int> bySizeDesc = (a, b) => size[b].CompareTo(size[a]);
        for (int i = 0; i < n; i++)
            if (_childCount[i] > 1)
                _children.AsSpan(_childStart[i], _childCount[i]).Sort(bySizeDesc);

        IsComplete = true;
    }

    public int Parent(int id) => _parent[id];
    public string Name(int id) => _name[id];
    public NodeFlags Flags(int id) => _flags[id];
    public bool IsDirectory(int id) => (_flags[id] & NodeFlags.Directory) != 0;
    public long Size(int id) => _size[id];
    public long Allocated(int id) => _alloc[id];
    public long GetSize(int id, SizeMetric metric) => metric == SizeMetric.Logical ? _size[id] : _alloc[id];
    public long ModifiedTicks(int id) => _mtime[id];
    public DateTime ModifiedUtc(int id) => ToUtc(_mtime[id]);
    public long CreatedTicks(int id) => _ctime[id];
    public DateTime CreatedUtc(int id) => ToUtc(_ctime[id]);
    public long AccessedTicks(int id) => _atime[id];
    public DateTime AccessedUtc(int id) => ToUtc(_atime[id]);

    static DateTime ToUtc(long ticks) => ticks > 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;
    public ushort ExtensionId(int id) => _ext[id];
    public string Extension(int id) => Extensions[_ext[id]];

    /// <summary>Number of files below a folder (1 for a file).</summary>
    public int FileCount(int id) => _fileCount[id];

    /// <summary>Number of folders below a folder, not counting itself.</summary>
    public int DirectoryCount(int id) => _dirCount[id];

    /// <summary>Children sorted by logical size, largest first.</summary>
    public ReadOnlySpan<int> Children(int id) =>
        IsComplete ? _children.AsSpan(_childStart[id], _childCount[id]) : throw new InvalidOperationException("Call Complete() first.");

    public int ChildCount(int id) => IsComplete ? _childCount[id] : 0;

    /// <summary>Children sorted by the given size, largest first.</summary>
    public int[] GetChildrenSorted(int id, SizeMetric metric)
    {
        var result = Children(id).ToArray();
        if (metric == SizeMetric.Allocated && result.Length > 1)
        {
            var alloc = _alloc;
            result.AsSpan().Sort((a, b) => alloc[b].CompareTo(alloc[a]));
        }
        return result;
    }

    public int Depth(int id)
    {
        int d = 0;
        for (int i = id; i > RootId; i = _parent[i])
            d++;
        return d;
    }

    public bool IsAncestorOrSelf(int ancestor, int id)
    {
        for (int i = id; i >= 0; i = _parent[i])
            if (i == ancestor)
                return true;
        return false;
    }

    /// <summary>Node ids from the root down to <paramref name="id"/>, inclusive.</summary>
    public int[] PathFromRoot(int id)
    {
        var list = new List<int>();
        for (int i = id; i >= 0; i = _parent[i])
            list.Add(i);
        list.Reverse();
        return [.. list];
    }

    public string GetFullPath(int id)
    {
        if (id == RootId)
            return RootPath;
        var parts = new List<string>();
        for (int i = id; i > RootId; i = _parent[i])
            parts.Add(_name[i]);
        if (IsComputerRoot)
        {
            // parts[^1] is the drive ("C:"): the path starts there.
            string drive = parts[^1];
            parts.RemoveAt(parts.Count - 1);
            if (parts.Count == 0)
                return drive + "\\";
            return drive + "\\" + string.Join('\\', Enumerable.Reverse(parts));
        }
        var sb = new StringBuilder(RootPath, RootPath.Length + parts.Count * 16);
        if (!RootPath.EndsWith('\\'))
            sb.Append('\\');
        for (int k = parts.Count - 1; k >= 0; k--)
        {
            sb.Append(parts[k]);
            if (k > 0)
                sb.Append('\\');
        }
        return sb.ToString();
    }

    /// <summary>Finds the node for a full path inside this tree, or -1.</summary>
    public int FindByPath(string path)
    {
        if (!IsComplete || string.IsNullOrWhiteSpace(path))
            return -1;
        string full = path.TrimEnd('\\');
        string root = RootPath.TrimEnd('\\');
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase))
            return RootId;
        if (IsComputerRoot)
        {
            int id0 = RootId;
            foreach (var part in full.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                id0 = FindChild(id0, part);
                if (id0 < 0)
                    return -1;
            }
            return id0;
        }
        if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
            return -1;

        int id = RootId;
        foreach (var part in full[(root.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            int found = FindChild(id, part);
            if (found < 0)
                return -1;
            id = found;
        }
        return id;
    }

    /// <summary>
    /// Calls <paramref name="visit"/> for every node below <paramref name="root"/> (not the root itself).
    /// The whole tree is walked in storage order; a subtree with an explicit stack, so depth never matters.
    /// </summary>
    public void VisitDescendants(int root, Action<int> visit, CancellationToken ct = default)
    {
        if (root == RootId)
        {
            for (int i = 1; i < _count; i++)
            {
                if ((i & 0xFFFF) == 0)
                    ct.ThrowIfCancellationRequested();
                visit(i);
            }
            return;
        }
        var stack = new Stack<int>();
        stack.Push(root);
        int n = 0;
        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            foreach (int c in Children(cur))
            {
                if ((++n & 0xFFFF) == 0)
                    ct.ThrowIfCancellationRequested();
                visit(c);
                if ((_flags[c] & NodeFlags.Directory) != 0)
                    stack.Push(c);
            }
        }
    }

    public int FindChild(int id, string name)
    {
        foreach (int c in Children(id))
            if (string.Equals(_name[c], name, StringComparison.OrdinalIgnoreCase))
                return c;
        return -1;
    }
}
