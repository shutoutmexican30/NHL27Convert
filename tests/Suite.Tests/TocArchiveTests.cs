using System.Buffers.Binary;
using System.Text;
using Suite.Core.Frostbite;

namespace Suite.Tests;

public sealed class TocArchiveTests
{
    [Fact]
    public void Real_compressed_toc_rebuild_preserves_every_record_and_resolves_hash_collisions()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../Debugging/donor-clone/game/Data/Gen4b/contentsb.toc"));
        byte[] originalBytes = File.ReadAllBytes(path);
        var original = TocArchive.Parse(originalBytes);
        Assert.Equal(44_249, original.Bundles.Count);
        Assert.Equal(67_903, original.Chunks.Count);

        string[] collisions = CollisionNames(original.Bundles.Count + 2);
        byte[] firstRecord = Record("first-new-bundle-record");
        byte[] secondRecord = Record("second-new-bundle-record");
        byte[] rebuiltBytes = original.Rebuild(
        [
            new TocArchive.NewBundle(collisions[0], 0x40000020, firstRecord),
            new TocArchive.NewBundle(collisions[1], 0x40000020, secondRecord),
        ]);
        var rebuilt = TocArchive.Parse(rebuiltBytes);

        Assert.Equal(original.Bundles.Count + 2, rebuilt.Bundles.Count);
        Assert.Equal(original.Chunks.Count, rebuilt.Chunks.Count);
        Assert.Equal(originalBytes.AsSpan(0, TocFile.HeaderSize).ToArray(),
            rebuiltBytes.AsSpan(0, TocFile.HeaderSize).ToArray());
        Assert.Equal(0u, ReadField(rebuiltBytes, 11) & TocArchive.HasCompressedNames);
        Assert.Equal(48u, ReadField(rebuiltBytes, 0));

        foreach (var before in original.Bundles)
        {
            var after = rebuilt.FindBundle(before.Name);
            Assert.NotNull(after);
            Assert.Equal(before.SizeAndFlags, after.SizeAndFlags);
            if (before.RecordOffset == ulong.MaxValue)
            {
                Assert.Equal(ulong.MaxValue, after.RecordOffset);
                continue;
            }
            int oldAt = checked(TocFile.HeaderSize + (int)before.RecordOffset);
            int newAt = checked(TocFile.HeaderSize + (int)after.RecordOffset);
            Assert.Equal(originalBytes.AsSpan(oldAt, 32).ToArray(), rebuiltBytes.AsSpan(newAt, 32).ToArray());
        }
        foreach (var before in original.Chunks)
        {
            var after = rebuilt.FindChunk(before.Id);
            Assert.NotNull(after);
            Assert.Equal(before, after);
        }

        AssertNewRecord(rebuilt, rebuiltBytes, collisions[0], firstRecord);
        AssertNewRecord(rebuilt, rebuiltBytes, collisions[1], secondRecord);
    }

    [Fact]
    public void Invalid_rebuild_inputs_are_rejected_before_a_game_file_could_be_written()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../Debugging/donor-clone/game/Data/Gen4b/contentsb.toc"));
        var toc = TocArchive.Parse(File.ReadAllBytes(path));
        string existing = toc.Bundles[0].Name;
        Assert.Throws<InvalidDataException>(() => toc.Rebuild(
            [new TocArchive.NewBundle(existing, 0, new byte[32])]));
        Guid existingChunk = toc.Chunks[0].Id;
        Assert.Throws<InvalidDataException>(() => toc.Rebuild(newChunks:
            [new TocArchive.Chunk(existingChunk, 0, 0, 0)]));
        Assert.Throws<InvalidDataException>(() => toc.Rebuild(
            [new TocArchive.NewBundle("content/new", 0, new byte[4])]));
    }

    static void AssertNewRecord(TocArchive toc, byte[] bytes, string name, byte[] record)
    {
        var bundle = toc.FindBundle(name);
        Assert.NotNull(bundle);
        int at = checked(TocFile.HeaderSize + (int)bundle.RecordOffset);
        Assert.Equal(record, bytes.AsSpan(at, record.Length).ToArray());
    }

    static byte[] Record(string text)
    {
        byte[] record = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(8), 40);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(16), 0x20);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(20), 0x20);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(24), 0x20);
        Encoding.ASCII.GetBytes(text).CopyTo(record, 32);
        return record;
    }

    static uint ReadField(byte[] bytes, int index) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(TocFile.HeaderSize + index * 4, 4));

    static string[] CollisionNames(int modulus)
    {
        var byBucket = new Dictionary<uint, string>();
        for (int i = 0; i < 100_000; i++)
        {
            string name = $"content/test/collision/{i}";
            uint bucket = Hash(Encoding.ASCII.GetBytes(name)) % (uint)modulus;
            if (byBucket.TryGetValue(bucket, out string? other)) return [other, name];
            byBucket.Add(bucket, name);
        }
        throw new InvalidOperationException("Could not construct a hash collision.");
    }

    static uint Hash(byte[] bytes)
    {
        const uint prime = 0x01000193;
        uint hash = 0x811C9DC5;
        unchecked { foreach (byte b in bytes) hash = (hash * prime) ^ (uint)(sbyte)b; }
        return hash % prime;
    }
}
