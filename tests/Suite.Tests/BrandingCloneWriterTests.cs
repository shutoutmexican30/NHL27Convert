using System.Security.Cryptography;
using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Tests;

public sealed class BrandingCloneWriterTests
{
    const string DonorPrefix = "content/character/skater/league/nhl/arizona/adidas/four";
    const string TargetPrefix = "content/character/skater/league/nhl/cims_writer_test/adidas/four";
    const string DonorName = DonorPrefix + "/branding_jersey_adidas_arizona_four_color_array";
    const string TargetName = TargetPrefix + "/branding_jersey_adidas_cims_writer_test_four_color_array";

    [Fact]
    public void Staging_apply_reads_back_and_restore_recovers_original_toc_and_cas_lengths()
    {
        string source = Game();
        Oodle.Ensure(SuiteSettings.Load());
        var index = BundleIndex.Build(source);
        var incomplete = BrandingClonePlan.Build(index, DonorPrefix, TargetPrefix, DonorName, TargetName);
        var artwork = incomplete.MissingArtwork.ToDictionary(name => name, name =>
            ChunkBlocks.Parse(incomplete.Entries.Single(e => e.Name == name && e.NewChunkId is not null).Replacement!).Decode());
        var plan = BrandingClonePlan.Build(index, DonorPrefix, TargetPrefix, DonorName, TargetName, artwork);

        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../Debugging/writer-tests", Guid.NewGuid().ToString("N")));
        string target = Path.Combine(root, "game");
        string backup = Path.Combine(root, "backup");
        byte[] sourceToc = File.ReadAllBytes(plan.SourceBundle.Toc);
        var sourceCas = plan.Entries.Where(e => e.IsClone).Select(e => e.Source.Cas)
            .Append(plan.SourceBundle.Manifest.Cas).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sourceLengths = sourceCas.ToDictionary(cas => cas, cas => new FileInfo(cas).Length);
        try
        {
            string targetToc = Path.Combine(target, Path.GetRelativePath(source, plan.SourceBundle.Toc));
            Directory.CreateDirectory(Path.GetDirectoryName(targetToc)!);
            File.WriteAllBytes(targetToc, sourceToc);
            foreach (var cas in sourceCas)
            {
                string destination = Path.Combine(target, Path.GetRelativePath(source, cas));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                stream.SetLength(sourceLengths[cas]); // Sparse staging placeholder; donor offsets are never read by the writer.
            }

            var result = BrandingCloneWriter.Apply(plan, target, backup);
            Assert.Equal(plan.NewBundleName, result.BundleName);
            Assert.True(result.ClonedEntries > 0);
            Assert.True(result.SharedEntries > 0);
            Assert.True(result.AppendedBytes > 0);
            byte[] changedToc = File.ReadAllBytes(targetToc);
            Assert.NotEqual(sourceToc, changedToc);
            var archive = TocArchive.Parse(changedToc);
            Assert.NotNull(archive.FindBundle(plan.NewBundleName));
            foreach (var entry in plan.Entries.Where(e => e.NewChunkId is not null))
                Assert.NotNull(archive.FindChunk(entry.NewChunkId!.Value));

            BrandingCloneWriter.Restore(target, backup);
            Assert.Equal(sourceToc, File.ReadAllBytes(targetToc));
            foreach (var cas in sourceCas)
                Assert.Equal(sourceLengths[cas], new FileInfo(Path.Combine(target, Path.GetRelativePath(source, cas))).Length);
            // The actual read-only donor copy was never changed.
            Assert.Equal(sourceToc, File.ReadAllBytes(plan.SourceBundle.Toc));
            foreach (var cas in sourceCas) Assert.Equal(sourceLengths[cas], new FileInfo(cas).Length);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Batch_commits_two_plans_sharing_one_toc_and_restores_together()
    {
        const string targetPrefix2 = "content/character/skater/league/nhl/cims_writer_test_two/adidas/four";
        const string targetName2 = targetPrefix2 + "/branding_jersey_adidas_cims_writer_test_two_four_color_array";
        string source = Game();
        Oodle.Ensure(SuiteSettings.Load());
        var index = BundleIndex.Build(source);
        var plans = new[] { (TargetPrefix, TargetName), (targetPrefix2, targetName2) }.Select(target =>
        {
            var incomplete = BrandingClonePlan.Build(index, DonorPrefix, target.Item1, DonorName, target.Item2);
            var artwork = incomplete.MissingArtwork.ToDictionary(name => name, name =>
                ChunkBlocks.Parse(incomplete.Entries.Single(e => e.Name == name && e.NewChunkId is not null).Replacement!).Decode());
            return BrandingClonePlan.Build(index, DonorPrefix, target.Item1, DonorName, target.Item2, artwork);
        }).ToArray();
        Assert.Equal(plans[0].SourceBundle.Toc, plans[1].SourceBundle.Toc);

        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../Debugging/writer-tests", Guid.NewGuid().ToString("N")));
        string target = Path.Combine(root, "game"), backup = Path.Combine(root, "backup");
        byte[] sourceToc = File.ReadAllBytes(plans[0].SourceBundle.Toc);
        var sourceCas = plans.SelectMany(p => p.Entries.Where(e => e.IsClone).Select(e => e.Source.Cas)
            .Append(p.SourceBundle.Manifest.Cas)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sourceLengths = sourceCas.ToDictionary(cas => cas, cas => new FileInfo(cas).Length);
        try
        {
            string targetToc = Path.Combine(target, Path.GetRelativePath(source, plans[0].SourceBundle.Toc));
            Directory.CreateDirectory(Path.GetDirectoryName(targetToc)!);
            File.WriteAllBytes(targetToc, sourceToc);
            foreach (var cas in sourceCas)
            {
                string destination = Path.Combine(target, Path.GetRelativePath(source, cas));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                stream.SetLength(sourceLengths[cas]);
            }

            var result = BrandingCloneWriter.ApplyBatch(plans, target, backup);
            Assert.Equal(2, result.Bundles.Count);
            byte[] changedToc = File.ReadAllBytes(targetToc);
            var archive = TocArchive.Parse(changedToc);
            foreach (var plan in plans)
            {
                Assert.NotNull(archive.FindBundle(plan.NewBundleName));
                foreach (var entry in plan.Entries.Where(e => e.NewChunkId is not null))
                    Assert.NotNull(archive.FindChunk(entry.NewChunkId!.Value));
            }

            BrandingCloneWriter.Restore(target, backup);
            Assert.Equal(sourceToc, File.ReadAllBytes(targetToc));
            foreach (var cas in sourceCas)
                Assert.Equal(sourceLengths[cas], new FileInfo(Path.Combine(target, Path.GetRelativePath(source, cas))).Length);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static string Game() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../Debugging/donor-clone/game"));
}
