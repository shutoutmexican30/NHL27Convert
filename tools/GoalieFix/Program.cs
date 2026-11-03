// GoalieFix --target <game copy> --nhl27 <NHL 27 CSV dump dir> [--apply [--restore-check]] | --show <game_id>
//           | --restore <backup folder> --apply
// Without --apply it only plans and writes the review (never the game).  Needs tools\GearMap's id tables first.
// In all five roster databases, in place (T3Db; layout identical):
//  0. every goalie-equipment row goes back to NHL 24's own values (the pristine stock DB in dbwork\stock, rows matched
//     by game_id + league + occurrence), which undoes every earlier write: renamed models, palettes copied across models,
//     a moved team field.
//  1. then every goalie on an NHL roster (live bio team 0-31) gets, on his personal row and his NHL club row:
//     - models (pads, blocker, trapper, stick): the brand and model NHL 27 gives him.  Both games use the same id for a
//       shared model, but the DB value is looked up in each game's goalie_equipment_mapping / goalie_stick_mapping, so
//       matching goes by model name.  An NHL 27-only model becomes the newest NHL 24 model of that brand line (Bauer
//       Supreme Fuse -> Supreme Mach), else of that brand; his own NHL 24 model is kept when it is already that line.
//     - colours, per item: a palette is laid out for one model's recolour zones, so it is only ever taken from a row
//       wearing that same model: his NHL 27 design (same model, same team), his own NHL 24 design (same model, same
//       team), a teammate's NHL 24 design on that model, or another team's NHL 24 design on that model recoloured into
//       his team's palette.
//     - mask: his own NHL 24 mask when he has not moved; else NHL 27's when NHL 24 uses it, else his team's home mask.
// Writes a review (every NHL goalie: models with where they came from, colour source, mask). Backup, read-back, and with
// --restore-check a restore to the backup verified byte for byte, then the write again. Refuses the original game.
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

void Say(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
string Arg(string name) => args[Array.IndexOf(args, name) + 1];
var settings = SuiteSettings.Load();
var target = Path.GetFullPath(Arg("--target")).TrimEnd(Path.DirectorySeparatorChar);
foreach (var original in new[] { settings.Nhl24GameDir, settings.Nhl24BaseDir, settings.Nhl24UpdateDir, settings.Nhl24ReadView })
    if (!string.IsNullOrWhiteSpace(original) && string.Equals(Path.GetFullPath(original).TrimEnd(Path.DirectorySeparatorChar), target, StringComparison.OrdinalIgnoreCase))
        { Say("REFUSED: the original game."); return 1; }
if (args.Contains("--restore"))
{
    // Undo a write from a suite backup (TOCs copied back, appended CAS data truncated), verified before and after.
    if (!WriteGate.Allowed(args)) { Say("REFUSED: --restore needs --target and --apply."); return 1; }
    var restoreFrom = Arg("--restore");
    if (!string.Equals(Path.GetFullPath(ApplyTarget.LoadSnapshot(restoreFrom).Game).TrimEnd(Path.DirectorySeparatorChar), target, StringComparison.OrdinalIgnoreCase))
        { Say("REFUSED: that backup belongs to a different game."); return 1; }
    if (ApplyTarget.RunningGame() is { } busy) { Say("REFUSED: " + busy + " is running"); return 1; }
    ApplyTarget.RestoreFromBackup(target, restoreFrom);
    var diffs = ApplyTarget.Compare(target, restoreFrom);
    Say($"restored {target} from {restoreFrom}: {(diffs.Count == 0 ? "identical to the backup" : "DIFFERENCES: " + string.Join("; ", diffs))}");
    return diffs.Count == 0 ? 0 : 1;
}
var nhl27 = Arg("--nhl27");
var meta = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes", "database", "t3db", "nhl_game-meta.xml");
var names = RosterSyncRun.Databases.ToDictionary(d => d, RosterSyncRun.LegacyName);
var review = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "goaliefix")), "review.txt");

