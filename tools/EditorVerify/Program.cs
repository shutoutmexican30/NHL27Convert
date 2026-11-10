using Suite.Convert27;
using Suite.Core;
using Suite.Rosters;

static string Argument(string[] args, string name, string fallback)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
}

string target = Argument(args, "--target", @"D:\CIMS-test\play");
string modPath = Argument(args, "--mod", Path.Combine(Environment.CurrentDirectory, "Debugging", "installed-fanatics-mod.json"));
bool restore = args.Contains("--verify-restore");
var settings = SuiteSettings.Load();
settings.UseGameBuild(target, target); // memory only: do not change the user's selected build
var session = InstalledEquipmentEditor.Load(target, settings);
var noOp = session.Document.BuildPlan();
Console.WriteLine($"READ-ONLY PREFLIGHT PASSED: {noOp.Databases.Count} databases; no pending changes: {!noOp.HasChanges}.");
if (restore)
{
    var row = session.Document.Rows(team: 7, equipment: 4, variant: 25).FirstOrDefault(r => session.Document.IsPaired(r.Key))
        ?? throw new InvalidDataException("Detroit Fanatics home helmet has no exactly paired row in both databases.");
    var zones = row.Zones.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    zones["zone1"] = (zones["zone1"] + 1) & 0xFFFFFF;
    session.Document.SetZones(row.Key, zones);
    var plan = session.Document.BuildPlan();
    Console.WriteLine($"TEMPORARY TEST EDIT: {row.Key.Summary}, zone1 #{zones["zone1"]:X6}; {plan.ChangedRows} changed rows across both databases.");
    InstalledEquipmentEditor.Apply(settings, session, plan, Console.WriteLine, verifyRestore: true);
}
if (args.Contains("--slots"))
{
    var mod = ModFile.Load(modPath) ?? throw new FileNotFoundException("Uniform mod not found", modPath);
    string artDir = Argument(args, "--art-dir", SuitePaths.Nhl27Images);
    var index = Suite.Core.Frostbite.BundleIndex.Build(target, Console.WriteLine);
    var slots = UniformSlotInspection.Build(target, index, mod.Additions ?? [],
        path => InstalledBrandingAudit.ResolveArtwork(path, artDir), settings.Workers, Console.WriteLine);
    Console.WriteLine($"ORDERED SLOT AND PIXEL AUDIT: {slots.Rows.Count} installed color/normal array copies; "
        + $"{slots.IntendedSponsors} intended front sponsors; {slots.Errors.Count} problems; valid: {slots.IsValid}.");
    foreach (string error in slots.Errors.Take(30)) Console.WriteLine("SLOT: " + error);
    string report = Path.Combine(Environment.CurrentDirectory, "Debugging", "equipment-live-slot-inspection.json");
    File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(slots,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine("Read-only inspection report: " + report);
    if (!slots.IsValid) return 2;
}
return 0;
