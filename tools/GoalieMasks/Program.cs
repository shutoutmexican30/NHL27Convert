// GoalieMasks --target <game> [--dbs <dir>] [--export-dbs <dir>] [--skip-textures] [--skip-dbs] [--apply [--allow-live] [--restore-check]]
//
// One re-appliable step for goalie masks (owner's rules, 2026-11-12):
//   1. Textures: the NHL 27 team mask art that differs from NHL 24's goes into the same NHL 24 itech mask slot (same
//      mask id in both games; one itech mesh, same UV layout). Arizona slot 22 is Utah: NHL 27 itech 515/516 -> NHL 24
//      itech 22/148, colour and coeff. Written with TextureSlots.Apply (backup, every copy, read-back, auto-restore).
//   2. Databases: every goalie on an NHL roster (teams 0-31) wears his team's home mask: masktype 0, maskid =
//      exhibitionteams.goaliemask0_starter for the team's G1, goaliemask0_backup for the others. Set on his
//      exhibitiongoalieequipment row(s) and on his first league-0 NHL stockgoalieequipment row, whose stale team id is
//      fixed too. Free agents, colours, models and every other row stay as they are. NHL 27 mask ids never enter the DB.
//      All five roster databases where the tables exist; LegacyFiles.Replace writes the game and update copies.
// Idempotent: a value is written only when it differs, and textures already current are skipped, so it can be re-run
// after any restore. Read only unless --apply and --target are given; the original game needs --allow-live as well.
// --dbs <dir>: take the databases from <dir>\<db>.db (a temp copy) instead of the game - always read only.
// --export-dbs <dir>: copy the game's five databases (the copy the game reads) into <dir> and stop.
// --save <dir>: also write the edited databases into <dir> (not the game), to re-run on them as an idempotency check.
using System.Text;
using Suite.Convert27;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

void Say(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var settings = SuiteSettings.Load();
Oodle.Ensure(settings);
var targetArg = Opt("--target");
if (targetArg is null) { Console.Error.WriteLine("usage: GoalieMasks --target <game> [--dbs <dir>] [--export-dbs <dir>] [--skip-textures] [--skip-dbs] [--apply [--allow-live] [--restore-check]]"); return 2; }
var target = Path.GetFullPath(targetArg).TrimEnd(Path.DirectorySeparatorChar);
var dbsDir = Opt("--dbs");
bool apply = WriteGate.Allowed(args) && dbsDir is null && Opt("--export-dbs") is null;
bool isOriginal = new[] { settings.Nhl24GameDir, settings.Nhl24BaseDir, settings.Nhl24UpdateDir, settings.Nhl24ReadView }
    .Any(o => !string.IsNullOrWhiteSpace(o) && string.Equals(Path.GetFullPath(o).TrimEnd(Path.DirectorySeparatorChar), target, StringComparison.OrdinalIgnoreCase));
if (apply && isOriginal && !args.Contains("--allow-live")) { Say("REFUSED: that is the original (live) game; add --allow-live to write it."); return 1; }
if (apply && ApplyTarget.RunningGame() is { } running) { Say("REFUSED: " + running + " is running"); return 1; }

var meta = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes", "database", "t3db", "nhl_game-meta.xml");
var names = RosterSyncRun.Databases.ToDictionary(d => d, RosterSyncRun.LegacyName);
var reportDir = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "goaliemasks"));
var report = new List<string> { $"GoalieMasks {(apply ? "APPLY" : "dry run")} {DateTime.Now:yyyy-MM-dd HH:mm} target {target}" + (dbsDir is null ? "" : $" (databases from {dbsDir})") };

