using Suite.Core;
using Suite.Rosters;

namespace Suite.Tests;

public sealed class VariantMapTests
{
    [Fact]
    public void Current_anaheim_team_zero_and_its_database_defaults_are_not_lost()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string fixture = Path.Combine(root, "Debugging", "row-planner");
        var db = T3Db.Load(File.ReadAllBytes(Path.Combine(fixture, "nhl_game.db")), Path.Combine(fixture, "nhl_game-meta.xml"));
        var teams = db.Tables["exhibitionteams"];
        int row = Enumerable.Range(0, teams.InUse).Single(r =>
            db.GetString(teams, r, "cityname") == "Anaheim" && db.GetString(teams, r, "teamname") == "Anaheim Ducks");
        int id = (int)db.GetInt(teams, row, "index");
        var jerseys = db.Tables["stockteamjerseys"];
        var defaults = Enumerable.Range(0, jerseys.InUse)
            .Where(r => db.GetInt(jerseys, r, "teamid") == id && db.GetInt(jerseys, r, "isdefault") == 1)
            .Select(r => (Light: db.GetInt(jerseys, r, "islightcolor") == 1, Variant: (int)db.GetInt(jerseys, r, "variant")))
            .ToList();
        var variants = VariantMap.Parse(File.ReadAllText(Path.Combine(root, "data", "variant_map.json")));
        var mapped = variants.Teams.SingleOrDefault(t => t.Index == id);
        string evidence = $"Database: Anaheim team {id}, defaults {string.Join(", ", defaults.Select(d => $"{(d.Light ? "away" : "home")}={d.Variant}"))}.";
        Assert.True(mapped is not null, evidence + " The current team is absent from variant_map.json.");
        Assert.Equal("anaheim", mapped.Nhl24.Folder);
        Assert.Equal(defaults.Single(d => !d.Light).Variant, mapped.Nhl24.HomeVariant);
        Assert.Equal(defaults.Single(d => d.Light).Variant, mapped.Nhl24.AwayVariant);
        Assert.Equal("home", mapped.Nhl24.Home);
        Assert.Equal("away", mapped.Nhl24.Away);
    }

    [Fact]
    public void A_missing_current_team_is_restored_without_replacing_custom_mappings()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string json = File.ReadAllText(Path.Combine(root, "data", "variant_map.json"));
        var shipped = VariantMap.Parse(json);
        var user = VariantMap.Parse(json);
        user.Teams.RemoveAll(t => t.Index == 0);
        user.Teams.Single(t => t.Index == 1).Nhl24.Home = "custom-home";

        Assert.Equal(1, VariantMap.RepairMissingCurrentTeams(user, shipped));
        Assert.Equal("anaheim", user.Teams.Single(t => t.Index == 0).Nhl24.Folder);
        Assert.Equal("custom-home", user.Teams.Single(t => t.Index == 1).Nhl24.Home);
        Assert.Equal(0, VariantMap.RepairMissingCurrentTeams(user, shipped));
    }
}
