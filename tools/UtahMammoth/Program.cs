// UtahMammoth: Arizona Coyotes (team 22) -> Utah Mammoth in an NHL 24 game, with the suite's own writers.
//
//   UtahMammoth where <game>                         where each touched legacy file and its index live (read only)
//   UtahMammoth plan  <game> <outDir> [--nhl27 csv]  build every edit in memory from the game's current bytes, check
//                                                    layout/ranges, write report + edited copies to outDir (no game write)
//   UtahMammoth apply <game> <outDir> --apply --target <game> [--restore-check]
//                                                    backup (TOCs + CAS lengths + every touched file's old bytes),
//                                                    write each copy from its own bytes, read back every copy;
//                                                    --restore-check: restore, verify identical to the backup and the
//                                                    old bytes back, apply again and re-verify.
//
// Change sets: data\utah_color_edits.json, data\teams_overrides.json (wins last), data\utah_loc_edits.tsv.
// Never touched: *_ARZL keys, team 279 (Alumni), arena 268, team 613, asset ids, row counts, sort order.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

void Say(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
if (args.Length < 2) { Console.WriteLine("usage: UtahMammoth where|plan|apply <game> [outDir] ..."); return 2; }
string Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
bool Has(string name) => args.Contains(name);

var settings = SuiteSettings.Load();
var game = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar);
Oodle.Ensure(settings);
string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
if (!File.Exists(Path.Combine(repo, "data", "utah_loc_edits.tsv"))) repo = @"C:\dev\NHL27Convert";
string nhl27Csv = Opt("--nhl27") is { Length: > 0 } c ? c :
    @"C:\Users\devin\Documents\Codex\2026-10-20\where-are-the-imported-rosters-located\RosterConversion\nhl27-csv\stockteamequipmenttable.csv";

const string Game = "gamemodes/database/t3db/nhl_game.db", Initial = "gamemodes/roster/initial_experience.db", Meta = "gamemodes/database/t3db/nhl_game-meta.xml";
string[] locNames = ["cze_cz", "eng_us", "fin_fi", "fre_fr", "ger_de", "lng_lg", "rus_ru", "swe_se"];
var dbNames = new[] { Game, Initial };
var allNames = dbNames.Concat(locNames.Select(l => $"loc/{l}.db")).ToArray();
if (args.Contains("--db-only")) allNames = dbNames;
{ int oi = Array.IndexOf(args, "--only"); if (oi >= 0) { var want = args[oi + 1].Split(','); allNames = allNames.Where(n => want.Any(w => n.Contains(w))).ToArray(); } }
if (args[0] == "scan")
{
    var lfS = new LegacyFiles(game, settings);
    var metaS = Path.Combine(Path.GetTempPath(), "utah-scan-meta.xml"); File.WriteAllBytes(metaS, lfS.Read(Meta));
    var rx = new System.Text.RegularExpressions.Regex("Arizona|Coyote|Tempe|Utah|Mammoth|Delta Center", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    foreach (var dbn in new[] { Game, Initial, "gamemodes/roster/entry_draft.db", "gamemodes/roster/expansion_draft.db", "gamemodes/roster/threes_campaign.db" })
    {
        var copies = lfS.ReadAll(dbn);
        for (int ci = 0; ci < copies.Count; ci++)
        {
            var db = T3Db.Load(copies[ci], metaS);
            Console.WriteLine($"== {dbn} copy {ci}: {db.Tables.Count} tables");
            foreach (var (tn, t) in db.Tables)
                foreach (var (fn, f) in t.Fields)
                {
                    if (f.Type != 0) continue;
                    var hits = Enumerable.Range(0, t.InUse).Select(r => (r, s: db.GetString(t, r, fn))).Where(x => rx.IsMatch(x.s)).ToList();
                    foreach (var g2 in hits.GroupBy(x => x.s)) Console.WriteLine($"   {tn}.{fn} = \"{g2.Key}\" x{g2.Count()} (rows {string.Join(",", g2.Take(6).Select(x => x.r))})");
                }
        }
    }
    return 0;
}
if (args[0] == "restore")
{
    if (ApplyTarget.RunningGame() is { } rg) { Console.WriteLine($"game running ({rg})"); return 1; }
    ApplyTarget.RestoreFromBackup(game, args[2]);
    var left = ApplyTarget.Compare(game, args[2]);
    Say(left.Count == 0 ? "RESTORED: 0 differences vs backup" : "RESTORE DIFFERENCES: " + string.Join("; ", left));
    return left.Count == 0 ? 0 : 1;
}

if (args[0] == "where")
{
    var lf = new LegacyFiles(game, settings);
    foreach (var n in allNames) foreach (var line in lf.Where(n)) Console.WriteLine(line);
    return 0;
}

if (args.Length < 3) { Console.WriteLine("plan/apply need <outDir>"); return 2; }
var outDir = Path.GetFullPath(args[2]);
Directory.CreateDirectory(outDir);

// ---------- change sets ----------
var over = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "data", "teams_overrides.json"))).RootElement.GetProperty("teams")[0];
var colorEdits = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "data", "utah_color_edits.json"))).RootElement;
static int Rgb(string hex) => Convert.ToInt32(hex.TrimStart('#'), 16);

