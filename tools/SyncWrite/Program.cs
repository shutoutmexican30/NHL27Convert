// SyncWrite <teams.tsv> <meta.xml> <name=db> [<name=db> ...] [--teams BOS,TOR]
// Runs the NHL API roster sync (RosterSyncRun) on each roster database file and writes the results to
// %LOCALAPPDATA%\CenterIceModdingSuite\dbwork\<name>\<name>.db (never to a game), with the layout guard, a read-back
// and a cross-database check. List nhl_game first. --teams limits the sync to players joining or leaving those clubs.
using Suite.Core;
using Suite.Rosters;

if (args.Length < 3) { Console.WriteLine("usage: SyncWrite <teams.tsv> <meta.xml> <name=db>... [--teams BOS,TOR]"); return 2; }
var teamMap = RosterSync.LoadTeamMap(args[0]);
var only = args.SkipWhile(a => a != "--teams").Skip(1).FirstOrDefault()?.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
var dbs = args.Skip(2).TakeWhile(a => a != "--teams").Select(a => a.Split('=', 2)).Select(p => new DbTarget(p[0], p[1], args[1])).ToList();

string last = "";
var result = await RosterSyncRun.RunAsync(SuiteSettings.Load(), dbs, teamMap, only, s =>
{
    // Counting progress lines ("NHL player search 12/230") print once per kind.
    var head = string.Join(' ', s.Split(' ').Take(2));
    if (head != last || !s.Contains('/')) Console.WriteLine(s);
    last = head;
});
foreach (var note in result.Notes) Console.WriteLine("note: " + note);
foreach (var d in result.Dbs)
{
    Console.WriteLine($"{d.Db.Name}: {string.Join(", ", d.Report.Counts.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))}; " +
                      $"{d.Report.Warnings.Count} warnings; guard: {d.Guard}; read back {(d.Problems.Count == 0 ? "OK" : d.Problems.Count + " problems")}");
    foreach (var w in d.Report.Warnings.Take(10)) Console.WriteLine("  warning: " + w);
    foreach (var p in d.Problems.Take(10)) Console.WriteLine("  problem: " + p);
}
Console.WriteLine($"cross-database check: {result.Dbs.Count} databases, {result.Disagreements.Count} players disagree");
foreach (var x in result.Disagreements.Take(20)) Console.WriteLine("  " + x);
return result.Ok ? 0 : 1;
