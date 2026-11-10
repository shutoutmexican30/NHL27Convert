// EquipmentCorrect --target <game> --mod <installed-mod.json> --equipment-source <csv-folder>
//   [--report <workspace-folder>] [--backups <folder>] [--apply] [--allow-live] [--verify-restore]
// Dry-run by default. Changes only stockteamequipmenttable rows for installed variants 24/25.
using System.Text.Json;
using Suite.Convert27;
using Suite.Core;

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
void Say(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
const string TestRoot = @"D:\CIMS-test\";
string workspace = Directory.GetCurrentDirectory();
string target = Path.GetFullPath(Opt("--target") ?? throw new ArgumentException("--target is required")).TrimEnd(Path.DirectorySeparatorChar);
string modPath = Path.GetFullPath(Opt("--mod") ?? throw new ArgumentException("--mod is required"));
string source27 = Path.GetFullPath(Opt("--equipment-source") ?? throw new ArgumentException("--equipment-source is required"));
bool apply = WriteGate.Allowed(args);
bool isTestCopy = target.StartsWith(TestRoot, StringComparison.OrdinalIgnoreCase);
if (apply && !isTestCopy && !args.Contains("--allow-live"))
{
    Say("REFUSED: this is not the isolated test copy. --allow-live is valid only after corrected in-game approval.");
    return 1;
}
if (args.Contains("--verify-restore") && (!apply || !isTestCopy))
{
    Say("REFUSED: --verify-restore requires --apply on the isolated test copy.");
    return 1;
}
string reportDir = Path.GetFullPath(Opt("--report") ?? Path.Combine(workspace, "Debugging", "fanatics-equipment-correction"));
string workspacePrefix = workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (!reportDir.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("The equipment report must stay inside the workspace.");
Directory.CreateDirectory(reportDir);
if (ApplyTarget.RunningGame() is { } running)
{
    Say("REFUSED: " + running + " is running.");
    return 1;
}
var mod = ModFile.Load(modPath) ?? throw new InvalidDataException("Could not load the installed uniform mod.");
var additions = mod.Additions ?? throw new InvalidDataException("The mod has no new uniforms.");
if (apply && (additions.Count != 64 || additions.Select(a => a.Team).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 32))
    throw new InvalidDataException("An equipment apply requires the complete 64-uniform installed set.");
var settings = SuiteSettings.Load();
var plan = InstalledEquipmentCorrection.Build(target, settings, source27, additions, Say);
var changes = plan.Changes;
var report = new
{
    Target = target,
    EquipmentSource = source27,
    Uniforms = additions.Count,
    Teams = additions.Select(a => a.TeamId).Distinct().Count(),
    PlannedRows = plan.Equipment.Rows.Count,
    InsertedRows = plan.Equipment.InsertedRows,
    ChangedDatabases = changes.Keys.Order().ToArray(),
    SelectionCounts = plan.Equipment.Rows.GroupBy(r => r.Selection).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
    Fallbacks = plan.Equipment.Rows.Where(r => r.UsesDefault || r.UsesWildcard)
        .Select(r => new { r.Database, r.Team, r.TeamName, r.Kit, r.Equipment, r.Brand, r.Selection, r.Source, r.SourceVariant, r.TargetVariant }).ToArray(),
    Rows = plan.Equipment.Rows,
};
string reportPath = Path.Combine(reportDir, apply ? "applied-plan.json" : "equipment-plan.json");
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Say($"Plan: {plan.Equipment.Rows.Count} rows, {plan.Equipment.InsertedRows} inserts across both databases, "
    + $"{changes.Count} changed legacy files. Every row includes its donor, candidates, and 15 zones. Report: {reportPath}");
if (!apply)
{
    Say("DRY RUN COMPLETE: no game files were changed.");
    return 0;
}
string backupRoot = Opt("--backups") ?? (isTestCopy ? Path.Combine(TestRoot, "fullkit-backups") : Path.Combine(workspace, "Debugging", "live-equipment-backups"));
string? backup = InstalledEquipmentCorrection.Apply(target, settings, plan, backupRoot, args.Contains("--verify-restore"), Say);
Say(backup is null ? "No equipment correction was necessary." : "Equipment correction complete. Backup: " + backup);
return 0;