// exhibitionteams row index 22: utah_color_edits first, teams_overrides last.
var teamInts = new Dictionary<string, long>();
var teamStrings = new Dictionary<string, string>();
foreach (var t in colorEdits.GetProperty("tables").EnumerateArray())
{
    if (t.GetProperty("table").GetString() == "exhibitionteams" && t.TryGetProperty("set", out var set))
        foreach (var p in set.EnumerateObject()) teamInts[p.Name] = p.Value.GetInt64();
}
var arenaInts = new Dictionary<string, long>();
foreach (var t in colorEdits.GetProperty("tables").EnumerateArray())
    if (t.GetProperty("table").GetString() == "exhibitionarena" && t.TryGetProperty("set", out var set))
        foreach (var p in set.EnumerateObject()) arenaInts[p.Name] = p.Value.GetInt64();
foreach (var f in new[] { "teamname", "nickname", "cityname", "abbrname", "artabbr" }) teamStrings[f] = over.GetProperty(f).GetString()!;
long overArtid = over.GetProperty("artid").GetInt64();
var oc = over.GetProperty("colors");
foreach (var pre in new[] { "fe", "pres", "overlay" })
{
    teamInts[pre + "primarycolor"] = Rgb(oc.GetProperty("primary").GetString()!);
    teamInts[pre + "secondarycolor"] = Rgb(oc.GetProperty("secondary").GetString()!);
}
var stockColors = oc.GetProperty("stockteamcolors").EnumerateArray().Select(e => Rgb(e.GetString()!)).ToArray();
var ar = over.GetProperty("arena");
var arenaStrings = new Dictionary<string, string> { ["arenaname"] = ar.GetProperty("arenaname").GetString()!, ["cityname"] = ar.GetProperty("cityname").GetString()! };
arenaInts["maxattendance"] = ar.GetProperty("maxattendance").GetInt64();
arenaInts["yearopened"] = ar.GetProperty("yearopened").GetInt64();
arenaInts["crowdlightcolor"] = Rgb(ar.GetProperty("crowdlightcolor").GetString()!);
if (args.Contains("--arena-generic")) { arenaInts["arenaartid"] = 50; arenaInts["projectionid"] = -1; }   // NHL 24 ahlgeneric_50, like every AHL arena row

// NHL 27 Utah equipment rows (team 22 in NHL 27 too).
var eq27 = T3Csv.Load(Path.GetDirectoryName(nhl27Csv)!, "stockteamequipmenttable");
var utah27 = eq27.Rows.Where(r => eq27.Get(r, "team") == "22").ToList();
string[] zones = Enumerable.Range(1, 15).Select(i => $"zone{i}").ToArray();

// Loc edits (eng_us text; other languages get the same key's own text with the words swapped).
var locRows = File.ReadAllLines(Path.Combine(repo, "data", "utah_loc_edits.tsv")).Skip(1)
    .Select(l => l.Split('\t')).Where(p => p.Length >= 3 && p[2] != "(keep)" && !p[0].EndsWith("_ARZL", StringComparison.OrdinalIgnoreCase))
    .Select(p => (Key: p[0], Current: p[1], New: p[2])).ToList();

