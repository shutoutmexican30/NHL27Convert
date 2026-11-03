// GoalieFix --target <game copy> --nhl27 <NHL 27 CSV dump dir> [--apply [--restore-check]] | --show <game_id>
//           | --restore <backup folder> --apply
// Without --apply it only plans and writes the review (never the game).
// For every goalie on an NHL roster (NHL 24 team 0-31), in all five roster databases, in place (T3Db; layout identical):
//  1. colours: every *color zone from NHL 24's own stock team row for his current team + league (the pristine stock DB in
//     dbwork\stock, not the game being edited).  NHL 27's zone numbers are laid out for NHL 27's models, so its palette
//     mis-colours NHL 24's blocker and trapper - the brand wordmark lands on the wrong zone and cannot be read.
//  2. masks: the team row's masktype/maskid, so a traded goalie wears his new team's mask instead of his old team's.
//  3. gear models are never touched: the model id carries the brand wordmark (a TRUE pad says TRUE), so copying the team
//     row's model renamed the goalie's gear.  He keeps the pads/blocker/trapper/stick he already wears.
//  4. brand contrast (fallback only, when no NHL 24 team row exists, plus the TRUE-pad letters guard): a zone that was
//     white/black ink in the NHL 24 design, apart from the item's main colour, that the new colours put on the main colour
//     becomes white, black or the team's secondary, whichever stands out most.
// Writes a review (Detroit swatches before/after, mask changes, contrast fixes/failures). Backup, read-back, and with
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
var log = new List<string>();

// ---- NHL 27 source (CSV dump) ----
static List<Dictionary<string, string>> Csv(string path)
{
    var lines = File.ReadAllLines(path);
    // Quoted fields; these tables hold numbers and names without commas.
    static string[] Split(string l) => l.Split(',').Select(v => v.Trim('"')).ToArray();
    var head = Split(lines[0].TrimStart('﻿'));
    return lines.Skip(1).Where(l => l.Length > 0).Select(l => { var v = Split(l); return head.Select((h, i) => (h, v.ElementAtOrDefault(i) ?? "")).ToDictionary(x => x.h, x => x.Item2); }).ToList();
}
var stock27 = Csv(Path.Combine(nhl27, "stockgoalieequipment.csv")).Where(r => r["league"] == "0" && int.TryParse(r["team"], out var t) && t is >= 0 and < 32).ToList();
var colourCols = stock27[0].Keys.Where(k => k.EndsWith("color", StringComparison.Ordinal) && k != "sticktapecolor" || k == "sticktapecolor").ToList();
var by27 = stock27.GroupBy(r => r["game_id"]).ToDictionary(g => g.Key, g => g.First());

// ---- NHL 24 databases ----
var lf = new LegacyFiles(target, settings);
var raw = names.ToDictionary(kv => kv.Key, kv => lf.Read(kv.Value));
var main = T3Db.Load((byte[])raw["nhl_game"].Clone(), meta);
var bio = main.Tables["exhibitionplayerbiotable"];
var teamOf = new Dictionary<long, int>();
var nameOf = new Dictionary<long, string>();
for (int r = 0; r < bio.InUse; r++)
{
    if (main.GetInt(bio, r, "position") != 4) continue;
    var team = (int)main.GetInt(bio, r, "team");
    if (team is < 0 or > 31) continue;
    teamOf[main.GetInt(bio, r, "game_id")] = team;
}
if (args.Contains("--show"))
{
    // Read only: one goalie's zones and mask in this game.
    var gidShow = long.Parse(Arg("--show"));
    foreach (var tn in new[] { "exhibitiongoalieequipment", "stockgoalieequipment" })
    {
        var t = main.Tables[tn];
        for (int r = 0; r < t.InUse; r++)
            if (main.GetInt(t, r, "game_id") == gidShow)
                Console.WriteLine(tn + (t.Fields.ContainsKey("team") ? $" team {main.GetInt(t, r, "team")} league {main.GetInt(t, r, "league")}" : "") +
                    $" mask {main.GetInt(t, r, "masktype")}/{main.GetInt(t, r, "maskid")} pads {main.GetInt(t, r, "pads")} showcustomcolors {main.GetInt(t, r, "showcustomcolors")}: " +
                    string.Join(' ', t.Fields.Keys.Where(f => f.EndsWith("color")).OrderBy(f => f).Select(f => $"{f.Replace("color", "").Replace("zone", "")}={main.GetInt(t, r, f):X6}")));
    }
    return 0;
}
var abbr = new Dictionary<int, string>();
var secondary = new Dictionary<int, long>();
var teams = main.Tables["exhibitionteams"];
for (int r = 0; r < teams.InUse; r++)
{
    int idx = (int)main.GetInt(teams, r, "index");
    if (idx is < 0 or > 31) continue;
    secondary[idx] = main.GetInt(teams, r, "fesecondarycolor");
}
foreach (var t in Csv(Path.Combine(nhl27, "exhibitionteams.csv"))) if (int.TryParse(t["index"], out var i) && i is >= 0 and < 32) abbr[i] = i == 0 ? "ANA" : t["artabbr"];
foreach (var p in Csv(Path.Combine(nhl27, "exhibitionplayerbiotable.csv"))) nameOf.TryAdd(long.Parse(p["game_id"]), p["firstname"] + " " + p["lastname"]);