// ---- NHL 27 source (CSV dump) ----
static List<Dictionary<string, string>> Csv(string path)
{
    var lines = File.ReadAllLines(path);
    // Quoted fields; these tables hold numbers and names without commas.
    static string[] Split(string l) => l.Split(',').Select(v => v.Trim('"')).ToArray();
    var head = Split(lines[0].TrimStart('﻿'));
    return lines.Skip(1).Where(l => l.Length > 0).Select(l => { var v = Split(l); return head.Select((h, i) => (h, v.ElementAtOrDefault(i) ?? "")).ToDictionary(x => x.h, x => x.Item2); }).ToList();
}
var row27 = new Dictionary<long, Dictionary<string, string>>();
foreach (var r in Csv(Path.Combine(nhl27, "exhibitiongoalieequipment.csv"))) row27.TryAdd(long.Parse(r["game_id"]), r);
var nameOf = new Dictionary<long, string>();
var team27 = new Dictionary<long, long>();
foreach (var p in Csv(Path.Combine(nhl27, "exhibitionplayerbiotable.csv")))
{
    long gid = long.Parse(p["game_id"]);
    nameOf.TryAdd(gid, p["firstname"] + " " + p["lastname"]);
    if (long.TryParse(p["team"], out var t) && t is >= 0 and < 32) team27.TryAdd(gid, t);
}
var rows27 = row27.Where(kv => team27.ContainsKey(kv.Key)).Select(kv => (Team: team27[kv.Key], R: kv.Value)).ToList();
// Team numbering is the same in both games for the 32 clubs (Dallas 9, Detroit 10, Utah 22).
var abbr = new Dictionary<long, string>();
foreach (var t in Csv(Path.Combine(nhl27, "exhibitionteams.csv")))
    if (int.TryParse(t["index"], out var i) && i is >= 0 and < 32 && t["artabbr"].Length > 0) abbr.TryAdd(i, t["artabbr"]);

// ---- id -> model name, both games (tools\GearMap) ----
Dictionary<long, string> Models(string file)
{
    var path = Path.Combine(SuitePaths.Roaming, "data", file);
    if (!File.Exists(path)) throw new FileNotFoundException(@"Run tools\GearMap first: no " + path);
    return File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => l.Split('\t')).ToDictionary(p => long.Parse(p[0]), p => p[1]);
}
var equip24 = Models("goalie_equipment_nhl24.tsv"); var equip27 = Models("goalie_equipment_nhl27.tsv");
var stick24 = Models("goalie_stick_nhl24.tsv"); var stick27 = Models("goalie_stick_nhl27.tsv");

// ---- the game being edited (live roster) ----
var lf = new LegacyFiles(target, settings);
var raw = names.ToDictionary(kv => kv.Key, kv => lf.Read(kv.Value));
var main = T3Db.Load((byte[])raw["nhl_game"].Clone(), meta);
var bio = main.Tables["exhibitionplayerbiotable"];
var teamOf = new Dictionary<long, long>();
for (int r = 0; r < bio.InUse; r++)
{
    if (main.GetInt(bio, r, "position") != 4) continue;
    var team = main.GetInt(bio, r, "team");
    if (team is >= 0 and < 32) teamOf[main.GetInt(bio, r, "game_id")] = team;
}
string ModelName(string group, long id) => (group == "stick" ? stick24 : equip24).GetValueOrDefault(id, "?" + id);
if (args.Contains("--show"))
{
    // Read only: one goalie's rows in this game.
    var gidShow = long.Parse(Arg("--show"));
    foreach (var tn in new[] { "exhibitiongoalieequipment", "stockgoalieequipment" })
    {
        var t = main.Tables[tn];
        for (int r = 0; r < t.InUse; r++)
            if (main.GetInt(t, r, "game_id") == gidShow)
                Console.WriteLine(tn + (t.Fields.ContainsKey("team") ? $" team {main.GetInt(t, r, "team")} league {main.GetInt(t, r, "league")}" : "") +
                    $" mask {main.GetInt(t, r, "masktype")}/{main.GetInt(t, r, "maskid")} pads {ModelName("pads", main.GetInt(t, r, "pads"))} stick {ModelName("stick", main.GetInt(t, r, "stick"))}: " +
                    string.Join(' ', t.Fields.Keys.Where(f => f.EndsWith("color")).OrderBy(f => f).Select(f => $"{f.Replace("color", "").Replace("zone", "")}={main.GetInt(t, r, f):X6}")));
    }
    Console.WriteLine(teamOf.TryGetValue(gidShow, out var liveTeam) ? $"live roster team {liveTeam} {abbr.GetValueOrDefault(liveTeam)}" : "live roster: not a goalie on an NHL team 0-31");
    return 0;
}

