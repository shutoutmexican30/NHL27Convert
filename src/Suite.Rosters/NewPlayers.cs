using System.Xml.Linq;

namespace Suite.Rosters;

/// <summary>One NHL player NHL 24 does not have, and how he will be created.</summary>
public sealed record NewPlayer(string Name, string Team, ApiPlayer Api, int GameId, int PlayerIndex, string DonorGameId, string DonorName,
    string? Nhl27GameId, bool Goalie);

/// <summary>
/// Creates the NHL players NHL 24 lacks (Yurov, Demidov, Michkov...) in the empty record slots every player table already
/// has (bio 13,300 slots with ~9,100 in use, and so on): no row of another player, template or retired player is reused,
/// and the files keep their size, tables, record sizes and layout; only the in-use counts grow, as the roster sync's
/// roster rows already do. Each new player gets one row in exhibitionplayerbiotable, exhibitionplayers, the skater or
/// goalie ratings table and the matching equipment table, inserted in key order (game_id / index), with the same values
/// in every database.
/// - Identity and career fields come from NHL 27's row (matched by name and birth date), each checked against NHL 24's
///   range; without an NHL 27 row, name / birth date / position / shot / number come from the NHL API.
/// - Everything that names an asset (head, hair, eyebrows, skin tone, equipment, intro song, class, mascot...) comes from
///   a donor: a real NHL 24 player with a generic head (headid >= 60000) and the same position group, skin tone and hair
///   colour, closest in age. So every asset id is one NHL 24 already uses. Commentary/voice ids are 0 and there is no
///   portrait until the portrait step gives one (artid 0, hasportrait 0).
/// - game_id: NHL 27's when no database uses it, else the next free id from 32000; player index: the next free index.
/// Nothing else refers to the new rows (no stats, contracts, draft, X-Factors), so nothing can dangle.
/// </summary>
public static class NewPlayers
{
    const string Bio = "exhibitionplayerbiotable", Players = "exhibitionplayers";

    /// <summary>Bio columns never taken from NHL 27: ids of assets / links, and fields the sync sets itself.</summary>
    static readonly HashSet<string> FromDonor = new(StringComparer.OrdinalIgnoreCase)
    {
        "headid", "audioid", "surnameid", "casualgearfk", "friendenemy0", "headtexture", "playercomp", "artid", "game_id",
        "haircolorcode", "eyebrowcolorcode", "skincolor", "eyecolorcode", "hairid", "eyebrowid", "casualgear", "ismascot",
        "mascotbodyid", "mascotheadid", "introsongid", "playerclassid", "friendenemycompat0", "hasportrait", "team", "proteam",
        "draftteam", "juniorteam", "juniorleague", "gender", "islegend", "playoffbeardtype", "playoffbeardcolor",
        "personalitytype", "personalitysubtype", "state",
    };

    static Dictionary<string, (int Low, int High, bool Text, int Bytes)> Fields(string metaXml, string table)
    {
        var map = new Dictionary<string, (int, int, bool, int)>();
        foreach (var t in XDocument.Load(metaXml).Descendants("table").Where(t => (string?)t.Attribute("name") == table))
            foreach (var f in t.Descendants("field"))
            {
                var name = (string)f.Attribute("name")!;
                bool text = (string?)f.Attribute("type") == "FIELDTYPE_STRING";
                int.TryParse((string?)f.Attribute("rangelow"), out var lo);
                int.TryParse((string?)f.Attribute("rangehigh"), out var hi);
                int.TryParse((string?)f.Attribute("depth"), out var depth);
                map[name] = (lo, hi, text, depth / 8);
            }
        return map;
    }

