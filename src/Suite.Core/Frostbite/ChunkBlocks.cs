using System.Buffers.Binary;

namespace Suite.Core.Frostbite;

/// <summary>
/// A stored chunk: a run of blocks, each an 8-byte header (u32 BE raw size; u16 BE: compression type in the high byte,
/// flags in the low byte whose low nibble holds bits 16-19 of the stored size; u16 BE low 16 bits of the stored size)
/// followed by the stored bytes. Type 0x11 is Oodle, 0x00 stored as is.
/// </summary>
public sealed class ChunkBlocks
{
    public sealed record Block(int StoredStart, int StoredEnd, int RawSize, byte Type, byte Flags, byte[] Stored);

    public List<Block> Blocks { get; } = [];

    public static ChunkBlocks Parse(byte[] stored)
    {
        var c = new ChunkBlocks();
        int p = 0;
        while (p < stored.Length)
        {
            if (p + 8 > stored.Length) throw new InvalidDataException("Truncated block header.");
            int raw = (int)(BinaryPrimitives.ReadUInt32BigEndian(stored.AsSpan(p)) & 0xFFFFFF);
            byte type = stored[p + 4], flags = stored[p + 5];
            int size = ((flags & 0x0F) << 16) | BinaryPrimitives.ReadUInt16BigEndian(stored.AsSpan(p + 6));
            int end = p + 8 + size;
            if (end > stored.Length) throw new InvalidDataException("Block runs past the chunk.");
            c.Blocks.Add(new Block(p, end, raw, type, flags, stored[p..end]));
            p = end;
        }
        return c;
    }

    public static byte[] DecodeBlock(Block b) => b.Type switch
    {
        0x11 => Oodle.Decompress(b.Stored.AsSpan(8), b.RawSize),
        0x00 => b.Stored[8..],
        _ => throw new InvalidDataException($"Unsupported block compression 0x{b.Type:X2}."),
    };

    public byte[] Decode()
    {
        var raw = new byte[Blocks.Sum(b => (long)b.RawSize)];
        int at = 0;
        foreach (var b in Blocks)
        {
            DecodeBlock(b).CopyTo(raw, at);
            at += b.RawSize;
        }
        return raw;
    }

    /// <summary>
    /// A whole stored chunk: <paramref name="raw"/> split into 256 KB blocks, which is how the game stores its large
    /// textures (Boston home normal: 21 x 262,144 + 1 x 92,160 = 22 blocks). One block cannot exceed the 20-bit stored
    /// size field, so a single block would be refused.
    /// </summary>
    public static byte[] EncodeBlocks(byte[] raw, int blockRawSize = 262144, int workers = 0)
    {
        if (blockRawSize is < 4096 or > 0xFFFFF) throw new ArgumentOutOfRangeException(nameof(blockRawSize));
        int count = (raw.Length + blockRawSize - 1) / blockRawSize;
        var blocks = new byte[count][];
        CpuParallel.For(0, count, workers, i =>
        {
            int at = i * blockRawSize;
            int n = Math.Min(blockRawSize, raw.Length - at);
            blocks[i] = EncodeBlock(raw.AsSpan(at, n));
        });
        using var ms = new MemoryStream();
        foreach (var block in blocks) ms.Write(block, 0, block.Length);
        return ms.ToArray();
    }

    /// <summary>A new Oodle block for <paramref name="raw"/> (stored as is when that would not be smaller).</summary>
    public static byte[] EncodeBlock(ReadOnlySpan<byte> raw)
    {
        var packed = Oodle.Compress(raw);
        byte type = 0x11;
        if (packed.Length >= raw.Length) { packed = raw.ToArray(); type = 0x00; }
        if (packed.Length > 0xFFFFF) throw new InvalidOperationException("Block too large for its header.");
        var block = new byte[8 + packed.Length];
        BinaryPrimitives.WriteUInt32BigEndian(block, (uint)raw.Length);
        block[4] = type;
        block[5] = (byte)(0x70 | (packed.Length >> 16));
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(6), (ushort)(packed.Length & 0xFFFF));
        packed.CopyTo(block, 8);
        return block;
    }
}
