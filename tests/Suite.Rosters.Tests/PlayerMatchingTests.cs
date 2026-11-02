using Suite.Rosters;

namespace Suite.Rosters.Tests;

public class PlayerMatchingTests
{
    [Fact]
    public void Api_ascii_spelling_matches_existing_danish_player()
    {
        var birth = new DateOnly(1999, 3, 31);
        var existing = new DbPlayer(0, "15576", "Jonas", "Røndbjerg", birth, 30, 46, 1, true, 15576);
        var players = new[] { existing };
        var api = new ApiPlayer(8480000, "Jonas", "Rondbjerg", 46, "L", birth, null, "VGK");
        Assert.Same(existing, RosterSync.Match(api,
            players.ToLookup(p => RosterSync.Norm(p.Last)),
            players.ToLookup(p => RosterSync.Norm(p.First + p.Last))));
        Assert.Equal(RosterSync.Norm("Røndbjerg"), RosterSync.Norm("RØNDBJERG"));
    }

    [Fact]
    public void Older_duplicate_is_resolved_using_the_main_database_identity()
    {
        var birth = new DateOnly(1997, 4, 28);
        var old = new DbPlayer(0, "20691", "Tomy", "Novak", birth, -1, 82, 0, true, 1);
        var main = new DbPlayer(1, "21778", "Thomas", "Novak", birth, 23, 82, 0, true, 2);
        var players = new[] { old, main };
        var last = players.ToLookup(p => RosterSync.Norm(p.Last));
        var full = players.ToLookup(p => RosterSync.Norm(p.First + p.Last));
        var api = new ApiPlayer(8480001, "Tommy", "Novak", 82, "C", birth, null, "PIT");
        Assert.Null(RosterSync.Match(api, last, full));
        Assert.Same(main, RosterSync.Match(api, last, full, new HashSet<string> { "21778" }));
    }
}