// Masks NHL 24 already uses (masktype, maskid), from both goalie tables of the main database.
var validMasks = new HashSet<(long, long)>();
foreach (var tn in new[] { "stockgoalieequipment", "exhibitiongoalieequipment" })
{
    var t = main.Tables[tn];
    for (int r = 0; r < t.InUse; r++) validMasks.Add((main.GetInt(t, r, "masktype"), main.GetInt(t, r, "maskid")));
}
// Team home mask and team colours from NHL 27's goalies on that team.
var teamMask = new Dictionary<int, (long Type, long Id)>();
var teamColours = new Dictionary<int, Dictionary<string, long>>();
foreach (var g in stock27.GroupBy(r => int.Parse(r["team"])))
{
    var mask = g.Select(r => (long.Parse(r["masktype"]), long.Parse(r["maskid"]))).Where(validMasks.Contains).GroupBy(m => m).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key;
    if (mask is { } m) teamMask[g.Key] = m;
    var best = g.GroupBy(r => string.Join(',', colourCols.Select(c => r[c]))).OrderByDescending(x => x.Count()).First().First();
    teamColours[g.Key] = colourCols.ToDictionary(c => c, c => long.Parse(best[c]));
}

var exMain = main.Tables["exhibitiongoalieequipment"]; var stMain = main.Tables["stockgoalieequipment"];
// NHL 24's own stock team rows: the palette and the mask were authored for NHL 24's gear, so this is the row a goalie's
// current team wears.  Read from the pristine stock DB (dbwork\stock), never from the game being edited (its rows
// already carry the earlier NHL 27-palette run).  Gear models are NOT taken from here - see the plan loop.
var stockDb = T3Db.Load(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(meta)!, "nhl_game.db")), meta);
var stockTeam = stockDb.Tables["stockgoalieequipment"];
var ownTeams = new Dictionary<(long Team, long League), int>();
for (int r = 0; r < stockTeam.InUse; r++) ownTeams.TryAdd((stockDb.GetInt(stockTeam, r, "team"), stockDb.GetInt(stockTeam, r, "league")), r);
// Teams NHL 27 gives no usable mask (Utah: NHL 24's team 22 is Arizona's slot, so its home mask is team 22's in NHL 24):
// the most common mask on NHL 24's own NHL gear rows for that team.
for (int t = 0; t < 32; t++)
{
    if (teamMask.ContainsKey(t)) continue;
    var own = Enumerable.Range(0, stMain.InUse).Where(r => main.GetInt(stMain, r, "league") == 0 && main.GetInt(stMain, r, "team") == t)
        .Select(r => (main.GetInt(stMain, r, "masktype"), main.GetInt(stMain, r, "maskid"))).GroupBy(m => m).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
    if (own is { } m) teamMask[t] = m;
}

// Pads model -> (brand letters zone, zone under them), from that model's recolor masks.
static Dictionary<long, (string Letters, string Under)> BrandOnPads() => new() { [126] = ("padszone5color", "padszone8color") };
var BrandOn = BrandOnPads();

static double Dist(long a, long b)
{
    double dr = ((a >> 16) & 255) - ((b >> 16) & 255), dg = ((a >> 8) & 255) - ((b >> 8) & 255), db = (a & 255) - (b & 255);
    return Math.Sqrt(dr * dr + dg * dg + db * db);
}
static double Ink(long c) => Math.Min(Dist(c, 0xFFFFFF), Dist(c, 0x000000));
static string Group(string col) => col.StartsWith("pads") ? "pads" : col.StartsWith("blocker") ? "blocker" : col.StartsWith("trapper") ? "trapper" : "stick";

