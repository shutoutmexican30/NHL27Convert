using System.Xml.Linq;

namespace Suite.Rosters;

/// <summary>
/// One set of player ratings for every roster database: NHL 27's where NHL 27 has the player, otherwise the main NHL 24
/// database's, so the same player has the same ratings in every mode. Only rating attributes both games have (c_* and
/// skill_* columns of exhibitionskaterai / exhibitiongoalieai) are copied, each checked against NHL 24's own range
/// (nhl_game-meta.xml); a value outside it keeps the NHL 24 value. Traits, growth and X-Factors are left alone (their
/// meaning can differ between the games). Players are matched by EA's game_id when name and birth year agree, else by
/// name and birth date.
/// The NHL 27 tables come from a dump of NHL 27's database (exhibitionplayerbiotable, exhibitionskaterai,
/// exhibitiongoalieai as CSV) in %LOCALAPPDATA%\CenterIceModdingSuite\nhl27db.
/// </summary>
public sealed class Ratings
{
    public sealed record Entry(string Table, Dictionary<string, int> Values, bool FromNhl27);
    public Dictionary<string, Entry> ByGameId { get; } = new(StringComparer.Ordinal);
    public int FromNhl27 => ByGameId.Values.Count(e => e.FromNhl27);
    public int FromMain => ByGameId.Values.Count(e => !e.FromNhl27);

    public static string Nhl27Dir => Path.Combine(Suite.Core.SuitePaths.Local, "nhl27db");
    static readonly string[] Tables = ["exhibitionskaterai", "exhibitiongoalieai"];

    static bool IsRating(string col) => col.StartsWith("c_") || col.StartsWith("skill_");

    /// <summary>Allowed range per (table, column) from the NHL 24 schema.</summary>
    static Dictionary<(string, string), (int Low, int High)> Ranges(string metaXml)
    {
        var map = new Dictionary<(string, string), (int, int)>();
        var doc = XDocument.Load(metaXml);
        foreach (var t in doc.Descendants("table").Where(t => Tables.Contains((string?)t.Attribute("name"))))
            foreach (var f in t.Descendants("field"))
                if (int.TryParse((string?)f.Attribute("rangelow"), out var lo) && int.TryParse((string?)f.Attribute("rangehigh"), out var hi))
                    map[((string)t.Attribute("name")!, (string)f.Attribute("name")!)] = (lo, hi);
        return map;
    }