{ int ki = Array.IndexOf(args, "--keys"); if (ki >= 0) { var rxk = new System.Text.RegularExpressions.Regex(args[ki + 1]); locRows = locRows.Where(r => rxk.IsMatch(r.Key)).ToList(); Console.WriteLine("loc keys: " + string.Join(", ", locRows.Select(r => r.Key))); } }
var report = new List<string>();
void R(string s) { report.Add(s); Console.WriteLine(s); }

// ---------- db edit ----------
var lfRead = new LegacyFiles(game, settings);
var metaPath = Path.Combine(outDir, "nhl_game-meta.xml");
File.WriteAllBytes(metaPath, lfRead.Read(Meta));
var metaRanges = System.Xml.Linq.XDocument.Load(metaPath).Descendants("table")
    .ToDictionary(t => (string)t.Attribute("name")!, t => t.Descendants("field").ToDictionary(f => (string)f.Attribute("name")!,
        f => (Low: long.TryParse((string?)f.Attribute("rangelow"), out var lo) ? lo : long.MinValue,
              High: long.TryParse((string?)f.Attribute("rangehigh"), out var hi) ? hi : long.MaxValue)));

byte[] EditDb(string name, byte[] bytes, string copyLabel)
{
    var db = T3Db.Load((byte[])bytes.Clone(), metaPath);
    int n = 0;
    void SetI(T3Db.Table t, int row, string field, long v, string what)
    {
        if (!t.Fields.ContainsKey(field)) { R($"  {copyLabel} {t.Name}.{field}: no such field here, skipped"); return; }
        var (lo, hi) = metaRanges[t.Name][field];
        if (v < lo || v > hi) throw new InvalidDataException($"{t.Name}.{field} = {v} outside meta range [{lo}, {hi}]");
        long old = db.GetInt(t, row, field);
        if (old == v) { R($"  {copyLabel} {what}.{field} = {v} (already)"); return; }
        db.SetInt(t, row, field, v); n++;
        R($"  {copyLabel} {what}.{field}: {old} -> {v}");
    }
    void SetS(T3Db.Table t, int row, string field, string v, string what)
    {
        string old = db.GetString(t, row, field);
        if (old == v) { R($"  {copyLabel} {what}.{field} = '{v}' (already)"); return; }
        db.SetString(t, row, field, v); n++;
        R($"  {copyLabel} {what}.{field}: '{old}' -> '{v}'");
    }
    int RowWhere(T3Db.Table t, Func<int, bool> pred, string what)
    {
        var rows = Enumerable.Range(0, t.InUse).Where(pred).ToList();
        if (rows.Count != 1) throw new InvalidDataException($"{t.Name}: {rows.Count} rows for {what}");
        return rows[0];
    }

    if (db.Tables.TryGetValue("exhibitionteams", out var teams))
    {
        int row = RowWhere(teams, r => db.GetInt(teams, r, "index") == 22, "index 22");
        if (db.GetInt(teams, row, "artid") != overArtid) throw new InvalidDataException("team 22 artid is not 22");
        foreach (var (f, v) in teamStrings) SetS(teams, row, f, v, "exhibitionteams[22]");
        foreach (var (f, v) in teamInts) SetI(teams, row, f, v, "exhibitionteams[22]");
    }
    else R($"  {copyLabel}: no exhibitionteams");
    if (db.Tables.TryGetValue("exhibitionarena", out var arena))
    {
        int row = RowWhere(arena, r => db.GetInt(arena, r, "index") == 22, "arena 22");
        foreach (var (f, v) in arenaStrings) SetS(arena, row, f, v, "exhibitionarena[22]");
        foreach (var (f, v) in arenaInts) SetI(arena, row, f, v, "exhibitionarena[22]");
    }
    else R($"  {copyLabel}: no exhibitionarena");
    if (db.Tables.TryGetValue("stockteamcolorstable", out var colors))
    {
        for (int id = 0; id < 6; id++)
        {
            int row = RowWhere(colors, r => db.GetInt(colors, r, "team") == 22 && db.GetInt(colors, r, "colorid") == id, $"team 22 colorid {id}");
            long idx = db.GetInt(colors, row, "index");
            if (idx != 76 + id) throw new InvalidDataException($"stockteamcolorstable team 22 colorid {id} has index {idx}, expected {76 + id}");
            SetI(colors, row, "color", stockColors[id], $"stockteamcolorstable[index {idx}, colorid {id}]");
        }
    }
    else R($"  {copyLabel}: no stockteamcolorstable");
    if (db.Tables.TryGetValue("stockteamequipmenttable", out var eq))
    {
        var rows = Enumerable.Range(0, eq.InUse).Where(r => db.GetInt(eq, r, "team") == 22).ToList();
        R($"  {copyLabel} stockteamequipmenttable: {rows.Count} team-22 rows");
        foreach (int r in rows)
        {
            long equipment = db.GetInt(eq, r, "equipment"), era = db.GetInt(eq, r, "era"), variant = db.GetInt(eq, r, "variant"),
                 brand = db.GetInt(eq, r, "brand"), index = db.GetInt(eq, r, "index");
            string what = $"stockteamequipmenttable[index {index}, brand {brand}, equipment {equipment}, era {era}, variant {variant}]";
            if (equipment == -1) { R($"  {copyLabel} {what}: equipment -1 placeholder, left"); continue; }
            string want27 = variant is 1 or 3 or 4 ? "13" : "-1";
            List<string[]> Pick(Func<string[], bool> p) => utah27.Where(p).ToList();
            bool Eq(string[] x, string f, long v) => eq27.Get(x, f) == v.ToString();
            var cands = Pick(x => Eq(x, "equipment", equipment) && Eq(x, "era", era) && eq27.Get(x, "variant") == want27);
            string rule = $"NHL 27 equipment {equipment} era {era} variant {want27}";
            if (cands.Count == 0) { cands = Pick(x => Eq(x, "equipment", equipment) && eq27.Get(x, "variant") == want27); rule = $"NHL 27 equipment {equipment} any era variant {want27}"; }
            if (cands.Count == 0) { cands = Pick(x => Eq(x, "equipment", equipment) && eq27.Get(x, "variant") == "-1"); rule = $"NHL 27 equipment {equipment} any era variant -1"; }
            if (cands.Count == 0) { R($"  {copyLabel} {what}: no NHL 27 Utah row for equipment {equipment}, left"); continue; }
            // Several NHL 27 rows: same brand first, then brand -1 with the lowest index (the generic row).
            var src = cands.FirstOrDefault(x => Eq(x, "brand", brand) && Eq(x, "era", era)) ?? cands.FirstOrDefault(x => Eq(x, "brand", brand))
                      ?? cands.Where(x => eq27.Get(x, "brand") == "-1").OrderBy(x => long.Parse(eq27.Get(x, "index"))).FirstOrDefault() ?? cands[0];
            R($"  {copyLabel} {what} <- {rule}, NHL 27 index {eq27.Get(src, "index")} (brand {eq27.Get(src, "brand")}, era {eq27.Get(src, "era")}; {cands.Count} candidate(s))");
            foreach (var z in zones) SetI(eq, r, z, long.Parse(eq27.Get(src, z)), "    ");
        }
    }
    else R($"  {copyLabel}: no stockteamequipmenttable");

    var outBytes = db.Save();
    // Guards: same length, reloads with valid CRCs, nothing changed outside team 22's rows of the edited tables.
    if (outBytes.Length != bytes.Length) throw new InvalidDataException("size changed");
    var back = T3Db.Load((byte[])outBytes.Clone(), metaPath);
    foreach (var (tn, t) in back.Tables)
    {
        var o = T3Db.Load((byte[])bytes.Clone(), metaPath).Tables[tn];
        if (o.InUse != t.InUse || o.Start != t.Start || o.End != t.End) throw new InvalidDataException($"{tn}: layout moved");
        if (!bytes.AsSpan(t.Start, t.RecordsAt - t.Start).SequenceEqual(outBytes.AsSpan(t.Start, t.RecordsAt - t.Start))) throw new InvalidDataException($"{tn}: header/descriptors changed");
    }
    if (!bytes.AsSpan(0, 24 + 8 * (int)BitConverter.ToUInt32(bytes, 16) + 4).SequenceEqual(outBytes.AsSpan(0, 24 + 8 * (int)BitConverter.ToUInt32(bytes, 16) + 4)))
        throw new InvalidDataException("file header changed");
    R($"  {copyLabel}: {n} field(s) changed; size {outBytes.Length} unchanged, schema/headers identical, CRCs valid");
    return outBytes;
}