// Per goalie: the new colours (with the contrast fix) and the mask decision, from the main database, applied to all five.
var plan = new Dictionary<long, (Dictionary<string, long> Colours, (long Type, long Id)? Mask, int Team)>();
var contrastFixed = new List<string>(); var contrastFailed = new List<string>(); var maskChanges = new List<string>(); var noRow = new List<string>();
var exRow = new Dictionary<long, int>();
for (int r = 0; r < exMain.InUse; r++) exRow.TryAdd(main.GetInt(exMain, r, "game_id"), r);
var stRow = new Dictionary<long, int>();
for (int r = 0; r < stMain.InUse; r++) if (main.GetInt(stMain, r, "league") == 0) stRow.TryAdd(main.GetInt(stMain, r, "game_id"), r);
foreach (var (gid, team) in teamOf)
{
    string who = nameOf.GetValueOrDefault(gid, gid.ToString());
    // Colours come from NHL 24's own row for his current team: NHL 27's zone numbers are laid out for NHL 27's models,
    // so its palette mis-colours NHL 24's blocker and trapper (the brand wordmark lands on the wrong zone).
    int? ownRow = ownTeams.TryGetValue((team, 0L), out var orow) ? orow : null;
    var colours = ownRow is { } row24
        ? colourCols.Where(stockTeam.Fields.ContainsKey).ToDictionary(c => c, c => stockDb.GetInt(stockTeam, row24, c))
        : by27.TryGetValue(gid.ToString(), out var r27) && int.Parse(r27["team"]) == team
            ? colourCols.ToDictionary(c => c, c => long.Parse(r27[c]))
            : teamColours.TryGetValue(team, out var tc) ? new Dictionary<string, long>(tc) : null;
    if (colours is null) continue;
    // Stock design to protect: the goalie's current NHL 24 colours.  Only the NHL 27 fallback needs the contrast fix;
    // the stock team palette is already authored for its own models.
    (T3Db.Table T, int R)? old = exRow.TryGetValue(gid, out var er) ? (exMain, er) : stRow.TryGetValue(gid, out var sr) ? (stMain, sr) : null;
    if (old is { } o && ownRow is null)
        foreach (var grp in colours.Keys.GroupBy(Group))
        {
            var cols = grp.Where(c => c != "sticktapecolor" && o.T.Fields.ContainsKey(c)).ToList();
            if (cols.Count < 2) continue;
            // The item's main colour (most zones), before and after.
            long oldBase = cols.GroupBy(c => main.GetInt(o.T, o.R, c)).OrderByDescending(g => g.Count()).First().Key;
            long newBase = cols.GroupBy(c => colours[c]).OrderByDescending(g => g.Count()).First().Key;
            foreach (var z in cols)
            {
                long oz = main.GetInt(o.T, o.R, z);
                // A brand-ink zone: white or black in the NHL 24 design and clearly apart from the main colour.
                if (Ink(oz) > 40 || Dist(oz, oldBase) < 100 || Dist(colours[z], newBase) >= 60) continue;
                var pick = new[] { 0xFFFFFFL, 0x101010L, secondary.GetValueOrDefault(team, 0xFFFFFF) }.OrderByDescending(c => Dist(c, newBase)).First();
                contrastFixed.Add($"{abbr.GetValueOrDefault(team)} {who}: {z} {colours[z]:X6} on {newBase:X6} -> {pick:X6}");
                colours[z] = pick;
                if (Dist(pick, newBase) < 60) contrastFailed.Add($"{abbr.GetValueOrDefault(team)} {who}: {z}");
            }
        }
    // Brand lettering read from the pad's recolor masks: on TRUE pads (model 126, HZRDUS) the "TRUE" letters are zone 5,
    // cut out of zone 8. Same colour on both = unreadable (Detroit: red on red), so the letters get the contrast colour.
    // The model stays the one the goalie wears, so the guard follows his current pads.
    long padsModel = exRow.TryGetValue(gid, out var padRow) ? main.GetInt(exMain, padRow, "pads")
        : stRow.TryGetValue(gid, out var padStock) ? main.GetInt(stMain, padStock, "pads") : -1;
    if (BrandOn.TryGetValue(padsModel, out var brand) && colours.ContainsKey(brand.Letters) && colours.ContainsKey(brand.Under)
        && Dist(colours[brand.Letters], colours[brand.Under]) < 60)
    {
        var under = colours[brand.Under];
        var pick = new[] { 0xFFFFFFL, 0x101010L, secondary.GetValueOrDefault(team, 0xFFFFFF) }.OrderByDescending(c => Dist(c, under)).First();
        contrastFixed.Add($"{abbr.GetValueOrDefault(team)} {who}: {brand.Letters} {colours[brand.Letters]:X6} on {brand.Under} {under:X6} -> {pick:X6}");
        colours[brand.Letters] = pick;
    }
    // The mask is the team's artwork, so it comes from the same NHL 24 team row: a traded goalie stops wearing his old
    // team's helmet (Gibson still had Anaheim's Duck mask after moving to Detroit).
    (long Type, long Id)? mask = ownRow is { } maskRow
        ? (Type: stockDb.GetInt(stockTeam, maskRow, "masktype"), Id: stockDb.GetInt(stockTeam, maskRow, "maskid"))
        : null;
    if (mask is null)
    {
        // No NHL 24 team row: fall back to the team's most common NHL 27 mask that NHL 24 already uses.
        if (!stRow.TryGetValue(gid, out var row))
        {
            // No NHL gear row: the game falls back to his personal row (exhibitiongoalieequipment), which still carries his
            // old (often AHL) mask. Give that row his team's home mask; the stock rows stay as they are.
            if (exRow.TryGetValue(gid, out var pr) && teamMask.TryGetValue(team, out var hm) &&
                (main.GetInt(exMain, pr, "masktype"), main.GetInt(exMain, pr, "maskid")) != hm)
            {
                mask = hm;
                maskChanges.Add($"{who} (personal row, no NHL gear row): mask {main.GetInt(exMain, pr, "masktype")}/{main.GetInt(exMain, pr, "maskid")} -> {abbr.GetValueOrDefault(team)} {hm.Type}/{hm.Id}");
            }
            else noRow.Add($"{abbr.GetValueOrDefault(team)} {who}");
        }
        else if (main.GetInt(stMain, row, "team") != team && teamMask.TryGetValue(team, out var tm))
        {
            mask = tm;
            long oldTeam = main.GetInt(stMain, row, "team");
            maskChanges.Add($"{who}: {(oldTeam is >= 0 and < 32 ? abbr.GetValueOrDefault((int)oldTeam) : oldTeam.ToString())} mask {main.GetInt(stMain, row, "masktype")}/{main.GetInt(stMain, row, "maskid")} -> {abbr.GetValueOrDefault(team)} {tm.Type}/{tm.Id}");
        }
    }
    else
    {
        // Report the change against the NHL gear row (or the personal row when he has none).
        (long Type, long Id, long Team) before = stRow.TryGetValue(gid, out var br)
            ? (main.GetInt(stMain, br, "masktype"), main.GetInt(stMain, br, "maskid"), main.GetInt(stMain, br, "team"))
            : exRow.TryGetValue(gid, out var er2)
                ? (main.GetInt(exMain, er2, "masktype"), main.GetInt(exMain, er2, "maskid"), team)
                : (-1L, -1L, team);
        if (before.Type != mask.Value.Type || before.Id != mask.Value.Id || before.Team != team)
            maskChanges.Add($"{who}: {(before.Team is >= 0 and < 32 ? abbr.GetValueOrDefault((int)before.Team) : before.Team.ToString())} mask {before.Type}/{before.Id} -> {abbr.GetValueOrDefault(team)} {mask.Value.Type}/{mask.Value.Id}");
    }
    // Gear models are deliberately not written: the model id is the brand wordmark (a TRUE pad says TRUE), so copying the
    // team row's model renamed the goalie's gear.  He keeps the pads/blocker/trapper/stick he already wears.
    plan[gid] = (colours, mask, team);
}

