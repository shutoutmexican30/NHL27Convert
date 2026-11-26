// Port27 <in-dir with the five .db + nhl_game-meta.xml> <nhl27 csv dir> <out-dir> [--work <dir>]
//
// Every league below the NHL takes its rosters from NHL 27 (AHL, ECHL, CHL, NCAA, the European leagues), written into
// copies of NHL 24's five roster databases inside NHL 24's own structure. The NHL itself is left for the NHL API sync
// that runs afterwards (tools\SyncWrite --reality <out>\reality.json).
//  - Teams: an NHL 27 team maps to the NHL 24 team with the same index and name, else the same name. A team NHL 24
//    does not have (relocated/renamed/new) takes over an unmatched NHL 24 team of the same league: city, name,
//    nickname and abbreviation only (NHL 24's art stays). International, all-star, CHEL and tournament leagues are not
//    ported (their players belong to clubs).
//  - Players: every player on a ported NHL 27 team. Players NHL 24 lacks are created in empty slots with NHL 27's
//    identity/career data and an NHL 24 donor's assets (NewPlayers), in every database.
//  - Rosters: each ported NHL 24 team's rows are replaced by its NHL 27 rows (slot, number, letters, lines, style).
//    Players who were on those NHL 24 teams and are on no ported roster get team -1 (hidden, off the free-agent list,
//    like the ~800 retired players stock NHL 24 hides).
//  - Free agents: NHL 27's free-agent list (players NHL 24 has or that were created), written to reality.json for the
//    sync, which also lists anyone it releases from an NHL roster.
// Layout: same file size, tables, slots and record sizes (T3DbIo.Import + the layout check); the in-use counts move.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Suite.Rosters;

