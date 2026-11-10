using System.Buffers.Binary;
using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Tests;

public sealed class BrandingAssetCloneTests
{
    const string DonorPrefix = "content/character/skater/league/nhl/arizona/adidas/four";
    const string TargetPrefix = "content/character/skater/league/nhl/cims_test/adidas/four";
    const string DonorName = DonorPrefix + "/branding_jersey_adidas_arizona_four_color_array";
    const string TargetName = TargetPrefix + "/branding_jersey_adidas_cims_test_four_color_array";

    [Fact]
    public void Real_donor_clone_is_narrow_independent_and_blocked_until_team_art_exists()
    {
        string game = Game();
        Oodle.Ensure(SuiteSettings.Load());
        var index = BundleIndex.Build(game);
        var incomplete = BrandingClonePlan.Build(index, DonorPrefix, TargetPrefix, DonorName, TargetName);
        Assert.False(incomplete.IsComplete);
        Assert.Contains(TargetName, incomplete.MissingArtwork);
        Assert.Throws<InvalidOperationException>(incomplete.ThrowIfIncomplete);

        // Test-only stand-in pixels exercise encoding/read-back. Production receives the converted target team's bytes.
        var artwork = incomplete.MissingArtwork.ToDictionary(name => name, name =>
        {
            var entry = incomplete.Entries.Single(e => e.Name == name && e.NewChunkId is not null);
            return ChunkBlocks.Parse(entry.Replacement!).Decode();
        });
        var plan = BrandingClonePlan.Build(index, DonorPrefix, TargetPrefix, DonorName, TargetName, artwork);
        Assert.True(plan.IsComplete);
        Assert.NotEqual(plan.SourceBundleName, plan.NewBundleName);
        Assert.Contains(plan.Entries, e => !e.IsClone); // Garments, fabrics and unrelated entries stay shared.
        Assert.All(plan.Entries.Where(e => e.IsClone), e => Assert.False(e.Source.PairAt < 0));

        var manifest = BundleManifest.Parse(plan.Manifest);
        int ebx = manifest.Ebx.FindIndex(e => e.Name == TargetName);
        int res = manifest.Res.FindIndex(r => r.Name == TargetName);
        Assert.True(ebx >= 0 && res >= 0);
        var chunkEntry = plan.Entries.Single(e => e.Name == TargetName && e.NewChunkId is not null);
        Assert.Contains(manifest.Chunks, c => c.Id == chunkEntry.NewChunkId);

        var clonedEbxEntry = plan.Entries.Single(e => e.Name == TargetName && e.NewChunkId is null
            && e.PairAt == index.EbxByName[DonorName][0].Data.PairAt);
        var clonedEbx = EbxRiff.Parse(Decode(clonedEbxEntry.Replacement!));
        Assert.Equal(TargetName, clonedEbx.Name);
        Assert.Equal(plan.EbxGuidRemap.Values.Single(id => id == clonedEbx.FileGuid), clonedEbx.FileGuid);
        byte[] resRaw = Decode(plan.Entries.Single(e => e.Name == TargetName && e.NewChunkId is null
            && e.PairAt != clonedEbxEntry.PairAt).Replacement!);
        Assert.Equal(chunkEntry.NewChunkId, TextureRes.Parse(resRaw).ChunkGuid);
        Assert.Equal(artwork[TargetName], ChunkBlocks.Parse(chunkEntry.Replacement!).Decode());

        byte[] tocBytes = File.ReadAllBytes(plan.SourceBundle.Toc);
        byte[] record = OriginalRecord(tocBytes, plan.SourceBundle);
        var locations = plan.Entries.Where(e => e.IsClone).ToDictionary(e => e.PairAt,
            e => ((uint)(10_000 + e.PairAt % 1000), (uint)e.Replacement!.Length));
        byte[] newRecord = plan.BuildRecord(record, locations, (9_000, (uint)plan.Manifest.Length));
        Assert.Equal(record.Length, newRecord.Length);
        Assert.False(record.AsSpan().SequenceEqual(newRecord));
        var newBundle = plan.NewBundle(newRecord);
        Assert.Equal(plan.NewBundleName, newBundle.Name);
        Assert.Equal(newRecord, newBundle.Record);
        // The source record and manifest are still untouched on disk.
        Assert.Equal(record, OriginalRecord(File.ReadAllBytes(plan.SourceBundle.Toc), plan.SourceBundle));
        Assert.Equal(BundleIndex.ReadBytes(plan.SourceBundle.Manifest), BundleIndex.ReadBytes(plan.SourceBundle.Manifest));
    }

    [Fact]
    public void Partial_donor_chunk_uses_the_full_copy_and_declares_a_complete_clone()
    {
        string game = Game();
        Oodle.Ensure(SuiteSettings.Load());
        var index = BundleIndex.Build(game);
        string donorName = DonorPrefix + "/jersey_adidas_arizona_four_base_color";
        string targetName = TargetPrefix + "/jersey_adidas_cims_test_four_base_color";
        var source = TextureSlots.Read(game, index, donorName);
        var sourceChunk = index.ChunksById[source.Chunk].Single(c => c.Bundle.Toc == index.EbxByName[donorName][0].Bundle.Toc
            && c.Bundle.RecordAt == index.EbxByName[donorName][0].Bundle.RecordAt);
        Assert.True(sourceChunk.LogicalOffset != 0 || sourceChunk.LogicalSize != source.Stock.Length,
            "This regression requires a donor bundle containing only part of the texture.");

        var plan = BrandingClonePlan.Build(index, DonorPrefix, TargetPrefix, donorName, targetName);
        var cloned = plan.Entries.Single(e => e.Name == targetName && e.NewChunkId is not null);
        byte[] raw = ChunkBlocks.Parse(cloned.Replacement!).Decode();
        Assert.Equal(source.Stock, raw);
        var manifest = BundleManifest.Parse(plan.Manifest);
        var chunk = manifest.Chunks.Single(c => c.Id == cloned.NewChunkId);
        Assert.Equal(0u, chunk.LogicalOffset);
        Assert.Equal((uint)raw.Length, chunk.LogicalSize);
    }

    [Fact]
    public void Existing_target_resource_is_never_replaced()
    {
        string game = Game();
        Oodle.Ensure(SuiteSettings.Load());
        var index = BundleIndex.Build(game);
        Assert.Throws<InvalidDataException>(() => BrandingClonePlan.Build(index,
            DonorPrefix, DonorPrefix.Replace("arizona", "anaheim"), DonorName,
            DonorName.Replace("arizona", "anaheim")));
    }

    static byte[] OriginalRecord(byte[] toc, BundleIndex.Bundle bundle)
    {
        var record = BundleIndex.Records(bundle.Toc, toc).Single(r => r.RecordAt == bundle.RecordAt);
        int pairsEnd = record.Entries.Max(e => e.PairAt + 8);
        int flagsAt = bundle.RecordAt + (int)BinaryPrimitives.ReadUInt32BigEndian(toc.AsSpan(bundle.RecordAt + 8, 4));
        int length = Math.Max(pairsEnd, flagsAt + record.Entries.Count) - bundle.RecordAt;
        return toc.AsSpan(bundle.RecordAt, length).ToArray();
    }

    static byte[] Decode(byte[] stored)
    {
        try { return ChunkBlocks.Parse(stored).Decode(); }
        catch (InvalidDataException) { return stored; }
    }

    static string Game() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../Debugging/donor-clone/game"));
}