// ---- Detroit swatches (before = the main database now) ----
var det = plan.Where(p => p.Value.Team == 10).Select(p => p.Key).ToList();
var padCols = colourCols.Where(c => Group(c) is "pads" or "blocker" or "trapper").OrderBy(c => c).ToList();
int sw = 14, width = padCols.Count * sw, height = det.Count * 2 * sw + det.Count * 6;
var px = new byte[width * height * 4];
for (int k = 0; k < det.Count; k++)
    for (int line = 0; line < 2; line++)
        for (int c = 0; c < padCols.Count; c++)
        {
            long v = line == 0
                ? (exRow.TryGetValue(det[k], out var rr) ? main.GetInt(exMain, rr, padCols[c]) : 0)
                : plan[det[k]].Colours[padCols[c]];
            for (int y = 0; y < sw; y++)
                for (int x = 0; x < sw - 1; x++)
                {
                    int p = (((k * (2 * sw + 6)) + line * sw + y) * width + c * sw + x) * 4;
                    px[p] = (byte)(v >> 16); px[p + 1] = (byte)(v >> 8); px[p + 2] = (byte)v; px[p + 3] = 255;
                }
        }
var swatch = Path.Combine(Path.GetDirectoryName(review)!, "detroit_before_after.png");
ImageIO.SavePng(new Rgba(width, height, px), swatch);

