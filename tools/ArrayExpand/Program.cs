// ArrayExpand --target <game> --mod <installed-mod.json> [--teams a,b] [--report <workspace-folder>]
//   [--backups <folder>] [--workers N] [--apply] [--allow-live] [--verify-restore]
//   [--reviewed-six-kit-repair]
// Dry-run by default. Expands only insufficient existing arrays; never clones a uniform or writes a database.
// A targeted apply requires the explicit six-kit review switch.
using System.Text.Json;
using Suite.Convert27;
using Suite.Core;
using Suite.Core.Frostbite;

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
void Say(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
const string TestRoot = @"D:\CIMS-test\";
string workspace = Directory.GetCurrentDirectory();
string target = Path.GetFullPath(Opt("--target") ?? throw new ArgumentException("--target is required")).TrimEnd(Path.DirectorySeparatorChar);
string modPath = Path.GetFullPath(Opt("--mod") ?? throw new ArgumentException("--mod is required"));
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
string reportDir = Path.GetFullPath(Opt("--report") ?? Path.Combine(workspace, "Debugging", "fanatics-array-expansion"));
string workspacePrefix = workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (!reportDir.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("The expansion report must stay inside the workspace.");
Directory.CreateDirectory(reportDir);
if (ApplyTarget.RunningGame() is { } running)
{
    if (apply)
    {
        Say("REFUSED: " + running + " is running.");
        return 1;
    }
    Say(running + " is running: continuing with a read-only expansion plan. No game files will be changed.");
}
var mod = ModFile.Load(modPath) ?? throw new InvalidDataException("Could not load the installed uniform mod.");
var additions = mod.Additions ?? throw new InvalidDataException("The mod has no new uniforms.");
if (Opt("--teams") is { } teams)
{
    var wanted = teams.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    additions = additions.Where(a => wanted.Contains(a.Team)).ToList();
}
if (additions.Count == 0) throw new InvalidDataException("No uniforms were selected.");
bool completeSet = additions.Count == 64 && additions.Select(a => a.Team).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 32;
bool reviewedRepair = args.Contains("--reviewed-six-kit-repair") && additions.Count == 6
    && additions.Select(a => a.Team).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3;
if (apply && !completeSet && !reviewedRepair)
    throw new InvalidDataException("Apply requires either the complete 64-uniform set or an explicitly reviewed six-kit repair mod "
        + "containing both kits for exactly three teams. Use --reviewed-six-kit-repair only for that reviewed mod.");
int workers = Opt("--workers") is { } value ? int.Parse(value) : Math.Min(8, Environment.ProcessorCount);
var index = BundleIndex.Build(target, Say);
additions = RegisteredKitSource.Normalize(additions, index, Say);
var jobs = new List<InstalledArrayExpansionJob>();
var problems = new List<string>();
foreach (var addition in additions)
{
    Say($"Checking installed array capacity: {addition.Team} {addition.SourceKit}");
    try
    {
        var plan = InstalledArrayExpansion.BuildAddition(target, index, addition, workers);
        jobs.AddRange(plan.Jobs);
        foreach (var job in plan.Jobs)
            Say($"  {job.Role}: {job.Name} chunk {job.Chunk}: {job.OldLayers} -> {job.NewLayers} slots");
        if (plan.Jobs.Count == 0) Say("  All copies already have enough slots.");
    }
    catch (Exception ex)
    {
        problems.Add($"{addition.Team} {addition.SourceKit}: {ex.Message}");
        Say("BLOCKER: " + problems[^1]);
    }
}
var uniqueJobs = jobs.GroupBy(j => j.Chunk).Select(g => g.First()).ToArray();
var report = new
{
    Target = target,
    Uniforms = additions.Count,
    ExpandedResources = uniqueJobs.Length,
    Problems = problems,
    Jobs = uniqueJobs.Select(j => new
    {
        j.Team, j.Kit, j.Name, j.Role, j.Chunk, j.OldLayers, j.NewLayers,
        OldBytes = j.Before.Length, NewBytes = j.Data.Length,
        ResourceBytes = j.ResourceAfter.Length,
    }).ToArray(),
};
File.WriteAllText(Path.Combine(reportDir, "expansion-plan.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Say($"Plan: {uniqueJobs.Length} existing resources need expansion; {problems.Count} blocker(s). Report: {Path.Combine(reportDir, "expansion-plan.json")}");
if (problems.Count > 0) return 2;
if (!apply)
{
    Say("DRY RUN COMPLETE: no game files were changed.");
    return 0;
}
string backupRoot = Opt("--backups") ?? (isTestCopy ? Path.Combine(TestRoot, "array-backups") : Path.Combine(workspace, "Debugging", "live-array-backups"));
string? backup = InstalledArrayExpansion.Apply(target, new InstalledArrayExpansionPlan(uniqueJobs), backupRoot,
    args.Contains("--verify-restore"), Say, workers);
Say(backup is null ? "No expansion was necessary." : "Expansion complete. Backup: " + backup);
return 0;
