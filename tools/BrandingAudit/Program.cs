// BrandingAudit --target <game> --mod <installed-mod.json> [--away-only] [--teams a,b]
//   [--report <workspace-folder>] [--workers N]
// Read-only pixel and physical-hotspot audit; never changes game files or databases.
using System.Text.Json;
using Suite.Convert27;
using Suite.Core;
using Suite.Core.Frostbite;

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
void Say(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
string workspace = Directory.GetCurrentDirectory();
string target = Path.GetFullPath(Opt("--target") ?? throw new ArgumentException("--target is required")).TrimEnd(Path.DirectorySeparatorChar);
string modPath = Path.GetFullPath(Opt("--mod") ?? throw new ArgumentException("--mod is required"));
string reportDir = Path.GetFullPath(Opt("--report") ?? Path.Combine(workspace, "Debugging", "fanatics-branding-audit-current"));
string workspacePrefix = workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (!reportDir.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("The audit report must stay inside the workspace.");
Directory.CreateDirectory(reportDir);
if (ApplyTarget.RunningGame() is { } running)
    Say(running + " is running: continuing with a read-only audit. No game files will be changed.");

var mod = ModFile.Load(modPath) ?? throw new InvalidDataException("Could not load the installed uniform mod.");
var additions = mod.Additions ?? throw new InvalidDataException("The mod has no new uniforms.");
bool awayOnly = args.Contains("--away-only");
var wanted = Opt("--teams")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
if (awayOnly || wanted is not null)
    additions = additions.Where(a => (!awayOnly || a.SourceKit == "away") && (wanted is null || wanted.Contains(a.Team))).ToList();
if (additions.Count == 0) throw new InvalidDataException("No uniforms were selected.");
int workers = Opt("--workers") is { } value ? int.Parse(value) : Math.Min(8, Environment.ProcessorCount);
var index = BundleIndex.Build(target, Say);
// Normalize the complete mod so one filtered kit cannot accidentally select its sibling's installed target as a source.
var normalized = RegisteredKitSource.Normalize(mod.Additions, index, Say);
var selectedTargets = additions.Select(a => a.TargetPrefix).ToHashSet(StringComparer.Ordinal);
additions = normalized.Where(a => selectedTargets.Contains(a.TargetPrefix)).ToList();
string artDir = Path.GetFullPath(Opt("--art-dir") ?? Path.Combine(workspace, "Debugging", "new-uniform-integration", "cache", "nhl27png"));
string ResolveArtwork(string path) => InstalledBrandingAudit.ResolveArtwork(path, artDir);
var results = new List<InstalledBrandingResult>();
foreach (var addition in additions)
{
    Say($"Auditing installed branding: {addition.Team} {addition.SourceKit}");
    try { results.Add(InstalledBrandingAudit.Addition(target, index, addition, ResolveArtwork, workers)); }
    catch (Exception ex)
    {
        Say("BLOCKER: " + ex.Message);
        results.Add(new InstalledBrandingResult(0, 0, [], [$"{addition.Team} {addition.SourceKit}: {ex.Message}"]));
    }
}
var problems = results.SelectMany(r => r.Problems).ToArray();
var report = new
{
    Target = target,
    Uniforms = additions.Count,
    ArrayCopies = results.Sum(r => r.ArrayCopies),
    Problems = problems,
    Results = results,
};
File.WriteAllText(Path.Combine(reportDir, "branding-audit.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
foreach (string problem in problems.Take(60)) Say("BRANDING: " + problem);
Say($"Audit: {additions.Count} uniforms, {results.Sum(r => r.ArrayCopies)} array copies, {problems.Length} problem(s). "
    + $"Report: {Path.Combine(reportDir, "branding-audit.json")}. No game files were changed.");
return problems.Length == 0 ? 0 : 3;