// ---- export only ----
if (Opt("--export-dbs") is { } exportDir)
{
    Directory.CreateDirectory(exportDir);
    var lfx = new LegacyFiles(target, settings);
    foreach (var (db, legacy) in names)
    {
        var copies = lfx.ReadAll(legacy);
        var read = lfx.Read(legacy);
        File.WriteAllBytes(Path.Combine(exportDir, db + ".db"), read);
        int same = copies.Count(c => c.AsSpan().SequenceEqual(read));
        Say($"{db}: {read.Length:N0} bytes -> {exportDir}; {copies.Count} cop{(copies.Count == 1 ? "y" : "ies")} in the game, {same} identical to the one it reads");
    }
    return 0;
}

// ---- 1. textures ----
const string Dir = "content/character/skater/goalie/helmet/itech/textures/goalie_helmet_itech_";
// NHL 24 mask id -> NHL 27 mask id, for the ids whose NHL 27 art differs (checked 2026-11-12, data\mask_map.json).
var colourIds = new (int Id24, int Id27)[]
{
    (0, 0), (13, 13), (21, 21), (22, 515), (58, 58), (65, 65), (69, 69), (86, 86),
    (126, 126), (128, 128), (129, 129), (133, 133), (135, 135), (139, 139), (148, 516), (150, 150),
};
var pairs = colourIds.Select(p => (To: $"{Dir}{p.Id24}_color", From: $"{Dir}{p.Id27}_color"))
    .Concat([(To: $"{Dir}22_coeff", From: $"{Dir}515_coeff"), (To: $"{Dir}148_coeff", From: $"{Dir}516_coeff")]).ToList();
var textureResult = "textures skipped";
if (!args.Contains("--skip-textures"))
{
    var index27 = AssetIndex.TryLoad("nhl27") ?? throw new InvalidOperationException("No NHL 27 asset index; open the suite once to build it.");
    var entries = index27.Entries.Where(e => e.Kind == AssetKind.Texture).GroupBy(e => e.Path).ToDictionary(g => g.Key, g => g.First());
    var wanted = pairs.Select(p => entries.TryGetValue(p.From, out var e) ? e : throw new InvalidOperationException("NHL 27 has no " + p.From)).ToList();
    var pngs = await new TextureImages(settings).Nhl27Async(wanted.Select(e => new Nhl27Texture(e)), s => { });
    var missing = pairs.Where(p => !pngs.ContainsKey(p.From)).Select(p => p.From).ToList();
    if (missing.Count > 0) { Say("REFUSED: NHL 27 art could not be decoded: " + string.Join(", ", missing)); return 1; }
    var replacements = pairs.Select(p => new TextureReplacement(p.To, pngs[p.From])).ToList();

    var index = BundleIndex.Build(target);
    int toWrite = 0, current = 0; var lines = new List<string>();
    foreach (var r in replacements)
    {
        int changed = 0, copies = 0;
        foreach (var slot in TextureSlots.ReadAll(target, index, r.Slot))
        {
            copies++;
            if (!TextureSlots.Build(slot, r).AsSpan().SequenceEqual(slot.Stock)) changed++;
        }
        if (changed > 0) toWrite++; else current++;
        lines.Add($"  {r.Slot.Split('/')[^1],-32} <- {Path.GetFileName(r.Source)}: {copies} slot cop{(copies == 1 ? "y" : "ies")}, {(changed > 0 ? $"{changed} to write" : "already current")}");
    }
    textureResult = $"textures: {replacements.Count} slots, {toWrite} to write, {current} already current";
    report.Add(textureResult); report.AddRange(lines);
    Say(textureResult);
    if (apply && toWrite > 0)
    {
        var (ok, message, _) = TextureSlots.Apply(target, replacements, s => Say("  " + s), settings.Workers);
        Say(message); report.Add(message);
        if (!ok) { File.WriteAllLines(Path.Combine(reportDir, "report.txt"), report); return 1; }
    }
}

