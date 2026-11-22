using Suite.Core;
using Suite.Rosters;
using Xunit;

namespace Suite.Rosters.Tests;

/// <summary>The editor's whole loop against a roster save created from the stock database: create → find →
/// load → edit a line and a strategy → write back → re-read. Skips without the stock export on the machine.</summary>
public class EditorSaveFlowTests
{
    static readonly string Stock = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes");
    static string Db => Path.Combine(Stock, "database", "t3db", "nhl_game.db");
    static string Meta => Path.Combine(Stock, "database", "t3db", "nhl_game-meta.xml");

    [Fact]
    public void Create_save_from_stock_then_edit_lines_and_strategies_and_write_back()
    {
        if (!File.Exists(Db) || !File.Exists(Meta)) return;
        var dir = Path.Combine(Path.GetTempPath(), "cims-flow-" + Guid.NewGuid().ToString("N"));
        var savedata = Path.Combine(dir, "user", "home", "1000", "savedata", "CUSA37933");
        Directory.CreateDirectory(savedata);
        var donor = Path.Combine(savedata, "Profile0");
        Directory.CreateDirectory(Path.Combine(donor, "sce_sys"));
        File.WriteAllText(Path.Combine(donor, "sce_sys", "param.sfo"), "x");
        File.WriteAllBytes(Path.Combine(donor, "INDEX"), new byte[80]);

        try
        {
            var stock = File.ReadAllBytes(Db);
            var save = SavedRoster.Create(savedata, "RosterCustom0", stock, donor);

            // The game (and the editor's source list) sees it.
            var found = SavedRoster.Find(savedata);
            var me = Assert.Single(found);
            Assert.Equal(stock, me.ReadDb());

            // The editor model opens it: teams, lines, strategies.
            var blob = me.ReadDb();
            var e = TeamEditor.Load(blob, Meta);
            var problemsBefore = e.Verify();   // the stock db ships with some (letters on scratches) — the edit must not add any
            var team = e.Teams().First(t => TeamEditor.CoreSlots.All(s => e.HolderRow(t.Index, s) is not null));
            var a = e.HolderRow(team.Index, "l1lw")!.Value;
            var b = e.HolderRow(team.Index, "l2lw")!.Value;
            e.SetSlot(team.Index, "l1lw", b);
            e.SetSlot(team.Index, "l2lw", a);
            var v = e.StrategyGroups(team.Index).SelectMany(g => g.Fields).First(x => x.Name == "oz_forecheck");
            e.SetStrategy(team.Index, "oz_forecheck", v.Value == 0 ? 1 : 0);
            Assert.Equal(problemsBefore, e.Verify());

            // Write back through the save container and confirm the edits stuck.
            me.WriteDb(e.Save(), Path.Combine(dir, "save-backups"));
            var again = TeamEditor.Load(me.ReadDb(), Meta);
            Assert.Equal(b, again.HolderRow(team.Index, "l1lw"));
            Assert.Equal(a, again.HolderRow(team.Index, "l2lw"));
            Assert.Equal(v.Value == 0 ? 1 : 0, again.StrategyGroups(team.Index).SelectMany(g => g.Fields).First(x => x.Name == "oz_forecheck").Value);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
