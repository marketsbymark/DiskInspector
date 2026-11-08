using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using DiskInspector.Core.Scanning;

namespace DiskInspector.Core.Analysis;

/// <summary>What a document says about itself. Any field may be null when the file does not record it.</summary>
public sealed record DocumentInfo(string? Author, string? LastSavedBy, string? Title)
{
    public static readonly DocumentInfo Empty = new(null, null, null);
}

/// <summary>
/// Reads author, last-saved-by and title from Office (new and old formats), Outlook .msg, .eml and PDF files.
/// Files are opened read-only, their last-access date is left unchanged, and cloud placeholders are never opened.
/// </summary>
public static class DocumentProperties
{
    static readonly HashSet<string> OpenXml = new(StringComparer.OrdinalIgnoreCase)
        { "docx", "docm", "dotx", "dotm", "xlsx", "xlsm", "xlsb", "xltx", "xltm", "xlam", "pptx", "pptm", "ppsx", "ppsm", "potx", "potm", "vsdx" };

    static readonly HashSet<string> Compound = new(StringComparer.OrdinalIgnoreCase)
        { "doc", "dot", "xls", "xlt", "xla", "ppt", "pps", "pot", "msg", "oft", "pub", "vsd" };

    static DocumentProperties() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>True when files with this extension can carry an author we know how to read.</summary>
    public static bool CanRead(string extension) =>
        OpenXml.Contains(extension) || Compound.Contains(extension) || extension.Equals("pdf", StringComparison.OrdinalIgnoreCase)
        || extension.Equals("eml", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the properties, or returns null if the file could not be opened or parsed.</summary>
    public static DocumentInfo? TryRead(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.');
        if (!CanRead(ext))
            return null;
        try
        {
            using var stream = Native.OpenForPeek(path);
            if (stream is null)
                return null;
            if (OpenXml.Contains(ext))
            {
                // Password-protected Office files are compound files, not zips; they rarely keep an author.
                Span<byte> sig = stackalloc byte[8];
                bool compound = stream.Read(sig) == 8 && BinaryPrimitives.ReadUInt64LittleEndian(sig) == 0xE11AB1A1E011CFD0;
                stream.Position = 0;
                return compound ? ReadOleSummary(stream) : ReadOpenXml(stream);
            }
            if (ext.Equals("pdf", StringComparison.OrdinalIgnoreCase))
                return ReadPdf(stream);
            if (ext.Equals("eml", StringComparison.OrdinalIgnoreCase))
                return ReadEml(stream);
            return ext.Equals("msg", StringComparison.OrdinalIgnoreCase) || ext.Equals("oft", StringComparison.OrdinalIgnoreCase)
                ? ReadMsg(stream)
                : ReadOleSummary(stream);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or XmlException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException or OverflowException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    // ---------- Office Open XML: docProps/core.xml inside the zip ----------

    internal static DocumentInfo ReadOpenXml(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("docProps/core.xml", StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length > 4 * 1024 * 1024)
            return DocumentInfo.Empty;
        using var xml = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        string? author = null, lastBy = null, title = null;
        xml.Read();
        while (!xml.EOF)
        {
            // ReadElementContentAsString already moves to the next node, so only call Read() otherwise
            // (an empty <dc:title></dc:title> would otherwise make us skip the element after it).
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName is "creator" or "lastModifiedBy" or "title")
            {
                string name = xml.LocalName;
                string value = xml.ReadElementContentAsString();
                if (name == "creator") author = Clean(value);
                else if (name == "lastModifiedBy") lastBy = Clean(value);
                else title = Clean(value);
            }
            else
            {
                xml.Read();
            }
        }
        return new DocumentInfo(author, lastBy, title);
    }

    // ---------- Legacy Office: the SummaryInformation property set in the compound file ----------

    internal static DocumentInfo ReadOleSummary(Stream stream)
    {
        var cf = new CompoundFile(stream);
        var data = cf.ReadRootStream("\u0005SummaryInformation");
        if (data is null)
            return DocumentInfo.Empty;
        var props = ReadPropertySet(data);
        return new DocumentInfo(props.GetValueOrDefault(4u), props.GetValueOrDefault(8u), props.GetValueOrDefault(2u));
    }

    /// <summary>String properties of the first section of an OLE property set (MS-OLEPS), by property id.</summary>
    internal static Dictionary<uint, string> ReadPropertySet(byte[] d)
    {
        var result = new Dictionary<uint, string>();
        if (d.Length < 48 || BinaryPrimitives.ReadUInt16LittleEndian(d) != 0xFFFE)
            return result;
        int section = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x2C));
        if (section < 0 || section + 8 > d.Length)
            return result;
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(section + 4));
        if (count < 0 || count > 1000 || section + 8 + count * 8 > d.Length)
            return result;

