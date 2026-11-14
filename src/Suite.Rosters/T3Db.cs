using System.Buffers.Binary;
using System.Xml.Linq;

namespace Suite.Rosters;

/// <summary>
/// NHL 24's T3DB roster databases, read and edited in place by the suite (replaces the Worker's db-import for edits that
/// keep the layout: integer fields changed in existing in-use records). Never grows a table, never moves a record, never
/// touches strings, so the file size and layout stay identical and only the edited table's section CRC changes.
///
/// Format (verified on all five stock databases, every CRC reproduced):
///   0  "DB\0\x08", u32 0, u32 file size, u32 0, u32 table count, u32 header CRC (bytes 0..20)
///   24 table list: count x [4-char short name][u32 offset from the end of the list CRC], then the list CRC
///   table: 32-byte header (u32: 2, record bytes, record bits, 0, (in-use &lt;&lt; 16 | ?), 0xFFFF0000, field count, 0),
///          u32 CRC of those 32 bytes, field descriptors (16 bytes: u32 type, u32 bit offset, 4-char short name,
///          u32 bit depth), the records (record bytes each, little-endian bit order inside a record), and a u32 CRC of
///          everything from the descriptors to it.
///   integers are stored as value - rangelow (rangelow from the shared meta XML, nhl_game-meta.xml).
///   CRC = CRC-32/MPEG-2 (poly 0x04C11DB7, init 0xFFFFFFFF, not reflected, no final xor).
/// </summary>
public sealed class T3Db
{
    public sealed record Field(string Name, string Short, int Type, int BitOffset, int Depth, long RangeLow);
    public sealed class Table
    {
        public required string Name { get; init; }
        public required string Short { get; init; }
        public required int Start { get; init; }
        public required int End { get; init; }            // exclusive; the section CRC is the u32 at End - 4
        public required int RecordBytes { get; init; }
        public required int RecordsAt { get; init; }
        public required int InUse { get; init; }
        public required Dictionary<string, Field> Fields { get; init; }
        public int Slots => (End - 4 - RecordsAt) / RecordBytes;
    }

    public byte[] Bytes { get; }
    public Dictionary<string, Table> Tables { get; } = new(StringComparer.Ordinal);
    readonly HashSet<Table> _dirty = [];

    T3Db(byte[] bytes) { Bytes = bytes; }

    public static T3Db Load(byte[] bytes, string metaXml)
    {
        var db = new T3Db(bytes);
        if (bytes.Length < 28 || bytes[0] != (byte)'D' || bytes[1] != (byte)'B') throw new InvalidDataException("Not a T3DB file.");
        if (U32(bytes, 8) != bytes.Length) throw new InvalidDataException("File size field disagrees with the file.");
        if (Crc(bytes.AsSpan(0, 20)) != U32(bytes, 20)) throw new InvalidDataException("Header CRC mismatch.");
        int n = (int)U32(bytes, 16), listEnd = 24 + 8 * n;
        if (Crc(bytes.AsSpan(24, 8 * n)) != U32(bytes, listEnd)) throw new InvalidDataException("Table list CRC mismatch.");
        int bas = listEnd + 4;
        var meta = XDocument.Load(metaXml).Descendants("table").ToDictionary(t => (string)t.Attribute("shortname")!, t => t);
        var starts = Enumerable.Range(0, n).Select(i => (Short: System.Text.Encoding.ASCII.GetString(bytes, 24 + 8 * i, 4), At: bas + (int)U32(bytes, 28 + 8 * i)))
            .OrderBy(x => x.At).ToList();
        for (int k = 0; k < starts.Count; k++)
        {
            var (shortName, s) = starts[k];
            int end = k + 1 < starts.Count ? starts[k + 1].At : bytes.Length;
            if (Crc(bytes.AsSpan(s, 32)) != U32(bytes, s + 32)) throw new InvalidDataException($"{shortName}: table header CRC mismatch.");
            if (Crc(bytes.AsSpan(s + 36, end - 4 - (s + 36))) != U32(bytes, end - 4)) throw new InvalidDataException($"{shortName}: section CRC mismatch.");
            int fieldCount = (int)U32(bytes, s + 24);
            if (!meta.TryGetValue(shortName, out var mt)) throw new InvalidDataException($"{shortName}: not in the meta XML.");
            var metaFields = mt.Descendants("field").ToDictionary(f => (string)f.Attribute("shortname")!, f => f);
            var fields = new Dictionary<string, Field>(StringComparer.Ordinal);
            for (int i = 0; i < fieldCount; i++)
            {
                int d = s + 36 + 16 * i;
                var fs = System.Text.Encoding.ASCII.GetString(bytes, d + 8, 4);
                var mf = metaFields[fs];
                fields[(string)mf.Attribute("name")!] = new Field((string)mf.Attribute("name")!, fs, (int)U32(bytes, d), (int)U32(bytes, d + 4),
                    (int)U32(bytes, d + 12), long.TryParse((string?)mf.Attribute("rangelow"), out var lo) ? lo : 0);
            }
            var table = new Table
            {
                Name = (string)mt.Attribute("name")!, Short = shortName, Start = s, End = end, RecordBytes = (int)U32(bytes, s + 4),
                RecordsAt = s + 36 + 16 * fieldCount, InUse = (int)(U32(bytes, s + 16) >> 16), Fields = fields,
            };
            db.Tables[table.Name] = table;
        }
        return db;
    }

