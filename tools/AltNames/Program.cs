using Suite.Convert27;
// AltNames --target <copy under D:\CIMS-test> --names <names27.tsv> [--apply]
// Gives the installed NHL 27 alternates NHL 27's own jersey names. Per kit (team id, NHL 24 variant): the jersey row's
// locstring (both databases), and for per-kit names (6) the English text of VINTAGE_2_<teamartid>_<variant> (changed in
// place when the key exists, added when not). Only those rows and keys change. Backup first, every copy read back,
// restore on any failure. Without --apply nothing is written.
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
void Say(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");

const string TestRoot = @"D:\CIMS-test\";
string target = Path.GetFullPath(Opt("--target") ?? throw new ArgumentException("--target is required")).TrimEnd('\\');
bool apply = args.Contains("--apply");
if (!target.StartsWith(TestRoot, StringComparison.OrdinalIgnoreCase) || target.Contains("prospects-copy", StringComparison.OrdinalIgnoreCase))
{ Say("REFUSED: the target must be this task's own copy under D:\\CIMS-test (not prospects-copy)."); return 1; }
if (apply && ApplyTarget.RunningGame() is { } running) { Say("REFUSED: " + running + " is running."); return 1; }

if (Opt("--set-active") is { } activeSpec)
{
    // --set-active <teamid>:<v,v,...>:<0|1>  jersey rows of that team/style 2/variants get active=0|1 in both databases.
    // Rows are kept (hidden, not deleted), so the table keeps its shape and the change undoes by setting 1 again.
    var sp = activeSpec.Split(':');
    int teamId = int.Parse(sp[0]), value = int.Parse(sp[2]);
    var variants = sp[1].Split(',').Select(int.Parse).ToHashSet();
    var cfg1 = SuiteSettings.Load();
    Oodle.Ensure(cfg1);
    var lf = new LegacyFiles(target, cfg1);
    string meta1 = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "meta-cache")), "nhl_game-meta.xml");
    File.WriteAllBytes(meta1, lf.Read("gamemodes/database/t3db/nhl_game-meta.xml"));
    var outFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (string db in new[] { "nhl_game", "initial_experience" })
    {
        string file = RosterSyncRun.LegacyName(db);
        var before = lf.Read(file);
        var t3 = T3Db.Load((byte[])before.Clone(), meta1);
        var t = t3.Tables["stockteamjerseys"];
        foreach (int r in Enumerable.Range(0, t.InUse).Where(r => t3.GetInt(t, r, "teamid") == teamId && t3.GetInt(t, r, "style") == 2
                     && variants.Contains((int)t3.GetInt(t, r, "variant"))))
        {
            if (t3.GetInt(t, r, "isdefault") == 1) throw new InvalidDataException($"{db}: variant {t3.GetInt(t, r, "variant")} is a default kit; refusing to hide it.");
            Say($"{db}: team {teamId} variant {t3.GetInt(t, r, "variant")} active {t3.GetInt(t, r, "active")} -> {value}");
            t3.SetInt(t, r, "active", value);
        }
        var saved = t3.Save();
        if (saved.Length != before.Length) throw new InvalidDataException(db + ": size changed");
        if (!saved.AsSpan().SequenceEqual(before)) outFiles[file] = saved;
    }
    if (!apply || outFiles.Count == 0) { Say(apply ? "Nothing to change." : "DRY RUN: nothing was written."); return 0; }
    if (ApplyTarget.RunningGame() is { } run1) { Say("REFUSED: " + run1 + " is running."); return 1; }
    string bk1 = ApplyTarget.Backup(target, TestRoot + "fullkit-backups");
    Say("backup: " + bk1);
    try
    {
        foreach (string line in lf.Replace(outFiles)) Say("DB: " + line);
        var fresh = new LegacyFiles(target, cfg1);
        foreach (var (file, bytes) in outFiles)
            if (fresh.ReadAll(file, everyToc: true) is var copies && (copies.Count == 0 || copies.Any(c => !c.AsSpan().SequenceEqual(bytes))))
                throw new InvalidDataException(file + ": a copy did not read back");
        Say("ACTIVE FLAGS WRITTEN AND READ BACK. Backup: " + bk1);
        return 0;
    }
    catch (Exception ex)
    {
        Say("FAILED: " + ex.Message + " - restoring");
        ApplyTarget.RestoreFromBackup(target, bk1);
        return 2;
    }
}
if (args.Contains("--check"))
{
    // Read-only KIT CHECK of every installed alternate (names file) and the 64 Fanatics kits (variants 24/25).
    var cfg0 = SuiteSettings.Load();
    Oodle.Ensure(cfg0);
    var ix = BundleIndex.Build(target);
    var kits = new List<(string Team, string Word, string Own, string Label)>();
    var fanatics27 = new Dictionary<(string, string), string>(); // report-only cross-reference for the 64 Fanatics kits
    foreach (var l in File.ReadAllLines(Opt("--names") ?? throw new ArgumentException("--names is required")).Skip(1).Select(l => l.Split('\t')))
        kits.Add((l[0], TextureName.Numbers[int.Parse(l[2])], "home", "alt " + l[4]));
    foreach (var t in VariantMap.Load().Teams.Where(t => t.Index is >= 0 and <= 31))
    {
        kits.Add((t.Nhl24.Folder, "twentyfive", "home", "Fanatics home"));
        kits.Add((t.Nhl24.Folder, "twentyfour", "away", "Fanatics away"));
        fanatics27[(t.Nhl24.Folder, "twentyfive")] = t.Nhl27.Home ?? "";
        fanatics27[(t.Nhl24.Folder, "twentyfour")] = t.Nhl27.Away ?? "";
    }
    // NHL 27 set word per installed alternate, from the AltPort plan next to the names file.
    var set27 = new Dictionary<(string, string), string>();
    string planPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Opt("--names")!))!, "plan.tsv");
    if (File.Exists(planPath))
        foreach (var l in File.ReadAllLines(planPath).Skip(1).Select(l => l.Split('\t')).Where(l => l.Length >= 8 && int.TryParse(l[7], out _)))
            set27[(l[0], TextureName.Numbers[int.Parse(l[7])])] = l[3];
    int fails = 0, donorLeaks = 0;
    foreach (var k in kits)
    {
        string prefix = $"content/character/skater/league/nhl/{k.Team}/adidas/{k.Word}";
        if (!ix.EbxByName.Keys.Any(n => n.StartsWith(prefix + "/", StringComparison.Ordinal))) continue;
        bool? sponsor = null;
        if ((k.Label.StartsWith("alt") ? set27 : fanatics27).TryGetValue((k.Team, k.Word), out var s27) && s27.Length > 0)
        {
            string team27 = VariantMap.Load().Teams.First(t => t.Nhl24.Folder == k.Team && t.Index is >= 0 and <= 31).Nhl27.Folder;
            if (Nhl27Stamps.Xml($"content/character/skater/league/nhl/{team27}/adidas/{s27}") is { } x27)
                sponsor = x27.Contains("name=\"sponsor\"", StringComparison.Ordinal)
                    || System.Text.RegularExpressions.Regex.IsMatch(x27, "type=\"brand\"[^>]*?name=\"[a-z_]*sponsor");
        }
        var issues = FanaticsKitCheck.Check(target, ix, prefix, k.Team, k.Own, k.Label.StartsWith("alt"), sponsor);
        if (issues.Count == 0) continue;
        fails++;
        if (issues.Any(i => i.Contains("donor's Anaheim"))) donorLeaks++;
        Say($"FAIL {k.Team} {k.Word} ({k.Label}): {string.Join("; ", issues)}");
    }
    Say($"checked {kits.Count} kits: {fails} fail, {donorLeaks} with donor (Anaheim) art left");
    return 0;
}
if (Opt("--fix-sponsor") is { } fixKit)
{
    // --fix-sponsor team/word: the installed kit's jersey stamp takes NHL 27's sponsor spot (any NHL 27 brand spot whose
    // name contains "sponsor", e.g. Detroit alternate 2's right_shoulder_sponsor), written in place in every copy.
    var parts = fixKit.Split('/');
    string folder = $"content/character/skater/league/nhl/{parts[0]}/adidas/{parts[1]}";
    string stamp = $"{folder}/stamp_jersey_adidas_{parts[0]}_{parts[1]}";
    string xml = Nhl27Stamps.Xml(folder) ?? throw new InvalidDataException("No NHL 27 stamp for " + folder);
    if (!xml.Contains("name=\"sponsor\"", StringComparison.Ordinal))
    {
        var m = System.Text.RegularExpressions.Regex.Match(xml, "type=\"brand\"[^>]*?name=\"([a-z_]*sponsor[a-z_]*)\"");
        if (!m.Success) throw new InvalidDataException("NHL 27 has no sponsor spot on " + folder);
        xml = xml.Replace($"name=\"{m.Groups[1].Value}\"", "name=\"sponsor\"", StringComparison.Ordinal);
    }
    var cfg = SuiteSettings.Load();
    Oodle.Ensure(cfg);
    var idx = BundleIndex.Build(target);
    var jobs = new List<BrandingStampJob>();
    foreach (var copy in idx.EbxByName.GetValueOrDefault(stamp) ?? [])
    {
        byte[] before = UniformPhysicalLayouts.Decode(BundleIndex.ReadBytes(copy.Data));
        byte[] after = UniformHotspots.CopyBrandSpotInPlace(before, xml, "sponsor") ?? throw new InvalidDataException("NHL 27's sponsor spot does not fit " + stamp);
        jobs.Add(new BrandingStampJob(parts[0], parts[1], stamp, stamp, copy.Bundle.Toc, copy.Bundle.RecordAt, before, after));
    }
    if (jobs.Count == 0) throw new InvalidDataException("Installed stamp not found: " + stamp);
    Say($"{stamp}: {jobs.Count} cop(ies) planned");
    if (!apply) { Say("DRY RUN: nothing was written."); return 0; }
    string bk = ApplyTarget.Backup(target, TestRoot + "fullkit-backups");
    Say("backup: " + bk);
    try
    {
        InstalledHotspotCorrection.Write(target, idx, jobs, Say);
        InstalledHotspotCorrection.Verify(target, jobs, before: false);
        var after = BundleIndex.Build(target);
        var issues = FanaticsKitCheck.Check(target, after, folder, parts[0], "home", nhl27Numbers: true);
        Say($"KIT CHECK {(issues.Count == 0 ? "OK" : "FAIL: " + string.Join("; ", issues))} {parts[0]} {parts[1]}");
    }
    catch (Exception ex)
    {
        Say("FAILED: " + ex.Message + " - restoring");
        ApplyTarget.RestoreFromBackup(target, bk);
        Say($"restored: {ApplyTarget.Compare(target, bk).Count} difference(s) left");
        return 2;
    }
    return 0;
}
var names = File.ReadAllLines(Opt("--names") ?? throw new ArgumentException("--names is required")).Skip(1)
    .Select(l => l.Split('\t')).Where(p => p.Length >= 5)
    .Select(p => (Team: p[0], TeamId: int.Parse(p[1]), Variant: int.Parse(p[2]), Loc: int.Parse(p[3]), Name: p[4])).ToList();

