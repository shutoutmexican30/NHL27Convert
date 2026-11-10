using Suite.Convert27;

namespace Suite.Tests;

public sealed class UniformViewerReferenceTests
{
    static readonly string[] NhlTeams =
    [
        "anaheim", "arizona", "boston", "buffalo", "calgary", "carolina", "chicago", "colorado",
        "columbus", "dallas", "detroit", "edmonton", "florida", "losangeles", "minnesota", "montreal",
        "nashville", "newjersey", "nyi", "nyr", "ottawa", "philadelphia", "pittsburgh", "sanjose",
        "seattle", "stlouis", "tampabay", "toronto", "vancouver", "vegas", "washington", "winnipeg",
    ];

    [Fact]
    public void Every_current_team_has_home_and_away_reference_coverage()
    {
        var selections = NhlTeams.SelectMany(team => new[] { "home", "away" }.Select(role =>
        {
            string folder = $"content/character/skater/league/nhl/{team}/adidas/{role}";
            var selected = UniformViewerReference.FindLocal(folder, "2026-27");
            Assert.NotNull(selected);
            Assert.Equal(role, selected.Role);
            Assert.Equal(role == "home" ? 0 : selected.Kits - 1, selected.Kit);
            Assert.True(File.Exists(selected.Png));
            return selected;
        })).ToList();

        Assert.Equal(64, selections.Count);
        Assert.Equal(32, selections.Select(s => s.TeamKey).Distinct().Count());
        Assert.Equal("Ducks", selections[0].TeamKey);
        Assert.All(selections.Where(s => s.TeamKey == "Mammoth"), s => Assert.Contains("Mammoth", s.Png));
        Assert.Equal(2, selections.Count(s => s.TeamKey == "Mammoth"));
    }

    [Fact]
    public void Three_kit_drawing_uses_last_band_for_away_and_rejects_unmapped_alternates()
    {
        string away = "content/character/skater/league/nhl/buffalo/adidas/away";
        var selected = UniformViewerReference.ForFolder(away, 3, @"C:\reference\Sabres.png");
        Assert.NotNull(selected);
        Assert.Equal(2, selected.Kit);
        Assert.Equal("away", selected.Role);

        Assert.Null(UniformViewerReference.ForFolder(
            "content/character/skater/league/nhl/anaheim/adidas/two", 2, @"C:\reference\Ducks.png"));
    }
}