if (args.Length < 3) { Console.WriteLine("usage: Port27 <in-dir> <nhl27-dir> <out-dir> [--work <dir>]"); return 2; }
string inDir = args[0], d27 = args[1], outDir = Directory.CreateDirectory(args[2]).FullName;
int wi = Array.IndexOf(args, "--work");
string work = Directory.CreateDirectory(wi >= 0 ? args[wi + 1] : Path.Combine(outDir, "work")).FullName;
string meta = Path.Combine(inDir, "nhl_game-meta.xml");
string[] dbs = ["nhl_game", "initial_experience", "entry_draft", "expansion_draft", "threes_campaign"];
// Player data is built in nhl_game only and copied into the other four at the end (owner: all five must be the same).
string[] playerDbs = ["nhl_game"];
void Say(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
static string N(string s)
{
    var f = s.Normalize(NormalizationForm.FormD); var sb = new StringBuilder();
    foreach (var ch in f) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
    return Regex.Replace(sb.ToString().Replace("ø", "o").Replace("Ø", "O").ToLowerInvariant(), "[^a-z0-9]", "");
}

// ---- dumps
var before = new Dictionary<string, string>(); var edit = new Dictionary<string, string>();
foreach (var db in dbs)
{
    before[db] = Path.Combine(work, db, "before"); edit[db] = Path.Combine(work, db, "edit");
    T3DbIo.Dump(Path.Combine(inDir, db + ".db"), meta, before[db]);
    T3DbIo.Dump(Path.Combine(inDir, db + ".db"), meta, edit[db]);
}
Say("dumped the five databases");

// ---- leagues and teams
// Club leagues with real rosters. Not ported: 0 NHL (API sync), 16-19/21-23 special/CHEL/all-star, 20 Spengler Cup,
// 25/26 national teams (their players belong to clubs).
var t27 = T3Csv.Load(d27, "exhibitionteams");
var teams27 = t27.Rows.Take(t27.ValidCount).Select(r => (Index: t27.Int(r, "index"), City: t27.Get(r, "cityname"), Name: t27.Get(r, "teamname"),
    Nick: t27.Get(r, "nickname"), Abbr: t27.Get(r, "abbrname"), League: t27.Int(r, "league"))).ToList();
var t24 = T3Csv.Load(edit["nhl_game"], "exhibitionteams");
var rows24 = t24.Rows.Take(t24.ValidCount).ToList();
var teams24 = rows24.Select(r => (Index: t24.Int(r, "index"), City: t24.Get(r, "cityname"), Name: t24.Get(r, "teamname"), League: t24.Int(r, "league"))).ToList();
var ported = new HashSet<int> { 1, 11, 12, 13 }; // AHL, OHL, QMJHL, WHL (owner: the four major leagues under the NHL)
foreach (var g in teams27.GroupBy(t => t.League).OrderBy(g => g.Key))
    Console.WriteLine($"  league {g.Key,2} {(ported.Contains(g.Key) ? "PORT" : "skip")}: {g.Count()} teams, e.g. {string.Join(", ", g.Take(3).Select(t => t.City + " " + t.Name))}");

var map = new Dictionary<int, int>(); // NHL 27 team index -> NHL 24 team index
foreach (var t in teams27.Where(t => ported.Contains(t.League)))
{
    var same = teams24.FirstOrDefault(x => x.Index == t.Index);
    if (same.Name is not null && N(same.Name) == N(t.Name)) { map[t.Index] = same.Index; continue; }
    var byName = teams24.Where(x => N(x.Name) == N(t.Name) && x.League == t.League).ToList();
    if (byName.Count == 1 && !map.ContainsValue(byName[0].Index)) map[t.Index] = byName[0].Index;
}
var renames = new List<(int T27, int T24)>();
foreach (var t in teams27.Where(t => ported.Contains(t.League) && !map.ContainsKey(t.Index)))
{
    // The NHL 24 team of the same league no NHL 27 team claims: same index first (relocations keep the slot).
    var free = teams24.Where(x => x.League == t.League && !map.ContainsValue(x.Index) && !teams27.Any(y => y.League == t.League && N(y.Name) == N(x.Name)))
        .OrderBy(x => x.Index == t.Index ? 0 : 1).ThenBy(x => x.Index).ToList();
    if (free.Count == 0) { Console.WriteLine($"  no NHL 24 slot for {t.City} {t.Name} (league {t.League}); its players stay unported"); continue; }
    map[t.Index] = free[0].Index; renames.Add((t.Index, free[0].Index));
}
Say($"teams: {map.Count} ported ({renames.Count} renamed into an unmatched NHL 24 team of their league)");

// ---- NHL 27 players on ported rosters
var ro27 = T3Csv.Load(d27, "exhibitionrostertable");
var pl27 = T3Csv.Load(d27, "exhibitionplayers");
var bio27 = T3Csv.Load(d27, "exhibitionplayerbiotable");
var fa27 = T3Csv.Load(d27, "exhibitionfreeagents");
var gameOfIdx27 = pl27.Rows.Take(pl27.ValidCount).GroupBy(r => pl27.Get(r, "index")).ToDictionary(g => g.Key, g => pl27.Get(g.First(), "playerid"));
var bioRow27 = bio27.Rows.Take(bio27.ValidCount).GroupBy(r => bio27.Get(r, "game_id")).ToDictionary(g => g.Key, g => g.First());
var portedRows27 = ro27.Rows.Take(ro27.ValidCount).Where(r => map.ContainsKey(ro27.Int(r, "team")) && gameOfIdx27.ContainsKey(ro27.Get(r, "playerindex"))).ToList();
// A player on two ported rosters (loan/affiliate quirks) keeps the first.
var club27 = new Dictionary<string, string[]>();
foreach (var r in portedRows27) club27.TryAdd(gameOfIdx27[ro27.Get(r, "playerindex")], r);
var faGames27 = fa27.Rows.Take(fa27.ValidCount).Select(r => gameOfIdx27.GetValueOrDefault(fa27.Get(r, "playerindex"))).OfType<string>().Where(bioRow27.ContainsKey).ToHashSet();
Say($"NHL 27: {club27.Count} players on ported rosters, {faGames27.Count} free agents");

ApiPlayer Api(string g27)
{
    var b = bioRow27[g27];
    string pos = bio27.Int(b, "position") switch { 0 => "C", 1 => "L", 2 => "R", 3 => "D", 4 => "G", _ => "" };
    return new ApiPlayer(0, bio27.Get(b, "firstname"), bio27.Get(b, "lastname"), bio27.Int(b, "jersey"), pos,
        RosterSync.BioDate(bio27.Int(b, "year"), bio27.Int(b, "month"), bio27.Int(b, "day")), null, "", bio27.Int(b, "handedness") == 1 ? "R" : "L");
}

// ---- match to NHL 24 (main database), create the missing ones in every database
var wanted = club27.Keys.Concat(faGames27).Distinct().ToList();
RosterDb Main() => RosterCsv.Load("nhl_game", edit["nhl_game"]);
var main = Main();
var byLast = main.Players.ToLookup(p => RosterSync.Norm(p.Last)); var byFull = main.Players.ToLookup(p => RosterSync.Norm(p.First + p.Last));
var missing = wanted.Where(g => RosterSync.Match(Api(g), byLast, byFull) is null).Select(g => (G: g, A: Api(g))).ToList();
// --prospects <ApiPlayer json>: organization prospects to have in the game (created when missing; placed below).
int pi = Array.IndexOf(args, "--prospects");
var prospectList = pi < 0 ? new List<ApiPlayer>() : JsonSerializer.Deserialize<List<ApiPlayer>>(File.ReadAllText(args[pi + 1]))!;
foreach (var p in prospectList) if (RosterSync.Match(p, byLast, byFull) is null && !missing.Any(x => RosterSync.Norm(x.A.First + x.A.Last) == RosterSync.Norm(p.First + p.Last) && x.A.Birth == p.Birth)) missing.Add(("api:" + p.Id, p));
var notes = new List<string>();
var plan = NewPlayers.Plan(dbs.Select(d => edit[d]).ToList(), missing.Select(m => m.A), d27, notes);
foreach (var n in notes) Console.WriteLine("  " + n);
foreach (var db in playerDbs)
{
    var warn = new List<string>();
    var changed = NewPlayers.AddTo(edit[db], edit["nhl_game"], meta, plan, d27, warn);
    Say($"{db}: new players added ({string.Join(", ", changed)}){(warn.Count > 0 ? $"; {warn.Count} warnings, e.g. {warn[0]}" : "")}");
}
foreach (var db in playerDbs.Skip(1))
{
    NewPlayers.AlignPlannedWithMain(edit[db], edit["nhl_game"], plan);
}

// ---- Player-record cap. NHL 24 only loads a fixed number of player records (exhibitionplayers): a player past it does
// not exist in game, and Edit Lines crashes on any team that lists one (confirmed 2026-11-23: the one ported team with no
// player past stock's count worked, every other crashed). So no record is added: each created player takes over the
// records of a hidden NHL 24 player nothing uses (team -1, not a free agent, on no roster or custom/league table, no
// record, award, goalie gear or resort row, not a legend) - same game_id, same player index - and that player's career
// stats rows go. The created rows (donor assets + NHL 27 identity, built by NewPlayers.AddTo) are moved onto the host.
var refIdx = new HashSet<string>(); var refGame = new HashSet<string>();
foreach (var db in new[] { "nhl_game" }) // the other four are made copies of nhl_game's player data at the end
{
    // Rows of the ported teams and the free-agent list are rebuilt below, so they do not hold a record; the online roster
    // copy is re-mirrored at the end. Everything else does.
    var rebuilt = map.Values.ToHashSet();
    foreach (var t in new[] { "exhibitionrostertable", "customrostertable", "customfreeagents", "leaguerostertable", "leaguedraftpick" })
        if (File.Exists(T3Csv.PathOf(edit[db], t))) { var x = T3Csv.Load(edit[db], t); foreach (var r in x.Rows.Take(x.ValidCount)) if (!(t == "exhibitionrostertable" && rebuilt.Contains(x.Int(r, "team")))) refIdx.Add(x.Get(r, "playerindex")); }
    foreach (var (t, col) in new[] { ("stockrecordstable", "playerid"), ("stockawardstable", "playerid"),
                 ("customplayerbiotable", "game_id"), ("customplayers", "playerid") })
        if (File.Exists(T3Csv.PathOf(edit[db], t))) { var x = T3Csv.Load(edit[db], t); foreach (var r in x.Rows.Take(x.ValidCount)) refGame.Add(x.Get(r, col)); }
}
var createdIds = plan.Select(p => p.GameId.ToString()).ToHashSet();
// Players NHL 27 keeps (matched before anything was created): never a host.
var stayers = wanted.Select(g => RosterSync.Match(Api(g), byLast, byFull)?.GameId).OfType<string>().ToHashSet();
var inAll = dbs.Select(db => { var b = T3Csv.Load(edit[db], "exhibitionplayerbiotable"); return b.Rows.Take(b.ValidCount).Select(r => b.Get(r, "game_id")).ToHashSet(); })
    .Aggregate((a, s) => { a.IntersectWith(s); return a; });
var mainBio0 = T3Csv.Load(edit["nhl_game"], "exhibitionplayerbiotable"); var mainEp0 = T3Csv.Load(edit["nhl_game"], "exhibitionplayers");
var idxOf0 = mainEp0.Rows.Take(mainEp0.ValidCount).GroupBy(r => mainEp0.Get(r, "playerid")).ToDictionary(g => g.Key, g => g.Select(r => mainEp0.Get(r, "index")).ToList());
var portedGames = club27.Keys.Concat(faGames27).Select(g => Api(g)).ToList();
var hosts = mainBio0.Rows.Take(mainBio0.ValidCount)
    .Where(r => (mainBio0.Int(r, "team") == -1 || map.ContainsValue(mainBio0.Int(r, "team"))) && !stayers.Contains(mainBio0.Get(r, "game_id")) && mainBio0.Int(r, "islegend") == 0 && !createdIds.Contains(mainBio0.Get(r, "game_id"))
        && !refGame.Contains(mainBio0.Get(r, "game_id")) && idxOf0.TryGetValue(mainBio0.Get(r, "game_id"), out var ix) && ix.All(i => !refIdx.Contains(i) && i != "0"))
    .OrderBy(r => mainBio0.Int(r, "year")).Select(r => mainBio0.Get(r, "game_id")).ToList();
if (hosts.Count < plan.Count) { Console.WriteLine($"STOPPED: {plan.Count} players to create, only {hosts.Count} free records"); return 1; }
var hostOf = plan.Select((p, i) => (p, i)).ToDictionary(x => x.p.GameId.ToString(), x => hosts[x.i]);
// The host's career stats, stock goalie gear and resort extras rows belong to the old player and go.
string[] statTables = ["stockcareerseasonplayerstats", "stockcareerplayoffplayerstats", "stockcareerseasongoaliestats", "stockcareerplayoffgoaliestats", "stockcareermiscstats",
   
    "stockgoalieequipment", "exhibitionresortskaterextras", "exhibitionresortgoalieextras", "exhibitionresortplayerextras"];
foreach (var db in playerDbs)
{
    var dir = edit[db];
    var bio = T3Csv.Load(dir, "exhibitionplayerbiotable");
    var rows = bio.Rows.Take(bio.ValidCount).ToList();
    var byId = rows.ToDictionary(r => bio.Get(r, "game_id"));
    foreach (var (pid, hid) in hostOf)
    {
        if (!byId.TryGetValue(pid, out var pr) || !byId.TryGetValue(hid, out var hr)) continue;
        foreach (var c in bio.Header) if (c != "game_id") bio.Set(hr, c, bio.Get(pr, c));
        rows.Remove(pr);
    }
    bio.ReplaceUsed(rows); bio.Save(dir);
    var ep = T3Csv.Load(dir, "exhibitionplayers");
    ep.ReplaceUsed(ep.Rows.Take(ep.ValidCount).Where(r => !hostOf.ContainsKey(ep.Get(r, "playerid"))).ToList()); ep.Save(dir);
    var hostSet = hostOf.Values.ToHashSet();
    foreach (var t in new[] { "exhibitionskaterai", "exhibitionskaterequipment", "exhibitiongoalieai", "exhibitiongoalieequipment" })
    {
        var x = T3Csv.Load(dir, t);
        var keep = x.Rows.Take(x.ValidCount).Where(r => !hostSet.Contains(x.Get(r, "game_id"))).ToList();
        foreach (var r in keep) if (hostOf.TryGetValue(x.Get(r, "game_id"), out var h)) x.Set(r, "game_id", h);
        x.ReplaceUsed(keep.OrderBy(r => x.Int(r, "game_id")).ToList()); x.Save(dir);
    }
    int statsGone = 0;
    foreach (var t in Array.Empty<string>()) // removed raw after the import (see below)
    {
        var x = T3Csv.Load(dir, t);
        var keep = x.Rows.Take(x.ValidCount).Where(r => !hostSet.Contains(x.Get(r, "game_id"))).ToList();
        statsGone += x.ValidCount - keep.Count;
        if (keep.Count != x.ValidCount) { x.ReplaceUsed(keep); x.Save(dir); }
    }
    Say($"{db}: {hostOf.Count} created players moved onto unused retired records; {statsGone} old career-stat rows removed");
}
plan = plan.Select(p => p with { GameId = int.Parse(hostOf[p.GameId.ToString()]) }).ToList();
main = Main();
byLast = main.Players.ToLookup(p => RosterSync.Norm(p.Last)); byFull = main.Players.ToLookup(p => RosterSync.Norm(p.First + p.Last));
var gameOf27to24 = new Dictionary<string, string>();
foreach (var g in wanted) if (RosterSync.Match(Api(g), byLast, byFull) is { } m) gameOf27to24[g] = m.GameId;
Say($"matched {gameOf27to24.Count} of {wanted.Count} NHL 27 players to NHL 24 game ids ({plan.Count} created)");
// Every database must hold every ported player (older snapshots lack many): copy from the main database.
foreach (var db in playerDbs.Skip(1))
    NewPlayers.CopyFromMain(edit[db], edit["nhl_game"], gameOf27to24.Values.ToHashSet(), notes);

// ---- X-Factors and traits of the created players. Their ratings rows are copies of an NHL 24 donor, so they carried the
// donor's X-Factors (xza = zone, xsa1-5 = specialist; 0 = none) and traits. Traits mean different things in NHL 27, so
// they are cleared. X-Factors come from NHL 27 through data\xfactor_map.tsv (closest NHL 24 equivalent, owner rule);
// an ability the map leaves empty is dropped. NHL 24 players that already existed keep their own.
var xmap = new Dictionary<int, int>();
var mapPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "xfactor_map.tsv");
foreach (var line in File.ReadAllLines(mapPath).Skip(1))
{
    var c = line.Split('\t');
    if (c.Length >= 2 && int.TryParse(c[0], out var a) && int.TryParse(c[1], out var b)) xmap[a] = b;
}
var created = plan.Where(p => p.Nhl27GameId is not null).ToDictionary(p => p.GameId.ToString(), p => p.Nhl27GameId!);
int xfSet = 0;
foreach (var db in playerDbs)
    foreach (var table in new[] { "exhibitionskaterai", "exhibitiongoalieai" })
    {
        var t = T3Csv.Load(edit[db], table);
        var src = T3Csv.Load(d27, table);
        var srcRow = src.Rows.Take(src.ValidCount).GroupBy(r => src.Get(r, "game_id")).ToDictionary(g => g.Key, g => g.First());
        bool goalieTable = table.Contains("goalie");
        bool changed = false;
        foreach (var r in t.Rows.Take(t.ValidCount))
        {
            if (!created.TryGetValue(t.Get(r, "game_id"), out var g27)) continue;
            foreach (var col in t.Header.Where(h => h is "xza" or "xsa1" or "xsa2" or "xsa3" or "xsa4" or "xsa5" || h.StartsWith("trait"))) t.Set(r, col, 0);
            changed = true;
            if (!srcRow.TryGetValue(g27, out var s)) continue;
            var abilities = new List<(int Tier, int Id)>();
            for (int k = 1; k <= 3; k++)
            {
                if (!src.Has($"xfactor{k}tier") || src.Int(s, $"xfactor{k}tier") <= 0) continue;
                if (!xmap.TryGetValue(src.Int(s, $"xfactor{k}ability"), out var id24)) continue;
                if ((id24 >= 200) != goalieTable) continue; // goalie abilities are 200+ in NHL 24
                abilities.Add((src.Int(s, $"xfactor{k}tier"), id24));
            }
            var ordered = abilities.OrderByDescending(a => a.Tier).Select(a => a.Id).Distinct().ToList();
            if (ordered.Count == 0) continue;
            t.Set(r, "xza", ordered[0]); // top-tier ability = zone X-Factor, the rest specialists
            for (int k = 1; k < ordered.Count && k <= 5; k++) t.Set(r, $"xsa{k}", ordered[k]);
            if (db == "nhl_game") xfSet++;
        }
        if (changed) t.Save(edit[db]);
    }