// ---- 2. databases ----
if (args.Contains("--skip-dbs")) { File.WriteAllLines(Path.Combine(reportDir, "report.txt"), report); return 0; }
var lf = dbsDir is null ? new LegacyFiles(target, settings) : null;
var raw = names.ToDictionary(kv => kv.Key, kv => dbsDir is null ? lf!.Read(kv.Value) : File.ReadAllBytes(Path.Combine(dbsDir, kv.Key + ".db")));
var dbs = raw.ToDictionary(kv => kv.Key, kv => T3Db.Load((byte[])kv.Value.Clone(), meta));

// Roster of a database: game_id -> (team, G1) for goalies on NHL teams 0-31, and the teams' home masks. A database
// without its own roster, bio or team table uses nhl_game's (the small roster databases borrow its team table).
(Dictionary<long, (long Team, bool G1)> Goalies, Dictionary<long, (long Starter, long Backup)> Home, string Source) Roster(string db)
{
    var d = dbs[db];
    bool own = new[] { "exhibitionrostertable", "exhibitionplayers", "exhibitionplayerbiotable", "exhibitionteams" }.All(d.Tables.ContainsKey);
    if (!own) d = dbs["nhl_game"];
    var players = d.Tables["exhibitionplayers"]; var gameOf = new Dictionary<long, long>();
    for (int r = 0; r < players.InUse; r++)
        if (d.GetInt(players, r, "table") == 0) gameOf.TryAdd(d.GetInt(players, r, "index"), d.GetInt(players, r, "playerid"));
    var bio = d.Tables["exhibitionplayerbiotable"]; var goalie = new HashSet<long>();
    for (int r = 0; r < bio.InUse; r++) if (d.GetInt(bio, r, "position") == 4) goalie.Add(d.GetInt(bio, r, "game_id"));
    var ros = d.Tables["exhibitionrostertable"]; var g = new Dictionary<long, (long, bool)>();
    for (int r = 0; r < ros.InUse; r++)
    {
        long team = d.GetInt(ros, r, "team");
        if (team is < 0 or > 31 || !gameOf.TryGetValue(d.GetInt(ros, r, "playerindex"), out var gid) || !goalie.Contains(gid)) continue;
        if (g.ContainsKey(gid)) throw new InvalidDataException($"{db}: goalie {gid} is on two NHL rosters.");
        g[gid] = (team, d.GetInt(ros, r, "g1") == 1);
    }
    var teams = d.Tables["exhibitionteams"]; var home = new Dictionary<long, (long, long)>();
    for (int t = 0; t < 32; t++) home[t] = (d.GetInt(teams, t, "goaliemask0_starter"), d.GetInt(teams, t, "goaliemask0_backup"));
    foreach (var t in Enumerable.Range(0, 32))
        if (g.Values.Count(x => x.Item1 == t && x.Item2) != 1) throw new InvalidDataException($"{db}: team {t} does not have exactly one G1.");
    return (g, home, own ? db : "nhl_game (borrowed)");
}

