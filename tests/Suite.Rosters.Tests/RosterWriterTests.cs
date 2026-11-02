using System.Text;
using Suite.Rosters;

namespace Suite.Rosters.Tests;

public class RosterWriterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cims-roster-tests", Guid.NewGuid().ToString("N"));
    string Dump => Path.Combine(_dir, "before");
    string Out => Path.Combine(_dir, "edit");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static readonly string[] Slots = ["l1c", "l1lw", "l1rw", "l1ld", "l1rd", "g1", "pp_1c"];

    void Table(string name, string[] header, IEnumerable<string[]> rows, int slots)
    {
        var sb = new StringBuilder(string.Join(",", header) + "\r\n");
        var list = rows.ToList();
        foreach (var r in list) sb.Append(string.Join(",", r)).Append("\r\n");
        for (int i = list.Count; i < slots; i++) sb.Append(string.Join(",", header.Select(_ => "0"))).Append("\r\n");
        Directory.CreateDirectory(Dump);
        File.WriteAllText(Path.Combine(Dump, name + ".csv"), sb.ToString(), new UTF8Encoding(true));
    }

    // Players: game_id = 100 + n, player index = n. Team 0 AAA (AHL affiliate 2 CCC), team 1 BBB.
    static readonly (string Name, int Team, int Pos, int Primary, string Slot)[] People =
    [
        ("a", 0, 0, 0, "l1c"), ("b", 0, 1, 1, "l1lw"), ("c", 0, 2, 2, "l1rw"), ("d", 0, 3, 3, "l1ld"), ("e", 0, 3, 4, "l1rd"), ("f", 0, 4, 5, "g1"), ("g", 0, 0, 0, ""),
        ("h", 1, 0, 0, "l1c"), ("i", 1, 1, 1, "l1lw"), ("j", 1, 2, 2, "l1rw"), ("k", 1, 3, 3, "l1ld"), ("l", 1, 3, 4, "l1rd"), ("m", 1, 4, 5, "g1"),
    ];

    void Build()
    {
        Table("exhibitionplayerbiotable",
            ["game_id", "firstname", "lastname", "year", "month", "day", "team", "proteam", "jersey", "position", "positionprimary", "handedness", "hasportrait", "headid"],
            People.Select((p, n) => new[] { (100 + n).ToString(), "P", "Player" + p.Name, "100", "0", n.ToString(), p.Team.ToString(), p.Team.ToString(), (10 + n).ToString(), p.Pos.ToString(), p.Primary.ToString(), "0", "0", "1" }), 20);
        Table("exhibitionplayers", ["index", "playerid", "table"], People.Select((p, n) => new[] { (n + 1).ToString(), (100 + n).ToString(), "0" }), 20);
        Table("exhibitionrostertable", ["playerindex", "key", "team", "rosterstatus", "captain", "playerstyle", "jersey", .. Slots],
            People.Select((p, n) => new[]
            {
                (n + 1).ToString(), (p.Team * 40 + n % 7).ToString(), p.Team.ToString(), p.Slot.Length > 0 ? "1" : "0",
                p.Name is "a" ? "1" : "0", "3", (10 + n).ToString(),
            }.Concat(Slots.Select(s => s == p.Slot || (s == "pp_1c" && p.Name is "a" or "h") ? "1" : "0")).ToArray()), 30);
        Table("exhibitionfreeagents", ["playerindex"], [], 5);
        Table("exhibitionteams", ["index", "abbrname", "ahlaffiliate", "league"],
            [["0", "AAA", "2", "0"], ["1", "BBB", "-1", "0"], ["2", "CCC", "-1", "1"]], 5);
    }

    static ApiPlayer Api(string name, int n, int? number = null) =>
        new(1000 + n, "P", "Player" + name, number ?? 10 + n, n switch { 0 or 6 or 7 => "C", 1 or 8 => "L", 2 or 9 => "R", 5 or 12 => "G", _ => "D" },
            new DateOnly(2000, 1, n + 1), null, "");

    static readonly Dictionary<string, int> TeamMap = new(StringComparer.OrdinalIgnoreCase) { ["AAA"] = 0, ["BBB"] = 1 };

    // AAA loses a (its captain) and gets h from BBB; BBB is left without a centre.
    Dictionary<string, List<ApiPlayer>> Rosters() => new()
    {
        ["AAA"] = [Api("b", 1), Api("c", 2), Api("d", 3), Api("e", 4), Api("f", 5), Api("g", 6), Api("h", 7, 99)],
        ["BBB"] = [Api("i", 8), Api("j", 9), Api("k", 10), Api("l", 11), Api("m", 12)],
    };

    [Fact]
    public void Moves_players_keeps_slot_counts_and_refills_lines()
    {
        Build();
        var report = RosterWriter.Write("test", Dump, Out, Rosters(), [], TeamMap);

        var ro = T3Csv.Load(Out, "exhibitionrostertable");
        var bio = T3Csv.Load(Out, "exhibitionplayerbiotable");
        var fa = T3Csv.Load(Out, "exhibitionfreeagents");
        Assert.Equal(30, ro.Rows.Count);
        Assert.Equal(20, bio.Rows.Count);
        Assert.Equal(5, fa.Rows.Count);

        var used = ro.Rows.Take(ro.ValidCount).ToList();
        string[] RowOf(int index) => used.Single(r => ro.Get(r, "playerindex") == index.ToString());
        // h (index 8) now on AAA, with the API number, in a's line and power-play slots.
        var h = RowOf(8);
        Assert.Equal("0", ro.Get(h, "team"));
        Assert.Equal("99", ro.Get(h, "jersey"));
        Assert.Equal("1", ro.Get(h, "l1c"));
        Assert.Equal("1", ro.Get(h, "pp_1c"));
        Assert.Equal("1", ro.Get(h, "rosterstatus"));
        // Keys stay inside each team's block, unique and in order.
        var keys = used.Select(r => ro.Int(r, "key")).ToList();
        Assert.Equal(keys.OrderBy(k => k), keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(used, r => Assert.InRange(ro.Int(r, "key") - ro.Int(r, "team") * 40, 0, 39));
        // Every line slot is still held by exactly one player on each team.
        foreach (var team in new[] { "0", "1" })
            foreach (var s in Slots.Where(s => s != "pp_1c" || team == "0"))
                Assert.Equal(1, used.Count(r => ro.Get(r, "team") == team && ro.Get(r, s) == "1"));
        // AAA keeps a captain: its top skater who stayed.
        Assert.Single(used, r => ro.Get(r, "team") == "0" && ro.Get(r, "captain") == "1");
        // a was released; BBB, with nobody left to play centre, signed him back from free agency.
        var a = RowOf(1);
        Assert.Equal("1", ro.Get(a, "team"));
        Assert.Contains(report.Changes, c => c.StartsWith("Release\tP Playera"));
        Assert.Contains(report.Changes, c => c.StartsWith("Signed\tP Playera"));
        Assert.Equal(0, fa.ValidCount);
        Assert.Equal("1", bio.Get(bio.Rows[0], "team"));
        Assert.Equal("0", bio.Get(bio.Rows[7], "team"));
        Assert.Equal("99", bio.Get(bio.Rows[7], "jersey"));
    }

    [Fact]
    public void Released_prospect_goes_to_the_ahl_affiliate()
    {
        Build();
        var rosters = Rosters();
        rosters["AAA"].RemoveAll(p => p.Last == "Playerg");
        var prospects = new Dictionary<string, List<ApiPlayer>> { ["AAA"] = [Api("g", 6)] };
        RosterWriter.Write("test", Dump, Out, rosters, prospects, TeamMap);
        var bio = T3Csv.Load(Out, "exhibitionplayerbiotable");
        var ro = T3Csv.Load(Out, "exhibitionrostertable");
        Assert.Equal("2", bio.Get(bio.Rows[6], "team"));
        Assert.Equal("0", bio.Get(bio.Rows[6], "proteam"));
        Assert.Single(ro.Rows.Take(ro.ValidCount), r => ro.Get(r, "playerindex") == "7" && ro.Get(r, "team") == "2");
    }

    [Fact]
    public void Csv_round_trips_quotes_commas_and_blank_slots()
    {
        Directory.CreateDirectory(Dump);
        File.WriteAllText(Path.Combine(Dump, "t.csv"), "﻿a,b\r\n\"x, \"\"y\"\"\",1\r\n0,0\r\n", new UTF8Encoding(false));
        var t = T3Csv.Load(Dump, "t");
        Assert.Equal("x, \"y\"", t.Rows[0][0]);
        Assert.Equal(1, t.ValidCount);
        t.Save(Out);
        Assert.Equal(File.ReadAllText(Path.Combine(Dump, "t.csv")), File.ReadAllText(Path.Combine(Out, "t.csv")));
        Assert.Throws<InvalidOperationException>(() => t.ReplaceUsed([["1", "1"], ["2", "2"], ["3", "3"]]));
    }

    [Fact]
    public void Guard_allows_only_the_crc_shift_in_use_records_explain()
    {
        Directory.CreateDirectory(_dir);
        var bytes = new byte[64];
        BitConverter.GetBytes(1).CopyTo(bytes, 16); // one table → 36 header bytes
        string a = Path.Combine(_dir, "a.db"), b = Path.Combine(_dir, "b.db");
        File.WriteAllBytes(a, bytes);
        bytes[40] = 7; // past the header: content may change
        File.WriteAllBytes(b, bytes);
        const string before = """[{"table":"t","headerCrcAt":40,"recordsCrcAt":100,"records":10,"recordSize":20,"strings":0}]""";
        const string after = """[{"table":"t","headerCrcAt":40,"recordsCrcAt":60,"records":10,"recordSize":20,"strings":0}]""";
        Assert.True(RosterDbJob.Guard(a, b, before, after, new Dictionary<string, int> { ["t"] = 5 }, new Dictionary<string, int> { ["t"] = 3 }).Ok);
        Assert.False(RosterDbJob.Guard(a, b, before, after, new Dictionary<string, int> { ["t"] = 5 }, new Dictionary<string, int> { ["t"] = 5 }).Ok);
        Assert.False(RosterDbJob.Guard(a, b, before, before.Replace("\"records\":10", "\"records\":11"), new Dictionary<string, int>(), new Dictionary<string, int>()).Ok);
        bytes[20] = 1; // inside the header
        File.WriteAllBytes(b, bytes);
        Assert.False(RosterDbJob.Guard(a, b, before, before, new Dictionary<string, int>(), new Dictionary<string, int>()).Ok);
    }
}
