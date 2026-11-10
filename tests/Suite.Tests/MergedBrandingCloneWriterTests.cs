using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Tests;

public sealed class MergedBrandingCloneWriterTests
{
    [Fact]
    public void Different_donor_bundles_merge_write_read_back_and_restore()
    {
        const string baseDonor = "content/character/skater/league/nhl/anaheim/adidas/away";
        const string brandingDonor = "content/character/skater/league/nhl/stlouis/adidas/away";
        const string target = "content/character/skater/league/nhl/cims_merge_test/adidas/twentyfive";
        const string targetBundle = "gen4b/" + target + "/jersey_adidas_cims_merge_test_twentyfive_hq_asp_player_team_brt";
        string baseName = baseDonor + "/jersey_adidas_anaheim_away_base_color";
        string brandingName = brandingDonor + "/branding_jersey_adidas_stlouis_away_color_array";
        string targetBase = target + "/jersey_adidas_cims_merge_test_twentyfive_base_color";
        string targetBranding = target + "/branding_jersey_adidas_cims_merge_test_twentyfive_color_array";

        string source = Game();
        Oodle.Ensure(SuiteSettings.Load());
        var index = BundleIndex.Build(source);
        var basePlan = Complete(BrandingClonePlan.Build(index, baseDonor, target, baseName, targetBase,
            newBundleName: targetBundle));
        var brandingPlan = Complete(BrandingClonePlan.Build(index, brandingDonor, target, brandingName, targetBranding,
            newBundleName: targetBundle));
        Assert.NotEqual(basePlan.SourceBundleName, brandingPlan.SourceBundleName);
        Assert.Equal(basePlan.NewBundleName, brandingPlan.NewBundleName);

        var merged = BrandingClonePlan.Merge([basePlan, brandingPlan]);
        merged.ThrowIfConventionalRootMissing();
        Assert.Contains(merged.Entries, e => e.IsClone && e.NewChunkId is null && e.Name == targetBundle["gen4b/".Length..]);
        Assert.Equal(targetBundle, merged.NewBundleName);
        Assert.Contains(merged.Entries, e => e.IsClone && e.Name == targetBase);
        Assert.Contains(merged.Entries, e => e.IsClone && e.Name == targetBranding);
        Assert.Equal(merged.Entries.Count, merged.Entries.Select(e => e.PairAt).Distinct().Count());
        var manifest = BundleManifest.Parse(merged.Manifest);
        Assert.Contains(manifest.Ebx, e => e.Name == targetBase);
        Assert.Contains(manifest.Ebx, e => e.Name == targetBranding);
        Assert.All(merged.Entries.Where(e => e.NewChunkId is not null),
            e => Assert.Contains(manifest.Chunks, c => c.Id == e.NewChunkId));

        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../Debugging/writer-tests", Guid.NewGuid().ToString("N")));
        string staging = Path.Combine(root, "game"), backup = Path.Combine(root, "backup");
        byte[] sourceToc = File.ReadAllBytes(merged.SourceBundle.Toc);
        var sourceCas = merged.Entries.Where(e => e.IsClone).Select(e => e.Source.Cas)
            .Append(merged.SourceBundle.Manifest.Cas).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sourceLengths = sourceCas.ToDictionary(cas => cas, cas => new FileInfo(cas).Length);
        try
        {
            string targetToc = Path.Combine(staging, Path.GetRelativePath(source, merged.SourceBundle.Toc));
            Directory.CreateDirectory(Path.GetDirectoryName(targetToc)!);
            File.WriteAllBytes(targetToc, sourceToc);
            foreach (string cas in sourceCas)
            {
                string destination = Path.Combine(staging, Path.GetRelativePath(source, cas));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                stream.SetLength(sourceLengths[cas]);
            }

            var result = BrandingCloneWriter.Apply(merged, staging, backup);
            Assert.Equal(targetBundle, result.BundleName);
            Assert.True(result.ClonedEntries >= 6); // Each root has EBX, RES and chunk payloads.
            byte[] changedToc = File.ReadAllBytes(targetToc);
            var archive = TocArchive.Parse(changedToc);
            Assert.NotNull(archive.FindBundle(targetBundle));
            foreach (var entry in merged.Entries.Where(e => e.NewChunkId is not null))
                Assert.NotNull(archive.FindChunk(entry.NewChunkId!.Value));

            BrandingCloneWriter.Restore(staging, backup);
            Assert.Equal(sourceToc, File.ReadAllBytes(targetToc));
            foreach (string cas in sourceCas)
                Assert.Equal(sourceLengths[cas], new FileInfo(Path.Combine(staging, Path.GetRelativePath(source, cas))).Length);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static BrandingClonePlan Complete(BrandingClonePlan incomplete)
    {
        var artwork = incomplete.MissingArtwork.ToDictionary(name => name, name =>
            ChunkBlocks.Parse(incomplete.Entries.Single(e => e.Name == name && e.NewChunkId is not null)
                .Replacement!).Decode());
        // Rebuild through the public single-root overload with the same names and explicit target bundle.
        var root = incomplete.Entries.First(e => e.IsClone && e.NewChunkId is null
            && incomplete.MissingArtwork.Contains(e.Name));
        string donorPrefix = incomplete.SourceBundleName.StartsWith("gen4b/", StringComparison.Ordinal)
            ? incomplete.SourceBundleName["gen4b/".Length..] : incomplete.SourceBundleName;
        donorPrefix = donorPrefix[..(donorPrefix.LastIndexOf('/') + 1)];
        string targetPrefix = incomplete.NewBundleName.StartsWith("gen4b/", StringComparison.Ordinal)
            ? incomplete.NewBundleName["gen4b/".Length..] : incomplete.NewBundleName;
        targetPrefix = targetPrefix[..(targetPrefix.LastIndexOf('/') + 1)];
        string donorName = donorPrefix + root.Name[targetPrefix.Length..];
        string donorIdentity = donorPrefix.Trim('/').Split('/') is { } dp ? dp[^3] + "_" + dp[^1] : "";
        string targetIdentity = targetPrefix.Trim('/').Split('/') is { } tp ? tp[^3] + "_" + tp[^1] : "";
        donorName = donorPrefix + root.Name[targetPrefix.Length..].Replace(targetIdentity, donorIdentity, StringComparison.Ordinal);
        var index = BundleIndex.Build(incomplete.SourceGame);
        string sourceRoot = incomplete.SourceBundleName.StartsWith("gen4b/", StringComparison.Ordinal)
            ? incomplete.SourceBundleName["gen4b/".Length..] : incomplete.SourceBundleName;
        var roots = index.EbxByName.Values.SelectMany(entries => entries)
            .Where(e => e.Bundle.Toc == incomplete.SourceBundle.Toc
                && e.Bundle.RecordAt == incomplete.SourceBundle.RecordAt
                && e.Name.StartsWith(donorPrefix, StringComparison.Ordinal)
                && (e.Name == sourceRoot || e.Name == sourceRoot + "_blueprint"))
            .Select(e => e.Name).Distinct(StringComparer.Ordinal)
            .Select(name => (DonorName: name, TargetName: targetPrefix
                + name[donorPrefix.Length..].Replace(donorIdentity, targetIdentity, StringComparison.Ordinal)))
            .ToList();
        if (!roots.Any(r => r.TargetName == root.Name)) roots.Add((donorName, root.Name));
        return BrandingClonePlan.Build(index, donorPrefix, targetPrefix,
            roots, artwork, incomplete.NewBundleName);
    }

    static string Game() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../Debugging/donor-clone/game"));
}
