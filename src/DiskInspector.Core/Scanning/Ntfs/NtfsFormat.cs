using System.Buffers.Binary;
using System.Text;

namespace DiskInspector.Core.Scanning.Ntfs;

internal readonly record struct NtfsBootSector(int BytesPerSector, long ClusterSize, long MftLcn, int RecordSize)
{
    public static NtfsBootSector Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 512 || !b.Slice(3, 8).SequenceEqual("NTFS    "u8))
            throw new InvalidDataException("The drive is not formatted as NTFS.");
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(b[0x0B..]);
        if (bps is not (512 or 1024 or 2048 or 4096))
            throw new InvalidDataException($"Unsupported sector size {bps}.");
        int spcRaw = b[0x0D];
        long spc = spcRaw <= 0x80 ? spcRaw : 1L << (256 - spcRaw);
        long cluster = bps * spc;
        if (cluster <= 0 || cluster > 2 * 1024 * 1024)
            throw new InvalidDataException($"Unsupported cluster size {cluster}.");
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(b[0x30..]);
        sbyte cpr = (sbyte)b[0x40];
        long recordSize = cpr > 0 ? cpr * cluster : 1L << -cpr;
        if (recordSize is < 256 or > 65536 || mftLcn <= 0)
            throw new InvalidDataException("The NTFS boot sector looks damaged.");
        return new NtfsBootSector(bps, cluster, mftLcn, (int)recordSize);
    }
}

internal readonly record struct DataRun(long Lcn, long Length)
{
    public bool IsSparse => Lcn < 0;
}

/// <summary>One $FILE_NAME entry: a name for the file inside one folder.</summary>
internal readonly record struct FileNameEntry(string Name, long ParentRecord, byte NameSpace)
{
    /// <summary>DOS-only 8.3 names duplicate a long name in the same folder; every other entry is a separate link.</summary>
    public bool IsDosOnly => NameSpace == 2;
}

/// <summary>What one FILE record tells us. Fields are only valid when the matching Has* flag is set.</summary>
internal struct MftRecordInfo
{
    public bool InUse;
    public bool IsDirectory;
    public long BaseRecord;          // 0 for a base record
    public bool HasStandardInfo;
    public long ModifiedFileTime;
    public uint Attributes;
    public bool HasData;
    public long DataSize;
    public long DataAllocated;
    public bool HasAttributeList;
}

internal static class NtfsFormat
{
    public const uint AttrStandardInformation = 0x10;
    public const uint AttrAttributeList = 0x20;
    public const uint AttrFileName = 0x30;
    public const uint AttrData = 0x80;
    public const uint AttrEnd = 0xFFFFFFFF;
    public const long RecordNumberMask = 0x0000FFFFFFFFFFFF;

    static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    static long I64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt64LittleEndian(b[o..]);

    public static bool HasFileSignature(ReadOnlySpan<byte> rec) => rec.Length >= 48 && rec[..4].SequenceEqual("FILE"u8);

    /// <summary>Applies the update-sequence fixups that protect each sector of a record. False if the record is torn or damaged.</summary>
    public static bool ApplyFixups(Span<byte> rec)
    {
        if (rec.Length < 48)
            return false;
        int usaOffset = U16(rec, 4), usaCount = U16(rec, 6);
        if (usaCount < 2 || usaOffset + usaCount * 2 > rec.Length)
            return false;
        int stride = rec.Length / (usaCount - 1);
        if (stride < 256 || stride * (usaCount - 1) != rec.Length)
            return false;
        ushort usn = U16(rec, usaOffset);
        for (int i = 1; i < usaCount; i++)
        {
            int pos = i * stride - 2;
            if (U16(rec, pos) != usn)
                return false;
            rec[pos] = rec[usaOffset + i * 2];
            rec[pos + 1] = rec[usaOffset + i * 2 + 1];
        }
        return true;
    }

    public static List<DataRun> DecodeRuns(ReadOnlySpan<byte> runs)
    {
        var list = new List<DataRun>();
        long lcn = 0;
        int p = 0;
        while (p < runs.Length && runs[p] != 0)
        {
            int header = runs[p++];
            int lenSize = header & 0xF, offSize = header >> 4;
            if (lenSize is 0 or > 8 || offSize > 8 || p + lenSize + offSize > runs.Length)
                throw new InvalidDataException("Corrupt NTFS data run.");
            long length = 0;
            for (int i = 0; i < lenSize; i++)
                length |= (long)runs[p + i] << (8 * i);
            p += lenSize;
            if (length <= 0)
                throw new InvalidDataException("Corrupt NTFS data run length.");
            if (offSize == 0)
            {
                list.Add(new DataRun(-1, length));
                continue;
            }
            long offset = 0;
            for (int i = 0; i < offSize; i++)
                offset |= (long)runs[p + i] << (8 * i);
            if (offSize < 8 && (runs[p + offSize - 1] & 0x80) != 0)
                offset |= -1L << (8 * offSize);
            p += offSize;
            lcn += offset;
            list.Add(new DataRun(lcn, length));
        }
        return list;
    }