        int codePage = 1252;
        var entries = new List<(uint Id, int Offset)>();
        for (int k = 0; k < count; k++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(section + 8 + k * 8));
            int off = section + (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(section + 12 + k * 8));
            if (off < section || off + 4 > d.Length)
                continue;
            entries.Add((id, off));
            if (id == 1 && BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(off)) == 2 /* VT_I2 */ && off + 6 <= d.Length)
                codePage = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(off + 4));
        }
        Encoding enc;
        try { enc = codePage == 1200 ? Encoding.Unicode : Encoding.GetEncoding(codePage); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { enc = Encoding.Latin1; }

        foreach (var (id, off) in entries)
        {
            ushort vt = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(off));
            if (off + 8 > d.Length)
                continue;
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 4));
            if (vt == 0x1E && len > 0 && off + 8 + len <= d.Length)          // VT_LPSTR
                result[id] = Clean(enc.GetString(d, off + 8, len)) ?? "";
            else if (vt == 0x1F && len > 0 && off + 8 + len * 2 <= d.Length) // VT_LPWSTR
                result[id] = Clean(Encoding.Unicode.GetString(d, off + 8, len * 2)) ?? "";
        }
        foreach (var key in result.Where(kv => kv.Value.Length == 0).Select(kv => kv.Key).ToList())
            result.Remove(key);
        return result;
    }

    // ---------- Outlook .msg: sender name and subject ----------

    internal static DocumentInfo ReadMsg(Stream stream)
    {
        var cf = new CompoundFile(stream);
        string? Prop(string tag)
        {
            var w = cf.ReadRootStream($"__substg1.0_{tag}001F");
            if (w is not null)
                return Clean(Encoding.Unicode.GetString(w));
            var a = cf.ReadRootStream($"__substg1.0_{tag}001E");
            return a is null ? null : Clean(Encoding.Latin1.GetString(a));
        }
        string? sender = Prop("0C1A") ?? Prop("0042") ?? Prop("0C1F");
        return new DocumentInfo(sender, null, Prop("0037"));
    }

    // ---------- .eml: From and Subject headers ----------

    internal static DocumentInfo ReadEml(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        int n = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        string text = Encoding.UTF8.GetString(buffer, 0, n);
        int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0)
            end = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (end >= 0)
            text = text[..end];
        // Unfold continuation lines.
        text = text.Replace("\r\n ", " ").Replace("\r\n\t", " ").Replace("\n ", " ").Replace("\n\t", " ");
        string? Header(string name)
        {
            foreach (var line in text.Split('\n'))
                if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                    return Clean(DecodeMimeWords(line[(name.Length + 1)..].Trim()));
            return null;
        }
        return new DocumentInfo(Header("From"), null, Header("Subject"));
    }

    /// <summary>Decodes RFC 2047 words such as =?utf-8?B?...?= and =?iso-8859-1?Q?...?=.</summary>
    internal static string DecodeMimeWords(string s)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            int start = s.IndexOf("=?", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(s, i, s.Length - i);
                break;
            }
            int q1 = s.IndexOf('?', start + 2), q2 = q1 < 0 ? -1 : s.IndexOf('?', q1 + 1), stop = q2 < 0 ? -1 : s.IndexOf("?=", q2 + 1, StringComparison.Ordinal);
            if (stop < 0)
            {
                sb.Append(s, i, s.Length - i);
                break;
            }
            string between = s[i..start];
            if (!(sb.Length > 0 && string.IsNullOrWhiteSpace(between) && i > 0))
                sb.Append(between);
            string charset = s[(start + 2)..q1], mode = s[(q1 + 1)..q2], payload = s[(q2 + 1)..stop];
            try
            {
                var enc = Encoding.GetEncoding(charset);
                byte[] bytes = mode.Equals("B", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(payload)
                    : DecodeQ(payload);
                sb.Append(enc.GetString(bytes));
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                sb.Append(s, start, stop + 2 - start);
            }
            i = stop + 2;
        }
        return sb.ToString();
    }

    static byte[] DecodeQ(string q)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < q.Length; i++)
        {
            if (q[i] == '_')
                bytes.Add(0x20);
            else if (q[i] == '=' && i + 2 < q.Length && Uri.IsHexDigit(q[i + 1]) && Uri.IsHexDigit(q[i + 2]))
            {
                bytes.Add(Convert.ToByte(q.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
                bytes.Add((byte)q[i]);
        }
        return [.. bytes];
    }

    // ---------- PDF: /Author and /Title in the document information dictionary ----------

    internal static DocumentInfo ReadPdf(Stream stream)
    {
        const int Window = 1024 * 1024;
        long length = stream.Length;
        byte[] head = ReadRange(stream, 0, (int)Math.Min(length, Window));
        byte[] tail = length > Window ? ReadRange(stream, Math.Max(Window, length - Window), (int)Math.Min(Window, length - Window)) : [];
        if (head.AsSpan(0, Math.Min(head.Length, 1024)).IndexOf("%PDF"u8) < 0)
            return DocumentInfo.Empty;
        // Encrypted documents keep their strings encrypted.
        if (Contains(tail, "/Encrypt"u8) || Contains(head, "/Encrypt"u8))
            return DocumentInfo.Empty;
        // The newest information dictionary is the last one (incremental updates append).
        string? author = LastPdfString(tail, "/Author"u8) ?? LastPdfString(head, "/Author"u8) ?? XmpCreator(head) ?? XmpCreator(tail);
        string? title = LastPdfString(tail, "/Title"u8) ?? LastPdfString(head, "/Title"u8);
        return new DocumentInfo(author, null, title);
    }

    static byte[] ReadRange(Stream s, long offset, int count)
    {
        var buffer = new byte[count];
        s.Position = offset;
        int n = s.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        return n == count ? buffer : buffer[..n];
    }

    static bool Contains(byte[] data, ReadOnlySpan<byte> token) => data.AsSpan().IndexOf(token) >= 0;

    internal static string? LastPdfString(byte[] data, ReadOnlySpan<byte> key)
    {
        var span = data.AsSpan();
        int at = span.LastIndexOf(key);
        while (at >= 0)
        {
            int p = at + key.Length;
            // The key must end here ("/Author" not "/AuthorX").
            if (p < span.Length && (span[p] is (byte)'(' or (byte)'<' or (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t'))
            {
                while (p < span.Length && span[p] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')
                    p++;
                if (p < span.Length && span[p] == '(')
                    return Clean(DecodePdfText(ParseLiteral(span, p + 1)));
                if (p + 1 < span.Length && span[p] == '<' && span[p + 1] != '<')
                    return Clean(DecodePdfText(ParseHex(span, p + 1)));
            }
            at = at == 0 ? -1 : span[..at].LastIndexOf(key);
        }
        return null;
    }

    static byte[] ParseLiteral(ReadOnlySpan<byte> s, int p)
    {
        var bytes = new List<byte>();
        int depth = 1;
        for (; p < s.Length && bytes.Count < 4096; p++)
        {
            byte b = s[p];
            if (b == '\\' && p + 1 < s.Length)
            {
                byte n = s[++p];
                switch (n)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case (byte)'\r':
                        if (p + 1 < s.Length && s[p + 1] == '\n') p++;
                        break;
                    case (byte)'\n': break;
                    default:
                        if (n is >= (byte)'0' and <= (byte)'7')
                        {
                            int v = n - '0';
                            for (int k = 0; k < 2 && p + 1 < s.Length && s[p + 1] is >= (byte)'0' and <= (byte)'7'; k++)
                                v = v * 8 + (s[++p] - '0');
                            bytes.Add((byte)v);
                        }
                        else
                            bytes.Add(n);
                        break;
                }
                continue;
            }
            if (b == '(') depth++;
            if (b == ')' && --depth == 0) break;
            bytes.Add(b);
        }
        return [.. bytes];
    }

    static byte[] ParseHex(ReadOnlySpan<byte> s, int p)
    {
        var hex = new StringBuilder();
        for (; p < s.Length && s[p] != '>' && hex.Length < 8192; p++)
            if (Uri.IsHexDigit((char)s[p]))
                hex.Append((char)s[p]);
        if (hex.Length % 2 == 1)
            hex.Append('0');
        return Convert.FromHexString(hex.ToString());
    }

    static string DecodePdfText(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        return Encoding.Latin1.GetString(b);   // close enough to PDFDocEncoding for names
    }

    static string? XmpCreator(byte[] data)
    {
        var span = data.AsSpan();
        int c = span.IndexOf("<dc:creator"u8);
        if (c < 0)
            return null;
        int li = span[c..].IndexOf("<rdf:li"u8);
        if (li < 0)
            return null;
        int start = span[(c + li)..].IndexOf((byte)'>');
        if (start < 0)
            return null;
        int from = c + li + start + 1;
        int end = span[from..].IndexOf("</rdf:li>"u8);
        if (end < 0 || end > 2000)
            return null;
        return Clean(System.Net.WebUtility.HtmlDecode(Encoding.UTF8.GetString(span.Slice(from, end))));
    }

    static string? Clean(string? s)
    {
        if (s is null)
            return null;
        s = new string(s.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return s.Length == 0 ? null : s.Length > 200 ? s[..200] : s;
    }
}

/// <summary>
/// Minimal reader for OLE compound files (MS-CFB), enough to read streams directly under the root storage.
/// Every count and chain is bounds-checked so a damaged file just yields nothing.
/// </summary>
internal sealed class CompoundFile
{
    const uint EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF, NoStream = 0xFFFFFFFF;
    readonly Stream _s;
    readonly int _sectorSize;
    readonly int _miniSectorSize;
    readonly uint _miniCutoff;
    readonly uint[] _fat;
    readonly List<DirEntry> _dir;
    uint[]? _miniFat;
    byte[]? _miniStream;
    readonly uint _firstMiniFat;
    readonly int _miniFatCount;

    record struct DirEntry(string Name, byte Type, uint Left, uint Right, uint Child, uint Start, long Size);

    public CompoundFile(Stream s)
    {
        _s = s;
        var h = new byte[512];
        s.Position = 0;
        if (s.ReadAtLeast(h, 512, throwOnEndOfStream: false) < 512 || BinaryPrimitives.ReadUInt64LittleEndian(h) != 0xE11AB1A1E011CFD0)
            throw new InvalidDataException("Not a compound file.");
        int shift = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(0x1E));
        int miniShift = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(0x20));
        if (shift is not (9 or 12) || miniShift != 6)
            throw new InvalidDataException("Unsupported compound file sector size.");
        _sectorSize = 1 << shift;
        _miniSectorSize = 1 << miniShift;
        int fatCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x2C));
        uint firstDir = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x30));
        _miniCutoff = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x38));
        _firstMiniFat = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x3C));
        _miniFatCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x40));
        uint difat = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x44));
        int difatCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x48));
        long maxSectors = Math.Max(1, s.Length / _sectorSize);
        if (fatCount < 0 || fatCount > maxSectors || difatCount < 0 || difatCount > maxSectors)
            throw new InvalidDataException("Damaged compound file header.");

        // FAT sector numbers: 109 in the header, the rest in the DIFAT chain.
        var fatSectors = new List<uint>();
        for (int i = 0; i < 109 && fatSectors.Count < fatCount; i++)
            fatSectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x4C + i * 4)));
        var sector = new byte[_sectorSize];
        for (int k = 0; k < difatCount && difat < EndOfChain && fatSectors.Count < fatCount; k++)
        {
            ReadSector(difat, sector);
            int per = _sectorSize / 4 - 1;
            for (int i = 0; i < per && fatSectors.Count < fatCount; i++)
                fatSectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(i * 4)));
            difat = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(per * 4));
        }
        _fat = new uint[fatSectors.Count * (_sectorSize / 4)];
        for (int f = 0; f < fatSectors.Count; f++)
        {
            ReadSector(fatSectors[f], sector);
            for (int i = 0; i < _sectorSize / 4; i++)
                _fat[f * (_sectorSize / 4) + i] = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(i * 4));
        }

        var dirBytes = ReadChain(firstDir, long.MaxValue);
        _dir = [];
        for (int off = 0; off + 128 <= dirBytes.Length; off += 128)
        {
            var e = dirBytes.AsSpan(off, 128);
            int nameLen = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(e[0x40..]), 0, 64);
            string name = Encoding.Unicode.GetString(e[..Math.Max(0, nameLen - 2)]);
            _dir.Add(new DirEntry(name, e[0x42], BinaryPrimitives.ReadUInt32LittleEndian(e[0x44..]), BinaryPrimitives.ReadUInt32LittleEndian(e[0x48..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[0x4C..]), BinaryPrimitives.ReadUInt32LittleEndian(e[0x74..]),
                (long)(BinaryPrimitives.ReadUInt64LittleEndian(e[0x78..]) & (_sectorSize == 512 ? 0xFFFFFFFFUL : ulong.MaxValue))));
        }
        if (_dir.Count == 0 || _dir[0].Type != 5)
            throw new InvalidDataException("Compound file has no root entry.");
    }

    void ReadSector(uint sector, byte[] buffer)
    {
        long offset = ((long)sector + 1) * _sectorSize;
        if (sector >= 0xFFFFFFFA || offset + _sectorSize > _s.Length)
            throw new InvalidDataException("Sector out of range.");
        _s.Position = offset;
        _s.ReadExactly(buffer, 0, _sectorSize);
    }

    byte[] ReadChain(uint start, long size)
    {
        var ms = new MemoryStream();
        var sector = new byte[_sectorSize];
        int guard = 0;
        for (uint cur = start; cur != EndOfChain && cur != FreeSect; cur = cur < _fat.Length ? _fat[cur] : EndOfChain)
        {
            if (++guard > _fat.Length + 1)
                throw new InvalidDataException("Loop in compound file chain.");
            ReadSector(cur, sector);
            ms.Write(sector);
            if (ms.Length >= size)
                break;
        }
        var bytes = ms.ToArray();
        return size < bytes.Length ? bytes[..(int)size] : bytes;
    }

    byte[] ReadMiniChain(uint start, long size)
    {
        if (_miniFat is null)
        {
            var mf = _miniFatCount > 0 ? ReadChain(_firstMiniFat, (long)_miniFatCount * _sectorSize) : [];
            _miniFat = new uint[mf.Length / 4];
            for (int i = 0; i < _miniFat.Length; i++)
                _miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(mf.AsSpan(i * 4));
            _miniStream = ReadChain(_dir[0].Start, _dir[0].Size);
        }
        var result = new MemoryStream();
        int guard = 0;
        for (uint cur = start; cur != EndOfChain && cur != FreeSect && result.Length < size; cur = cur < _miniFat.Length ? _miniFat[cur] : EndOfChain)
        {
            if (++guard > _miniFat.Length + 1)
                throw new InvalidDataException("Loop in mini stream chain.");
            long off = (long)cur * _miniSectorSize;
            if (off + _miniSectorSize > _miniStream!.Length)
                throw new InvalidDataException("Mini sector out of range.");
            result.Write(_miniStream, (int)off, _miniSectorSize);
        }
        var bytes = result.ToArray();
        return size < bytes.Length ? bytes[..(int)size] : bytes;
    }

    /// <summary>Contents of a stream that sits directly in the root storage, or null if there is none.</summary>
    public byte[]? ReadRootStream(string name)
    {
        // The root's children form a binary tree linked through Left/Right.
        var stack = new Stack<uint>();
        stack.Push(_dir[0].Child);
        int guard = 0;
        while (stack.Count > 0 && ++guard < 100_000)
        {
            uint id = stack.Pop();
            if (id == NoStream || id >= _dir.Count)
                continue;
            var e = _dir[(int)id];
            if (e.Type == 2 && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (e.Size <= 0 || e.Size > 16 * 1024 * 1024)
                    return e.Size == 0 ? [] : null;
                return e.Size < _miniCutoff ? ReadMiniChain(e.Start, e.Size) : ReadChain(e.Start, e.Size);
            }
            stack.Push(e.Left);
            stack.Push(e.Right);
        }
        return null;
    }
}