Say($"created players: donor traits and X-Factors cleared; {xfSet} given their NHL 27 X-Factors via xfactor_map.tsv");

// ---- rosters, bio teams, free agents, team names: every database on its own rows
var t24Index = teams24.Select(t => t.Index).ToHashSet();
var targetTeams = map.Values.ToHashSet();
var slotCols = ro27.Header.Where(c => c is not ("playerindex" or "team" or "key")).ToList();
var activeUnsigned = new HashSet<string>();
// Roster style ids stock NHL 24 uses per position, from the stock dump (NHL 27's ids can belong to another position).
var stockRo = T3Csv.Load(before["nhl_game"], "exhibitionrostertable"); var stockBio = T3Csv.Load(before["nhl_game"], "exhibitionplayerbiotable"); var stockEp = T3Csv.Load(before["nhl_game"], "exhibitionplayers");
var stockPos = stockBio.Rows.Take(stockBio.ValidCount).GroupBy(r => stockBio.Get(r, "game_id")).ToDictionary(g => g.Key, g => stockBio.Int(g.First(), "position"));
var stockGameOfIdx = stockEp.Rows.Take(stockEp.ValidCount).GroupBy(r => stockEp.Get(r, "index")).ToDictionary(g => g.Key, g => stockEp.Get(g.First(), "playerid"));
var styleUse = stockRo.Rows.Take(stockRo.ValidCount).Select(r => (P: stockPos.GetValueOrDefault(stockGameOfIdx.GetValueOrDefault(stockRo.Get(r, "playerindex"), ""), -1), S: stockRo.Int(r, "playerstyle"))).Where(x => x.P >= 0).ToList();
var stylesFor = styleUse.GroupBy(x => x.P).ToDictionary(g => g.Key, g => g.GroupBy(x => x.S).Where(s => s.Count() >= 3).Select(s => s.Key).ToHashSet());
var styleDefault = styleUse.GroupBy(x => x.P).ToDictionary(g => g.Key, g => g.GroupBy(x => x.S).OrderByDescending(s => s.Count()).First().Key);
foreach (var db in playerDbs)
{
    var dir = edit[db];
    var bio = T3Csv.Load(dir, "exhibitionplayerbiotable");
    var ep = T3Csv.Load(dir, "exhibitionplayers");
    var ro = T3Csv.Load(dir, "exhibitionrostertable");
    var fa = T3Csv.Load(dir, "exhibitionfreeagents");
    var bioByGame = bio.Rows.Take(bio.ValidCount).GroupBy(r => bio.Get(r, "game_id")).ToDictionary(g => g.Key, g => g.First());
    var idxOfGame = ep.Rows.Take(ep.ValidCount).GroupBy(r => ep.Get(r, "playerid")).ToDictionary(g => g.Key, g => g.Select(r => ep.Get(r, "index")).ToList());
    var gameOfIdx = ep.Rows.Take(ep.ValidCount).GroupBy(r => ep.Get(r, "index")).ToDictionary(g => g.Key, g => ep.Get(g.First(), "playerid"));

    var keep = ro.Rows.Take(ro.ValidCount).Where(r => !targetTeams.Contains(ro.Int(r, "team"))).ToList();
    var leaving = ro.Rows.Take(ro.ValidCount).Where(r => targetTeams.Contains(ro.Int(r, "team"))).Select(r => gameOfIdx.GetValueOrDefault(ro.Get(r, "playerindex"), "")).Where(g => g.Length > 0).ToHashSet();
    var onNhlRow = keep.Where(r => ro.Int(r, "team") is >= 0 and < 32).Select(r => ro.Get(r, "playerindex")).ToHashSet();
    var placed = new HashSet<string>();
    var newRows = new List<string[]>();
    int skippedNoPlayer = 0;
    foreach (var (g27, r27) in club27)
    {
        if (!gameOf27to24.TryGetValue(g27, out var g24) || !idxOfGame.TryGetValue(g24, out var idxs) || !bioByGame.TryGetValue(g24, out var b)) { skippedNoPlayer++; continue; }
        // A player already on a club row this database keeps (an NHL roster) stays there; the API sync decides the NHL.
        if (idxs.Any(onNhlRow.Contains)) continue;
        int team27 = ro27.Int(r27, "team"), team24 = map[team27];
        int slot = ro27.Int(r27, "key") - team27 * 40;
        var row = ro.BlankRow();
        foreach (var c in slotCols) if (ro.Has(c)) ro.Set(row, c, ro27.Get(r27, c));
        ro.Set(row, "playerindex", idxs[0]); ro.Set(row, "team", team24); ro.Set(row, "key", team24 * 40 + slot);
        // NHL 27's style id must be one NHL 24 uses for this player's (NHL 24) position.
        int p24 = bio.Int(b, "position");
        if (!stylesFor.GetValueOrDefault(p24, []).Contains(ro.Int(row, "playerstyle")))
            ro.Set(row, "playerstyle", styleDefault.GetValueOrDefault(p24, 9));
        newRows.Add(row);
        placed.Add(g24);
        bio.Set(b, "team", team24);
        var b27 = bioRow27[g27];
        int pro27 = bio27.Int(b27, "proteam");
        bio.Set(b, "proteam", pro27 is >= 0 and < 32 ? pro27 : -1);
        if (int.TryParse(ro27.Get(r27, "jersey"), out var j)) bio.Set(b, "jersey", j);
    }
    // Keys must be unique per team: NHL 27 rows that collide (two players, one slot) move to the team's next free slot.
    var all = keep.Concat(newRows).ToList();
    foreach (var grp in newRows.GroupBy(r => ro.Int(r, "team")))
    {
        var used = all.Where(r => ro.Int(r, "team") == grp.Key).Select(r => ro.Int(r, "key")).GroupBy(k => k).Where(k => k.Count() > 1).Select(k => k.Key).ToHashSet();
        if (used.Count == 0) continue;
        var taken = all.Where(r => ro.Int(r, "team") == grp.Key).Select(r => ro.Int(r, "key")).ToHashSet();
        foreach (var r in grp.Where(r => used.Contains(ro.Int(r, "key"))).Skip(1))
        {
            int k = Enumerable.Range(grp.Key * 40, 40).FirstOrDefault(x => !taken.Contains(x), -1);
            if (k < 0) { all.Remove(r); continue; }
            ro.Set(r, "key", k); taken.Add(k);
        }
    }
    // Everyone who was on a ported team and is not on any ported roster now: hidden.
    int hidden = 0;
    foreach (var g in leaving.Where(g => !placed.Contains(g)))
        if (bioByGame.TryGetValue(g, out var b) && targetTeams.Contains(bio.Int(b, "team"))) { bio.Set(b, "team", -1); bio.Set(b, "proteam", -1); hidden++; }
    ro.ReplaceUsed(all.OrderBy(r => ro.Int(r, "key")).ToList());
    ro.Save(dir);
    bio.Save(dir);

    // Free agents: NHL 27's list, nothing else (the sync adds players it releases from NHL rosters).
    var faRows = new List<string[]>();
    foreach (var g27 in faGames27)
        if (gameOf27to24.TryGetValue(g27, out var g24) && idxOfGame.TryGetValue(g24, out var idxs) && idxs[0] != "0"
            && bioByGame.TryGetValue(g24, out var b) && !placed.Contains(g24))
        {
            var r = fa.BlankRow(); fa.Set(r, "playerindex", idxs[0]); faRows.Add(r);
            if (bio.Int(b, "team") is < 0 or >= 32) { bio.Set(b, "team", -1); bio.Set(b, "proteam", -1); }
            activeUnsigned.Add(g24);
        }
    fa.ReplaceUsed(faRows.DistinctBy(r => fa.Get(r, "playerindex")).OrderBy(r => int.Parse(fa.Get(r, "playerindex"))).ToList());
    fa.Save(dir);
    bio.Save(dir);

    // Team names for the renamed slots (databases with a team table).
    if (File.Exists(T3Csv.PathOf(dir, "exhibitionteams")))
    {
        var tt = T3Csv.Load(dir, "exhibitionteams");
        var f = Fields(meta, "exhibitionteams");
        int renamed = 0;
        foreach (var (i27, i24) in renames)
        {
            var src = teams27.First(t => t.Index == i27);
            var row = tt.Rows.Take(tt.ValidCount).FirstOrDefault(r => tt.Int(r, "index") == i24);
            if (row is null) continue;
            void Put(string col, string v) { if (tt.Has(col) && Encoding.UTF8.GetByteCount(v) < f.GetValueOrDefault(col, 0)) tt.Set(row, col, v); }
            Put("cityname", src.City); Put("teamname", src.Name); Put("nickname", src.Nick); Put("abbrname", src.Abbr);
            renamed++;
        }
        tt.Save(dir);
        Say($"{db}: {renamed} team names updated");
    }
    Say($"{db}: {newRows.Count} roster rows from NHL 27, {hidden} players hidden, {faRows.Count} free agents, {skippedNoPlayer} NHL 27 players not placeable");
}

