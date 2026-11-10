using Suite.Core;
using Suite.Rosters;
using Xunit;

namespace Suite.Rosters.Tests;

/// <summary>The teams/lines/strategies editor against the real stock databases (skipped when they are not on this machine).</summary>
public class TeamEditorTests
{
    static readonly string Stock = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes");
    static string Db => Path.Combine(Stock, "database", "t3db", "nhl_game.db");
    static string Meta => Path.Combine(Stock, "database", "t3db", "nhl_game-meta.xml");
    static bool HasStock => File.Exists(Db) && File.Exists(Meta);

    static TeamEditor Load() => TeamEditor.Load(File.ReadAllBytes(Db), Meta);

    [Fact]
    public void Teams_and_players_are_readable()
    {
        if (!HasStock) return;
        var e = Load();
        Assert.True(e.HasTeams);
        var teams = e.Teams();
        Assert.True(teams.Count > 200, $"expected the exhibition team table, got {teams.Count}");
        Assert.All(teams, t => Assert.False(string.IsNullOrWhiteSpace(t.Name)));
        var nhl = teams.First(t => t.Index == 22); // Utah in the 1.60 stock
        var players = e.Players(nhl.Index);
        Assert.True(players.Count >= 20, $"Utah should have a full roster, got {players.Count}");
        Assert.All(players, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        Assert.Contains(players, p => p.Dressed == 1);
    }

    [Fact]
    public void No_edits_save_byte_identical()
    {
        if (!HasStock) return;
        var bytes = File.ReadAllBytes(Db);
        Assert.Equal(bytes, Load().Save());
    }

    [Fact]
    public void Swapping_two_wingers_passes_verify_and_reads_back()
    {
        if (!HasStock) return;
        var e = Load();
        var before = e.Verify();
        var team = e.Teams().First(t => TeamEditor.CoreSlots.All(s => e.HolderRow(t.Index, s) is not null));
        var a = e.HolderRow(team.Index, "l1lw")!.Value;
        var b = e.HolderRow(team.Index, "l2lw")!.Value;
        Assert.NotEqual(a, b);

        e.SetSlot(team.Index, "l1lw", b);
        e.SetSlot(team.Index, "l2lw", a);
        Assert.Equal(b, e.HolderRow(team.Index, "l1lw"));
        Assert.Equal(a, e.HolderRow(team.Index, "l2lw"));
        var after = e.Verify();
        Assert.Equal(before, after); // a clean swap adds no problems

        var again = TeamEditor.Load(e.Save(), Meta);
        Assert.Equal(b, again.HolderRow(team.Index, "l1lw"));
        Assert.Equal(a, again.HolderRow(team.Index, "l2lw"));
    }

    [Fact]
    public void A_goalie_cannot_play_left_wing()
    {
        if (!HasStock) return;
        var e = Load();
        var team = e.Teams().First(t => e.HolderRow(t.Index, "g1") is not null);
        var goalie = e.HolderRow(team.Index, "g1")!.Value;
        Assert.Throws<ArgumentException>(() => e.SetSlot(team.Index, "l1lw", goalie));
    }

    [Fact]
    public void Setting_a_slot_moves_it_off_the_previous_row()
    {
        if (!HasStock) return;
        var e = Load();
        var team = e.Teams().First(t => e.HolderRow(t.Index, "s1") is not null && e.HolderRow(t.Index, "s2") is not null);
        var first = e.HolderRow(team.Index, "s1")!.Value;
        var second = e.HolderRow(team.Index, "s2")!.Value;
        Assert.NotEqual(first, second);
        e.SetSlot(team.Index, "s2", first);
        Assert.Equal(first, e.HolderRow(team.Index, "s2"));          // s2 moved to first's row
        Assert.Equal(0, e.Db.GetInt(e.Db.Tables["exhibitionrostertable"], second, "s2")); // and off second's row
        Assert.Equal(first, e.HolderRow(team.Index, "s1"));          // s1 is a different unit, untouched
    }

    [Fact]
    public void A_slot_held_twice_is_flagged()
    {
        if (!HasStock) return;
        var e = Load();
        var team = e.Teams().First(t => e.HolderRow(t.Index, "s5") is not null);
        var holder = e.HolderRow(team.Index, "s5")!.Value;
        var other = e.Players(team.Index).First(p => p.RosterRow != holder).RosterRow;
        e.Db.SetInt(e.Db.Tables["exhibitionrostertable"], other, "s5", 1); // force the illegal state behind the editor's back
        Assert.Contains(e.Verify(), p => p.Contains("s5") && p.Contains("held by both"));
    }

    [Fact]
    public void Jersey_captain_and_style_edits_read_back()
    {
        if (!HasStock) return;
        var e = Load();
        var team = e.Teams().First(t => e.Players(t.Index).Count >= 20);
        var p = e.Players(team.Index)[0];
        var jersey = p.Jersey == 99 ? 98 : p.Jersey + 1;
        e.SetJersey(p.RosterRow, jersey);
        e.SetCaptain(p.RosterRow, p.Captain == 1 ? 2 : 1);
        e.SetPlayerStyle(p.RosterRow, (p.Style + 1) % 16);
        var again = TeamEditor.Load(e.Save(), Meta);
        var q = again.Players(team.Index)[0];
        Assert.Equal(jersey, q.Jersey);
        Assert.Equal(p.Captain == 1 ? 2 : 1, q.Captain);
        Assert.Equal((p.Style + 1) % 16, q.Style);
    }

    [Fact]
    public void Saved_roster_in_the_cims_copy_loads_and_round_trips()
    {
        var game = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "NHL24PC-CIMS", "game");
        if (!File.Exists(Meta) || !Directory.Exists(game)) return;
        var save = SavedRoster.SavedataRoots(game).SelectMany(SavedRoster.Find).FirstOrDefault();
        if (save is null) return; // no roster saved in game on this machine
        var blob = save.ReadDb();
        var n = SavedRoster.DbLength(blob);
        var e = TeamEditor.Load(blob[..n], Meta);
        Assert.True(e.HasTeams);
        var teams = e.Teams();
        Assert.True(teams.Count > 200, $"expected the exhibition team table, got {teams.Count}");
        var utah = teams.First(t => t.Index == 22);
        Assert.True(e.Players(utah.Index).Count >= 20);
        e.Verify(); // must not throw
        Assert.Equal(blob[..n], e.Save()); // no edits -> byte-identical
    }

    [Fact]
    public void Strategy_groups_cover_every_field_and_edits_read_back()
    {
        if (!HasStock) return;
        var e = Load();
        Assert.True(e.HasStrategies);
        var team = e.Teams().First(t => e.StrategyGroups(t.Index).Count > 0);
        var groups = e.StrategyGroups(team.Index);
        var flat = groups.SelectMany(g => g.Fields).ToList();
        Assert.True(flat.Count >= 180, $"expected the full strategy row, got {flat.Count}");
        Assert.Equal(flat.Count, flat.Select(f => f.Name).Distinct().Count()); // no field in two groups

        var f = flat.First(x => x.Name == "oz_forecheck");
        var v = f.Value == f.High ? f.Value - 1 : f.Value + 1;
        e.SetStrategy(team.Index, f.Name, v);
        Assert.Throws<ArgumentOutOfRangeException>(() => e.SetStrategy(team.Index, f.Name, f.High + 1));
        var again = TeamEditor.Load(e.Save(), Meta);
        Assert.Equal(v, again.StrategyGroups(team.Index).SelectMany(g => g.Fields).First(x => x.Name == "oz_forecheck").Value);
    }
}
