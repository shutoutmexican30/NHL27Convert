using Suite.Core;
using Suite.Rosters;

namespace Suite.Rosters.Tests;

public sealed class NewUniformEquipmentPlannerTests
{
    static string Fixture(params string[] parts) => Path.GetFullPath(Path.Combine(
        [AppContext.BaseDirectory, "..", "..", "..", "..", "..", "reference", "nhl24-full-csv", "gamemodes", "database", "t3db", .. parts]));
    static string Meta => Fixture("nhl_game-meta.xml");
    static string Source27 => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "reference", "nhl27-full-csv"));

    static IReadOnlyDictionary<string, byte[]> Sources() => new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["nhl_game"] = File.ReadAllBytes(Fixture("nhl_game.db")),
        ["initial_experience"] = File.ReadAllBytes(Fixture("..", "..", "roster", "initial_experience.db")),
    };

    static NewUniformAddition Addition(int team, string kit)
    {
        int source = (team, kit) switch
        {
            (2, "home") => 13, (2, "away") => 14,
            (22, "home") => 12, (22, "away") => 13,
            (24, "home") => 14, (24, "away") => 15,
            (5, "away") => 13,
            _ => kit == "home" ? 10 : 11,
        };
        return new NewUniformAddition
        {
            TeamId = team, Team = $"team{team:D2}", SourceKit = kit, IsLight = kit == "away", Style = 2,
            TargetVariant = kit == "home" ? 25 : 24,
            SourceFolder27 = $"content/character/skater/league/nhl/team{team:D2}/adidas/{TextureName.Numbers[source]}",
        };
    }

    static (NewUniformEquipmentPlan Equipment, IReadOnlyDictionary<string, byte[]> Databases) FullPlan()
    {
        var additions = Enumerable.Range(0, 32).SelectMany(team => new[] { Addition(team, "home"), Addition(team, "away") }).ToList();
        var jerseys = JerseyRowPlanner.Build(Sources(), Meta,
            additions.Select(a => new NewJerseyRowRequest(a.TeamId, a.TargetVariant, a.IsLight)));
        var equipment = NewUniformEquipmentPlanner.Build(jerseys.Databases, Meta, Source27, additions);
        return (equipment, equipment.Databases);
    }

    [Fact]
    public void All_64_kits_get_unambiguous_current_equipment_without_touching_stock_rows()
    {
        var (plan, _) = FullPlan();
        foreach (string database in plan.Databases.Keys)
        {
            var rows = plan.Rows.Where(r => r.Database == database).ToList();
            Assert.Equal(128, rows.Count(r => r.Brand == -1));
            Assert.Equal(64, rows.Select(r => (r.Team, r.TargetVariant)).Distinct().Count());
            Assert.All(rows.GroupBy(r => (r.Team, r.Brand, r.JerseyArtId, r.Equipment, r.Era, r.TargetVariant)),
                group => Assert.Single(group));
            Assert.All(rows.Where(r => r.TargetVariant == 25), r => Assert.Equal(25, r.TargetVariant));
            Assert.All(rows.Where(r => r.TargetVariant == 24), r => Assert.Equal(24, r.TargetVariant));
        }
        Assert.Equal(2, plan.ChangedDatabases.Count);
        Assert.True(plan.InsertedRows >= 256);
    }

    [Fact]
    public void Exact_current_rows_beat_historical_alternates_and_wildcards()
    {
        var (plan, _) = FullPlan();
        var rows = plan.Rows.Where(r => r.Database == "nhl_game").ToList();
        var torontoHelmet = rows.Single(r => r.Team == 27 && r.TargetVariant == 25 && r.Equipment == 4 && r.Brand == -1);
        Assert.Equal(0x052E6C, torontoHelmet.Zones["zone1"]);
        Assert.Equal(-1, torontoHelmet.Source.Variant);
        Assert.Equal(2, torontoHelmet.Source.Era);
        Assert.DoesNotContain(rows, r => r.Team == 27 && r.TargetVariant == 25 && r.Equipment == 4 && r.Brand == -1
            && r.Zones["zone1"] == 0x007938);
        var torontoGloves = rows.Single(r => r.Team == 27 && r.TargetVariant == 25 && r.Equipment == 7 && r.Brand == -1);
        Assert.Equal(10, torontoGloves.Source.Variant);
        Assert.Equal(0x102962, torontoGloves.Zones["zone1"]);
        var branded = rows.Single(r => r.Team == 27 && r.TargetVariant == 25 && r.Equipment == 4 && r.Brand == 58);
        Assert.Equal(0xE7E7E7, branded.Zones["zone2"]);
        Assert.Equal(-1, branded.Source.Variant); // CCM wildcard, not the old green exact alternate.
        Assert.All(rows.Where(r => r.Team == 24 && r.Equipment == 4), r => Assert.Equal(0x0072CE, r.Zones["zone1"]));
        Assert.All(rows.Where(r => r.Team == 22 && r.Brand == -1), r =>
        {
            Assert.Equal(r.TargetVariant == 25 ? 12 : 13, r.SourceVariant);
            Assert.DoesNotContain(r.Zones.Values, value => value == 0x862633 || value == 0x154734);
        });
    }

    [Fact]
    public void Replanning_is_idempotent_and_retains_source_candidate_evidence()
    {
        var (plan, databases) = FullPlan();
        var additions = Enumerable.Range(0, 32).SelectMany(team => new[] { Addition(team, "home"), Addition(team, "away") }).ToList();
        var again = NewUniformEquipmentPlanner.Build(databases, Meta, Source27, additions);
        Assert.Equal(0, again.InsertedRows);
        foreach (string database in databases.Keys)
            Assert.Equal(databases[database], again.Databases[database]);
        var wildcardAddition = Addition(25, "home");
        wildcardAddition.SourceFolder27 = "content/character/skater/league/nhl/sanjose/adidas/nine";
        var wildcardJerseys = JerseyRowPlanner.Build(Sources(), Meta, [new NewJerseyRowRequest(25, 25, false)]);
        var wildcardPlan = NewUniformEquipmentPlanner.Build(wildcardJerseys.Databases, Meta, Source27, [wildcardAddition]);
        var sanJose = wildcardPlan.Rows.Where(r => r.Database == "nhl_game" && r.Team == 25 && r.TargetVariant == 25
            && r.Equipment == 7 && r.Brand == -1).Single();
        Assert.True(sanJose.Candidates.Count > 1);
        Assert.Equal(-1, sanJose.Source.Variant);
        Assert.Equal(sanJose.Candidates.Where(c => c.Era == 2 && c.Variant == -1).Min(c => c.SourceOrder), sanJose.Source.SourceOrder);
        Assert.All(plan.Rows, r => Assert.Equal(15, r.Zones.Count));
    }

    [Fact]
    public void Structural_home_away_donor_cannot_override_the_numbered_source()
    {
        var addition = Addition(0, "home");
        addition.SourcePrefix = "content/character/skater/league/nhl/anaheim/adidas/home/";
        addition.TargetVariant = 25;
        var jerseys = JerseyRowPlanner.Build(Sources(), Meta, [new NewJerseyRowRequest(0, 25, false)]);
        var plan = NewUniformEquipmentPlanner.Build(jerseys.Databases, Meta, Source27, [addition]);
        var anaheim = plan.Rows.Single(r => r.Database == "nhl_game" && r.Equipment == 4 && r.Brand == -1);
        Assert.Equal(10, anaheim.SourceVariant);
        Assert.Equal(0x202020, anaheim.Zones["zone1"]);
        Assert.Equal(0xFC5F1A, anaheim.Zones["zone6"]);
    }
}