// ---- Organization prospects NHL 27 does not roster (college, Europe, e.g. Michael Hage): stock NHL 24 never keeps a
// rights-only player (team -1 with a pro team), so each goes on his club's AHL affiliate (not dressed), the only place
// the game shows an organization's prospect. A roster holds at most 40.
if (prospectList.Count > 0)
{
    var dir = edit["nhl_game"];
    var bio = T3Csv.Load(dir, "exhibitionplayerbiotable"); var ep = T3Csv.Load(dir, "exhibitionplayers"); var ro = T3Csv.Load(dir, "exhibitionrostertable");
    var tm = T3Csv.Load(dir, "exhibitionteams");
    var affiliateOf = tm.Rows.Take(tm.ValidCount).Where(r => tm.Int(r, "index") < 32 && tm.Int(r, "ahlaffiliate") >= 0).ToDictionary(r => tm.Int(r, "index"), r => tm.Int(r, "ahlaffiliate"));
    var teamMap = RosterSync.LoadTeamMap(Path.Combine(Directory.GetCurrentDirectory(), "data", "nhlapi_teams.tsv"));
    var mainNow = RosterCsv.Load("nhl_game", dir);
    var bl = mainNow.Players.ToLookup(p => RosterSync.Norm(p.Last)); var bf = mainNow.Players.ToLookup(p => RosterSync.Norm(p.First + p.Last));
    var bioBy = bio.Rows.Take(bio.ValidCount).GroupBy(r => bio.Get(r, "game_id")).ToDictionary(g => g.Key, g => g.First());
    var idxBy = ep.Rows.Take(ep.ValidCount).GroupBy(r => ep.Get(r, "playerid")).ToDictionary(g => g.Key, g => g.Select(r => ep.Get(r, "index")).ToList());
    var rows = ro.Rows.Take(ro.ValidCount).ToList();
    var onRow = rows.Select(r => ro.Get(r, "playerindex")).ToHashSet();
    int placed = 0, full = 0, unknown = 0;
    foreach (var p in prospectList)
    {
        if (!teamMap.TryGetValue(p.TeamAbbrev, out var club) || !affiliateOf.TryGetValue(club, out var ahl)) { unknown++; continue; }
        if (RosterSync.Match(p, bl, bf) is not { } m || !bioBy.TryGetValue(m.GameId, out var b) || !idxBy.TryGetValue(m.GameId, out var idxs)) { unknown++; continue; }
        if (idxs.Any(onRow.Contains)) continue; // already on a roster (NHL 27 placed him)
        var used = rows.Where(r => ro.Int(r, "team") == ahl).Select(r => ro.Int(r, "key") - ahl * 40).ToHashSet();
        int slot = Enumerable.Range(0, 40).FirstOrDefault(s => !used.Contains(s), -1);
        if (slot < 0) { full++; continue; }
        var row = ro.BlankRow();
        ro.Set(row, "playerindex", idxs[0]); ro.Set(row, "team", ahl); ro.Set(row, "key", ahl * 40 + slot);
        ro.Set(row, "jersey", bio.Get(b, "jersey"));
        ro.Set(row, "playerstyle", styleDefault.GetValueOrDefault(bio.Int(b, "position"), 9));
        rows.Add(row); onRow.Add(idxs[0]);
        bio.Set(b, "team", ahl); bio.Set(b, "proteam", club);
        placed++;
    }
    ro.ReplaceUsed(rows.OrderBy(r => ro.Int(r, "key")).ToList()); ro.Save(dir); bio.Save(dir);
    Say($"organization prospects: {placed} placed on their club's AHL team, {full} left out (team full), {unknown} unknown club or not created");
}

