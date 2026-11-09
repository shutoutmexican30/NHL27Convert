using System.Buffers.Binary;
using System.Text;

namespace Suite.Rosters;

/// <summary>
/// A language database (loc/eng_us.db): one T3DB table of 16-byte rows (text offset, key hash, key offset, 0) sorted by hash,
/// followed by a pool holding a Huffman tree and the compressed strings. A row's offsets are relative to the pool, so
/// rows can be added without moving any existing string. Keys are stored as a 1-byte length, text as a 2-byte length,
/// each followed by MSB-first Huffman bits.
/// </summary>
public sealed class LanguageStrings
{
    const int TableStart = 36, RecordBytes = 16;
    const int FieldCount = 3, RecordsAt = TableStart + 36 + 16 * FieldCount;

    readonly byte[] _bytes;
    readonly int _count, _treeLength;
    readonly Dictionary<byte, (uint Bits, int Length)> _codes = [];
    readonly List<(uint Text, uint Hash, uint Key)> _rows;
    readonly MemoryStream _added = new();
    readonly int _poolLength;

    LanguageStrings(byte[] bytes)
    {
        _bytes = bytes;
        _count = (int)(U32(TableStart + 16) >> 16);
        _poolLength = (int)U32(TableStart + 12);
        _rows = new List<(uint, uint, uint)>(_count);
        for (int i = 0; i < _count; i++)
        {
            int at = RecordsAt + i * RecordBytes;
            _rows.Add((U32(at), U32(at + 4), U32(at + 8)));
        }
        _treeLength = (int)_rows.Min(r => Math.Min(r.Text, r.Key));
        BuildCodes(0, 0, 0);
    }