    public long GetInt(Table t, int record, string field)
    {
        var f = IntField(t, field);
        int at = t.RecordsAt + record * t.RecordBytes;
        ulong v = 0;
        for (int b = 0; b < f.Depth; b++)
        {
            int bit = f.BitOffset + b;
            if ((Bytes[at + (bit >> 3)] >> (bit & 7) & 1) != 0) v |= 1UL << b;
        }
        return (long)v + f.RangeLow;
    }

    public void SetInt(Table t, int record, string field, long value)
    {
        var f = IntField(t, field);
        if (record < 0 || record >= t.InUse) throw new ArgumentOutOfRangeException(nameof(record), $"{t.Name}: record {record} is not in use ({t.InUse}).");
        long raw = value - f.RangeLow;
        if (raw < 0 || (f.Depth < 63 && raw >= 1L << f.Depth)) throw new ArgumentOutOfRangeException(nameof(value), $"{t.Name}.{field}: {value} does not fit {f.Depth} bits from {f.RangeLow}.");
        int at = t.RecordsAt + record * t.RecordBytes;
        for (int b = 0; b < f.Depth; b++)
        {
            int bit = f.BitOffset + b, p = at + (bit >> 3), m = 1 << (bit & 7);
            Bytes[p] = (byte)(((raw >> b) & 1) != 0 ? Bytes[p] | m : Bytes[p] & ~m);
        }
        _dirty.Add(t);
    }

    static Field IntField(Table t, string field)
    {
        if (!t.Fields.TryGetValue(field, out var f)) throw new KeyNotFoundException($"{t.Name} has no field {field}.");
        if (f.Type != 3) throw new InvalidOperationException($"{t.Name}.{field} is not an integer field (type {f.Type}).");
        return f;
    }

    /// <summary>Reads a string field (type 0) of a record, up to the first NUL. Strings are read-only in this editor:
    /// writing a different length would move the table's bytes and break the layout.</summary>
    public string GetString(Table t, int record, string field)
    {
        if (!t.Fields.TryGetValue(field, out var f)) throw new KeyNotFoundException($"{t.Name} has no field {field}.");
        if (f.Type != 0) throw new InvalidOperationException($"{t.Name}.{field} is not a string field (type {f.Type}).");
        if (record < 0 || record >= t.InUse) throw new ArgumentOutOfRangeException(nameof(record), $"{t.Name}: record {record} is not in use ({t.InUse}).");
        var span = Bytes.AsSpan(t.RecordsAt + record * t.RecordBytes + f.BitOffset / 8, f.Depth / 8);
        var end = span.IndexOf((byte)0);
        if (end >= 0) span = span[..end];
        return System.Text.Encoding.UTF8.GetString(span);
    }

    /// <summary>
    /// Writes a fixed-width string field (type 0) in place: UTF-8 bytes, NUL-padded to the field's width, which must hold
    /// the text plus one NUL. The file size and layout never change; refuses rather than truncates.
    /// </summary>
    public void SetString(Table t, int record, string field, string value)
    {
        if (!t.Fields.TryGetValue(field, out var f)) throw new KeyNotFoundException($"{t.Name} has no field {field}.");
        if (f.Type != 0) throw new InvalidOperationException($"{t.Name}.{field} is not a string field (type {f.Type}).");
        if (f.BitOffset % 8 != 0 || f.Depth % 8 != 0) throw new InvalidDataException($"{t.Name}.{field}: string not byte aligned.");
        if (record < 0 || record >= t.InUse) throw new ArgumentOutOfRangeException(nameof(record), $"{t.Name}: record {record} is not in use ({t.InUse}).");
        var text = System.Text.Encoding.UTF8.GetBytes(value);
        int width = f.Depth / 8;
        if (text.Length + 1 > width) throw new ArgumentOutOfRangeException(nameof(value), $"{t.Name}.{field}: '{value}' needs {text.Length + 1} bytes, the field holds {width}.");
        var span = Bytes.AsSpan(t.RecordsAt + record * t.RecordBytes + f.BitOffset / 8, width);
        span.Clear();
        text.CopyTo(span);
        _dirty.Add(t);
    }