    /// <summary>
    /// Plans every missing player from the main database's dump (donors) and every database's ids.
    /// <paramref name="missing"/>: API players no database row matches.
    /// </summary>
    public static List<NewPlayer> Plan(IReadOnlyList<string> dumps, IEnumerable<ApiPlayer> missing, string? nhl27Dir, List<string> notes)
    {
        var usedIds = new HashSet<int>(); var usedIndex = new HashSet<int>();
        var whoHas = new Dictionary<int, string>(); // id -> "name|birth" of its holder in some database
        foreach (var d in dumps)
        {
            var b = T3Csv.Load(d, Bio);
            foreach (var r in b.Rows.Take(b.ValidCount))
            {
                usedIds.Add(b.Int(r, "game_id"));
                whoHas.TryAdd(b.Int(r, "game_id"), RosterSync.Norm(b.Get(r, "firstname") + b.Get(r, "lastname")) + "|" + b.Get(r, "year") + "/" + b.Get(r, "month") + "/" + b.Get(r, "day"));
            }
            var e = T3Csv.Load(d, Players); foreach (var r in e.Rows.Take(e.ValidCount)) usedIndex.Add(e.Int(r, "index"));
        }
        var main = T3Csv.Load(dumps[0], Bio);
        var mainRows = main.Rows.Take(main.ValidCount).ToList();
        var inAll = dumps.Skip(1).Select(d => { var b = T3Csv.Load(d, Bio); return b.Rows.Take(b.ValidCount).Select(r => b.Get(r, "game_id")).ToHashSet(); })
            .Aggregate(mainRows.Select(r => main.Get(r, "game_id")).ToHashSet(), (a, s) => { a.IntersectWith(s); return a; });
        // Donors: rostered-age players with a generic head, present in every database (their asset ids exist everywhere).
        var donors = mainRows.Where(r => main.Int(r, "headid") >= 60000 && main.Int(r, "team") is >= 0 and < 32 && inAll.Contains(main.Get(r, "game_id"))).ToList();

        T3Csv? b27 = null; ILookup<string, DbPlayer>? byLast = null, byFull = null;
        if (nhl27Dir is not null && File.Exists(T3Csv.PathOf(nhl27Dir, Bio)))
        {
            b27 = T3Csv.Load(nhl27Dir, Bio);
            var p27 = b27.Rows.Take(b27.ValidCount).Select((r, i) => new DbPlayer(i, b27.Get(r, "game_id"), b27.Get(r, "firstname"), b27.Get(r, "lastname"),
                RosterSync.BioDate(b27.Int(r, "year"), b27.Int(r, "month"), b27.Int(r, "day")), 0, 0, b27.Int(r, "position"), false, 0)).ToList();
            byLast = p27.ToLookup(p => RosterSync.Norm(p.Last)); byFull = p27.ToLookup(p => RosterSync.Norm(p.First + p.Last));
        }

        int nextId = 32000, nextIndex = (usedIndex.Count == 0 ? 0 : usedIndex.Max()) + 1;
        var plan = new List<NewPlayer>();
        var donorUse = new Dictionary<string[], int>();
        foreach (var p in missing.OrderBy(p => p.TeamAbbrev).ThenBy(p => p.Last))
        {
            string[]? r27 = null; string? id27 = null;
            if (b27 is not null && RosterSync.Match(p, byLast!, byFull!) is { } m) { r27 = b27.Rows[m.Row]; id27 = m.GameId; }
            bool goalie = p.Position == "G";
            int skin = r27 is null ? -1 : b27!.Int(r27, "skincolor"), hair = r27 is null ? -1 : b27!.Int(r27, "haircolorcode");
            int year = p.Birth is { } bd ? bd.Year - 1900 : 100;
            var donor = donors.Where(d => (main.Int(d, "position") == 4) == goalie)
                .OrderBy(d => skin < 0 || main.Int(d, "skincolor") == skin ? 0 : 1)
                .ThenBy(d => hair < 0 || main.Int(d, "haircolorcode") == hair ? 0 : 1)
                .ThenBy(d => Math.Abs(main.Int(d, "year") - year) / 3) // within ~3 years counts as equally close
                .ThenBy(d => donorUse.GetValueOrDefault(d))                 // then spread faces over different donors
                .ThenBy(d => Math.Abs(main.Int(d, "year") - year))
                .ThenBy(d => main.Int(d, "game_id")).FirstOrDefault();
            if (donor is null) { notes.Add($"No donor for {p.First} {p.Last}; skipped"); continue; }
            donorUse[donor] = donorUse.GetValueOrDefault(donor) + 1;
            // NHL 27's id when it is free, or when an older database already holds this same player under it.
            var self = r27 is null ? null : RosterSync.Norm(b27!.Get(r27, "firstname") + b27.Get(r27, "lastname")) + "|" + b27.Get(r27, "year") + "/" + b27.Get(r27, "month") + "/" + b27.Get(r27, "day");
            int id = id27 is not null && int.TryParse(id27, out var x) && x < 65535 && (!usedIds.Contains(x) || whoHas.GetValueOrDefault(x) == self) ? x : 0;
            if (id == 0) { while (usedIds.Contains(nextId)) nextId++; id = nextId; }
            usedIds.Add(id);
            while (usedIndex.Contains(nextIndex)) nextIndex++;
            usedIndex.Add(nextIndex);
            plan.Add(new NewPlayer($"{p.First} {p.Last}", p.TeamAbbrev, p, id, nextIndex, main.Get(donor, "game_id"),
                $"{main.Get(donor, "firstname")} {main.Get(donor, "lastname")}", id27, goalie));
        }
        notes.Add($"New players: {plan.Count} planned ({plan.Count(p => p.Nhl27GameId is not null)} from NHL 27 data, {plan.Count(p => p.Nhl27GameId is null)} from the NHL API only)");
        return plan;
    }