// ---- NHL-organization players NHL 27 has off any club roster (college, national junior teams): ask the NHL API where
// they play this season; AHL players go to their club's affiliate in the sync (Trey Augustine: NCAA in NHL 27, AHL now).
var ahlNow = new HashSet<string>();
if (!args.Contains("--no-api"))
{
    var api = new NhlApi(); var day = DateOnly.FromDateTime(DateTime.Today);
    var mb = T3Csv.Load(edit["nhl_game"], "exhibitionplayerbiotable");
    var tm = T3Csv.Load(edit["nhl_game"], "exhibitionteams");
    var clubTeams = tm.Rows.Take(tm.ValidCount).Where(r => tm.Int(r, "index") < 32 && tm.Int(r, "ahlaffiliate") >= 0).Select(r => tm.Int(r, "ahlaffiliate")).Concat(Enumerable.Range(0, 32)).ToHashSet();
    var candidates = mb.Rows.Take(mb.ValidCount).Where(r => mb.Int(r, "proteam") is >= 0 and < 32 && !clubTeams.Contains(mb.Int(r, "team"))
        && 1900 + mb.Int(r, "year") >= DateTime.Today.Year - 26).ToList();
    Say($"asking the NHL API about {candidates.Count} organization players off NHL/AHL rosters");
    int n = 0;
    foreach (var r in candidates)
    {
        string first = mb.Get(r, "firstname"), last = mb.Get(r, "lastname");
        try
        {
            var hits = (await api.SearchAsync($"{first} {last}", day)).Where(h => N(h.First + h.Last) == N(first + last)).ToList();
            if (hits.Count != 1) continue;
            if (await api.LatestLeagueAsync(hits[0].Id, day) == "AHL") ahlNow.Add(mb.Get(r, "game_id"));
        }
        catch (Exception ex) { Console.WriteLine($"  {first} {last}: {ex.Message}"); }
        if (++n % 100 == 0) Say($"  {n}/{candidates.Count} checked, {ahlNow.Count} in the AHL");
    }
    Say($"{ahlNow.Count} of them are playing in the AHL this season");
}

