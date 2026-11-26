using Suite.Core;
using Suite.Rosters;
using Xunit;

namespace Suite.Rosters.Tests;

/// <summary>The studio's player model against the stock roster databases. Skips when the stock export is not on the machine.</summary>
public class PlayerBookTests
{
    static readonly string Stock = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes");
    static string Meta => Path.Combine(Stock, "database", "t3db", "nhl_game-meta.xml");
    static string DbPath(string db) => db == "nhl_game" ? Path.Combine(Stock, "database", "t3db", "nhl_game.db") : Path.Combine(Stock, "roster", db + ".db");
    static bool Have => File.Exists(Meta) && File.Exists(DbPath("nhl_game")) && File.Exists(DbPath("entry_draft"));
    static T3Db Open(string db) => T3Db.Load(File.ReadAllBytes(DbPath(db)), Meta);

    [Fact]
    public void Lists_players_and_their_fields_in_groups()
    {
        if (!Have) return;
        var book = new PlayerBook(Open("nhl_game"));
        var players = book.Players();
        Assert.True(players.Count > 1000);
        var skater = players.First(p => !p.Goalie);
        var fields = book.FieldsOf(skater.GameId);
        Assert.Contains(fields, f => f.Field.Name == "skill_overall");
        Assert.Contains(fields, f => f.Field.Name == "firstname" && f.Spec.Kind == FieldKind.Text);
        Assert.All(fields.Where(f => f.Field.Name == "game_id"), f => Assert.True(f.ReadOnly));
    }

    [Fact]
    public void Edit_round_trips_and_out_of_range_is_refused()
    {
        if (!Have) return;
        var db = Open("nhl_game");
        var book = new PlayerBook(db);
        var id = book.Players().First(p => !p.Goalie).GameId;
        var f = book.FieldsOf(id).First(x => x.Field.Name == "skill_overall");
        var (low, high) = PlayerBook.Range(f.Field);
        book.SetInt(f, Math.Min(high, low + 40));
        var again = new PlayerBook(T3Db.Load((byte[])db.Save().Clone(), Meta));
        Assert.Equal(Math.Min(high, low + 40), again.GetInt(again.FieldsOf(id).First(x => x.Field.Name == "skill_overall")));
        Assert.ThrowsAny<Exception>(() => book.SetInt(f, high + 1));
    }

    [Fact]
    public void Mirror_makes_player_tables_equal()
    {
        if (!Have) return;
        var main = Open("nhl_game"); var other = Open("entry_draft");
        var book = new PlayerBook(main);
        var id = book.Players().First(p => !p.Goalie).GameId;
        book.SetText(book.FieldsOf(id).First(x => x.Field.Name == "lastname"), "Mirrored");
        PlayerMirror.CopyInto(main, other);
        var copy = new PlayerBook(T3Db.Load((byte[])other.Save().Clone(), Meta));
        Assert.Equal("Mirrored", copy.Players().First(p => p.GameId == id).Last);
        Assert.Equal(book.Players().Count, copy.Players().Count);
    }
}