    /// <summary>
    /// Adds the planned players to one database dump (its CSVs, in place) and returns the tables changed. Rows already
    /// there (same game_id) are left alone, so running twice changes nothing. The main database dump supplies the donors.
    /// </summary>
    public static List<string> AddTo(string dumpDir, string mainDump, string metaXml, IReadOnlyList<NewPlayer> plan, string? nhl27Dir, List<string> warnings)
    {
        var changed = new List<string>();
        if (plan.Count == 0) return changed;
        var bio = T3Csv.Load(dumpDir, Bio);
        var have = bio.Rows.Take(bio.ValidCount).Select(r => bio.Int(r, "game_id")).ToHashSet();
        var todo = plan.Where(p => !have.Contains(p.GameId)).ToList();
        if (todo.Count == 0) return changed;

        var mainBio = T3Csv.Load(mainDump, Bio);
        var donorBio = mainBio.Rows.Take(mainBio.ValidCount).GroupBy(r => mainBio.Get(r, "game_id")).ToDictionary(g => g.Key, g => g.First());
        var f = Fields(metaXml, Bio);
        T3Csv? b27 = nhl27Dir is not null && File.Exists(T3Csv.PathOf(nhl27Dir, Bio)) ? T3Csv.Load(nhl27Dir, Bio) : null;
        var r27ById = b27?.Rows.Take(b27.ValidCount).GroupBy(r => b27.Get(r, "game_id")).ToDictionary(g => g.Key, g => g.First());

        string Fit(string col, string value, string fallback)
        {
            if (!f.TryGetValue(col, out var d)) return fallback;
            if (d.Text) return System.Text.Encoding.UTF8.GetByteCount(value) < Math.Max(1, d.Bytes) ? value : fallback;
            return int.TryParse(value, out var v) && v >= d.Low && v <= d.High ? value : fallback;
        }

        var bioRows = bio.Rows.Take(bio.ValidCount).ToList();
        var accepted = new List<NewPlayer>();
        foreach (var p in todo)
        {
            var row = (string[])donorBio[p.DonorGameId].Clone();
            var donorRow = donorBio[p.DonorGameId];
            if (p.Nhl27GameId is not null && r27ById!.TryGetValue(p.Nhl27GameId, out var r27))
                foreach (var col in bio.Header.Where(c => !FromDonor.Contains(c) && b27!.Has(c)))
                    bio.Set(row, col, Fit(col, b27!.Get(r27, col), bio.Get(row, col)));
            else
            {
                bio.Set(row, "firstname", Fit("firstname", p.Api.First, ""));
                bio.Set(row, "lastname", Fit("lastname", p.Api.Last, ""));
                if (p.Api.Birth is { } b)
                {
                    bio.Set(row, "year", Fit("year", (b.Year - 1900).ToString(), bio.Get(row, "year")));
                    bio.Set(row, "month", Fit("month", (b.Month - 1).ToString(), bio.Get(row, "month")));
                    bio.Set(row, "day", Fit("day", (b.Day - 1).ToString(), bio.Get(row, "day")));
                }
                int pos = RosterSync.PositionCode(p.Api.Position);
                if (pos >= 0)
                {
                    bio.Set(row, "position", pos);
                    bio.Set(row, "positionprimary", pos switch { 3 => p.Api.Shoots == "R" ? 4 : 3, 4 => 5, _ => pos });
                }
                if (p.Api.Shoots is "L" or "R") bio.Set(row, "handedness", p.Api.Shoots == "R" ? 1 : 0);
                if (p.Api.Number is { } n) bio.Set(row, "jersey", Fit("jersey", n.ToString(), bio.Get(row, "jersey")));
                bio.Set(row, "nickname", "");
            }
            bio.Set(row, "game_id", p.GameId);
            bio.Set(row, "team", -1); bio.Set(row, "proteam", -1);
            bio.Set(row, "artid", 0); bio.Set(row, "hasportrait", 0);
            bio.Set(row, "audioid", 0); bio.Set(row, "surnameid", 0);
            if (string.IsNullOrEmpty(bio.Get(row, "firstname")) || string.IsNullOrEmpty(bio.Get(row, "lastname")))
            { warnings.Add($"{p.Name}: name does not fit; skipped"); continue; }
            bioRows.Add(row);
            accepted.Add(p);
        }
        // A rejected bio must not leave orphan player-index, ratings or equipment records behind.
        todo = accepted;
        if (todo.Count == 0) return changed;
        Insert(bio, bioRows, "game_id", dumpDir); changed.Add(Bio);

        // Player index row.
        var ep = T3Csv.Load(dumpDir, Players);
        var epRows = ep.Rows.Take(ep.ValidCount).ToList();
        foreach (var p in todo)
        {
            var r = ep.BlankRow();
            ep.Set(r, "index", p.PlayerIndex); ep.Set(r, "playerid", p.GameId); ep.Set(r, "table", 0);
            epRows.Add(r);
        }
        Insert(ep, epRows, "index", dumpDir); changed.Add(Players);

        // Ratings and equipment: the donor's rows (ratings are then replaced by NHL 27's in the sync).
        foreach (var table in new[] { "exhibitionskaterai", "exhibitionskaterequipment", "exhibitiongoalieai", "exhibitiongoalieequipment" })
        {
            if (!File.Exists(T3Csv.PathOf(dumpDir, table))) continue;
            var t = T3Csv.Load(dumpDir, table);
            var mt = T3Csv.Load(mainDump, table);
            var donors = mt.Rows.Take(mt.ValidCount).GroupBy(r => mt.Get(r, "game_id")).ToDictionary(g => g.Key, g => g.First());
            var rows = t.Rows.Take(t.ValidCount).ToList();
            bool goalieTable = table.Contains("goalie");
            int added = 0;
            foreach (var p in todo.Where(p => p.Goalie == goalieTable))
            {
                if (!donors.TryGetValue(p.DonorGameId, out var d)) { warnings.Add($"{p.Name}: donor has no {table} row"); continue; }
                var r = (string[])d.Clone();
                t.Set(r, "game_id", p.GameId);
                // The donor's X-Factors (xza, xsa1-5) and traits are the donor's own, not this player's: none by default
                // (tools\Port27 maps NHL 27 X-Factors through data\xfactor_map.tsv).
                foreach (var col in t.Header.Where(h => h is "xza" or "xsa1" or "xsa2" or "xsa3" or "xsa4" or "xsa5" || h.StartsWith("trait", StringComparison.Ordinal)))
                    t.Set(r, col, 0);
                rows.Add(r); added++;
            }
            if (added == 0) continue;
            Insert(t, rows, "game_id", dumpDir); changed.Add(table);
        }
        return changed;
    }

