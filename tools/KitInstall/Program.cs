// KitInstall --target <game> --mod <mod.json> [--teams a,b] [--kits home,away] [--art-dir <folder>]
//   [--backups <folder>] [--restore-first <backup>] [--compare-only] [--verify-restore] [--workers N] [--apply] [--allow-live]
//   [--equipment-source <csv-folder>] (defaults to the packaged data/nhl27-equipment directory)
// Whole-kit uniform install: same-team clone + converted art + player_team_brt registration + jersey and equipment rows.
// Without --apply nothing is written. The original game folder needs --allow-live as well.
using Suite.Convert27;
using Suite.Core;
using Suite.Core.Frostbite;

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
void Say(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");

const string TestRoot = @"D:\CIMS-test\";
string target = Path.GetFullPath(Opt("--target") ?? throw new ArgumentException("--target is required")).TrimEnd(Path.DirectorySeparatorChar);
bool apply = WriteGate.Allowed(args);
bool isTestCopy = target.StartsWith(TestRoot, StringComparison.OrdinalIgnoreCase);
if (apply && !isTestCopy && !args.Contains("--allow-live")) { Say("REFUSED: this is not the test copy. Pass --allow-live only after the kits are confirmed in game."); return 1; }
if (args.Contains("--verify-restore") && (!apply || !isTestCopy)) { Say("REFUSED: --verify-restore requires --apply and the isolated test copy."); return 1; }

if (args.Contains("--compare-only"))
{
    string comparisonBackup = Opt("--restore-first") ?? throw new ArgumentException("--compare-only requires --restore-first <backup>.");
    var differences = ApplyTarget.Compare(target, comparisonBackup);
    foreach (string difference in differences) Say(difference);
    Say($"COMPARE ONLY: {differences.Count} difference(s) from {comparisonBackup}. Nothing was written.");
    return differences.Count == 0 ? 0 : 2;
}

var mod = ModFile.Load(Opt("--mod") ?? throw new ArgumentException("--mod is required")) ?? throw new InvalidDataException("The mod could not be loaded.");
if (Opt("--teams") is { } teams)
{
    var wanted = teams.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    mod.Additions = (mod.Additions ?? []).Where(a => wanted.Contains(a.Team)).ToList();
}
if (Opt("--kits") is { } kits)
{
    var wanted = kits.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    mod.Additions = (mod.Additions ?? []).Where(a => wanted.Contains(a.SourceKit)).ToList();
}
if (mod.Additions is not { Count: > 0 }) { Say("No uniforms selected."); return 1; }
Say($"{mod.Additions.Count} uniform(s): {string.Join(", ", mod.Additions.Select(a => $"{a.Team} {a.SourceKit}->{a.TargetVariant}"))}");

if (Opt("--restore-first") is { } clean)
{
    if (!apply) { Say($"(dry run) would restore {clean} first; {ApplyTarget.Compare(target, clean).Count} difference(s) now"); }
    else
    {
        ApplyTarget.RestoreFromBackup(target, clean);
        var left = ApplyTarget.Compare(target, clean);
        Say($"restored {clean}: {left.Count} difference(s) left");
        if (left.Count > 0) { foreach (var l in left.Take(10)) Say("  " + l); return 1; }
    }
}

var settings = SuiteSettings.Load();
string art = Opt("--art-dir") ?? @"C:\dev\NHL27Convert\Debugging\new-uniform-integration\cache\nhl27png";
int workers = Opt("--workers") is { } value ? int.Parse(value) : Math.Min(8, Environment.ProcessorCount);
return FullKitInstall.Run(new FullKitInstall.Options(target, mod, art,
    Opt("--backups") ?? (isTestCopy ? TestRoot + "fullkit-backups" : null), apply, Say, workers,
    CutExtraLayers: args.Contains("--cut-extra-layers"),
    JerseyName: Opt("--jersey-name") ?? FullKitInstall.DefaultJerseyName,
    RestoreAfterVerify: args.Contains("--verify-restore"),
    EquipmentSource: Opt("--equipment-source")), settings);
