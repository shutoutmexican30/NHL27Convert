using System.Text;
using Suite.Rosters;

namespace Suite.Rosters.Tests;

public sealed class GoalieGearTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "cims-goalie-gear-tests", Guid.NewGuid().ToString("N"));
    string Main => Path.Combine(root, "main");
    string Source => Path.Combine(root, "nhl27");
    string Out => Path.Combine(root, "out");
    const string Table = "exhibitiongoalieequipment";
    const string TeamTable = "stockgoalieequipment";
    static readonly string[] Header = ["game_id", "padszone1color", "blockerzone1color", "sticktapecolor", "pads", "trapper", "blocker", "stick", "maskid", "masktype", "showcustomcolors", "showcustomstick"];
    static readonly string[] TeamHeader = ["game_id", "team", "league", "key", "padszone1color", "blockerzone1color", "sticktapecolor", "pads", "trapper", "blocker", "stick", "maskid", "masktype", "showcustomcolors", "showcustomstick"];

    public GoalieGearTests()
    {
        Directory.CreateDirectory(Main); Directory.CreateDirectory(Source);
        Write(Main, [
            ["10", "100", "101", "1", "34", "34", "34", "69", "48", "0", "0", "0"],
            ["20", "200", "201", "2", "65", "65", "65", "41", "22", "0", "0", "0"]
        ]);
        Write(Source, [
            // All source model ids occur in the NHL 24 domain: take the correct pad/mask choices and colours.
            ["10", "999", "998", "3", "65", "65", "65", "41", "22", "1", "1", "1"],
            // 999 is not a target model: preserve the NHL 24 model but still take every colour zone.
            ["20", "888", "887", "4", "999", "999", "999", "999", "999", "1", "1", "1"]
        ]);
        Write(Main, TeamTable, TeamHeader, [["10", "2", "0", "1", "100", "101", "1", "34", "34", "34", "69", "48", "0", "0", "0"]]);
        Write(Source, TeamTable, TeamHeader, [["10", "2", "0", "1", "999", "998", "3", "65", "65", "65", "41", "22", "1", "1", "1"]]);
    }

    void Write(string dir, string[][] rows)
    {
        Write(dir, Table, Header, rows);
    }
    static void Write(string dir, string name, string[] header, string[][] rows)
    {
        File.WriteAllText(T3Csv.PathOf(dir, name), string.Join(',', header) + "\r\n" +
            string.Join("\r\n", rows.Select(r => string.Join(',', r))) + "\r\n", new UTF8Encoding(true));
    }

    [Fact]
    public void Copies_colour_zones_and_only_models_known_to_nhl24()
    {
        var notes = new List<string>();
        var gear = GoalieGear.Build(Source, Main, notes);
        Assert.NotNull(gear);
        var report = new WriteReport();
        Assert.Equal(2, gear!.ApplyTo(Main, Out, report));
        var result = T3Csv.Load(Out, Table);
        Assert.Equal("999", result.Get(result.Rows[0], "padszone1color"));
        Assert.Equal("998", result.Get(result.Rows[0], "blockerzone1color"));
        Assert.Equal("3", result.Get(result.Rows[0], "sticktapecolor"));
        Assert.Equal("65", result.Get(result.Rows[0], "pads"));
        Assert.Equal("48", result.Get(result.Rows[0], "maskid"));
        Assert.Equal("888", result.Get(result.Rows[1], "padszone1color"));
        Assert.Equal("65", result.Get(result.Rows[1], "pads"));
        Assert.Equal("22", result.Get(result.Rows[1], "maskid"));
        var team = T3Csv.Load(Out, TeamTable);
        Assert.Equal("22", team.Get(team.Rows[0], "maskid"));
        Assert.Equal("999", team.Get(team.Rows[0], "padszone1color"));
        Assert.Equal(7, report.Counts["GoalieGearModelFallbacks"]);
    }

    public void Dispose() => Directory.Delete(root, true);
}
