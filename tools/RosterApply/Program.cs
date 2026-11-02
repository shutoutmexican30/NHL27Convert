// RosterApply <teams.tsv> [--teams BOS,TOR]
// The "Sync rosters" button end to end, for the CIMS game copy (settings.ApplyTargetDir) only:
//  1. refuses while NHL 24 runs or when the target fails the apply guard,
//  2. exports the five stock roster databases from the original game (read view) and syncs them with today's NHL API
//     (plus roster saves found in the CIMS copy's user folder); stops without writing if any check fails,
//  3. saves the mod to the mod list, backs the target up and writes the databases with the suite's own legacy writer,
//  4. reads the five databases back out of the CIMS game and compares them byte for byte with what was written,
//  5. restore check: restores, verifies the game is identical to the stock backup and holds the stock databases,
//     then applies again and re-checks,
//  6. writes the synced roster saves (backed up first).
using System.IO;
using Suite.Convert27;
using Suite.Core;
using Suite.Rosters;

if (args.Length < 1) { Console.WriteLine("usage: RosterApply <teams.tsv> [--teams BOS,TOR]"); return 2; }
var teamMap = RosterSync.LoadTeamMap(args[0]);
var only = args.SkipWhile(a => a != "--teams").Skip(1).FirstOrDefault()?.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
var settings = SuiteSettings.Load();
void Say(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");

if (ApplyTarget.RunningGame() is { } running) { Say($"REFUSED: {running} is running. Close the game first."); return 1; }
var problems = ModApply.Problems(settings);
if (problems.Count > 0) { Say("REFUSED: " + string.Join("; ", problems)); return 1; }
var target = Path.GetFullPath(settings.ApplyTargetDir).TrimEnd(Path.DirectorySeparatorChar);
Say("target: " + target);

// 2. Sync on copies.
var dbs = await RosterSyncRun.ExportStockAsync(settings, Say);
var saves = SavedRoster.SavedataRoots(target).SelectMany(SavedRoster.Find).ToList();
Say($"saved rosters in the CIMS copy: {saves.Count}");
string last = "";
var result = await RosterSyncRun.RunAsync(settings, dbs, teamMap, only, s =>
{
    var head = string.Join(' ', s.Split(' ').Take(2));
    if (head != last || !s.Contains('/')) Say(s);
    last = head;
}, default, saves);
foreach (var d in result.Dbs)
{
    Say($"{d.Db.Name}: {string.Join(", ", d.Report.Counts.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))}; " +
        $"{d.Report.Warnings.Count} warnings; guard: {d.Guard}; read back {(d.Problems.Count == 0 ? "OK" : d.Problems.Count + " problems")}");
    foreach (var p in d.Problems.Take(10)) Say("  problem: " + p);
}
Say($"cross-database check: {result.Disagreements.Count} players disagree");
if (!result.Ok) { Say("STOPPED: a check failed; nothing was written to the game."); return 1; }

// 3. Mod list + apply.
var mod = RosterSyncRun.ToModFile(result);
Say("mod saved: " + mod.Save());
string? backup = null;
bool Apply()
{
    if (ApplyTarget.RunningGame() is { } r) { Say($"REFUSED: {r} is running."); return false; }
    Say("applying with the suite's legacy writer (backup first)...");
    var (ok, msg, b) = Suite.Core.Frostbite.LegacyApply.Apply(settings, mod.Legacy!, Say);
    backup ??= b;
    Say($"apply ok={ok}: {msg}");
    return ok;
}
bool GameHolds(IReadOnlyDictionary<string, string> expected, string label)
{
    var lf = new Suite.Core.Frostbite.LegacyFiles(target, settings);
    bool all = true;
    foreach (var (name, source) in expected)
    {
        bool same = lf.Read(name).AsSpan().SequenceEqual(File.ReadAllBytes(source));
        Say($"  {name}: {(same ? "identical" : "DIFFERENT")} ({label})");
        all &= same;
    }
    return all;
}
var synced = mod.Legacy!.ToDictionary(l => l.Name, l => l.Source);
var stock = result.Dbs.Where(d => d.Save is null).ToDictionary(d => RosterSyncRun.LegacyName(d.Db.Name), d => d.Db.DbPath);
if (!Apply()) return 1;
if (!GameHolds(synced, "applied")) { Say("FAILED: the game does not hold the synced databases."); return 1; }

// 5. Restore check, then apply again.
Say("restore check...");
ApplyTarget.RestoreFromBackup(target, backup!);
var left = ApplyTarget.Compare(target, backup!);
Say($"restore: {left.Count} differences from the backup");
if (left.Count > 0 || !GameHolds(stock, "restored")) { Say("FAILED: restore did not bring the stock databases back. Game left restored."); return 1; }
if (!Apply() || !GameHolds(synced, "reapplied")) { Say("FAILED on re-apply."); return 1; }

// 6. Saved rosters.
if (result.Dbs.Any(d => d.Save is not null))
    foreach (var s in RosterSyncRun.WriteSaves(result)) Say("saved roster written: " + s);
Say("DONE: roster sync applied to " + target);
return 0;