// Brand lettering zones per gear model (data\goalie_lettering_zones.json, read from the NHL 24 recolor textures).
static double Lum(long c)
{
    static double Ch(long v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
    return 0.2126 * Ch((c >> 16) & 255) + 0.7152 * Ch((c >> 8) & 255) + 0.0722 * Ch(c & 255);
}
static double Contrast(long a, long b) { double x = Lum(a), y = Lum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
string? zonesFile = null;
for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && zonesFile is null; dir = dir.Parent)
    if (File.Exists(Path.Combine(dir.FullName, "data", "goalie_lettering_zones.json"))) zonesFile = Path.Combine(dir.FullName, "data", "goalie_lettering_zones.json");
if (Opt("--zones") is { } zo) zonesFile = zo;
if (zonesFile is null) { Say("REFUSED: data\\goalie_lettering_zones.json not found (pass --zones)"); return 1; }
var Lettering = new Dictionary<(string Item, long Model), List<(int Zone, List<int> Backs)>>();
var modelNames = new Dictionary<long, string>();
{
    using var zj = System.Text.Json.JsonDocument.Parse(File.ReadAllText(zonesFile));
    var ids = zj.RootElement.GetProperty("model_ids");
    foreach (var item in zj.RootElement.GetProperty("zones").EnumerateObject())
        foreach (var model in item.Value.EnumerateObject())
            foreach (var id in ids.GetProperty(model.Name).EnumerateArray())
            {
                modelNames[id.GetInt64()] = model.Name;
                var list = model.Value.EnumerateArray().Select(z => (z.GetProperty("zone").GetInt32(),
                    z.GetProperty("backgrounds").EnumerateArray().Select(b => b.GetInt32()).ToList())).Where(z => z.Item2.Count > 0).ToList();
                if (list.Count > 0) Lettering[(item.Name, id.GetInt64())] = list;
            }
}
string ModelName(long id) => modelNames.GetValueOrDefault(id, "#" + id);

var output = new Dictionary<string, byte[]>();
var names24 = new Dictionary<long, string>();
{
    var d = dbs["nhl_game"]; var bio = d.Tables["exhibitionplayerbiotable"];
    for (int r = 0; r < bio.InUse; r++) names24.TryAdd(d.GetInt(bio, r, "game_id"), d.GetString(bio, r, "firstname") + " " + d.GetString(bio, r, "lastname"));
}
foreach (var (db, d) in dbs)
{
    bool hasSt = d.Tables.TryGetValue("stockgoalieequipment", out var st), hasEx = d.Tables.TryGetValue("exhibitiongoalieequipment", out var ex);
    if (!hasSt && !hasEx) { Say($"{db}: no goalie equipment tables, skipped"); report.Add($"{db}: no goalie equipment tables, skipped"); continue; }
    var (goalies, home, source) = Roster(db);
    long Want(long gid) => goalies[gid].G1 ? home[goalies[gid].Team].Starter : home[goalies[gid].Team].Backup;
    int exRows = 0, stRows = 0, teamFixed = 0, cells = 0;
    var noStock = new List<long>(); var noPersonal = new List<long>(); var extraClub = new List<string>();
    var detail = new List<string>();
    bool Set(T3Db.Table t, int r, string f, long v) { if (d.GetInt(t, r, f) == v) return false; d.SetInt(t, r, f, v); cells++; return true; }
    if (hasEx)
    {
        var seen = new HashSet<long>();
        for (int r = 0; r < ex!.InUse; r++)
        {
            long gid = d.GetInt(ex, r, "game_id");
            if (!goalies.ContainsKey(gid)) continue;
            seen.Add(gid);
            long beforeT = d.GetInt(ex, r, "masktype"), beforeI = d.GetInt(ex, r, "maskid");
            if (Set(ex, r, "masktype", 0) | Set(ex, r, "maskid", Want(gid)))
            { exRows++; detail.Add($"  personal {names24.GetValueOrDefault(gid, gid.ToString())} ({gid}) team {goalies[gid].Team}{(goalies[gid].G1 ? " G1" : "")}: mask {beforeT}/{beforeI} -> 0/{Want(gid)}"); }
        }
        noPersonal.AddRange(goalies.Keys.Where(g => !seen.Contains(g)));
    }
    if (hasSt)
    {
        var done = new HashSet<long>();
        for (int r = 0; r < st!.InUse; r++)
        {
            long gid = d.GetInt(st, r, "game_id");
            if (!goalies.ContainsKey(gid) || d.GetInt(st, r, "league") != 0 || d.GetInt(st, r, "team") is < 0 or > 31) continue;
            if (!done.Add(gid)) { extraClub.Add($"{gid}/team {d.GetInt(st, r, "team")}"); continue; }
            long bt = d.GetInt(st, r, "team"), bmt = d.GetInt(st, r, "masktype"), bmi = d.GetInt(st, r, "maskid");
            bool tf = Set(st, r, "team", goalies[gid].Team);
            bool mf = Set(st, r, "masktype", 0) | Set(st, r, "maskid", Want(gid));
            if (tf) teamFixed++;
            if (tf || mf)
            { stRows++; detail.Add($"  club     {names24.GetValueOrDefault(gid, gid.ToString())} ({gid}): team {bt} -> {goalies[gid].Team}, mask {bmt}/{bmi} -> 0/{Want(gid)}"); }
        }
        noStock.AddRange(goalies.Keys.Where(g => !done.Contains(g)));
    }

    // ---- gear colours: club row = personal row, then readable brand lettering on both ----
    // Each row's final colours are worked out first and written once, so a second run finds nothing to change.
    int synced = 0, lettered = 0;
    var personalRow = new Dictionary<long, int>(); var clubRow = new Dictionary<long, int>();
    if (hasEx) for (int r = 0; r < ex!.InUse; r++) { long gid = d.GetInt(ex, r, "game_id"); if (goalies.ContainsKey(gid)) personalRow.TryAdd(gid, r); }
    if (hasSt) for (int r = 0; r < st!.InUse; r++)
    {
        long gid = d.GetInt(st, r, "game_id");
        if (goalies.ContainsKey(gid) && d.GetInt(st, r, "league") == 0 && d.GetInt(st, r, "team") is >= 0 and <= 31) clubRow.TryAdd(gid, r);
    }
    // Lettering: in <colours> (zone column -> colour), for the row's own models, every brand-lettering zone below 3:1
    // against its background zone(s) gets the row's team colour reaching 3:1 (the strongest), else the strongest of
    // the team colours, white and black. Returns report lines.
    List<string> Letter(T3Db.Table t, int r, Dictionary<string, long> colours, string who)
    {
        var lines = new List<string>();
        var palette = colours.Where(kv => kv.Key.Contains("zone")).Select(kv => kv.Value).Distinct().ToList();
        var choices = palette.Select(c => (C: c, Team: true)).Concat([(C: 0xFFFFFFL, Team: false), (C: 0x000000L, Team: false)]).ToList();
        foreach (var item in new[] { "pads", "blocker", "trapper" })
        {
            if (!Lettering.TryGetValue((item, d.GetInt(t, r, item)), out var zones)) continue;
            foreach (var (zone, backs) in zones)
            {
                string Col(int z) => $"{item}zone{z}color";
                var bgs = backs.Select(b => colours[Col(b)]).ToList();
                long cur = colours[Col(zone)];
                double now = bgs.Min(b => Contrast(cur, b));
                if (now >= 3) continue;
                var best = choices.Where(c => c.Team && bgs.Min(b => Contrast(c.C, b)) >= 3).OrderByDescending(c => bgs.Min(b => Contrast(c.C, b))).FirstOrDefault();
                if (best == default) best = choices.OrderByDescending(c => Math.Round(bgs.Min(b => Contrast(c.C, b)), 1)).ThenByDescending(c => c.Team).First();
                double after = bgs.Min(b => Contrast(best.C, b));
                if (after <= now) continue;
                colours[Col(zone)] = best.C;
                lines.Add($"{who} {item} {ModelName(d.GetInt(t, r, item))} zone {zone} on {string.Join("/", backs)}: {cur:X6} -> {best.C:X6}{(best.Team ? "" : " (white/black)")}, contrast {now:F1} -> {after:F1}");
            }
        }
        return lines;
    }
    Dictionary<string, long> Colours(T3Db.Table t, int r) =>
        t.Fields.Keys.Where(f => f.EndsWith("color", StringComparison.Ordinal)).ToDictionary(f => f, f => d.GetInt(t, r, f));
    int Put(T3Db.Table t, int r, Dictionary<string, long> colours) { int n = 0; foreach (var (f, v) in colours) if (Set(t, r, f, v)) n++; return n; }
    var personalFinal = new Dictionary<long, Dictionary<string, long>>();
    foreach (var (gid, r) in personalRow)
    {
        var who = $"  personal {names24.GetValueOrDefault(gid, gid.ToString())} ({gid})";
        var c = Colours(ex!, r); var lines = Letter(ex!, r, c, who);
        personalFinal[gid] = c;
        if (Put(ex!, r, c) > 0) { lettered += lines.Count; detail.AddRange(lines); }
    }
    foreach (var (gid, r) in clubRow)
    {
        var who = $"  club     {names24.GetValueOrDefault(gid, gid.ToString())} ({gid})";
        var c = Colours(st!, r);
        // The personal row carries the team design (11/03 gear colours); a moved club row kept the old team's colours.
        bool sync = personalFinal.TryGetValue(gid, out var p);
        if (sync) foreach (var f in c.Keys.ToList()) if (p!.TryGetValue(f, out var v)) c[f] = v;
        var lines = Letter(st!, r, c, who);
        int changed = Put(st!, r, c);
        if (changed > 0)
        {
            if (sync) { synced++; detail.Add($"{who}: gear colours = his personal row, then lettering ({changed} cells)"); }
            lettered += lines.Count; detail.AddRange(lines);
        }
    }

    var line = $"{db}: roster from {source}, {goalies.Count} NHL goalies; personal rows changed {exRows}, league-0 club rows changed {stRows} " +
               $"(stale team fixed {teamFixed}), club colours synced {synced}, lettering zones fixed {lettered}, {cells} cells; no personal row {noPersonal.Count}, no league-0 club row {noStock.Count}" +
               (extraClub.Count > 0 ? $", extra league-0 rows left alone {extraClub.Count} ({string.Join(", ", extraClub)})" : "");
    Say(line); report.Add(line); report.AddRange(detail);
    if (cells == 0) continue;
    var saved = d.Save();
    if (saved.Length != raw[db].Length) throw new InvalidOperationException($"{db}: size changed");
    T3Db.Load((byte[])saved.Clone(), meta);   // every CRC valid again
    output[names[db]] = saved;
}
File.WriteAllLines(Path.Combine(reportDir, "report.txt"), report, Encoding.UTF8);
Say($"{output.Count} database(s) to write. Report: {Path.Combine(reportDir, "report.txt")}");
if (Opt("--save") is { } saveDir)
{
    // The edited databases into a folder (never the game), e.g. to re-run on them and check nothing is left to change.
    Directory.CreateDirectory(saveDir);
    foreach (var (db, legacy) in names)
        File.WriteAllBytes(Path.Combine(saveDir, db + ".db"), output.TryGetValue(legacy, out var b) ? b : raw[db]);
    Say("edited databases saved to " + saveDir);
}
if (!apply) { Say("dry run (pass --apply with --target to write)"); return 0; }
if (output.Count == 0) { Say("databases already current"); return 0; }

// ---- write databases ----
var backup = ApplyTarget.Backup(target);
Say("backup: " + backup);
bool Write(string label)
{
    foreach (var l in new LegacyFiles(target, settings).Replace(output)) SuiteLog.Write("goaliemasks: " + l);
    var check = new LegacyFiles(target, settings);
    var wrong = output.Where(kv => check.ReadAll(kv.Key).Any(c => !c.AsSpan().SequenceEqual(kv.Value))).Select(kv => kv.Key).ToList();
    Say($"  {label}: {output.Count} database(s), every copy read back {(wrong.Count == 0 ? "identical" : "DIFFERENT: " + string.Join(", ", wrong))}");
    return wrong.Count == 0;
}
if (!Write("applied")) { ApplyTarget.RestoreFromBackup(target, backup); Say("FAILED; restored"); return 1; }
if (args.Contains("--restore-check"))
{
    ApplyTarget.RestoreFromBackup(target, backup);
    var check = new LegacyFiles(target, settings);
    bool back = raw.All(kv => check.Read(names[kv.Key]).AsSpan().SequenceEqual(kv.Value));
    Say("  restored: databases identical to before: " + back);
    if (!back || !Write("re-applied")) return 1;
}
Say("done");
return 0;