// ---- the other four databases get nhl_game's player data, table for table (initial_experience lacks four women's
// national teams, so their roster rows stay out there).
string[] playerTables = ["exhibitionplayerbiotable", "exhibitionplayers", "exhibitionrostertable", "exhibitionskaterai", "exhibitionskaterequipment",
    "exhibitiongoalieai", "exhibitiongoalieequipment", "exhibitionfreeagents"];
foreach (var db in dbs.Where(d => d != "nhl_game"))
{
    foreach (var t in playerTables)
    {
        var src = T3Csv.Load(edit["nhl_game"], t); var dst = T3Csv.Load(edit[db], t);
        var used = src.Rows.Take(src.ValidCount).Select(r => (string[])r.Clone()).ToList();
        if (t == "exhibitionrostertable" && File.Exists(T3Csv.PathOf(edit[db], "exhibitionteams")))
        {
            var tt = T3Csv.Load(edit[db], "exhibitionteams"); var have = tt.Rows.Take(tt.ValidCount).Select(r => tt.Int(r, "index")).ToHashSet();
            used = used.Where(r => have.Contains(src.Int(r, "team"))).ToList();
        }
        dst.ReplaceUsed(used); dst.Save(edit[db]);
    }
    foreach (var t in Array.Empty<string>()) if (false) foreach (var unused in new[] { "stockcareerseasonplayerstats", "stockcareerplayoffplayerstats", "stockcareerseasongoaliestats", "stockcareerplayoffgoaliestats", // stockcareermiscstats left: stock has an all-zero record mid-table, which the importer cannot rewrite
   
                 "stockgoalieequipment", "exhibitionresortskaterextras", "exhibitionresortgoalieextras", "exhibitionresortplayerextras" })
        if (File.Exists(T3Csv.PathOf(edit[db], t)))
        {
            var x = T3Csv.Load(edit[db], t); var gone = hostOf.Values.ToHashSet();
            var keep = x.Rows.Take(x.ValidCount).Where(r => !gone.Contains(x.Get(r, "game_id"))).ToList();
            if (keep.Count != x.ValidCount) { x.ReplaceUsed(keep); x.Save(edit[db]); }
        }
    Say($"{db}: player data copied from nhl_game");
}
// nhl_game's online roster copy is identical to its roster in stock.
{
    var src = T3Csv.Load(edit["nhl_game"], "exhibitionrostertable"); var dst = T3Csv.Load(edit["nhl_game"], "customonlinerostertable");
    dst.ReplaceUsed(src.Rows.Take(src.ValidCount).Select(r => dst.Header.Select(h => src.Get(r, h)).ToArray()).ToList()); dst.Save(edit["nhl_game"]);
}
// ---- import, check (only tables that changed; an untouched table is left exactly as stock)
foreach (var db in dbs)
    foreach (var csv in Directory.GetFiles(edit[db], "*.csv"))
        if (File.ReadAllBytes(csv).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(before[db], Path.GetFileName(csv))))) File.Delete(csv);
