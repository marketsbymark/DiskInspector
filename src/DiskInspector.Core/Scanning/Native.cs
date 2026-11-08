using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskInspector.Core.Scanning;

internal static unsafe partial class Native
{
    const uint InvalidFileSize = 0xFFFFFFFF;
    static readonly nint InvalidHandle = -1;

    public const uint FileAttributeReparsePoint = 0x400;
    public const uint FileAttributeOffline = 0x1000;
    public const uint FileAttributeRecallOnOpen = 0x40000;
    public const uint FileAttributeRecallOnDataAccess = 0x400000;

    const uint IoReparseTagCloudMask = 0xFFFF0FFF;
    const uint IoReparseTagCloud = 0x9000001A;
    const uint IoReparseTagOneDrive = 0x80000021;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Win32FindData
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh;
        public uint AccessLow, AccessHigh;
        public uint WriteLow, WriteHigh;
        public uint FileSizeHigh, FileSizeLow;
        public uint Reserved0, Reserved1;
        public fixed char FileName[260];
        public fixed char AlternateFileName[14];
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpace(string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint numberOfFreeClusters, out uint totalNumberOfClusters);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindFirstFileExW(string fileName, int infoLevel, out Win32FindData data, int searchOp, nint filter, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindClose")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(SafeFileHandle file, byte* buffer, int bytesToRead, out int bytesRead, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFilePointerEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFilePointerEx(SafeFileHandle file, long distance, out long newPointer, uint moveMethod);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileTime", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileTime(SafeFileHandle file, long* creation, long* lastAccess, long* lastWrite);

    /// <summary>
    /// Opens a file to read its contents without changing it: Windows is told not to update its last-access date,
    /// and cloud placeholders are never downloaded (FILE_FLAG_OPEN_NO_RECALL). Returns null if it cannot be opened.
    /// </summary>
    public static FileStream? OpenForPeek(string path)
    {
        const uint GenericRead = 0x80000000, FileWriteAttributes = 0x100, ShareAll = 0x7, OpenExisting = 3, OpenNoRecall = 0x00100000;
        string p = ToExtendedPath(path);
        // Write-attributes access is only used to switch off the last-access update for this handle.
        var h = CreateFile(p, GenericRead | FileWriteAttributes, ShareAll, 0, OpenExisting, OpenNoRecall, 0);
        if (h.IsInvalid)
        {
            h.Dispose();
            h = CreateFile(p, GenericRead, ShareAll, 0, OpenExisting, OpenNoRecall, 0);
            if (h.IsInvalid)
            {
                h.Dispose();
                return null;
            }
        }
        else
        {
            long keep = -1;   // 0xFFFFFFFF/0xFFFFFFFF: "do not update the last-access time through this handle"
            SetFileTime(h, null, &keep, null);
        }
        try
        {
            return new FileStream(h, FileAccess.Read, 1);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            h.Dispose();
            return null;
        }
    }

    /// <summary>Adds the \\?\ prefix so Win32 calls accept paths longer than 260 characters.</summary>
    public static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    /// <summary>Space used on disk by a compressed, sparse or WOF-compressed file, or -1 on failure.</summary>
    public static long GetCompressedFileSize(string path)
    {
        uint low = GetCompressedFileSizeW(ToExtendedPath(path), out uint high);
        if (low == InvalidFileSize && Marshal.GetLastPInvokeError() != 0)
            return -1;
        return ((long)high << 32) | low;
    }

    /// <summary>Reparse tag of a file or folder, or 0 when it has none or cannot be read.</summary>
    public static uint GetReparseTag(string path)
    {
        nint h = FindFirstFileExW(ToExtendedPath(path.TrimEnd('\\')), 1 /* FindExInfoBasic */, out var data, 0, 0, 0);
        if (h == InvalidHandle)
            return 0;
        FindClose(h);
        return (data.FileAttributes & FileAttributeReparsePoint) != 0 ? data.Reserved0 : 0;
    }

    /// <summary>True for cloud-sync reparse points (OneDrive and other cloud file providers), which are real folders.</summary>
    public static bool IsCloudTag(uint tag) => (tag & IoReparseTagCloudMask) == IoReparseTagCloud || tag == IoReparseTagOneDrive;

    public static void ReadAt(SafeFileHandle handle, long offset, Span<byte> buffer)
    {
        if (!SetFilePointerEx(handle, offset, out _, 0))
            throw new IOException($"Seek to {offset} failed (error {Marshal.GetLastPInvokeError()}).");
        fixed (byte* p = buffer)
        {
            if (!ReadFile(handle, p, buffer.Length, out int read, 0))
                throw new IOException($"Read at {offset} failed (error {Marshal.GetLastPInvokeError()}).");
            if (read != buffer.Length)
                throw new IOException($"Short read at {offset}: {read} of {buffer.Length} bytes.");
        }
    }
}
