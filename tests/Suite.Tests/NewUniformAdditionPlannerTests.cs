using Suite.Convert27;
using Suite.Core;
using Suite.Rosters;

namespace Suite.Tests;

public sealed class NewUniformAdditionPlannerTests
{
    static string Fixture(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../Debugging/row-planner", name));
    static string Meta => Fixture("nhl_game-meta.xml");
    static Dictionary<string, byte[]> Sources() => new(StringComparer.Ordinal)
    {
        ["nhl_game"] = File.ReadAllBytes(Fixture("nhl_game.db")),
        ["initial_experience"] = File.ReadAllBytes(Fixture("initial_experience.db")),
    };

    [Fact]
    public void Sixty_four_additions_allocate_spare_rows_and_never_map_to_existing_resources()
    {
        var additions = Enumerable.Range(0, 32).SelectMany(team => new[]
        {
            Addition(team, "home"), Addition(team, "away"),
        }).ToList();

        var plan = NewUniformAdditionPlanner.Build(additions, Sources(), Meta, _ => false);

        Assert.Equal(64, plan.UniformCount);
        Assert.Equal(128, plan.JerseyRows.InsertedRows);
        Assert.Equal(64, plan.TargetSlots.Distinct(StringComparer.Ordinal).Count());
        Assert.All(plan.Additions.Where(a => a.SourceKit == "home"), a => Assert.Equal(25, a.TargetVariant));
        Assert.All(plan.Additions.Where(a => a.SourceKit == "away"), a => Assert.Equal(24, a.TargetVariant));
        Assert.Contains(plan.TargetSlots, s => s.Contains("/twentyfive/", StringComparison.Ordinal));
        Assert.Contains(plan.TargetSlots, s => s.Contains("/twentyfour/", StringComparison.Ordinal));
        var mod = plan.ToMod("64 Fanatics uniforms");
        Assert.Equal(NewUniformAddition.ModKind, mod.Kind);
        Assert.Empty(mod.Replacements);
        Assert.Empty(ModApply.EffectiveReplacements([mod]));
    }

    [Fact]
    public void An_existing_target_resource_is_a_hard_failure()
    {
        var addition = Addition(2, "home");
        string target = NewUniformAdditionPlanner.MapTargetSlot(addition.Textures[0].SourceSlot,
            addition.SourcePrefix, "content/character/skater/league/nhl/team02/adidas/twentyfive/");
        var ex = Assert.Throws<InvalidDataException>(() => NewUniformAdditionPlanner.Build(
            [addition], Sources(), Meta, name => name == target));
        Assert.Contains("will not be replaced", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_variants_cannot_reuse_an_existing_jersey_row()
    {
        var addition = Addition(0, "home");
        addition.TargetVariant = 2; // Anaheim's stock alternate in the real fixture.
        var ex = Assert.Throws<InvalidDataException>(() => NewUniformAdditionPlanner.Build(
            [addition], Sources(), Meta, _ => false));
        Assert.Contains("already has a jersey row", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_slot_outside_the_team_folder_is_refused()
    {
        var addition = Addition(2, "home");
        addition.Textures[0].SourceSlot = addition.Textures[0].SourceSlot.Replace("/team02/", "/team03/", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => NewUniformAdditionPlanner.Build([addition], Sources(), Meta, _ => false));
    }

    [Fact]
    public void Clone_bundle_names_follow_the_stock_target_convention()
    {
        // The engine asks for the bundle by target folder + target family identity + platform suffix. Mapping the
        // donor bundle name must produce exactly that, or the game shows placeholder art despite present textures.
        const string donor = "gen4b/content/character/skater/league/nhl/vegas/adidas/away/";
        const string target = "gen4b/content/character/skater/league/nhl/vegas/adidas/twentyfive/";
        string mapped = NewUniformAdditionPlanner.MapTargetSlot(
            donor + "jersey_adidas_vegas_away_launch_player_team_brt", donor, target);
        Assert.Equal(target + "jersey_adidas_vegas_twentyfive_launch_player_team_brt", mapped);
        Assert.DoesNotContain("__cims_", mapped, StringComparison.Ordinal);
        Assert.DoesNotContain("away", mapped, StringComparison.Ordinal);
    }

    static NewUniformAddition Addition(int team, string kit)
    {
        string name = $"team{team:D2}";
        string source = $"content/character/skater/league/nhl/{name}/adidas/{kit}";
        string slot = $"{source}/jersey_adidas_{name}_{kit}_base_color";
        return new NewUniformAddition
        {
            TeamId = team,
            Team = name,
            SourceKit = kit,
            IsLight = kit == "away",
            SourcePrefix = source,
            Textures = [new NewUniformTexture
            {
                SourceSlot = slot,
                Artwork = new TextureReplacement(slot, $"{name}-{kit}.png"),
            }],
        };
    }
}
