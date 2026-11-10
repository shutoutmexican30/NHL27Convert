using System.Buffers.Binary;
using System.Text;
using Suite.Core.Frostbite;

namespace Suite.Tests;

public sealed class BundleManifestTests
{
    static readonly Guid ChunkA = new("00112233-4455-6677-8899-aabbccddeeff");
    static readonly Guid ChunkB = new("ffeeddcc-bbaa-9988-7766-554433221100");

    [Fact]
    public void Rebuild_preserves_metadata_sizes_ranges_and_checksums()
    {
        var manifest = BundleManifest.Parse(MinimalManifest());
        manifest.Ebx.Add(new BundleManifest.EbxEntry("content/team/shared", 352, Sha(2)));
        manifest.Res.Add(new BundleManifest.ResEntry("content/team/shared", 132, Sha(3),
            0x6BDE20BA, Enumerable.Range(0, 16).Select(i => (byte)(i + 30)).ToArray(), 0x0102030405060708));
        manifest.Chunks.Add(new BundleManifest.ChunkEntry(ChunkA, 4_194_304, 1_048_576, Sha(4)));
        manifest.ChunkMeta = Encoding.ASCII.GetBytes("raw-chunk-meta");

        byte[] built = manifest.Build();
        var check = BundleManifest.Parse(built);

        Assert.Equal((uint)(built.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(built));
        Assert.Equal(0, built.Length % 16);
        Assert.Equal(manifest.Magic, check.Magic);
        Assert.Equal(manifest.ChunkMeta, check.ChunkMeta);
        Assert.Equal(2, check.Ebx.Count);
        Assert.Equal(352u, check.Ebx[1].OriginalSize);
        Assert.Equal(Sha(2), check.Ebx[1].Sha1);
        var res = Assert.Single(check.Res);
        Assert.Equal(132u, res.OriginalSize);
        Assert.Equal(0x6BDE20BAu, res.Type);
        Assert.Equal(0x0102030405060708ul, res.Rid);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)(i + 30)).ToArray(), res.Meta);
        var chunk = Assert.Single(check.Chunks);
        Assert.Equal(ChunkA, chunk.Id);
        Assert.Equal(4_194_304u, chunk.LogicalOffset);
        Assert.Equal(1_048_576u, chunk.LogicalSize);
        Assert.Equal(Sha(4), chunk.Sha1);
    }

    [Fact]
    public void Real_donor_manifests_round_trip_without_losing_entries()
    {
        string game = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../Debugging/donor-clone/game"));
        var index = BundleIndex.Build(game);
        const string name = "content/character/skater/league/nhl/arizona/adidas/four/branding_jersey_adidas_arizona_four_color_array";
        var copies = index.EbxByName[name];
        Assert.Equal(3, copies.Count);

        foreach (var copy in copies)
        {
            var before = BundleManifest.Parse(BundleIndex.ReadBytes(copy.Bundle.Manifest));
            var after = BundleManifest.Parse(before.Build());
            Assert.Equal(before.Magic, after.Magic);
            Assert.Equal(before.ChunkMeta, after.ChunkMeta);
            Assert.Equal(before.Ebx.Count, after.Ebx.Count);
            Assert.Equal(before.Res.Count, after.Res.Count);
            Assert.Equal(before.Chunks.Count, after.Chunks.Count);
            for (int i = 0; i < before.Ebx.Count; i++) Equal(before.Ebx[i], after.Ebx[i]);
            for (int i = 0; i < before.Res.Count; i++) Equal(before.Res[i], after.Res[i]);
            for (int i = 0; i < before.Chunks.Count; i++) Equal(before.Chunks[i], after.Chunks[i]);
        }
    }

    static void Equal(BundleManifest.EbxEntry a, BundleManifest.EbxEntry b)
    {
        Assert.Equal(a.Name, b.Name); Assert.Equal(a.OriginalSize, b.OriginalSize); Assert.Equal(a.Sha1, b.Sha1);
    }
    static void Equal(BundleManifest.ResEntry a, BundleManifest.ResEntry b)
    {
        Equal(new BundleManifest.EbxEntry(a.Name, a.OriginalSize, a.Sha1), new BundleManifest.EbxEntry(b.Name, b.OriginalSize, b.Sha1));
        Assert.Equal(a.Type, b.Type); Assert.Equal(a.Meta, b.Meta); Assert.Equal(a.Rid, b.Rid);
    }
    static void Equal(BundleManifest.ChunkEntry a, BundleManifest.ChunkEntry b)
    {
        Assert.Equal(a.Id, b.Id); Assert.Equal(a.LogicalOffset, b.LogicalOffset);
        Assert.Equal(a.LogicalSize, b.LogicalSize); Assert.Equal(a.Sha1, b.Sha1);
    }

    static byte[] Sha(int value) => Enumerable.Range(0, 20).Select(i => (byte)(value + i)).ToArray();

    static byte[] MinimalManifest()
    {
        var body = new MemoryStream();
        body.Write(Sha(1));
        body.Write(BitConverter.GetBytes(0));
        body.Write(BitConverter.GetBytes(250));
        uint strings = (uint)body.Position + 0x20;
        body.Write(Encoding.ASCII.GetBytes("content/team/a\0"));
        while ((body.Position + 0x24) % 16 != 0) body.WriteByte(0);
        var output = new byte[body.Position + 0x24];
        BinaryPrimitives.WriteUInt32BigEndian(output, (uint)(output.Length - 4));
        new byte[] { 0xD6, 0x8E, 0x79, 0x9D }.CopyTo(output, 4);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(24), strings);
        body.Position = 0; body.ReadExactly(output.AsSpan(36));
        return output;
    }
}
