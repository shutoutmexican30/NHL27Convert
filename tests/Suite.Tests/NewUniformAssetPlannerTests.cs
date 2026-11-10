using Suite.Convert27;
using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Tests;

public sealed class NewUniformAssetPlannerTests
{
    const string SourcePrefix = "content/character/skater/league/nhl/arizona/adidas/four";
    const string TargetPrefix = "content/character/skater/league/nhl/arizona/adidas/twentyfive";
    const string SourceName = SourcePrefix + "/branding_jersey_adidas_arizona_four_color_array";

    [Fact]
    public void Real_source_array_requires_every_layer_and_clones_only_new_team_assets()
    {
        string game = Game();
        Oodle.Ensure(SuiteSettings.Load());
        var index = BundleIndex.Build(game);
        var source = TextureSlots.Read(game, index, SourceName);
        string target = NewUniformAdditionPlanner.MapTargetSlot(SourceName, SourcePrefix, TargetPrefix);
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../Debugging/asset-planner-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var images = Ps4Codec.DecodeSlices(source.Res, source.Stock);
            var layers = new Dictionary<int, string>();
            for (int i = 0; i < images.Count; i++)
            {
                string path = Path.Combine(root, $"layer-{i}.png");
                ImageIO.SavePng(images[i], path);
                layers.Add(i, path);
            }
            string[] roles = [.. Enumerable.Range(0, layers.Count).Select(i => "role" + i)];
            NewUniformAddition Addition(Dictionary<int, string> art) => new()
            {
                TeamId = 22, Team = "arizona", SourceKit = "home", SourcePrefix = SourcePrefix,
                TargetPrefix = TargetPrefix, TargetVariant = 25,
                Textures = [new NewUniformTexture
                {
                    SourceSlot = SourceName, TargetSlot = target, RequiredRoles = [.. roles],
                    Artwork = new TextureReplacement(SourceName, "", art),
                }],
            };
            var roleMaps = new Dictionary<string, IReadOnlyList<string>> { [SourceName] = roles };

            var incomplete = layers.Where(kv => kv.Key != layers.Keys.Max()).ToDictionary();
            var blocked = Assert.Throws<InvalidDataException>(() => NewUniformAssetPlanner.Build(index, Addition(incomplete), roleMaps));
            Assert.Contains("donor pixels cannot fill", blocked.Message, StringComparison.Ordinal);

            string toc = index.ResByName[SourceName][0].Bundle.Toc;
            byte[] tocBefore = File.ReadAllBytes(toc);
            var plan = NewUniformAssetPlanner.Build(index, Addition(layers), roleMaps);
            plan.ThrowIfIncomplete();
            Assert.NotEmpty(plan.Clones);
            Assert.All(plan.Clones, clone =>
            {
                clone.ThrowIfConventionalRootMissing();
                string root = clone.NewBundleName.StartsWith("gen4b/", StringComparison.Ordinal)
                    ? clone.NewBundleName["gen4b/".Length..] : clone.NewBundleName;
                if (!root.EndsWith("_player_team_brt", StringComparison.Ordinal)) return;
                Assert.Contains(clone.Entries, e => e.IsClone && e.NewChunkId is null && e.Name == root);
                Assert.Contains(clone.Entries, e => e.IsClone && e.NewChunkId is null && e.Name == root + "_blueprint");
            });
            const string sourceIdentity = "arizona_four";
            const string targetIdentity = "arizona_twentyfive";
            string[] companionLeaves =
            [
                $"jersey_adidas_{targetIdentity}_base_player_team_brt",
                $"font_adidas_{targetIdentity}_player_team_brt",
                $"branding_jersey_adidas_{targetIdentity}_color_array_player_team_brt",
                $"branding_jersey_adidas_{targetIdentity}_normal_array_player_team_brt",
                $"branding_jersey_adidas_{targetIdentity}_coeff_array_player_team_brt",
                $"branding_pant_adidas_{targetIdentity}_color_array_player_team_brt",
                $"branding_pant_adidas_{targetIdentity}_normal_array_player_team_brt",
                $"branding_pant_adidas_{targetIdentity}_coeff_array_player_team_brt",
            ];
            foreach (string leaf in companionLeaves)
            {
                string sourceRoot = SourcePrefix + "/" + leaf.Replace(targetIdentity, sourceIdentity, StringComparison.Ordinal);
                Assert.Contains(sourceRoot, index.EbxByName.Keys);
                string targetRoot = TargetPrefix + "/" + leaf;
                var companion = Assert.Single(plan.Clones.Where(c =>
                    (c.NewBundleName.StartsWith("gen4b/", StringComparison.Ordinal)
                        ? c.NewBundleName["gen4b/".Length..] : c.NewBundleName) == targetRoot));
                Assert.Contains(companion.Entries, e => e.IsClone && e.NewChunkId is null && e.Name == targetRoot);
                Assert.Contains(companion.Entries, e => e.IsClone && e.NewChunkId is null && e.Name == targetRoot + "_blueprint");
            }
            var sourceKitRoots = index.EbxByName.Keys.Where(name => name.StartsWith(SourcePrefix + "/", StringComparison.Ordinal)
                && name.EndsWith("_player_team_brt", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(sourceKitRoots);
            foreach (string sourceRoot in sourceKitRoots)
            {
                string targetRoot = NewUniformAdditionPlanner.MapTargetSlot(sourceRoot, SourcePrefix, TargetPrefix);
                var companion = Assert.Single(plan.Clones.Where(c =>
                    (c.NewBundleName.StartsWith("gen4b/", StringComparison.Ordinal)
                        ? c.NewBundleName["gen4b/".Length..] : c.NewBundleName) == targetRoot));
                Assert.Contains(companion.Entries, e => e.IsClone && e.NewChunkId is null && e.Name == targetRoot);
                Assert.Contains(companion.Entries, e => e.IsClone && e.NewChunkId is null && e.Name == targetRoot + "_blueprint");
            }
            Assert.Equal(tocBefore, File.ReadAllBytes(toc));
            Assert.All(plan.Clones.SelectMany(c => c.Entries.Where(e => e.NewChunkId is not null)),
                e => Assert.DoesNotContain(index.ChunksById.Keys, id => id == e.NewChunkId));
            var chunk = plan.Clones.SelectMany(c => c.Entries).Single(e => e.Name == target && e.NewChunkId is not null);
            Assert.Equal(plan.Artwork[target], ChunkBlocks.Parse(chunk.Replacement!).Decode());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static string Game() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../Debugging/donor-clone/game"));
}
