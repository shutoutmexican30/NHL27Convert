using System.Buffers.Binary;
using Suite.Core.Frostbite;

namespace Suite.Rosters.Tests;

public class FrostbiteTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cims-fb-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>A TOC laid out like NHL 24's: 0x22C header, then a chunk id table and a chunk location table.</summary>
    static byte[] MakeToc(Guid id, uint offset, uint size)
    {
        var b = new byte[TocFile.HeaderSize + 0x100];
        b[1] = 0xD1; b[2] = 0xCE; b[3] = 0x01;
        void F(int i, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(TocFile.HeaderSize + 4 * i), v);
        F(4, 0x60); F(5, 1); F(6, 0x80);
        var g = id.ToByteArray(); Array.Reverse(g);
        g.CopyTo(b, TocFile.HeaderSize + 0x60);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(TocFile.HeaderSize + 0x70), 0x01000003); // flag byte + index 3 (u32s)
        int e = TocFile.HeaderSize + 0x80 + 12;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(e), 0x00010002); // patch 1, catalog 0, cas 2
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(e + 4), offset);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(e + 8), size);
        return b;
    }

    [Fact]
    public void Toc_chunk_entry_is_found_and_repointed_in_place()
    {
        Directory.CreateDirectory(_dir);
        var id = Guid.NewGuid();
        var path = Path.Combine(_dir, "globals.toc");
        var original = MakeToc(id, 1000, 200);
        File.WriteAllBytes(path, original);
        var toc = TocFile.Load(path);
        var r = Assert.IsType<ChunkRef>(toc.Find(id));
        Assert.True(r.Patch); Assert.Equal(0, r.Catalog); Assert.Equal(2, r.Cas);
        Assert.Equal(1000u, r.Offset); Assert.Equal(200u, r.Size);
        toc.Repoint(r.EntryPos, 5000, 300);
        toc.Save();
        var again = TocFile.Load(path).Find(id)!;
        Assert.Equal(5000u, again.Offset); Assert.Equal(300u, again.Size);
        // Only the 8 offset/size bytes changed.
        var now = File.ReadAllBytes(path);
        Assert.InRange(Enumerable.Range(0, now.Length).Count(i => now[i] != original[i]), 1, 8);
        Assert.Null(TocFile.Load(path).Find(Guid.NewGuid()));
    }

    [Fact]
    public void Stored_blocks_parse_with_the_size_high_bits_in_the_flag_nibble()
    {
        // One stored (type 0x00) block of 70,000 bytes: size 0x11170 → flag nibble 1, u16 0x1170.
        var raw = Enumerable.Range(0, 70_000).Select(i => (byte)i).ToArray();
        var block = new byte[8 + raw.Length];
        BinaryPrimitives.WriteUInt32BigEndian(block, (uint)raw.Length);
        block[4] = 0x00; block[5] = 0x71;
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(6), (ushort)(raw.Length & 0xFFFF));
        raw.CopyTo(block, 8);
        var parsed = ChunkBlocks.Parse([.. block, .. block]);
        Assert.Equal(2, parsed.Blocks.Count);
        Assert.Equal(raw.Length + 8, parsed.Blocks[1].StoredStart);
        Assert.Equal([.. raw, .. raw], parsed.Decode());
    }

    [Fact]
    public void Oodle_block_round_trips_when_the_library_is_installed()
    {
        try { Oodle.Ensure(); } catch (FileNotFoundException) { return; }
        var raw = Enumerable.Range(0, 262_144).Select(i => (byte)(i % 251 ^ i / 1024)).ToArray();
        var block = ChunkBlocks.EncodeBlock(raw);
        var parsed = ChunkBlocks.Parse(block);
        Assert.Single(parsed.Blocks);
        Assert.Equal(raw, parsed.Decode());
    }
}