// ---- NHL 24's own data (pristine stock DBs) ----
T3Db? Pristine(string db)
{
    var path = Path.Combine(SuitePaths.Local, "dbwork", "stock", names[db]);
    return File.Exists(path) ? T3Db.Load(File.ReadAllBytes(path), meta) : null;
}
var p24 = Pristine("nhl_game") ?? throw new FileNotFoundException("No pristine nhl_game.db in dbwork\\stock.");
var pEx = p24.Tables["exhibitiongoalieequipment"]; var pSt = p24.Tables["stockgoalieequipment"];
string[] groups = ["pads", "blocker", "trapper", "stick"];
static string Group(string col) => col.StartsWith("pads") ? "pads" : col.StartsWith("blocker") ? "blocker" : col.StartsWith("trapper") ? "trapper" : "stick";
var colourCols = pSt.Fields.Keys.Where(f => f.EndsWith("color", StringComparison.Ordinal)).ToList();
var groupCols = groups.ToDictionary(g => g, g => colourCols.Where(c => Group(c) == g).ToList());
string[] gearFields = [.. groups, .. colourCols, "masktype", "maskid"];
// Reset to NHL 24's own: everything GoalieFix or the roster sync ever wrote on a goalie row.
string[] resetFields = [.. gearFields, "team", "showcustomcolors", "showcustomstick"];
Dictionary<string, long> Values(T3Db d, T3Db.Table t, int r) => gearFields.Where(t.Fields.ContainsKey).ToDictionary(f => f, f => d.GetInt(t, r, f));

var pBio = p24.Tables["exhibitionplayerbiotable"];
var team24 = new Dictionary<long, long>();
for (int r = 0; r < pBio.InUse; r++) team24.TryAdd(p24.GetInt(pBio, r, "game_id"), p24.GetInt(pBio, r, "team"));
// NHL club rows (league 0, team 0-31): the designs EA authored, per model and team.
var clubRows = new List<(long Gid, long Team, Dictionary<string, long> V)>();
for (int r = 0; r < pSt.InUse; r++)
    if (p24.GetInt(pSt, r, "league") == 0 && p24.GetInt(pSt, r, "team") is var t and >= 0 and < 32)
        clubRows.Add((p24.GetInt(pSt, r, "game_id"), t, Values(p24, pSt, r)));
// A goalie's own NHL 24 design: his personal row, else his first club row.
var own24 = new Dictionary<long, Dictionary<string, long>>();
for (int r = 0; r < pEx.InUse; r++) own24.TryAdd(p24.GetInt(pEx, r, "game_id"), Values(p24, pEx, r));
foreach (var c in clubRows) own24.TryAdd(c.Gid, c.V);
var validMasks = clubRows.Select(c => (c.V["masktype"], c.V["maskid"])).ToHashSet();
var homeMask = clubRows.GroupBy(c => c.Team).ToDictionary(g => g.Key,
    g => g.GroupBy(c => (c.V["masktype"], c.V["maskid"])).OrderByDescending(m => m.Count()).First().Key);

static double Dist(long a, long b)
{
    double dr = ((a >> 16) & 255) - ((b >> 16) & 255), dg = ((a >> 8) & 255) - ((b >> 8) & 255), db = (a & 255) - (b & 255);
    return Math.Sqrt(dr * dr + dg * dg + db * db);
}
static int Chroma(long c) { int r = (int)(c >> 16) & 255, g = (int)(c >> 8) & 255, b = (int)c & 255; return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)); }
// Team palette: the coloured (non white/grey/black) zone colours of its club rows, most used first.
var palette = clubRows.GroupBy(c => c.Team).ToDictionary(g => g.Key, g =>
{
    var counts = new List<(long C, int N)>();
    foreach (var v in g.SelectMany(c => colourCols.Select(col => c.V[col])).Where(v => Chroma(v) >= 40))
    {
        int i = counts.FindIndex(x => Dist(x.C, v) < 40);
        if (i < 0) counts.Add((v, 1)); else counts[i] = (counts[i].C, counts[i].N + 1);
    }
    return counts.OrderByDescending(x => x.N).Select(x => x.C).ToList();
});
long Recolour(long c, long from, long to)
{
    var a = palette.GetValueOrDefault(from, []); var b = palette.GetValueOrDefault(to, []);
    int i = a.FindIndex(x => Dist(x, c) < 40);
    return i < 0 || b.Count == 0 ? c : b[Math.Min(i, b.Count - 1)];
}

