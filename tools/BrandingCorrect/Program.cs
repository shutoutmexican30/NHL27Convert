// BrandingCorrect --target <game> --mod <installed-mod.json> [--teams a,b] [--report <workspace-folder>]
//   [--backups <folder>] [--workers N] [--apply] [--allow-live] [--verify-restore]
//   [--reviewed-six-kit-repair]
// Dry-run by default. Rebuilds only existing new-uniform branding chunks and reviewed stamp hotspots;
// never reclones assets or writes databases. A targeted apply requires the explicit six-kit review switch.
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
string reportDir = Path.GetFullPath(Opt("--report") ?? Path.Combine(workspace, "Debugging", "fanatics-branding-correction"));
string workspacePrefix = workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (!reportDir.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("The correction report must stay inside the workspace.");
Directory.CreateDirectory(reportDir);
if (ApplyTarget.RunningGame() is { } running)
{
    if (apply)
    {
        Say("REFUSED: " + running + " is running.");
        return 1;
    }
    Say(running + " is running: continuing with a read-only plan. No game files will be changed.");
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
string artDir = Path.GetFullPath(Opt("--art-dir") ?? Path.Combine(workspace, "Debugging", "new-uniform-integration", "cache", "nhl27png"));
string ResolveArtwork(string path)
{
    if (File.Exists(path)) return path;
    string local = Path.Combine(artDir, Path.GetFileName(path));
    return File.Exists(local) ? local : throw new FileNotFoundException("Converted artwork required by the correction is missing.", path);
}
int workers = Opt("--workers") is { } value ? int.Parse(value) : Math.Min(8, Environment.ProcessorCount);
var index = BundleIndex.Build(target, Say);
additions = RegisteredKitSource.Normalize(additions, index, Say);
var jobs = new List<BrandingCorrectionJob>();
var copies = new List<BrandingCorrectionCopy>();
var stamps = new List<BrandingStampJob>();
var problems = new List<string>();
foreach (var addition in additions)
{
    Say($"Preparing final installed arrays: {addition.Team} {addition.SourceKit}");
    try
    {
        var plan = InstalledBrandingCorrection.BuildAddition(target, index, addition, ResolveArtwork, workers);
        jobs.AddRange(plan.Jobs);
        copies.AddRange(plan.Copies);
        stamps.AddRange(plan.Stamps);
        Say($"  {plan.Copies.Count} array copies; {plan.Jobs.Count} texture chunk(s) and {plan.Stamps.Count} stamp copies prepared.");
    }
    catch (Exception ex)
    {
        problems.Add($"{addition.Team} {addition.SourceKit}: {ex.Message}");
        Say("BLOCKER: " + problems[^1]);
    }
}
var report = new
{
    Target = target,
    Uniforms = additions.Count,
    ArrayCopies = copies.Count,
    ChangedChunks = jobs.GroupBy(j => j.Chunk).Count(),
    ChangedByRole = jobs.GroupBy(j => j.Role).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
    ChangedStamps = stamps.Count(j => !j.Before.AsSpan().SequenceEqual(j.Data)),
    Problems = problems,
    Copies = copies,
    Jobs = jobs.Select(j => new { j.Team, j.Kit, j.Slot, j.Role, j.Chunk, j.Layers, Bytes = j.Data.Length }).ToArray(),
    Stamps = stamps.Select(j => new { j.Team, j.Kit, j.SourceSlot, j.TargetSlot, j.BundleToc, j.BundleRecordAt,
        Bytes = j.Data.Length, Changed = !j.Before.AsSpan().SequenceEqual(j.Data) }).ToArray(),
};
File.WriteAllText(Path.Combine(reportDir, "correction-plan.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Say($"Plan: {copies.Count} array copies, {jobs.Count} changed role chunks, {stamps.Count} reviewed stamp copies, "
    + $"{problems.Count} blocker(s). Report: {Path.Combine(reportDir, "correction-plan.json")}");
if (problems.Count > 0) return 2;
if (!apply)
{
    Say("DRY RUN COMPLETE: no game files were changed.");
    return 0;
}
string backupRoot = Opt("--backups") ?? (isTestCopy ? Path.Combine(TestRoot, "fullkit-backups") : Path.Combine(workspace, "Debugging", "live-branding-backups"));
string? backup = InstalledBrandingCorrection.Apply(target,
    new InstalledBrandingCorrectionPlan(jobs, copies, stamps), backupRoot, args.Contains("--verify-restore"), Say, workers);
Say(backup is null ? "No correction was necessary." : "Correction complete. Backup: " + backup);
return 0;
