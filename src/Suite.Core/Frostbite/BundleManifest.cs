using System.Buffers.Binary;
using System.Text;

namespace Suite.Core.Frostbite;

/// <summary>
/// The small binary manifest stored as entry zero of a CAS bundle. It preserves each entry's payload SHA1, original
/// size, texture RES metadata/RID, logical chunk range and the raw chunk-metadata object while allowing names and IDs
/// to be changed for a new, independent resource.
/// </summary>
public sealed class BundleManifest
{
    public sealed record EbxEntry(string Name, uint OriginalSize, byte[] Sha1);
    public sealed record ResEntry(string Name, uint OriginalSize, byte[] Sha1, uint Type, byte[] Meta, ulong Rid);
    public sealed record ChunkEntry(Guid Id, uint LogicalOffset, uint LogicalSize, byte[] Sha1);

    public List<EbxEntry> Ebx { get; } = [];
    public List<ResEntry> Res { get; } = [];
    public List<ChunkEntry> Chunks { get; } = [];
    public byte[] ChunkMeta { get; set; } = [];
    public byte[] Magic { get; private set; } = [];

    public static BundleManifest Parse(ReadOnlySpan<byte> source)
    {
        byte[] b = source.ToArray();
        if (b.Length < 40 || BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(0, 4)) != b.Length - 4)
            throw new InvalidDataException("Bundle-manifest size does not match its payload.");
        int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at, 4));
        int total = I(8), ebx = I(12), res = I(16), chunks = I(20), strings = I(24), metaOffset = I(28), metaSize = I(32);
        if (ebx < 0 || res < 0 || chunks < 0 || total != ebx + res + chunks || total > 100_000 || strings < 0x20)
            throw new InvalidDataException("Invalid bundle-manifest counts or offsets.");
        int ebxAt = 36 + 20 * total, resAt = ebxAt + 8 * ebx, typesAt = resAt + 8 * res,
            metaAt = typesAt + 4 * res, ridAt = metaAt + 16 * res, chunkAt = ridAt + 8 * res;
        if (chunkAt + 24 * chunks > b.Length || 4 + strings > b.Length)
            throw new InvalidDataException("Bundle-manifest tables run past their payload.");

        string Name(int offset)
        {
            long at = 4L + strings + offset;
            if (offset < 0 || at >= b.Length) throw new InvalidDataException("Bundle-manifest name is outside its string table.");
            int zero = Array.IndexOf(b, (byte)0, (int)at);
            if (zero < 0) throw new InvalidDataException("Bundle-manifest name is not terminated.");
            string name = Encoding.ASCII.GetString(b, (int)at, zero - (int)at);
            if (name.Length == 0 || name.Any(c => c < 0x20 || c > 0x7e))
                throw new InvalidDataException("Bundle-manifest name is not printable ASCII.");
            return name;
        }

        var result = new BundleManifest { Magic = b.AsSpan(4, 4).ToArray() };
        for (int i = 0; i < total; i++)
        {
            byte[] sha = b.AsSpan(36 + 20 * i, 20).ToArray();
            if (i < ebx)
            {
                int at = ebxAt + 8 * i;
                result.Ebx.Add(new EbxEntry(Name(I(at)), (uint)I(at + 4), sha));
            }
            else if (i < ebx + res)
            {
                int k = i - ebx, at = resAt + 8 * k;
                result.Res.Add(new ResEntry(Name(I(at)), (uint)I(at + 4), sha,
                    (uint)I(typesAt + 4 * k), b.AsSpan(metaAt + 16 * k, 16).ToArray(),
                    BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(ridAt + 8 * k, 8))));
            }
            else
            {
                int at = chunkAt + 24 * (i - ebx - res);
                result.Chunks.Add(new ChunkEntry(new Guid(b.AsSpan(at, 16)),
                    (uint)I(at + 16), (uint)I(at + 20), sha));
            }
        }
        if (chunks > 0 && metaSize > 0)
        {
            long start = 4L + metaOffset;
            if (metaOffset < 0x20 || start < 0 || start + metaSize > b.Length)
                throw new InvalidDataException("Chunk metadata runs past the bundle manifest.");
            result.ChunkMeta = b.AsSpan((int)start, metaSize).ToArray();
        }
        return result;
    }

    public byte[] Build()
    {
        if (Magic.Length != 4) throw new InvalidDataException("A four-byte bundle-manifest magic is required.");
        ValidateNames(Ebx.Select(e => e.Name), "EBX");
        ValidateNames(Res.Select(e => e.Name), "RES");
        if (Ebx.Any(e => e.Sha1.Length != 20) || Res.Any(e => e.Sha1.Length != 20 || e.Meta.Length != 16)
            || Chunks.Any(e => e.Sha1.Length != 20))
            throw new InvalidDataException("Invalid SHA1 or RES metadata length.");
        if (Chunks.Select(c => c.Id).Distinct().Count() != Chunks.Count)
            throw new InvalidDataException("A bundle cannot contain the same chunk ID twice.");
        if (Chunks.Count == 0 && ChunkMeta.Length != 0) throw new InvalidDataException("Chunk metadata requires a chunk.");
        if (Chunks.Any(c => (ulong)c.LogicalOffset + c.LogicalSize > uint.MaxValue))
            throw new InvalidDataException("A chunk's logical range exceeds 4 GB.");

        var strings = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint stringBytes = 0;
        uint StringOffset(string name)
        {
            if (strings.TryGetValue(name, out uint offset)) return offset;
            offset = stringBytes;
            strings.Add(name, offset);
            stringBytes = checked(stringBytes + (uint)Encoding.ASCII.GetByteCount(name) + 1);
            return offset;
        }
        var body = new MemoryStream();
        void U32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); body.Write(b); }
        void U64(ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); body.Write(b); }

        foreach (var entry in Ebx) body.Write(entry.Sha1);
        foreach (var entry in Res) body.Write(entry.Sha1);
        foreach (var entry in Chunks) body.Write(entry.Sha1);
        var ebxOffsets = Ebx.Select(e => (Offset: StringOffset(e.Name), e.OriginalSize)).ToList();
        var resOffsets = Res.Select(e => (Offset: StringOffset(e.Name), e.OriginalSize)).ToList();
        foreach (var entry in ebxOffsets) { U32(entry.Offset); U32(entry.OriginalSize); }
        foreach (var entry in resOffsets) { U32(entry.Offset); U32(entry.OriginalSize); }
        foreach (var entry in Res) U32(entry.Type);
        foreach (var entry in Res) body.Write(entry.Meta);
        foreach (var entry in Res) U64(entry.Rid);
        Span<byte> id = stackalloc byte[16];
        foreach (var entry in Chunks)
        {
            entry.Id.TryWriteBytes(id); body.Write(id);
            U32(entry.LogicalOffset); U32(entry.LogicalSize);
        }

        uint metaOffset = 0, metaSize = 0;
        if (ChunkMeta.Length > 0)
        {
            metaOffset = checked((uint)body.Position + 0x20);
            body.Write(ChunkMeta);
            metaSize = (uint)ChunkMeta.Length;
        }
        uint stringsOffset = checked((uint)body.Position + 0x20);
        foreach (var entry in strings.OrderBy(kv => kv.Value))
        {
            body.Write(Encoding.ASCII.GetBytes(entry.Key)); body.WriteByte(0);
        }
        while ((body.Position + 0x24) % 16 != 0) body.WriteByte(0);

        var output = new byte[checked(body.Position + 0x24)];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), (uint)(output.Length - 4));
        Magic.CopyTo(output, 4);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8, 4), Ebx.Count + Res.Count + Chunks.Count);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12, 4), Ebx.Count);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16, 4), Res.Count);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(20, 4), Chunks.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(24, 4), stringsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(28, 4), metaOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(32, 4), metaSize);
        body.Position = 0; body.ReadExactly(output.AsSpan(36));
        return output;
    }

    static void ValidateNames(IEnumerable<string> names, string kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (name.Length == 0 || name.Any(c => c < 0x20 || c > 0x7e))
                throw new InvalidDataException($"{kind} names must be printable ASCII.");
            if (!seen.Add(name)) throw new InvalidDataException($"Duplicate {kind} name: {name}");
        }
    }
}