// NHL 27's model -> the NHL 24 model to wear: the same model, else the newest of that brand line, else of that brand.
var worn = clubRows.SelectMany(c => groups.Select(g => (g == "stick" ? stick24 : equip24).GetValueOrDefault(c.V[g])))
    .Concat(rows27.SelectMany(x => groups.Select(g => long.TryParse(x.R[g], out var i) ? (g == "stick" ? stick27 : equip27).GetValueOrDefault(i) : null)))
    .OfType<string>().ToHashSet();
(long Id, string Why) Pick(string name27, long? current, Dictionary<long, string> table)
{
    foreach (var (id, n) in table) if (n == name27) return (id, "same model");
    // The line ignores a generation number: optik2_original, optik_3 and optik_4 are all Brian's Optik.
    static string Line(string[] p) => (p.ElementAtOrDefault(1) ?? "").TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
    var parts = name27.Split('_');
    string brand = parts[0], line = Line(parts);
    // A stand-in must be a model some NHL 24 or NHL 27 row wears, so there is a design laid out for its zones.
    var real = table.Where(kv => !kv.Value.StartsWith("casual") && !kv.Value.StartsWith("nhl_") && !kv.Value.StartsWith("special") && kv.Value is not "common" and not "classic"
        && worn.Contains(kv.Value)).ToList();
    var cur = current is { } c ? table.GetValueOrDefault(c, "").Split('_') : [];
    if (cur.Length > 1 && cur[0] == brand && Line(cur) == line) return (current!.Value, "his NHL 24 model, same line");
    // Same line only among current models: an old Vaughn Velocity is not a stand-in for a Velocity V11.
    var sameLine = real.Where(kv => kv.Value.Split('_') is var p && p[0] == brand && Line(p) == line && kv.Key >= 90).ToList();
    if (sameLine.Count > 0) return (sameLine.Max(kv => kv.Key), "newest NHL 24 " + brand + " " + line);
    if (cur.Length > 0 && cur[0] == brand) return (current!.Value, "his NHL 24 model, same brand");
    var sameBrand = real.Where(kv => kv.Value.Split('_')[0] == brand).ToList();
    if (sameBrand.Count > 0) return (sameBrand.Max(kv => kv.Key), "newest NHL 24 " + brand);
    return current is { } keep ? (keep, "no NHL 24 " + brand + ": his NHL 24 model") : (-1, "no model");
}

