// SyncPlan <dumps-root> <nhlapi_teams.tsv> [report.txt]
// Plans the NHL API roster sync for every roster database dump under <dumps-root> (one folder per db with
// exhibitionplayerbiotable.csv) and prints per-database change counts plus a cross-database agreement check.
using Suite.Rosters;

if (args.Length < 2) { Console.WriteLine("usage: SyncPlan <dumps-root> <nhlapi_teams.tsv> [report.txt]"); return 2; }
var dbNames = new[] { "nhl_game_160", "initial_experience", "entry_draft", "expansion_draft", "threes_campaign" };
var dbs = dbNames.Where(n => File.Exists(Path.Combine(args[0], n, "exhibitionplayerbiotable.csv")))
    .Select(n => RosterCsv.Load(n, Path.Combine(args[0], n))).ToList();
var teamMap = RosterSync.LoadTeamMap(args[1]);
var api = await new NhlApi().AllRostersAsync(DateOnly.FromDateTime(DateTime.Today), s => { });
Console.WriteLine($"NHL API: {api.Count} teams, {api.Values.Sum(v => v.Count)} rostered players; unmapped teams: {string.Join(",", api.Keys.Where(k => !teamMap.ContainsKey(k)))}");

var report = new List<string>();
var plans = dbs.Select(db => RosterSync.PlanFor(db, api, teamMap)).ToList();
foreach (var p in plans)
{
    Console.WriteLine($"{p.Db,-20} moves {p.Count(ChangeKind.Move),4}  numbers {p.Count(ChangeKind.Number),4}  positions {p.Count(ChangeKind.Position),4}  " +
                      $"releases {p.Count(ChangeKind.Release),4}  not in db {p.Count(ChangeKind.NotInDb),4}  already right {p.Count(ChangeKind.Same),4}");
    report.Add($"== {p.Db}");
    report.AddRange(p.Changes.Where(c => c.Kind != ChangeKind.Same).OrderBy(c => c.Kind).ThenBy(c => c.Player)
        .Select(c => $"{c.Kind}\t{c.Player}\t{c.From}\t{c.To}\t{c.ApiId}\t{c.Row}"));
}

// After the sync every API-rostered player must have the same team/number/position in every db that has him.
var matched = plans.Select(p => p.Changes.Where(c => c.ApiId is not null && c.Kind != ChangeKind.NotInDb).Select(c => c.ApiId!.Value).ToHashSet()).ToList();
var inAll = matched.Aggregate((a, b) => { var s = new HashSet<long>(a); s.IntersectWith(b); return s; });
var anyDb = matched.SelectMany(x => x).ToHashSet();
Console.WriteLine($"API players matched in every db: {inAll.Count}; matched in some dbs only: {anyDb.Count - inAll.Count}; " +
                  $"in no db: {api.Values.Sum(v => v.Count) - anyDb.Count}");
if (args.Length > 2) { File.WriteAllLines(args[2], report); Console.WriteLine("report: " + args[2]); }
return 0;