    public static LanguageStrings Load(byte[] bytes)
    {
        if (bytes.Length < RecordsAt || bytes[0] != (byte)'D' || bytes[1] != (byte)'B') throw new InvalidDataException("Not a language database.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != bytes.Length) throw new InvalidDataException("Language database size is wrong.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) != 1) throw new InvalidDataException("Expected one table in a language database.");
        return new LanguageStrings((byte[])bytes.Clone());
    }

    uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(at));
    int PoolAt => RecordsAt + _count * RecordBytes;

    void BuildCodes(int node, uint bits, int length)
    {
        if (length > 32) throw new InvalidDataException("Language tree is too deep.");
        for (int side = 0; side < 2; side++)
        {
            int at = PoolAt + node * 4 + side * 2;
            byte first = _bytes[at], second = _bytes[at + 1];
            uint code = (bits << 1) | (uint)side;
            if (first == 0 && second != 0) _codes[second] = (code, length + 1);
            else BuildCodes(first | second << 8, code, length + 1);
        }
    }

    /// <summary>CRC-32 (reflected, no initial or final inversion) of the key with bit 5 of every character cleared.</summary>
    public static uint Hash(string key)
    {
        uint crc = 0;
        foreach (byte c in Encoding.ASCII.GetBytes(key))
        {
            crc ^= (byte)(c & 0xDF);
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc;
    }

    byte[] Compress(string text)
    {
        var output = new List<byte>();
        int current = 0, used = 0;
        foreach (byte c in Encoding.Latin1.GetBytes(text))
        {
            if (!_codes.TryGetValue(c, out var code)) throw new InvalidDataException($"The game's language table has no character '{(char)c}'.");
            for (int i = code.Length - 1; i >= 0; i--)
            {
                current = current << 1 | (int)((code.Bits >> i) & 1);
                if (++used == 8) { output.Add((byte)current); current = 0; used = 0; }
            }
        }
        if (used > 0) output.Add((byte)(current << (8 - used)));
        return [.. output];
    }

    public string Decompress(int poolOffset, int length)
    {
        int bitPosition = 0, node = 0;
        var output = new StringBuilder(length);
        var pool = _bytes.AsSpan(PoolAt);
        while (output.Length < length)
        {
            int side = pool[poolOffset + bitPosition / 8] >> (7 - bitPosition % 8) & 1;
            bitPosition++;
            byte first = pool[node * 4 + side * 2], second = pool[node * 4 + side * 2 + 1];
            if (first == 0 && second != 0) { output.Append((char)second); node = 0; }
            else node = first | second << 8;
        }
        return output.ToString();
    }

    /// <summary>The text stored for a key, or null when the key is not in the table.</summary>
    public string? Find(string key)
    {
        uint hash = Hash(key);
        int index = _rows.FindIndex(r => r.Hash == hash);
        if (index < 0) return null;
        var row = _rows[index];
        int at = (int)row.Text;
        return row.Text >= _poolLength ? null : Decompress(at + 2, _bytes[PoolAt + at] << 8 | _bytes[PoolAt + at + 1]);
    }

    /// <summary>Adds a string for a key that is not in the table yet.</summary>
    public void Add(string key, string text)
    {
        if (key.Length is 0 or > 255) throw new ArgumentException("Language key length must be 1 to 255.", nameof(key));
        if (text.Length is 0 or > 0xFFFF) throw new ArgumentException("Language text is too long.", nameof(text));
        uint hash = Hash(key);
        if (_rows.Any(r => r.Hash == hash)) throw new InvalidDataException($"The language table already has {key}.");
        if (_rows.Count >= 0xFFFF) throw new InvalidDataException("The language table is full.");
        uint textAt = (uint)(_poolLength + _added.Length);
        _added.Write([(byte)(text.Length >> 8), (byte)text.Length]);
        _added.Write(Compress(text));
        uint keyAt = (uint)(_poolLength + _added.Length);
        _added.WriteByte((byte)key.Length);
        _added.Write(Compress(key));
        int index = _rows.FindIndex(r => r.Hash > hash);
        _rows.Insert(index < 0 ? _rows.Count : index, (textAt, hash, keyAt));
    }

    /// <summary>The database with every added string, its counts, sizes and checksums updated.</summary>
    public byte[] Build()
    {
        byte[] pool = _bytes.AsSpan(PoolAt, _poolLength).ToArray();
        int newCount = _rows.Count, newPool = pool.Length + (int)_added.Length, poolAt = RecordsAt + newCount * RecordBytes;
        int fileLength = poolAt + newPool + 4;
        var output = new byte[fileLength];
        _bytes.AsSpan(0, RecordsAt).CopyTo(output);
        void W(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), value);
        for (int i = 0; i < newCount; i++)
        {
            int at = RecordsAt + i * RecordBytes;
            W(at, _rows[i].Text); W(at + 4, _rows[i].Hash); W(at + 8, _rows[i].Key); W(at + 12, 0);
        }
        pool.CopyTo(output, poolAt);
        _added.ToArray().CopyTo(output, poolAt + pool.Length);
        W(8, (uint)fileLength);
        W(20, T3Db.Crc(output.AsSpan(0, 20)));
        W(TableStart + 12, (uint)newPool);
        W(TableStart + 16, (uint)(newCount << 16 | newCount));
        W(TableStart + 32, T3Db.Crc(output.AsSpan(TableStart, 32)));
        W(fileLength - 4, T3Db.Crc(output.AsSpan(TableStart + 36, fileLength - 4 - (TableStart + 36))));
        return output;
    }

    /// <summary>The bytes of an empty language database with a tree of the given characters, for tests.</summary>
    public static byte[] ForTest(string alphabet)
    {
        var leaves = Encoding.ASCII.GetBytes(alphabet);
        var nodes = new List<(byte, byte, byte, byte)>();
        void Build(int low, int high)
        {
            int self = nodes.Count;
            nodes.Add(default);
            int mid = (low + high) / 2;
            (byte, byte) Side(int a, int b)
            {
                if (b - a == 1) return (0, leaves[a]);
                int child = nodes.Count;
                Build(a, b);
                return ((byte)child, (byte)(child >> 8));
            }
            var left = Side(low, mid);
            var right = Side(mid, high);
            nodes[self] = (left.Item1, left.Item2, right.Item1, right.Item2);
        }
        Build(0, leaves.Length);
        var pool = nodes.SelectMany(n => new[] { n.Item1, n.Item2, n.Item3, n.Item4 }).ToArray();
        // One row, so the tree's length can be read from the first string offset.
        var firstKey = new byte[] { 0, 0 };
        var data = pool.Concat(new byte[] { 0, 1, 0x80, 1, 0x80 }).ToArray();
        var output = new byte[RecordsAt + RecordBytes + data.Length + 4];
        void W(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), value);
        output[0] = (byte)'D'; output[1] = (byte)'B';
        W(8, (uint)output.Length); W(16, 1);
        W(TableStart + 4, RecordBytes); W(TableStart + 12, (uint)data.Length); W(TableStart + 16, 1u << 16 | 1u); W(TableStart + 24, FieldCount);
        W(RecordsAt, (uint)pool.Length); W(RecordsAt + 4, Hash("A")); W(RecordsAt + 8, (uint)(pool.Length + 3));
        data.CopyTo(output, RecordsAt + RecordBytes);
        _ = firstKey;
        return output;
    }
}