foreach (var db in dbs)
{
    var outDb = Path.Combine(outDir, db + ".db");
    var cells = T3DbIo.Import(Path.Combine(inDir, db + ".db"), meta, before[db], edit[db], outDb);
    var a = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(T3DbIo.Layout(Path.Combine(inDir, db + ".db"), meta))!;
    var b = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(T3DbIo.Layout(outDb, meta))!;
    bool same = new FileInfo(outDb).Length == new FileInfo(Path.Combine(inDir, db + ".db")).Length && a.Count == b.Count
        && a.Zip(b).All(p => p.First["table"].GetString() == p.Second["table"].GetString() && p.First["records"].GetInt32() == p.Second["records"].GetInt32()
            && p.First["recordSize"].GetInt32() == p.Second["recordSize"].GetInt32() && p.First["headerCrcAt"].GetInt64() == p.Second["headerCrcAt"].GetInt64());
    var repair = T3DbIo.RepairInUse(File.ReadAllBytes(outDb), meta);
    Say($"{db}: written ({cells.Values.Sum():n0} cells in {cells.Count(c => c.Value > 0)} tables); layout {(same ? "identical" : "DIFFERENT")}; in-use repairs needed: {repair.Count}");
    if (!same || repair.Count > 0) { Console.WriteLine("STOPPED: layout check failed"); return 1; }
}
// ---- the hosts' old rows (career stats, stock goalie gear, resort extras) go, raw: some stock tables hold an all-zero
// record mid-table, which the CSV importer refuses to rewrite. DeleteRecord moves the tail; the freed slot is zeroed.
foreach (var db in dbs)
{
    var path = Path.Combine(outDir, db + ".db");
    var d = T3Db.Load(File.ReadAllBytes(path), meta);
    var gone = hostOf.Values.Select(long.Parse).ToHashSet();
    int removed = 0;
    foreach (var tn in statTables)
    {
        if (!d.Tables.TryGetValue(tn, out var t)) continue;
        for (int r = t.InUse - 1; r >= 0; r--)
            if (gone.Contains(d.GetInt(t, r, "game_id")))
            {
                t = d.DeleteRecord(t, r);
                Array.Clear(d.Bytes, t.RecordsAt + t.InUse * t.RecordBytes, t.RecordBytes);
                removed++;
            }
    }
    var bytes = d.Save();
    T3Db.Load((byte[])bytes.Clone(), meta);
    File.WriteAllBytes(path, bytes);
    Say($"{db}: {removed} old rows of the reused records removed (career stats, goalie gear, resort extras)");
}
File.Copy(meta, Path.Combine(outDir, "nhl_game-meta.xml"), true);
File.WriteAllText(Path.Combine(outDir, "reality.json"), JsonSerializer.Serialize(new { ActiveUnsignedGameIds = activeUnsigned.OrderBy(x => x).ToList(), PortedLeagues = true, AhlNowGameIds = ahlNow.OrderBy(x => x).ToList(), PortedLeagueIds = ported.Order().ToList() }));
Say($"done: {outDir}");
return 0;

static Dictionary<string, int> Fields(string metaXml, string table) =>
    System.Xml.Linq.XDocument.Load(metaXml).Descendants("table").Where(t => (string?)t.Attribute("name") == table)
        .SelectMany(t => t.Descendants("field")).ToDictionary(f => (string)f.Attribute("name")!, f => int.TryParse((string?)f.Attribute("depth"), out var d) ? d / 8 : 0);
