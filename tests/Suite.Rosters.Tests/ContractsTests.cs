using Suite.Core;
using Suite.Rosters;
using Xunit;

namespace Suite.Rosters.Tests;

public class ContractsTests
{
    static readonly string Stock = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes");
    static string Meta => Path.Combine(Stock, "database", "t3db", "nhl_game-meta.xml");
    static string DbPath(string db) => db == "nhl_game" ? Path.Combine(Stock, "database", "t3db", "nhl_game.db") : Path.Combine(Stock, "roster", db + ".db");

    [Fact]
    public void Clauses_parse_and_format_round_trip()
    {
        var list = Clause.ParseList("1:m-ntc/10; 0:NMC;2:NMC+M-NTC/15;3:NTC");
        Assert.Equal("0:NMC;1:M-NTC/10;2:NMC+M-NTC/15;3:NTC", Clause.FormatList(list));
        Assert.Equal(ClauseKind.ModifiedNtc, list[1].Kind);
        Assert.Equal(10, list[1].ListSize);
        Assert.Empty(Clause.ParseList(""));
        Assert.Throws<FormatException>(() => Clause.ParseList("0:XYZ"));
        Assert.Throws<FormatException>(() => Clause.ParseList("0:NMC;0:NTC"));
        Assert.Throws<FormatException>(() => Clause.ParseList("12:NMC"));
    }

    [Fact]
    public void Csv_round_trips_every_field()
    {
        var c = new Contract
        {
            GameId = 4321, First = "Test", Last = "O'Player, Jr", Team = 11, Signed = true, CapHit = 12_500_000, Years = 2, TwoWay = false, MultiYear = true,
            ExtCapHit = 13_250_000, ExtYears = 8, ExtTwoWay = false, YearUfa = 2028, RetainedTrades = 1,
            Clauses = Clause.ParseList("0:NMC;1:M-NTC/10"), Source = "hand", Note = "say \"hi\"",
        };
        var back = ContractStore.FromCsv(ContractStore.ToCsv([c])).Single();
        Assert.Equal(ContractStore.ToCsv([c]), ContractStore.ToCsv([back]));
        Assert.Equal("O'Player, Jr", back.Last);
        Assert.Equal(2028, back.YearUfa);
        Assert.Throws<FormatException>(() => ContractStore.FromCsv("game_id,clauses\n5,0:BAD"));
    }

    [Fact]
    public void Mapping_uses_5000_dollar_steps_and_shifts_ufa_years_to_the_game_season()
    {
        var map = new ContractMapping(GameSeason: 2023, DataSeason: 2026);
        var f = map.Fields(new Contract { Signed = true, CapHit = 11_640_000, Years = 1, YearUfa = 2027, ExtCapHit = 13_250_000, ExtYears = 4 });
        Assert.Equal(2328, f["contractdollars"]);
        Assert.Equal(2650, f["extensiondollars"]);
        Assert.Equal(0, f["contractstatus"]);
        Assert.Equal(2027 - 3 - 1900, f["yearufa"]);
        Assert.False(f.ContainsKey("contractismultiyear"));
        var none = map.Fields(new Contract { Signed = false, CapHit = 5_000_000, Years = 3 });
        Assert.Equal(-1, none["contractstatus"]);
        Assert.Equal(0, none["contractdollars"]);
        Assert.Equal(16383, ContractMapping.Units(200_000_000));
    }

    [Fact]
    public void Team_cap_lists_parse_and_bound()
    {
        var v = TeamCap.ParseList("2600000, 1500000,,5", int.MaxValue, "Dead cap");
        Assert.Equal(TeamCap.Seasons, v.Length);
        Assert.Equal("2600000,1500000,0,5", TeamCap.FormatList(v));
        Assert.Throws<FormatException>(() => TeamCap.ParseList("8", 7, "Retained contracts"));
        Assert.Throws<FormatException>(() => TeamCap.ParseList("-1", int.MaxValue, "Dead cap"));
    }

    /// <summary>NHL 27 team dead cap / retained salary on temp copies of the five stock databases, read back.</summary>
    [Fact]
    public void Nhl27_team_cap_applies_to_all_five_databases_and_reads_back()
    {
        var csv = Nhl27Contracts.FindTable(TeamCap.Table);
        if (!File.Exists(Meta) || csv is null) return;
        var dbs = RosterSyncRun.Databases.ToDictionary(d => d, d => File.ReadAllBytes(DbPath(d)));
        var teams = TeamCap.LoadNhl27(csv).Where(t => t.Team is >= 0 and < 32).ToList();
        var plans = ContractRun.Plan(dbs, [], new ContractMapping(), Meta, teams);
        Assert.NotEmpty(plans);
        foreach (var p in plans)
        {
            var db = T3Db.Load((byte[])p.Bytes.Clone(), Meta);
            var now = TeamCap.Read(db, p.Db).ToDictionary(t => t.Team);
            foreach (var t in teams)
            {
                Assert.Equal(t.DeadCap, now[t.Team].DeadCap);
                Assert.Equal(t.Retained, now[t.Team].Retained);
                Assert.Equal(t.RetainedCount, now[t.Team].RetainedCount);
            }
            Assert.All(p.Changes, c => Assert.True(c.Field.StartsWith("cbaexpense") || c.Field.StartsWith("retainedtrade") || c.Field.StartsWith("numretained")));
        }
    }

    /// <summary>NHL 27 prefill applied to temp copies of the five stock databases, read back (skipped without the data).</summary>
    [Fact]
    public void Nhl27_prefill_applies_to_all_five_databases_and_reads_back()
    {
        if (!File.Exists(Meta) || !File.Exists(Nhl27Contracts.DefaultCsv)) return;
        var dbs = RosterSyncRun.Databases.ToDictionary(d => d, d => File.ReadAllBytes(DbPath(d)));
        var main = T3Db.Load((byte[])dbs["nhl_game"].Clone(), Meta);
        var pre = Nhl27Contracts.Prefill(Nhl27Contracts.Load(Nhl27Contracts.DefaultCsv), main);
        Assert.True(pre.Matched > 1000, $"only {pre.Matched} matched");
        var map = new ContractMapping();
        var plans = ContractRun.Plan(dbs, pre.Contracts, map, Meta);
        Assert.Equal(5, plans.Count);
        var mcdavid = pre.Contracts.Single(c => c.Name == "Connor McDavid");
        var twoWay = pre.Contracts.First(c => c.TwoWay && c.CapHit < 1_000_000);
        foreach (var p in plans)
        {
            Assert.Equal(dbs[p.Db].Length, p.Bytes.Length);
            var db = T3Db.Load((byte[])p.Bytes.Clone(), Meta);
            var now = ContractDb.Read(db, map, p.Db).ToDictionary(c => c.GameId);
            var m = now[mcdavid.GameId];
            Assert.Equal(12_500_000, m.CapHit);
            Assert.Equal(mcdavid.Years, m.Years);
            Assert.Equal(mcdavid.YearUfa, m.YearUfa);
            if (now.TryGetValue(twoWay.GameId, out var t)) { Assert.True(t.TwoWay); Assert.Equal(ContractMapping.Units(twoWay.CapHit) * 5000, t.CapHit); }
            // Only contract cells changed: a second plan over the result has nothing to do.
            Assert.Empty(ContractDb.Diff(p.Db, db, pre.Contracts, map));
        }
    }
}