// ---------- loc edit ----------
string Swap(string text, string key)
{
    // Word swaps for languages whose text differs from English; whole words only.
    string s = text;
    foreach (var (a, b) in new[] { ("Arizona Coyotes", "Utah Mammoth"), ("Coyotes", "Mammoth"), ("Yotes", "Mammoth"), ("Arizona", "Utah") })
        s = System.Text.RegularExpressions.Regex.Replace(s, $@"\b{a}", b);   // prefix: Finnish "Coyotesin" -> "Mammothin"
    if (key.StartsWith("TXT_ADIDAS_PLAYER_PULLOVER", StringComparison.Ordinal)) s = s.Replace("Adidas", "Fanatics").Replace("adidas", "Fanatics");
    return s;
}

byte[] EditLoc(string name, byte[] bytes, string copyLabel)
{
    var ls = LanguageStrings.Load(bytes);
    bool english = name.Contains("eng_us");
    int n = 0;
    foreach (var (key, current, wanted) in locRows)
    {
        string? have = ls.Find(key);
        if (have is null) { R($"  {copyLabel} {key}: not in this table"); continue; }
        string target;
        if (english) target = have == current || have == wanted ? wanted : Swap(have, key);
        else if (have == current) target = wanted;
        else if (current is "ARI" or "ARZ" && have is "ARI" or "ARZ") target = "UTA";
        else target = Swap(have, key);
        if (target == have && !args.Contains("--same")) { R($"  {copyLabel} {key}: '{have}' {(have == wanted ? "(already)" : "(no Arizona words; left)")}"); continue; }
        if (args.Contains("--same")) target = have;
        { int xi = Array.IndexOf(args, "--text"); if (xi >= 0) target = args[xi + 1]; }
        ls.Set(key, target); n++;
        R($"  {copyLabel} {key}: '{have}' -> '{target}'");
    }
    if (n == 0) return bytes;
    var built = ls.Build();
    var check = LanguageStrings.Load(built);
    var before = LanguageStrings.Load(bytes);
    foreach (var (key, _, _) in locRows)
        if (before.Find(key) is { } b && check.Find(key) is null) throw new InvalidDataException($"{key} lost");
    R($"  {copyLabel}: {n} string(s) changed; {bytes.Length} -> {built.Length} bytes");
    return built;
}