    /// <summary>
    /// Adds one record at <paramref name="at"/>, a copy of record <paramref name="copyOf"/> (an index before the insert),
    /// inside the table's own spare slots: the records from <paramref name="at"/> move down one slot, the header's in-use
    /// count (high 16 bits of u32 +16) goes up and its CRC is recomputed.  The file size and every other table stay put;
    /// only usable for tables whose records nothing points at by position (stockteamjerseys: keyed by team + variant).
    /// Returns the table with the new count (also replacing it in <see cref="Tables"/>).
    /// </summary>
    public Table InsertRecord(Table t, int at, int copyOf)
    {
        if (t.InUse >= t.Slots) throw new InvalidOperationException($"{t.Name}: no spare slot ({t.InUse}/{t.Slots}).");
        if (at < 0 || at > t.InUse || copyOf < 0 || copyOf >= t.InUse) throw new ArgumentOutOfRangeException(nameof(at));
        var copy = Bytes.AsSpan(t.RecordsAt + copyOf * t.RecordBytes, t.RecordBytes).ToArray();
        int from = t.RecordsAt + at * t.RecordBytes;
        Buffer.BlockCopy(Bytes, from, Bytes, from + t.RecordBytes, (t.InUse - at) * t.RecordBytes);
        copy.CopyTo(Bytes.AsSpan(from));
        uint head = U32(Bytes, t.Start + 16);
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(t.Start + 16), (uint)(t.InUse + 1) << 16 | (head & 0xFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(t.Start + 32), Crc(Bytes.AsSpan(t.Start, 32)));
        var grown = new Table { Name = t.Name, Short = t.Short, Start = t.Start, End = t.End, RecordBytes = t.RecordBytes, RecordsAt = t.RecordsAt, InUse = t.InUse + 1, Fields = t.Fields };
        Tables[t.Name] = grown;
        _dirty.Remove(t);
        _dirty.Add(grown);
        return grown;
    }

    /// <summary>
    /// Removes in-use record <paramref name="at"/> by moving the records after it down one slot. The file size and every
    /// other table stay put; only usable for tables whose records nothing points at by position (stockteamjerseys is keyed).
    /// Returns the table with the new count (also replacing it in <see cref="Tables"/>).
    /// </summary>
    public Table DeleteRecord(Table t, int at)
    {
        if (at < 0 || at >= t.InUse) throw new ArgumentOutOfRangeException(nameof(at));
        var tail = Bytes.AsSpan(t.RecordsAt + (at + 1) * t.RecordBytes, (t.InUse - at - 1) * t.RecordBytes).ToArray();
        tail.CopyTo(Bytes.AsSpan(t.RecordsAt + at * t.RecordBytes));
        uint head = U32(Bytes, t.Start + 16);
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(t.Start + 16), (uint)(t.InUse - 1) << 16 | (head & 0xFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(t.Start + 32), Crc(Bytes.AsSpan(t.Start, 32)));
        var shrunk = new Table { Name = t.Name, Short = t.Short, Start = t.Start, End = t.End, RecordBytes = t.RecordBytes, RecordsAt = t.RecordsAt, InUse = t.InUse - 1, Fields = t.Fields };
        Tables[t.Name] = shrunk;
        _dirty.Remove(t);
        _dirty.Add(shrunk);
        return shrunk;
    }

    /// <summary>Recomputes the section CRC of every edited table and returns the bytes (same length as loaded).</summary>
    public byte[] Save()
    {
        foreach (var t in _dirty)
            BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(t.End - 4), Crc(Bytes.AsSpan(t.Start + 36, t.End - 4 - (t.Start + 36))));
        _dirty.Clear();
        return Bytes;
    }

    /// <summary>
    /// Applies an edited CSV dump in place: for each table CSV in <paramref name="editDir"/>, every integer cell that differs
    /// from <paramref name="beforeDir"/> is written to the same record. Refuses anything that is not an in-place integer
    /// edit (a different in-use count, a changed string, a changed key order), so the layout cannot move.
    /// Returns the number of cells written per table.
    /// </summary>
    public Dictionary<string, int> ApplyCsvEdits(string beforeDir, string editDir)
    {
        var counts = new Dictionary<string, int>();
        foreach (var file in Directory.GetFiles(editDir, "*.csv"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!Tables.TryGetValue(name, out var t)) throw new InvalidOperationException($"{name}: not in this database.");
            var before = T3Csv.Load(beforeDir, name);
            var after = T3Csv.Load(editDir, name);
            if (after.ValidCount != before.ValidCount || before.ValidCount != t.InUse)
                throw new InvalidOperationException($"{name}: in-use rows {before.ValidCount} -> {after.ValidCount} (file {t.InUse}); only in-place edits are supported.");
            int n = 0;
            for (int r = 0; r < t.InUse; r++)
            {
                var a = before.Rows[r]; var b = after.Rows[r];
                foreach (var (field, f) in t.Fields)
                {
                    if (!before.Has(field)) continue;
                    string va = before.Get(a, field), vb = after.Get(b, field);
                    if (va == vb) continue;
                    if (f.Type != 3) throw new InvalidOperationException($"{name}.{field} row {r}: non-integer change '{va}' -> '{vb}'.");
                    if (GetInt(t, r, field) != long.Parse(va)) throw new InvalidOperationException($"{name}.{field} row {r}: file holds {GetInt(t, r, field)}, the before dump says {va}.");
                    SetInt(t, r, field, long.Parse(vb));
                    n++;
                }
            }
            counts[name] = n;
        }
        return counts;
    }

    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i << 24;
        for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
        return c;
    }).ToArray();

    public static uint Crc(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data) c = (c << 8) ^ CrcTable[((c >> 24) ^ b) & 0xFF];
        return c;
    }
}