File.WriteAllLines(review, new[] { $"Goalie gear review ({DateTime.Now:yyyy-MM-dd HH:mm})", "",
    $"Detroit goalies (swatch rows: before, after; columns: {string.Join(' ', padCols)}):" }
    .Concat(det.Select(g => "  " + nameOf.GetValueOrDefault(g, g.ToString())))
    .Concat(["", $"Mask changes ({maskChanges.Count}):"]).Concat(maskChanges.Select(s => "  " + s))
    .Concat(["", $"Brand contrast fixed ({contrastFixed.Count}):"]).Concat(contrastFixed.Select(s => "  " + s))
    .Concat(["", $"Contrast still failing ({contrastFailed.Count}):"]).Concat(contrastFailed.Select(s => "  " + s))
    .Concat(["", $"No NHL 24 team row and no usable personal row (left as is, {noRow.Count}):"]).Concat(noRow.Select(s => "  " + s)));
Say($"{plan.Count} goalies planned, {maskChanges.Count} mask changes, {contrastFixed.Count} contrast fixes, {contrastFailed.Count} failing. Review: {review}");
// Writing needs --apply: anything else (a typo'd or missing mode included) stops here, read only.
if (!WriteGate.Allowed(args)) { Say("dry run (pass --apply with --target to write)"); return 0; }

// ---- write all five databases in place ----
var output = new Dictionary<string, byte[]>();
var customOn = new List<(string Db, int Team, long Gid)>();
foreach (var (db, bytes) in raw)
{
    var t3 = T3Db.Load((byte[])bytes.Clone(), meta);
    int cells = 0;
    foreach (var tn in new[] { "exhibitiongoalieequipment", "stockgoalieequipment" })
    {
        if (!t3.Tables.TryGetValue(tn, out var t)) continue;
        bool stock = tn == "stockgoalieequipment";
        for (int r = 0; r < t.InUse; r++)
        {
            if (!plan.TryGetValue(t3.GetInt(t, r, "game_id"), out var p)) continue;
            if (stock && t3.GetInt(t, r, "league") != 0) continue;
            foreach (var (col, v) in p.Colours)
                if (t.Fields.ContainsKey(col) && t3.GetInt(t, r, col) != v) { t3.SetInt(t, r, col, v); cells++; }
            if (t.Fields.ContainsKey("showcustomcolors") && t3.GetInt(t, r, "showcustomcolors") != 1) { t3.SetInt(t, r, "showcustomcolors", 1); cells++; customOn.Add((db, p.Team, t3.GetInt(t, r, "game_id"))); }
            if (t.Fields.ContainsKey("showcustomstick") && t3.GetInt(t, r, "showcustomstick") != 1) { t3.SetInt(t, r, "showcustomstick", 1); cells++; }
            if (p.Mask is { } m)
            {
                if (stock && t3.GetInt(t, r, "team") != p.Team) { t3.SetInt(t, r, "team", p.Team); cells++; }
                if (t3.GetInt(t, r, "masktype") != m.Type) { t3.SetInt(t, r, "masktype", m.Type); cells++; }
                if (t3.GetInt(t, r, "maskid") != m.Id) { t3.SetInt(t, r, "maskid", m.Id); cells++; }
            }
        }
    }
    var saved = t3.Save();
    if (saved.Length != bytes.Length) throw new InvalidOperationException($"{db}: size changed");
    T3Db.Load((byte[])saved.Clone(), meta);   // every CRC valid again
    Say($"{db}: {cells} cells");
    if (cells > 0) output[names[db]] = saved;
}

foreach (var g in customOn.Where(c => c.Db == "nhl_game").Select(c => c.Gid).Distinct())
    Say($"  custom colours switched on: {abbr.GetValueOrDefault(teamOf.GetValueOrDefault(g))} {nameOf.GetValueOrDefault(g, g.ToString())}");
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