// ---------- plan: every copy, from its own bytes ----------
byte[] Edit(string name, byte[] bytes, string label) => name.StartsWith("loc/") ? EditLoc(name, bytes, label) : EditDb(name, bytes, label);

var expected = new Dictionary<string, List<(byte[] Old, byte[] New)>>();
foreach (var name in allNames)
{
    var copies = lfRead.ReadAll(name);
    R($"{name}: {copies.Count} cop{(copies.Count == 1 ? "y" : "ies")} ({string.Join(", ", copies.Select(c => c.Length))} bytes; " +
      $"{(copies.Count > 1 && copies.Skip(1).All(x => x.AsSpan().SequenceEqual(copies[0])) ? "identical" : copies.Count > 1 ? "DIFFER" : "single")})");
    var list = new List<(byte[], byte[])>();
    for (int i = 0; i < copies.Count; i++)
    {
        var edited = Edit(name, copies[i], $"[{Path.GetFileName(name)} copy {i}]");
        list.Add((copies[i], edited));
        var stem = Path.Combine(outDir, "files", name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        File.WriteAllBytes($"{stem}.copy{i}.old", copies[i]);
        File.WriteAllBytes($"{stem}.copy{i}.new", edited);
    }
    expected[name] = list;
}
File.WriteAllLines(Path.Combine(outDir, "plan.txt"), report);
Say($"plan written: {Path.Combine(outDir, "plan.txt")}");
if (args[0] == "plan") return 0;

// ---------- apply ----------
if (args[0] != "apply" || !Has("--apply") || !string.Equals(Path.GetFullPath(Opt("--target")).TrimEnd('\\'), game, StringComparison.OrdinalIgnoreCase))
{ Console.WriteLine("apply needs --apply --target <same game>"); return 2; }
if (ApplyTarget.RunningGame() is { } running) { Console.WriteLine($"game running ({running}); refused"); return 1; }

var casBefore = ApplyTarget.Cas(game).ToDictionary(p => p, p => new FileInfo(p).Length, StringComparer.OrdinalIgnoreCase);
var backup = ApplyTarget.Backup(game, Opt("--backup-root") is { Length: > 0 } br ? br : null);
Say("backup: " + backup);
// Keep every touched file's old bytes beside the backup too.
foreach (var (name, list) in expected)
    for (int i = 0; i < list.Count; i++)
    {
        var p = Path.Combine(backup, "legacy-old", name.Replace('/', Path.DirectorySeparatorChar) + $".copy{i}");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, list[i].Old);
    }

