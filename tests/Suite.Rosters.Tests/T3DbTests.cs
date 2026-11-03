using Suite.Core;
using Suite.Rosters;
using Xunit;

namespace Suite.Rosters.Tests;

/// <summary>The in-place T3DB editor against the real stock databases (skipped when they are not on this machine).</summary>
public class T3DbTests
{
    static readonly string Stock = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes");
    static string Meta => Path.Combine(Stock, "database", "t3db", "nhl_game-meta.xml");
    static IEnumerable<string> Dbs() => File.Exists(Meta)
        ? new[] { Path.Combine(Stock, "database", "t3db", "nhl_game.db") }.Concat(Directory.GetFiles(Path.Combine(Stock, "roster"), "*.db"))
        : [];

    [Fact]
    public void Load_and_save_unchanged_is_byte_identical()
    {
        foreach (var path in Dbs())
        {
            var bytes = File.ReadAllBytes(path);
            var db = T3Db.Load((byte[])bytes.Clone(), Meta);
            Assert.Equal(bytes, db.Save());
        }
    }

    [Fact]
    public void Integers_match_the_dump_and_an_edit_reads_back_with_valid_crcs()
    {
        var path = Path.Combine(Stock, "database", "t3db", "nhl_game.db");
        var dump = Path.Combine(SuitePaths.Local, "dbwork", "nhl_game", "before");
        if (!File.Exists(path) || !File.Exists(T3Csv.PathOf(dump, "exhibitionplayerbiotable"))) return;
        var db = T3Db.Load(File.ReadAllBytes(path), Meta);
        // Tables the roster sync does not rebuild, so the "before" dump still equals the stock file.
        foreach (var name in new[] { "exhibitionteams", "stockteamequipmenttable" })
        {
            var t = db.Tables[name];
            var csv = T3Csv.Load(dump, name);
            Assert.Equal(csv.ValidCount, t.InUse);
            foreach (var r in new[] { 0, t.InUse / 2, t.InUse - 1 })
                foreach (var f in t.Fields.Values.Where(f => f.Type == 3 && csv.Has(f.Name)))
                    Assert.Equal(long.Parse(csv.Get(csv.Rows[r], f.Name)), db.GetInt(t, r, f.Name));
        }
        var teams = db.Tables["exhibitionteams"];
        var old = db.GetInt(teams, 22, "feprimarycolor");
        db.SetInt(teams, 22, "feprimarycolor", 7122148);
        var saved = db.Save();
        var again = T3Db.Load(saved, Meta);   // throws on any CRC mismatch
        Assert.Equal(7122148, again.GetInt(again.Tables["exhibitionteams"], 22, "feprimarycolor"));
        Assert.Equal(File.ReadAllBytes(path).Length, saved.Length);
        Assert.NotEqual(7122148, old);
    }
}