    /// <summary>Older snapshots may already hold a planned player's ID. Keep their links, but use the main database's
    /// authoritative bio, ratings and equipment instead of skipping that player's older appearance.</summary>
    public static List<string> AlignPlannedWithMain(string dumpDir, string mainDump, IReadOnlyList<NewPlayer> plan)
    {
        var changed = new List<string>();
        if (plan.Count == 0 || Path.GetFullPath(dumpDir).Equals(Path.GetFullPath(mainDump), StringComparison.OrdinalIgnoreCase)) return changed;
        foreach (var name in new[] { Bio, "exhibitionskaterai", "exhibitionskaterequipment", "exhibitiongoalieai", "exhibitiongoalieequipment" })
        {
            var source = T3Csv.Load(mainDump, name);
            var target = T3Csv.Load(dumpDir, name);
            var from = source.Rows.Take(source.ValidCount).GroupBy(r => source.Get(r, "game_id")).ToDictionary(g => g.Key, g => g.First());
            var to = target.Rows.Take(target.ValidCount).GroupBy(r => target.Get(r, "game_id")).ToDictionary(g => g.Key, g => g.First());
            bool any = false;
            foreach (var p in plan.Where(p => name == Bio || p.Goalie == name.Contains("goalie")))
            {
                var id = p.GameId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!from.TryGetValue(id, out var src) || !to.TryGetValue(id, out var dst))
                    throw new InvalidDataException($"{p.Name}: missing {name} record during cross-database alignment.");
                foreach (var col in target.Header)
                {
                    if (!source.Has(col)) continue;
                    var value = source.Get(src, col);
                    if (target.Get(dst, col) == value) continue;
                    target.Set(dst, col, value); any = true;
                }
            }
            if (any) { target.Save(dumpDir); changed.Add(name); }
        }
        return changed;
    }

    /// <summary>
    /// The older roster databases lack some players the main database has (rookies added after their snapshot). Those
    /// an NHL roster needs are copied from the main database row for row (bio, player index, ratings, equipment), so every
    /// database has the same players. The player index stays the main database's when this database does not use it.
    /// </summary>
    public static List<string> CopyFromMain(string dumpDir, string mainDump, IReadOnlyCollection<string> gameIds, List<string> notes)
    {
        var changed = new List<string>();
        var bio = T3Csv.Load(dumpDir, Bio);
        var have = bio.Rows.Take(bio.ValidCount).Select(r => bio.Get(r, "game_id")).ToHashSet();
        var todo = gameIds.Where(g => !have.Contains(g)).ToHashSet();
        if (todo.Count == 0) return changed;
        var mainBio = T3Csv.Load(mainDump, Bio);
        var rows = bio.Rows.Take(bio.ValidCount).ToList();
        rows.AddRange(mainBio.Rows.Take(mainBio.ValidCount).Where(r => todo.Contains(mainBio.Get(r, "game_id"))).Select(r => (string[])r.Clone()));
        Insert(bio, rows, "game_id", dumpDir); changed.Add(Bio);

        var ep = T3Csv.Load(dumpDir, Players);
        var mainEp = T3Csv.Load(mainDump, Players);
        var epRows = ep.Rows.Take(ep.ValidCount).ToList();
        var usedIndex = epRows.Select(r => ep.Int(r, "index")).ToHashSet();
        int next = usedIndex.Max() + 1;
        foreach (var r in mainEp.Rows.Take(mainEp.ValidCount).Where(r => todo.Contains(mainEp.Get(r, "playerid"))))
        {
            var copy = (string[])r.Clone();
            if (!usedIndex.Add(ep.Int(copy, "index"))) { while (!usedIndex.Add(next)) next++; ep.Set(copy, "index", next); }
            epRows.Add(copy);
        }
        Insert(ep, epRows, "index", dumpDir); changed.Add(Players);

        foreach (var table in new[] { "exhibitionskaterai", "exhibitionskaterequipment", "exhibitiongoalieai", "exhibitiongoalieequipment" })
        {
            if (!File.Exists(T3Csv.PathOf(dumpDir, table)) || !File.Exists(T3Csv.PathOf(mainDump, table))) continue;
            var t = T3Csv.Load(dumpDir, table); var mt = T3Csv.Load(mainDump, table);
            var tr = t.Rows.Take(t.ValidCount).ToList();
            var add = mt.Rows.Take(mt.ValidCount).Where(r => todo.Contains(mt.Get(r, "game_id"))).Select(r => (string[])r.Clone()).ToList();
            if (add.Count == 0) continue;
            tr.AddRange(add);
            Insert(t, tr, "game_id", dumpDir); changed.Add(table);
        }
        notes.Add($"{Path.GetFileName(Path.GetDirectoryName(dumpDir))}: {todo.Count} players copied from the main database");
        return changed;
    }

    /// <summary>Puts the in-use rows back in key order at the front and saves the dump (slot count unchanged).</summary>
    static void Insert(T3Csv t, List<string[]> rows, string key, string dir)
    {
        // Stock tables can hold a duplicate key of their own; only the new rows must not collide with anything.
        var stock = t.Rows.Take(t.ValidCount).ToHashSet();
        var oldKeys = stock.Select(r => t.Int(r, key)).ToHashSet();
        var added = rows.Where(r => !stock.Contains(r)).Select(r => t.Int(r, key)).ToList();
        if (added.Distinct().Count() != added.Count || added.Any(oldKeys.Contains)) throw new InvalidOperationException($"{t.Name}: a new {key} is already used.");
        int slots = t.Rows.Count;
        t.ReplaceUsed(rows.OrderBy(r => t.Int(r, key)).ToList()); // stable: stock rows keep their relative order
        if (t.Rows.Count != slots) throw new InvalidOperationException($"{t.Name}: slot count changed.");
        t.Save(dir);
    }
}
