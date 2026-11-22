using Suite.Convert27;
using Suite.Rosters;

namespace Suite.Tests;

public sealed class KitDefaultsTests
{
    static string Fixture(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Debugging/row-planner", name));

    /// <summary>The fixture database with Fanatics rows (variants 25/24) added for Detroit (team 10), copied from its home/away rows.</summary>
    static T3Db WithFanatics()
    {
        var db = T3Db.Load(File.ReadAllBytes(Fixture("nhl_game.db")), Fixture("nhl_game-meta.xml"));
        var t = db.Tables["stockteamjerseys"];
        foreach (var (from, to, order) in new[] { (0, 25, 4), (1, 24, 5) })
        {
            if (Enumerable.Range(0, t.InUse).Any(r => db.GetInt(t, r, "teamid") == 10 && db.GetInt(t, r, "style") == 2 && db.GetInt(t, r, "variant") == to)) continue;
            int src = Enumerable.Range(0, t.InUse).First(r => db.GetInt(t, r, "teamid") == 10 && db.GetInt(t, r, "style") == 2 && db.GetInt(t, r, "variant") == from);
            t = db.InsertRecord(t, t.InUse, src);
            int row = t.InUse - 1;
            db.SetInt(t, row, "variant", to); db.SetInt(t, row, "isdefault", 0); db.SetInt(t, row, "orderid", order); db.SetInt(t, row, "locstring", 6);
        }
        return db;
    }

    [Fact]
    public void Fanatics_become_the_defaults_and_the_old_kits_alternates_named_23_24_adidas()
    {
        var db = WithFanatics();
        var t = db.Tables["stockteamjerseys"];
        int Row(int v) => Enumerable.Range(0, t.InUse).First(r => db.GetInt(t, r, "teamid") == 10 && db.GetInt(t, r, "style") == 2 && db.GetInt(t, r, "variant") == v);
        Assert.Contains(10, KitDefaults.Teams(db));
        KitDefaults.NameOldKits = true;
        List<KitDefaults.RowState> before; List<string> keys;
        try { before = KitDefaults.MakeFanaticsDefault(db, out keys); } finally { KitDefaults.NameOldKits = false; }
        Assert.Equal(1, db.GetInt(t, Row(25), "isdefault"));
        Assert.Equal(1, db.GetInt(t, Row(24), "isdefault"));
        Assert.Equal(0, db.GetInt(t, Row(0), "isdefault"));
        Assert.Equal(0, db.GetInt(t, Row(1), "isdefault"));
        Assert.Equal(before.First(r => r.TeamId == 10 && r.Variant == 0).OrderId, db.GetInt(t, Row(25), "orderid"));
        Assert.Equal(6, db.GetInt(t, Row(0), "locstring"));
        Assert.Contains("VINTAGE_2_10_0", keys);
        // The variant numbers never change.
        Assert.Equal(25, db.GetInt(t, Row(25), "variant"));

        KitDefaults.Restore(db, before);
        Assert.Equal(1, db.GetInt(t, Row(0), "isdefault"));
        Assert.Equal(0, db.GetInt(t, Row(25), "isdefault"));
        Assert.Equal(0, db.GetInt(t, Row(0), "locstring"));
    }

    /// <summary>
    /// Boston-style (defaults on other variants) and Philadelphia-style (two default pairs): every current default is
    /// cleared, the Fanatics kits take the primary pair's order, and undo puts every value back exactly.
    /// </summary>
    [Fact]
    public void Real_current_defaults_are_swapped_not_variants_0_and_1()
    {
        var db = WithFanatics();
        var t = db.Tables["stockteamjerseys"];
        int Row(int v) => Enumerable.Range(0, t.InUse).First(r => db.GetInt(t, r, "teamid") == 10 && db.GetInt(t, r, "style") == 2 && db.GetInt(t, r, "variant") == v);
        var others = Enumerable.Range(0, t.InUse).Where(r => db.GetInt(t, r, "teamid") == 10 && db.GetInt(t, r, "style") == 2
            && db.GetInt(t, r, "variant") is not (0 or 1 or 24 or 25)).Select(r => (int)db.GetInt(t, r, "variant")).Order().Take(2).ToList();
        Assert.Equal(2, others.Count);
        // A second default pair that the game lists first (Philadelphia's 6/7 next to 0/1).
        db.SetInt(t, Row(others[0]), "isdefault", 1); db.SetInt(t, Row(others[0]), "islightcolor", 0); db.SetInt(t, Row(others[0]), "orderid", 0);
        db.SetInt(t, Row(others[1]), "isdefault", 1); db.SetInt(t, Row(others[1]), "islightcolor", 1); db.SetInt(t, Row(others[1]), "orderid", 1);
        db.SetInt(t, Row(0), "isdefault", 1); db.SetInt(t, Row(0), "islightcolor", 0); db.SetInt(t, Row(0), "orderid", 2);
        db.SetInt(t, Row(1), "isdefault", 1); db.SetInt(t, Row(1), "islightcolor", 1); db.SetInt(t, Row(1), "orderid", 3);
        var snapshot = Enumerable.Range(0, t.InUse).Select(r => (db.GetInt(t, r, "isdefault"), db.GetInt(t, r, "orderid"), db.GetInt(t, r, "locstring"))).ToList();

        var before = KitDefaults.MakeFanaticsDefault(db, out _);
        Assert.True(KitDefaults.IsFanaticsDefault(db, t, 10));
        foreach (int v in new[] { 0, 1, others[0], others[1] }) Assert.Equal(0, db.GetInt(t, Row(v), "isdefault"));
        Assert.Equal(0, db.GetInt(t, Row(25), "orderid"));
        Assert.Equal(1, db.GetInt(t, Row(24), "orderid"));
        Assert.All(KitDefaults.Teams(db), team => Assert.InRange(team, 0, 31));
        // Running again is a no-op.
        Assert.DoesNotContain(KitDefaults.MakeFanaticsDefault(db, out _), s => s.TeamId == 10);

        KitDefaults.Restore(db, before);
        var after = Enumerable.Range(0, t.InUse).Select(r => (db.GetInt(t, r, "isdefault"), db.GetInt(t, r, "orderid"), db.GetInt(t, r, "locstring"))).ToList();
        Assert.Equal(snapshot, after);
    }
}