// ---- plan: every goalie on an NHL roster ----
var plan = new Dictionary<long, (Dictionary<string, long> V, long Team)>();
var lines = new List<(long Team, string Line)>();
foreach (var (gid, team) in teamOf)
{
    string who = nameOf.GetValueOrDefault(gid, gid.ToString());
    var own = own24.GetValueOrDefault(gid);
    long ownTeam = team24.GetValueOrDefault(gid, -1);
    var r27 = row27.GetValueOrDefault(gid);
    bool same27 = team27.GetValueOrDefault(gid, -1) == team;
    if (own is null && r27 is null) { lines.Add((team, $"{who}: no NHL 24 or NHL 27 gear row, left as is")); continue; }
    var v = new Dictionary<string, long>();
    var notes = new List<string>();
    foreach (var g in groups)
    {
        var t24 = g == "stick" ? stick24 : equip24; var t27 = g == "stick" ? stick27 : equip27;
        long? cur = own?.GetValueOrDefault(g);
        (long Id, string Why) pick = r27 is not null && long.TryParse(r27[g], out var id27) && t27.TryGetValue(id27, out var name27)
            ? Pick(name27, cur, t24)
            : cur is { } k ? (k, "his NHL 24 model (no NHL 27 row)") : (-1, "no model");
        if (pick.Id < 0) continue;
        long model = pick.Id;
        var name24 = t24.GetValueOrDefault(model, "?" + model);
        v[g] = model;
        var cols = groupCols[g];
        string src;
        var mates = clubRows.Where(c => c.Team == team && c.V[g] == model).ToList();
        var anyTeam = clubRows.Where(c => c.V[g] == model).ToList();
        // NHL 27 goalies wearing this model (matched by name: a few shared ids were renamed between the games).
        var on27 = rows27.Where(x => long.TryParse(x.R[g], out var i) && t27.GetValueOrDefault(i) == name24 && cols.All(x.R.ContainsKey)).ToList();
        static Dictionary<string, long> Common(IEnumerable<Dictionary<string, long>> rows, List<string> cols) =>
            rows.GroupBy(x => string.Join(',', cols.Select(c => x[c]))).OrderByDescending(x => x.Count()).First().First();
        bool own27 = r27 is not null && t27.GetValueOrDefault(long.Parse(r27[g])) == name24 && cols.All(r27.ContainsKey);
        // His own design first (his look, on this exact model), recoloured into his team's palette when he has moved.
        if (own27 && same27)
        { foreach (var c in cols) v[c] = long.Parse(r27[c]); src = "NHL 27 design"; }
        else if (own is not null && own[g] == model && ownTeam == team)
        { foreach (var c in cols) v[c] = own[c]; src = "his NHL 24 design"; }
        else if (own27 && team27.TryGetValue(gid, out var from27))
        { foreach (var c in cols) v[c] = Recolour(long.Parse(r27![c]), from27, team); src = $"his NHL 27 design, recoloured from {abbr.GetValueOrDefault(from27)}"; }
        else if (own is not null && own[g] == model && ownTeam is >= 0 and < 32)
        { foreach (var c in cols) v[c] = Recolour(own[c], ownTeam, team); src = $"his NHL 24 design, recoloured from {abbr.GetValueOrDefault(ownTeam)}"; }
        else if (mates.Count > 0)
        { var d = Common(mates.Select(m => m.V), cols); foreach (var c in cols) v[c] = d[c]; src = "NHL 24 " + abbr.GetValueOrDefault(team) + " design on this model"; }
        else if (anyTeam.Count > 0)
        {
            var from = anyTeam.GroupBy(x => x.Team).OrderByDescending(x => x.Count()).First();
            var d = Common(from.Select(m => m.V), cols);
            foreach (var c in cols) v[c] = Recolour(d[c], from.Key, team);
            src = "NHL 24 " + abbr.GetValueOrDefault(from.Key) + " design on this model, recoloured";
        }
        else if (on27.Count > 0)
        {
            // NHL 24 ships the model but no NHL 24 row wears it: an NHL 27 design on it, his own team's if there is one.
            var from = on27.Where(x => x.Team == team).DefaultIfEmpty(on27.GroupBy(x => x.Team).OrderByDescending(x => x.Count()).First().First()).First();
            foreach (var c in cols) v[c] = Recolour(long.Parse(from.R[c]), from.Team, team);
            src = "NHL 27 " + abbr.GetValueOrDefault(from.Team) + " design on this model" + (from.Team == team ? "" : ", recoloured");
        }
        else if (own is not null)
        { foreach (var c in cols) v[c] = Recolour(own[c], ownTeam, team); src = "his NHL 24 design recoloured (no NHL 24 row wears this model)"; }
        else { src = "colours unchanged"; }
        notes.Add($"{g} {(cur is { } cc ? ModelName(g, cc) : "-")} -> {ModelName(g, model)} [{pick.Why}; {src}]");
    }
    (long Type, long Id) mask; string maskWhy;
    if (own is not null && ownTeam == team) { mask = (own["masktype"], own["maskid"]); maskWhy = "his NHL 24 mask"; }
    else if (r27 is not null && same27 && validMasks.Contains((long.Parse(r27["masktype"]), long.Parse(r27["maskid"]))))
    { mask = (long.Parse(r27["masktype"]), long.Parse(r27["maskid"])); maskWhy = "NHL 27's mask"; }
    else if (homeMask.TryGetValue(team, out var hm)) { mask = hm; maskWhy = "team home mask"; }
    else { mask = own is not null ? (own["masktype"], own["maskid"]) : (0, team); maskWhy = "unchanged"; }
    v["masktype"] = mask.Type; v["maskid"] = mask.Id;
    v["showcustomcolors"] = 1; v["showcustomstick"] = 1;
    plan[gid] = (v, team);
    lines.Add((team, $"{who}{(ownTeam != team && ownTeam >= 0 ? $" (NHL 24: {abbr.GetValueOrDefault(ownTeam, ownTeam.ToString())})" : "")}: " +
        string.Join("; ", notes) + $"; mask {mask.Type}/{mask.Id} [{maskWhy}]"));
}

