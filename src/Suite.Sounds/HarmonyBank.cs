using System.Buffers.Binary;

namespace Suite.Sounds;

/// <summary>
/// Read-only view of a Harmony "SBle" bank: the RES that sits beside every NewWaveAsset (and the hornbank/music banks).
/// Layout (little endian), worked out on NHL 24 speech waves:
/// <code>
/// header  "SBle" u32 size, u16 ?, u16 tableCount, u32 bankHash ... at 0x50: tableCount x { u32 tableOffset, u32 ? }
/// table   "TESD" u32 size, u32 nameHash, u32 bankHash, ... +0x38 u32 rows, +0x3C u16 columns, +0x40 u16 descriptorOffset
/// column  24 bytes: u32 tagHash, u32 type, u32 param, u32 0, u32 dataOffset (absolute), u32 extra
///         type = [kind u8][? u8][signed u8][width u8]; width 0 = every row equals param;
///         kind 5 = float: value = asFloat(param) + stored / 2^25; otherwise value = stored + param.
/// </code>
/// Speech-wave tables (names are hashes, roles found by matching against the chunk):
/// <list type="bullet">
/// <item><c>11D5B185</c> lookup: <c>6AC4E4EA</c> row index, <c>F5F914D9</c> VariationId (selection key order).</item>
/// <item><c>A29AF127</c> per clip, sorted by VariationId: <c>F5F914D9</c> VariationId plus five flag columns.</item>
/// <item><c>3FE2AFD5</c> per clip (same order): <c>6CFCCE5B</c> duration (s), <c>E8E591DD</c> stream offset + 3.</item>
/// <item><c>A28D4A0D</c> one row: <c>DC19107B</c> chunk size.</item>
/// </list>
/// </summary>
public sealed class HarmonyBank
{
    public const uint Lookup = 0x11D5B185, Clips = 0xA29AF127, Streams = 0x3FE2AFD5, ChunkInfo = 0xA28D4A0D;
    public const uint ColRow = 0x6AC4E4EA, ColVariation = 0xF5F914D9, ColDuration = 0x6CFCCE5B, ColOffset = 0xE8E591DD, ColChunkSize = 0xDC19107B;

    public sealed record Column(uint Tag, uint Type, uint Param, int DescriptorAt, int DataAt, double[] Values)
    {
        public int Kind => (int)(Type & 0xFF);
        public int Width => (int)(Type >> 24);
        public bool Signed => ((Type >> 16) & 0xFF) != 0;
        public long Long(int row) => (long)Values[row];
    }

    public sealed record Table(uint Name, int At, int Rows, IReadOnlyList<Column> Columns)
    {
        public Column? this[uint tag] => Columns.FirstOrDefault(c => c.Tag == tag);
    }

    public IReadOnlyList<Table> Tables { get; }
    public byte[] Bytes { get; }

    HarmonyBank(byte[] bytes, List<Table> tables) { Bytes = bytes; Tables = tables; }

    public Table? this[uint name] => Tables.FirstOrDefault(t => t.Name == name);

    public static HarmonyBank Parse(byte[] b)
    {
        if (b.Length < 0x58 || b[0] != (byte)'S' || b[1] != (byte)'B' || b[2] != (byte)'l' || b[3] != (byte)'e')
            throw new InvalidDataException("Not an SBle Harmony bank.");
        int tableCount = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(10));
        var tables = new List<Table>(tableCount);
        for (int t = 0; t < tableCount; t++)
        {
            int to = checked((int)U32(b, 0x50 + 8 * t));
            if (to + 0x44 > b.Length || b[to] != (byte)'T' || b[to + 1] != (byte)'E' || b[to + 2] != (byte)'S' || b[to + 3] != (byte)'D')
                throw new InvalidDataException($"Table {t} at 0x{to:X} is not a TESD table.");
            int rows = checked((int)U32(b, to + 0x38));
            int cols = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(to + 0x3C));
            int cd = to + BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(to + 0x40));
            var columns = new List<Column>(cols);
            for (int c = 0; c < cols; c++)
            {
                int d = cd + 24 * c;
                uint tag = U32(b, d), type = U32(b, d + 4), param = U32(b, d + 8);
                int data = checked((int)U32(b, d + 16));
                int width = (int)(type >> 24), kind = (int)(type & 0xFF);
                bool signed = ((type >> 16) & 0xFF) != 0;
                var values = new double[rows];
                for (int r = 0; r < rows; r++)
                {
                    long stored = width == 0 ? 0 : ReadInt(b, data + r * width, width, signed);
                    values[r] = kind == 5
                        ? BitConverter.Int32BitsToSingle((int)param) + stored / 33554432.0
                        : width == 0 ? param : stored + param;
                }
                columns.Add(new(tag, type, param, d, data, values));
            }
            tables.Add(new(U32(b, to + 8), to, rows, columns));
        }
        return new HarmonyBank(b, tables);
    }

    /// <summary>A speech wave's clips: VariationId, stream offset in the chunk, duration. Throws if the tables are missing.</summary>
    public IReadOnlyList<(uint VariationId, int Offset, double Seconds)> SpeechClips()
    {
        var clips = this[Clips] ?? throw new InvalidDataException("No per-clip table (A29AF127).");
        var streams = this[Streams] ?? throw new InvalidDataException("No stream table (3FE2AFD5).");
        var ids = clips[ColVariation] ?? throw new InvalidDataException("No VariationId column.");
        var off = streams[ColOffset] ?? throw new InvalidDataException("No offset column.");
        var dur = streams[ColDuration];
        return Enumerable.Range(0, clips.Rows)
            .Select(r => ((uint)ids.Long(r), (int)off.Long(r) - 3, dur?.Values[r] ?? 0))
            .ToList();
    }

    public long? ChunkSize => (long?)this[ChunkInfo]?[ColChunkSize]?.Values[0];

    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    static long ReadInt(byte[] b, int at, int width, bool signed)
    {
        ulong v = 0;
        for (int i = width - 1; i >= 0; i--) v = v << 8 | b[at + i];
        if (signed && width < 8 && (v & (1UL << (width * 8 - 1))) != 0) v |= ulong.MaxValue << (width * 8);
        return (long)v;
    }
}