    /// <summary>
    /// Parses a record whose fixups were already applied. Returns false if it is not a usable FILE record.
    /// Its $FILE_NAME entries are added to <paramref name="names"/> (cleared first).
    /// </summary>
    public static bool Parse(ReadOnlySpan<byte> rec, out MftRecordInfo info, List<FileNameEntry> names)
    {
        info = default;
        names.Clear();
        if (!HasFileSignature(rec))
            return false;
        ushort flags = U16(rec, 0x16);
        info.InUse = (flags & 1) != 0;
        if (!info.InUse)
            return true;
        info.IsDirectory = (flags & 2) != 0;
        info.BaseRecord = I64(rec, 0x20) & RecordNumberMask;

        int off = U16(rec, 0x14);
        int limit = (int)Math.Min(U32(rec, 0x18), (uint)rec.Length);
        while (off + 16 <= limit)
        {
            uint type = U32(rec, off);
            if (type == AttrEnd)
                break;
            int len = (int)U32(rec, off + 4);
            if (len < 16 || off + len > limit)
                return false;
            var attr = rec.Slice(off, len);
            bool nonResident = attr[8] != 0;
            int nameLength = attr[9];

            switch (type)
            {
                case AttrStandardInformation when !nonResident:
                    if (TryResidentValue(attr, out var si) && si.Length >= 0x24)
                    {
                        info.HasStandardInfo = true;
                        info.ModifiedFileTime = I64(si, 0x08);
                        info.Attributes = U32(si, 0x20);
                    }
                    break;

                case AttrAttributeList:
                    info.HasAttributeList = true;
                    break;

                case AttrFileName when !nonResident:
                    if (TryResidentValue(attr, out var fn) && fn.Length >= 0x42)
                    {
                        int chars = fn[0x40];
                        if (0x42 + chars * 2 <= fn.Length)
                            names.Add(new FileNameEntry(Encoding.Unicode.GetString(fn.Slice(0x42, chars * 2)), I64(fn, 0) & RecordNumberMask, fn[0x41]));
                    }
                    break;

                case AttrData when nameLength == 0:
                    if (!nonResident)
                    {
                        info.HasData = true;
                        info.DataSize = U32(attr, 0x10);
                        info.DataAllocated = 0;   // stored inside the file record itself
                    }
                    else if (len >= 0x40 && I64(attr, 0x10) == 0)   // first extent carries the sizes
                    {
                        info.HasData = true;
                        info.DataAllocated = I64(attr, 0x28);
                        info.DataSize = I64(attr, 0x30);
                        ushort attrFlags = U16(attr, 0x0C);
                        int runsOffset = U16(attr, 0x20);
                        // Compressed or sparse: the header carries the real allocated total at 0x40.
                        if ((attrFlags & 0x8001) != 0 && runsOffset >= 0x48 && len >= 0x48)
                            info.DataAllocated = I64(attr, 0x40);
                    }
                    break;
            }
            off += len;
        }
        return true;
    }

    static bool TryResidentValue(ReadOnlySpan<byte> attr, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (attr.Length < 0x18)
            return false;
        int vLen = (int)U32(attr, 0x10);
        int vOff = U16(attr, 0x14);
        if (vOff < 0x18 || vLen < 0 || vOff + vLen > attr.Length)
            return false;
        value = attr.Slice(vOff, vLen);
        return true;
    }

    /// <summary>Unnamed $DATA attribute extents in a record: (starting VCN, runs, data size or -1 when not the first extent).</summary>
    public static List<(long StartVcn, List<DataRun> Runs, long DataSize)> ReadDataExtents(ReadOnlySpan<byte> rec)
    {
        var result = new List<(long, List<DataRun>, long)>();
        int off = U16(rec, 0x14);
        int limit = (int)Math.Min(U32(rec, 0x18), (uint)rec.Length);
        while (off + 16 <= limit)
        {
            uint type = U32(rec, off);
            if (type == AttrEnd)
                break;
            int len = (int)U32(rec, off + 4);
            if (len < 16 || off + len > limit)
                break;
            var attr = rec.Slice(off, len);
            if (type == AttrData && attr[9] == 0 && attr[8] != 0 && len >= 0x40)
            {
                long startVcn = I64(attr, 0x10);
                int runsOffset = U16(attr, 0x20);
                if (runsOffset < len)
                    result.Add((startVcn, DecodeRuns(attr[runsOffset..]), startVcn == 0 ? I64(attr, 0x30) : -1));
            }
            off += len;
        }
        return result;
    }

    /// <summary>Record numbers that hold $DATA extents, from a resident $ATTRIBUTE_LIST.</summary>
    public static List<long> ReadAttributeListDataRecords(ReadOnlySpan<byte> rec)
    {
        var result = new List<long>();
        int off = U16(rec, 0x14);
        int limit = (int)Math.Min(U32(rec, 0x18), (uint)rec.Length);
        while (off + 16 <= limit)
        {
            uint type = U32(rec, off);
            if (type == AttrEnd)
                break;
            int len = (int)U32(rec, off + 4);
            if (len < 16 || off + len > limit)
                break;
            var attr = rec.Slice(off, len);
            if (type == AttrAttributeList)
            {
                if (attr[8] != 0 || !TryResidentValue(attr, out var list))
                    throw new InvalidDataException("The $MFT attribute list is stored outside its record; not supported.");
                int p = 0;
                while (p + 0x1A <= list.Length)
                {
                    uint t = U32(list, p);
                    int entryLen = U16(list, p + 4);
                    if (entryLen < 0x1A || p + entryLen > list.Length)
                        break;
                    if (t == AttrData && list[p + 6] == 0)
                        result.Add(I64(list, p + 0x10) & RecordNumberMask);
                    p += entryLen;
                }
            }
            off += len;
        }
        return result;
    }
}