// ---- review ----
File.WriteAllLines(review, new[] { $"Goalie gear review ({DateTime.Now:yyyy-MM-dd HH:mm})", "",
    "Every goalie row is first put back to NHL 24's own values; then each NHL goalie below gets this on his personal and club rows.", "" }
    .Concat(lines.OrderBy(l => abbr.GetValueOrDefault(l.Team, "")).ThenBy(l => l.Line).Select(l => $"{abbr.GetValueOrDefault(l.Team, l.Team.ToString()),-4} {l.Line}")));

// Swatches, one band per goalie in review order: the colours his personal row has in the game now, then the plan
// (pads, blocker, trapper, stick zones left to right).
{
    var liveEx = main.Tables["exhibitiongoalieequipment"];
    var liveRow = new Dictionary<long, int>();
    for (int r = 0; r < liveEx.InUse; r++) liveRow.TryAdd(main.GetInt(liveEx, r, "game_id"), r);
    var order = plan.Keys.OrderBy(g => abbr.GetValueOrDefault(plan[g].Team, "")).ThenBy(g => nameOf.GetValueOrDefault(g, "")).ToList();
    var cols = groups.SelectMany(g => groupCols[g].OrderBy(c => c.Length).ThenBy(c => c)).ToList();
    int sw = 10, gap = 4, width = cols.Count * sw, height = order.Count * (2 * sw + gap);
    var px = new byte[width * height * 4];
    for (int k = 0; k < order.Count; k++)
        for (int line = 0; line < 2; line++)
            for (int c = 0; c < cols.Count; c++)
            {
                long v = line == 1 ? plan[order[k]].V.GetValueOrDefault(cols[c]) : liveRow.TryGetValue(order[k], out var lr) ? main.GetInt(liveEx, lr, cols[c]) : 0;
                for (int y = 0; y < sw; y++)
                    for (int x = 0; x < sw - 1; x++)
                    {
                        int p = ((k * (2 * sw + gap) + line * sw + y) * width + c * sw + x) * 4;
                        px[p] = (byte)(v >> 16); px[p + 1] = (byte)(v >> 8); px[p + 2] = (byte)v; px[p + 3] = 255;
                    }
            }
    ImageIO.SavePng(new Rgba(width, height, px), Path.Combine(Path.GetDirectoryName(review)!, "swatches.png"));
    File.AppendAllLines(review, new[] { "", "swatches.png: one band per goalie in this order (top: in the game now, bottom: planned; pads, blocker, trapper, stick):" }
        .Concat(order.Select((g, i) => $"  {i + 1,2}. {abbr.GetValueOrDefault(plan[g].Team)} {nameOf.GetValueOrDefault(g, g.ToString())}")));
}

