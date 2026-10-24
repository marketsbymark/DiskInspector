namespace DiskInspector.Core.Model;

[Flags]
public enum NodeFlags : ushort
{
    None = 0,
    Directory = 1 << 0,
    /// <summary>Junction, symlink or mount point that was not followed.</summary>
    ReparsePoint = 1 << 1,
    AccessDenied = 1 << 2,
    /// <summary>Cloud file (e.g. OneDrive) whose content is not stored locally.</summary>
    CloudPlaceholder = 1 << 3,
    HardLink = 1 << 4,
    Compressed = 1 << 5,
    Sparse = 1 << 6,
    Hidden = 1 << 7,
    System = 1 << 8,
    /// <summary>The folder could not be fully read (vanished, I/O error, path problem).</summary>
    Error = 1 << 9,
    /// <summary>NTFS metadata file such as $MFT (fast scan only).</summary>
    Metadata = 1 << 10,
}

public enum SizeMetric
{
    /// <summary>File length as shown in Explorer's "Size".</summary>
    Logical,
    /// <summary>Space actually used on the drive ("Size on disk").</summary>
    Allocated,
}
