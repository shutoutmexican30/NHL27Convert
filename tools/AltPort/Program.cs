// AltPort --target <test copy under D:\CIMS-test> [--teams a,b] [--plan-only] [--apply]
// NHL 27 alternates that NHL 24 does not have, for the 32 NHL clubs: lists them (NHL 27 stockteamjerseys vs the target's
// nhl_game), builds each one with the app's own steps (preflight, complete arrays, Anaheim donor, NHL 24 fabric rules),
// then installs them with FullKitInstall (backup, kit data in a new kit CAS file, read-back, KIT CHECK).
// Existing rows are never changed: every kit is a new variant row. Without --apply nothing is written to the game.
using System.IO;
using System.Text.Json;
using Suite.Core.Frostbite;
using Suite.Convert27;
using Suite.Core;
using Suite.Rosters;

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
void Say(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");

const string TestRoot = @"D:\CIMS-test\";
string target = Path.GetFullPath(Opt("--target") ?? throw new ArgumentException("--target is required")).TrimEnd('\\');
bool apply = args.Contains("--apply");
if (!target.StartsWith(TestRoot, StringComparison.OrdinalIgnoreCase) || target.Contains("prospects-copy", StringComparison.OrdinalIgnoreCase))
{ Say("REFUSED: the target must be this task's own copy under D:\\CIMS-test (not prospects-copy)."); return 1; }
if (Opt("--restore-first") is { } clean)
{
    // Back to a suite backup of this copy: TOCs and CAS lengths from the backup, and kit CAS files the backup does not
    // know (made by the install being undone) removed, so the copy compares equal to the backup.
    if (!apply) { Say($"(dry run) would restore {clean} first; {ApplyTarget.Compare(target, clean).Count} difference(s) now"); }
    else
    {
        if (ApplyTarget.RunningGame() is { } running) { Say("REFUSED: " + running + " is running."); return 1; }
        ApplyTarget.RestoreFromBackup(target, clean);
        var known = ApplyTarget.LoadSnapshot(clean).CasLengths.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string cas in ApplyTarget.Cas(target).Where(c => !known.Contains(Path.GetRelativePath(GameLayout.Normalize(target), c))).ToList())
        {
            if (!cas.Contains(@"\superbundlelayout\", StringComparison.OrdinalIgnoreCase)) { Say("REFUSED: unexpected new file " + cas); return 1; }
            File.Delete(cas);
            Say("removed kit file " + cas);
        }
        var left = ApplyTarget.Compare(target, clean);
        Say($"restored {clean}: {left.Count} difference(s) left");
        if (left.Count > 0) { foreach (var l in left.Take(10)) Say("  " + l); return 1; }
    }
    if (args.Contains("--restore-only")) return 0;
}
string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string outDir = Path.Combine(repo, "Debugging", "altport");
Directory.CreateDirectory(outDir);

var settings = SuiteSettings.Load();
Oodle.Ensure(settings);
var variants = VariantMap.Load();
var legacy = new LegacyFiles(target, settings);
string meta = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "meta-cache")), "nhl_game-meta.xml");
File.WriteAllBytes(meta, legacy.Read("gamemodes/database/t3db/nhl_game-meta.xml"));
var db24 = T3Db.Load(legacy.Read("gamemodes/database/t3db/nhl_game.db"), meta);
var t24 = db24.Tables["stockteamjerseys"];
var have = Enumerable.Range(0, t24.InUse).Where(r => db24.GetInt(t24, r, "style") == 2)
    .Select(r => ((int)db24.GetInt(t24, r, "teamid"), (int)db24.GetInt(t24, r, "variant"))).ToHashSet();

// NHL 27 rows (the CSV dump of NHL 27's nhl_game stockteamjerseys).
var csv = File.ReadAllLines(Path.Combine(repo, "reference", "nhl27-full-csv", "stockteamjerseys.csv"));
var head = csv[0].Split(',').Select(h => h.Trim('"')).ToList();
int C(string n) => head.IndexOf(n) is var i && i >= 0 ? i : throw new InvalidDataException("CSV has no column " + n);
var rows27 = csv.Skip(1).Select(l => l.Split(',').Select(v => v.Trim('"')).ToArray()).Where(v => v.Length == head.Count)
    .Select(v => new { Team = int.Parse(v[C("teamid")]), Style = int.Parse(v[C("style")]), Variant = int.Parse(v[C("variant")]),
        Active = v[C("active")] == "1", Light = v[C("islightcolor")] == "1", Loc = int.Parse(v[C("locstring")]),
        YearStart = int.Parse(v[C("yearstart")]), YearEnd = int.Parse(v[C("yearend")]) }).ToList();

// Asset folders already in the target (some NHL 24 variants have art but no jersey row, e.g. Tampa's "four").
var index24 = BundleIndex.Shared(target, s => { });
var folders24 = index24.ResByName.Keys.Concat(index24.EbxByName.Keys)
    .Where(n => n.StartsWith("content/character/skater/league/nhl/", StringComparison.Ordinal))
    .Select(n => string.Join('/', n.Split('/').Take(8))).ToHashSet(StringComparer.Ordinal);