// ---- every database: back to NHL 24's own, then the plan ----
(byte[] Saved, int Reset, int Planned)? Build(string db, byte[] bytes)
{
    var t3 = T3Db.Load((byte[])bytes.Clone(), meta);
    var src = Pristine(db);
    int reset = 0, planned = 0;
    foreach (var tn in new[] { "exhibitiongoalieequipment", "stockgoalieequipment" })
    {
        if (!t3.Tables.TryGetValue(tn, out var t)) continue;
        bool leagues = t.Fields.ContainsKey("league");
        if (src is not null && src.Tables.TryGetValue(tn, out var s))
        {
            string Key(T3Db d, T3Db.Table x, int r, Dictionary<string, int> seen)
            {
                var k = $"{d.GetInt(x, r, "game_id")}/{(leagues ? d.GetInt(x, r, "league") : 0)}";
                seen[k] = seen.GetValueOrDefault(k) + 1;
                return k + "/" + seen[k];
            }
            var orig = new Dictionary<string, int>(); var seenSrc = new Dictionary<string, int>(); var seenNow = new Dictionary<string, int>();
            for (int r = 0; r < s.InUse; r++) orig[Key(src, s, r, seenSrc)] = r;
            for (int r = 0; r < t.InUse; r++)
            {
                if (!orig.TryGetValue(Key(t3, t, r, seenNow), out var o)) continue;
                foreach (var f in resetFields.Where(f => t.Fields.ContainsKey(f) && s.Fields.ContainsKey(f)))
                    if (t3.GetInt(t, r, f) != src.GetInt(s, o, f)) { t3.SetInt(t, r, f, src.GetInt(s, o, f)); reset++; }
            }
        }
        // The plan goes on the personal row and on one club row (league 0, an NHL team), which moves to his team.
        var clubDone = new HashSet<long>();
        for (int r = 0; r < t.InUse; r++)
        {
            long gid = t3.GetInt(t, r, "game_id");
            if (!plan.TryGetValue(gid, out var p)) continue;
            if (leagues)
            {
                if (t3.GetInt(t, r, "league") != 0 || t3.GetInt(t, r, "team") is < 0 or > 31 || !clubDone.Add(gid)) continue;
                if (t3.GetInt(t, r, "team") != p.Team) { t3.SetInt(t, r, "team", p.Team); planned++; }
            }
            foreach (var (f, val) in p.V)
                if (t.Fields.ContainsKey(f) && t3.GetInt(t, r, f) != val) { t3.SetInt(t, r, f, val); planned++; }
        }
    }
    if (reset + planned == 0) return null;
    var saved = t3.Save();
    if (saved.Length != bytes.Length) throw new InvalidOperationException($"{db}: size changed");
    T3Db.Load((byte[])saved.Clone(), meta);   // every CRC valid again
    return (saved, reset, planned);
}
var output = new Dictionary<string, byte[]>();
foreach (var (db, bytes) in raw)
{
    var built = Build(db, bytes);
    Say($"{db}: {(built is { } b ? $"{b.Reset} cells back to NHL 24's own, {b.Planned} cells planned" : "nothing to change")}");
    if (built is { } ok) output[names[db]] = ok.Saved;
}
Say($"{plan.Count} NHL goalies planned. Review: {review}");
// Writing needs --apply: anything else (a typo'd or missing mode included) stops here, read only.
if (!WriteGate.Allowed(args)) { Say("dry run (pass --apply with --target to write)"); return 0; }

// ---- write ----
if (ApplyTarget.RunningGame() is { } running) { Say("REFUSED: " + running + " is running"); return 1; }
var backup = ApplyTarget.Backup(target);
Say("backup: " + backup);
bool Write(string label)
{
    foreach (var line in new LegacyFiles(target, settings).Replace(output)) SuiteLog.Write("goaliefix: " + line);
    var check = new LegacyFiles(target, settings);
    var wrong = output.Where(kv => !check.Read(kv.Key).AsSpan().SequenceEqual(kv.Value)).Select(kv => kv.Key).ToList();
    Say($"  {label}: {output.Count} database(s) read back {(wrong.Count == 0 ? "identical" : "DIFFERENT: " + string.Join(", ", wrong))}");
    return wrong.Count == 0;
}
if (!Write("applied")) { ApplyTarget.RestoreFromBackup(target, backup); Say("FAILED; restored"); return 1; }
if (args.Contains("--restore-check"))
{
    ApplyTarget.RestoreFromBackup(target, backup);
    var check = new LegacyFiles(target, settings);
    bool stockBack = raw.All(kv => check.Read(names[kv.Key]).AsSpan().SequenceEqual(kv.Value));
    Say("  restored: databases identical to before: " + stockBack);
    if (!stockBack || !Write("re-applied")) return 1;
}
Say("done");
return 0;