var settings = SuiteSettings.Load();
Oodle.Ensure(settings);
var legacy = new LegacyFiles(target, settings);
string meta = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "meta-cache")), "nhl_game-meta.xml");
File.WriteAllBytes(meta, legacy.Read("gamemodes/database/t3db/nhl_game-meta.xml"));
const string English = "loc/eng_us.db";
var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
var keys = new Dictionary<string, string>(StringComparer.Ordinal);
int rowChanges = 0;
foreach (string db in new[] { "nhl_game", "initial_experience" })
{
    string file = RosterSyncRun.LegacyName(db);
    var before = legacy.Read(file);
    var t3 = T3Db.Load((byte[])before.Clone(), meta);
    var t = t3.Tables["stockteamjerseys"];
    foreach (var n in names)
    {
        var rows = Enumerable.Range(0, t.InUse).Where(r => t3.GetInt(t, r, "teamid") == n.TeamId && t3.GetInt(t, r, "style") == 2
            && t3.GetInt(t, r, "variant") == n.Variant).ToList();
        if (rows.Count != 1) throw new InvalidDataException($"{db}: {n.Team} variant {n.Variant} has {rows.Count} jersey rows (install the kits first).");
        if (t3.GetInt(t, rows[0], "locstring") != n.Loc) { t3.SetInt(t, rows[0], "locstring", n.Loc); rowChanges++; }
        if (n.Loc == 6)
        {
            string key = $"VINTAGE_2_{t3.GetInt(t, rows[0], "teamartid")}_{n.Variant}";
            if (keys.TryGetValue(key, out var other) && other != n.Name) throw new InvalidDataException($"{key}: two names ({other} / {n.Name}).");
            keys[key] = n.Name;
        }
    }
    var saved = t3.Save();
    if (saved.Length != before.Length) throw new InvalidDataException($"{db}: database size changed.");
    if (!saved.AsSpan().SequenceEqual(before)) files[file] = saved;
}
var strings = LanguageStrings.Load(legacy.Read(English));
int changed = 0, added = 0, same = 0;
foreach (var (key, text) in keys)
{
    string? now = strings.Find(key);
    if (now == text) { same++; continue; }
    if (now is null) { strings.Add(key, text); added++; }
    else { strings.Set(key, text); changed++; }
}
if (changed + added > 0)
{
    var built = strings.Build();
    if (built.Length % 8 != 0) throw new InvalidDataException("English table size is not a multiple of 8.");
    var check = LanguageStrings.Load(built);
    foreach (var (key, text) in keys) if (check.Find(key) != text) throw new InvalidDataException("English table did not read back: " + key);
    files[English] = built;
}
Say($"jersey rows: {rowChanges} locstring change(s) across both databases; English: {changed} renamed, {added} added, {same} already right");
foreach (var (key, text) in keys) Say($"  {key} = {text}");
if (!apply || files.Count == 0) { Say(apply ? "Nothing to write." : "DRY RUN: nothing was written."); return 0; }

string backup = ApplyTarget.Backup(target, TestRoot + "fullkit-backups");
Say("backup: " + backup);
try
{
    foreach (string line in legacy.Replace(files)) Say("DB: " + line);
    var fresh = new LegacyFiles(target, settings);
    foreach (var (file, bytes) in files)
    {
        var copies = fresh.ReadAll(file, everyToc: true);
        if (copies.Count == 0 || copies.Any(c => !c.AsSpan().SequenceEqual(bytes))) throw new InvalidDataException($"{file}: a copy did not read back ({copies.Count} copies).");
        Say($"read back {file}: {copies.Count} cop{(copies.Count == 1 ? "y" : "ies")} identical");
    }
}
catch (Exception ex)
{
    Say("FAILED: " + ex.Message + " - restoring");
    ApplyTarget.RestoreFromBackup(target, backup);
    var left = ApplyTarget.Compare(target, backup);
    Say($"restored: {left.Count} difference(s) left");
    return 2;
}
Say($"NAMES WRITTEN AND READ BACK. Backup: {backup}");
return 0;