    public static Ratings Build(string? nhl27Dir, string mainDump, string metaXml, List<string> notes)
    {
        var r = new Ratings();
        var ranges = Ranges(metaXml);
        var bio24 = T3Csv.Load(mainDump, "exhibitionplayerbiotable");
        var main = Tables.ToDictionary(t => t, t => T3Csv.Load(mainDump, t));
        var mainRow = Tables.ToDictionary(t => t, t => main[t].Rows.Take(main[t].ValidCount).GroupBy(x => main[t].Get(x, "game_id")).ToDictionary(g => g.Key, g => g.First()));

        // NHL 27 side (optional).
        Dictionary<string, T3Csv>? n27 = null;
        Dictionary<string, Dictionary<string, string[]>>? row27 = null;
        Dictionary<string, string[]>? bioById27 = null;
        ILookup<string, DbPlayer>? byLast = null, byFull = null;
        T3Csv? bio27 = null;
        if (nhl27Dir is not null && Tables.Append("exhibitionplayerbiotable").All(t => File.Exists(T3Csv.PathOf(nhl27Dir, t))))
        {
            n27 = Tables.ToDictionary(t => t, t => T3Csv.Load(nhl27Dir, t));
            row27 = Tables.ToDictionary(t => t, t => n27[t].Rows.Take(n27[t].ValidCount).GroupBy(x => n27[t].Get(x, "game_id")).ToDictionary(g => g.Key, g => g.First()));
            bio27 = T3Csv.Load(nhl27Dir, "exhibitionplayerbiotable");
            var rows = bio27.Rows.Take(bio27.ValidCount).ToList();
            bioById27 = rows.GroupBy(x => bio27.Get(x, "game_id")).ToDictionary(g => g.Key, g => g.First());
            var players = rows.Select((x, i) => new DbPlayer(i, bio27.Get(x, "game_id"), bio27.Get(x, "firstname"), bio27.Get(x, "lastname"),
                RosterSync.BioDate(bio27.Int(x, "year"), bio27.Int(x, "month"), bio27.Int(x, "day")), 0, 0, bio27.Int(x, "position"), false, 0)).ToList();
            byLast = players.ToLookup(p => RosterSync.Norm(p.Last));
            byFull = players.ToLookup(p => RosterSync.Norm(p.First + p.Last));
        }
        else notes.Add("No NHL 27 database dump in " + (nhl27Dir ?? "(none)") + "; every database gets the main database's ratings.");

        int outOfRange = 0;
        foreach (var b in bio24.Rows.Take(bio24.ValidCount))
        {
            var id = bio24.Get(b, "game_id");
            var table = bio24.Int(b, "position") == 4 ? "exhibitiongoalieai" : "exhibitionskaterai";
            if (!mainRow[table].TryGetValue(id, out var own)) continue;
            var t24 = main[table];
            var values = t24.Header.Where(IsRating).ToDictionary(c => c, c => t24.Int(own, c));
            bool from27 = false;
            if (n27 is not null)
            {
                string? id27 = null;
                if (bioById27!.TryGetValue(id, out var same) && RosterSync.Norm(bio27!.Get(same, "lastname")) == RosterSync.Norm(bio24.Get(b, "lastname"))
                    && bio27.Get(same, "year") == bio24.Get(b, "year"))
                    id27 = id;
                else
                {
                    var probe = new ApiPlayer(0, bio24.Get(b, "firstname"), bio24.Get(b, "lastname"), null, "",
                        RosterSync.BioDate(bio24.Int(b, "year"), bio24.Int(b, "month"), bio24.Int(b, "day")), null, "");
                    if (probe.Birth is not null && RosterSync.Match(probe, byLast!, byFull!) is { Birth: not null } m27 && m27.Birth == probe.Birth) id27 = m27.GameId;
                }
                if (id27 is not null && row27![table].TryGetValue(id27, out var src))
                {
                    var t27 = n27[table];
                    foreach (var c in values.Keys.ToList())
                    {
                        if (!t27.Has(c) || !int.TryParse(t27.Get(src, c), out var v)) continue;
                        if (ranges.TryGetValue((table, c), out var rg) && (v < rg.Low || v > rg.High)) { outOfRange++; continue; }
                        values[c] = v;
                    }
                    from27 = true;
                }
            }
            r.ByGameId[id] = new Entry(table, values, from27);
        }
        if (outOfRange > 0) notes.Add($"{outOfRange} NHL 27 rating values were outside NHL 24's range and kept the NHL 24 value.");
        notes.Add($"Ratings: {r.FromNhl27} players from NHL 27, {r.FromMain} from the main NHL 24 database.");
        return r;
    }

    /// <summary>Writes these ratings into one database's rating tables (rows it has). Returns the number of rows changed.</summary>
    public int ApplyTo(string dumpDir, string outDir, WriteReport report)
    {
        int changed = 0;
        foreach (var table in Tables)
        {
            if (!File.Exists(T3Csv.PathOf(dumpDir, table))) continue;
            var t = T3Csv.Load(dumpDir, table);
            int before = changed;
            foreach (var row in t.Rows.Take(t.ValidCount))
            {
                if (!ByGameId.TryGetValue(t.Get(row, "game_id"), out var e) || e.Table != table) continue;
                bool any = false;
                foreach (var (c, v) in e.Values)
                    if (t.Has(c) && t.Int(row, c) != v) { t.Set(row, c, v); any = true; }
                if (any) changed++;
            }
            if (changed > before) { t.Save(outDir); report.Tables.Add(table); }
        }
        return changed;
    }
}