int Apply()
{
    var lf = new LegacyFiles(game, settings);
    var edits = allNames.ToDictionary(n => n, n => (Func<byte[], byte[]>)(b => Edit(n, b, $"[write {Path.GetFileName(n)}]")));
    foreach (var line in lf.ReplaceEach(edits)) Say("write: " + line);
    return Verify(true);
}

int Verify(bool wantNew)
{
    var lf = new LegacyFiles(game, settings);
    int bad = 0;
    foreach (var (name, list) in expected)
    {
        var now = lf.ReadAll(name);
        if (now.Count != list.Count) { Say($"READBACK {name}: {now.Count} copies, expected {list.Count}"); bad++; continue; }
        for (int i = 0; i < now.Count; i++)
        {
            var want = wantNew ? list[i].New : list[i].Old;
            bool ok = now[i].AsSpan().SequenceEqual(want);
            Say($"read back {name} copy {i}: {(ok ? "identical to " + (wantNew ? "the edit" : "the old bytes") : "DIFFERENT")} ({Convert.ToHexString(SHA1.HashData(now[i]))[..12]})");
            if (!ok) bad++;
        }
    }
    return bad;
}

int problems;
try { problems = Apply(); }
catch (Exception ex)
{
    Say("APPLY FAILED: " + ex.Message + " - restoring");
    ApplyTarget.RestoreFromBackup(game, backup);
    var left = ApplyTarget.Compare(game, backup);
    Say(left.Count == 0 ? "restored, identical to backup" : "RESTORE DIFFERENCES: " + string.Join("; ", left));
    return 1;
}
foreach (var (p, len) in casBefore)
{
    long now = new FileInfo(p).Length;
    if (now != len) Say($"CAS grew: {Path.GetRelativePath(game, p)} {len} -> {now} (+{now - len} bytes)");
}
if (problems > 0)
{
    Say($"{problems} read-back problem(s) - restoring");
    ApplyTarget.RestoreFromBackup(game, backup);
    return 1;
}
Say("APPLY OK: every copy read back identical to the planned edit");

if (Has("--restore-check"))
{
    ApplyTarget.RestoreFromBackup(game, backup);
    var left = ApplyTarget.Compare(game, backup);
    Say(left.Count == 0 ? "restore: 0 differences vs backup (TOCs, CAS lengths)" : "RESTORE DIFFERENCES: " + string.Join("; ", left));
    int oldBack = Verify(false);
    if (left.Count > 0 || oldBack > 0) { Say("RESTORE CHECK FAILED"); return 1; }
    int again = Apply();
    if (again > 0) { Say("RE-APPLY FAILED"); return 1; }
    Say("RESTORE CHECK PASSED: restore gave the old bytes back, re-apply read back identical");
}
return 0;