var taken = new HashSet<(int, int)>(have);
int PickVariant(VariantMap.Team team, int preferred)
{
    bool Free(int v) => !taken.Contains((team.Index, v)) && !folders24.Contains($"content/character/skater/league/nhl/{team.Nhl24.Folder}/adidas/{TextureName.Numbers[v]}");
    int v = Free(preferred) ? preferred : Enumerable.Range(2, 24).Reverse().FirstOrDefault(Free);
    if (v < 2) throw new InvalidDataException($"{team.Nhl24.Folder}: no free variant (row and folder) in 2-25");
    taken.Add((team.Index, v));
    return v;
}
var wanted = Opt("--teams")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
var requests = new List<NewUniformExport.AlternateRequest>();
var plan = new List<string> { "team\tfolder24\tvariant27\tset27\tlight\tlocstring27\tlabel\ttargetVariant\tname" };
foreach (var team in variants.Teams.Where(t => t.Index is >= 0 and <= 31).OrderBy(t => t.Index))
{
    if (wanted is not null && !wanted.Contains(team.Nhl24.Folder)) continue;
    foreach (var r in rows27.Where(r => r.Team == team.Index && r.Style == 2 && r.Active).OrderBy(r => r.Variant))
    {
        if (r.Variant == team.Nhl27.HomeVariant || r.Variant == team.Nhl27.AwayVariant) continue; // Fanatics home/away: installed as 24/25
        if (have.Contains((team.Index, r.Variant))) continue;
        if (r.Variant is < 2 or > 25) { plan.Add($"{team.Nhl24.Folder}\t\t{r.Variant}\t\t{r.Light}\t{r.Loc}\tSKIPPED\t\tvariant outside 2-25"); continue; }
        string set27 = TextureName.Numbers[r.Variant];
        // Shared names where NHL 27 uses one (2 = Alternate; 12 is NHL 27-only and shows as Alternate here). A per-kit
        // NHL 27 name (6) needs its own English entry; NHL 27's own text is not on this PC, so the kit's seasons name it.
        int? loc = r.Loc == 6 ? 6 : 2;
        string? name = loc == 6
            ? (r.YearStart > 0 ? $"{r.YearStart}-{(r.YearEnd > 0 ? r.YearEnd % 100 : (r.YearStart + 1) % 100):D2} Alternate"
                : $"Alternate {2 + requests.Count(q => q.Team == team && q.LocString == 6)}")
            : null;
        int targetVariant = PickVariant(team, r.Variant);
        var req = new NewUniformExport.AlternateRequest(team, set27, r.Light, "alt-" + set27, targetVariant, loc, name);
        requests.Add(req);
        plan.Add($"{team.Nhl24.Folder}\t{team.Nhl24.Folder}\t{r.Variant}\t{set27}\t{r.Light}\t{r.Loc}\t{req.KitLabel}\t{targetVariant}\t{name ?? "(Alternate)"}");
    }
}
File.WriteAllLines(Path.Combine(outDir, "plan.tsv"), plan);
Say($"{requests.Count} NHL alternates missing in NHL 24 ({requests.Count(r => r.LocString == 6)} need their own name); plan: {Path.Combine(outDir, "plan.tsv")}");
if (args.Contains("--plan-only")) { foreach (var l in plan.Skip(1)) Console.WriteLine("  " + l); return 0; }

var vm = new Convert27ViewModel(settings);
await vm.LoadAsync();
var (built, failed) = await NewUniformExport.BuildAlternatesAsync(vm, requests, Say);
foreach (var a in built) a.Nhl27Numbers = true; // each alternate's own NHL 27 numbers (verified fit, else the team's NHL 24 sheet)
foreach (var f in failed) Say("NOT BUILT: " + f);
Say($"built {built.Count} of {requests.Count}");
if (built.Count == 0) return 2;
var mod = new ModFile { Name = $"NHL 27 alternates {DateTime.Now:yyyy-MM-dd HHmm}", Kind = NewUniformAddition.ModKind, Additions = built };
string modPath = Path.Combine(outDir, "alternates.mod.json");
File.WriteAllText(modPath, JsonSerializer.Serialize(mod, new JsonSerializerOptions { WriteIndented = true }));
Say("mod: " + modPath);

int rc = FullKitInstall.Run(new FullKitInstall.Options(target, mod, @"C:\dev\NHL27Convert\Debugging\new-uniform-integration\cache\nhl27png",
    TestRoot + "fullkit-backups", apply, Say, Math.Min(8, Environment.ProcessorCount),
    CutExtraLayers: true, JerseyName: "Alternate 2"), settings);
Say($"FullKitInstall exit {rc}; not built: {failed.Count}");
return rc;
