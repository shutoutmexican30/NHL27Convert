// SyncWrite <teams.tsv> <meta.xml> <name=db> [<name=db> ...] [--teams BOS,TOR]
//           [--include-prospects players.json] [--prepare-portraits] [--verify-only]
// Runs the NHL API roster sync (RosterSyncRun) on each roster database file and writes the results to
// %LOCALAPPDATA%\CenterIceModdingSuite\dbwork\<name>\<name>.db (never to a game), with the layout guard, a read-back
// and a cross-database check. List nhl_game first. --teams limits the sync to players joining or leaving those clubs.
using Suite.Core;
using Suite.Rosters;

if (args.Length < 3) { Console.WriteLine("usage: SyncWrite <teams.tsv> <meta.xml> <name=db>... [--teams BOS,TOR]"); return 2; }
var teamMap = RosterSync.LoadTeamMap(args[0]);
var only = args.SkipWhile(a => a != "--teams").Skip(1).FirstOrDefault()?.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
var dbs = args.Skip(2).TakeWhile(a => !a.StartsWith("--")).Select(a => a.Split('=', 2)).Select(p => new DbTarget(p[0], p[1], args[1])).ToList();
var include = Array.IndexOf(args, "--include-prospects");
var extra = include < 0 ? null : System.Text.Json.JsonSerializer.Deserialize<List<ApiPlayer>>(File.ReadAllText(args[include + 1]));
// --reality <json>: {ActiveUnsignedGameIds:[...], PortedLeagues:true} from tools\Port27 (free agents = real ones; ported AHL kept).
var realityAt = Array.IndexOf(args, "--reality");
if (realityAt >= 0)
{
    using var rj = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[realityAt + 1]));
    var ids = rj.RootElement.GetProperty("ActiveUnsignedGameIds").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
    var ahl = rj.RootElement.TryGetProperty("AhlNowGameIds", out var an) ? an.EnumerateArray().Select(e => e.GetString()!).ToHashSet() : new HashSet<string>();
    var leagues = rj.RootElement.TryGetProperty("PortedLeagueIds", out var li) ? li.EnumerateArray().Select(e => e.GetInt32()).ToHashSet() : null;
    RosterWriter.Reality = new RosterWriter.RealityInfo(ids, new HashSet<long>(), rj.RootElement.TryGetProperty("PortedLeagues", out var pl) && pl.GetBoolean(), ahl, leagues);
    Console.WriteLine($"reality: {ids.Count} real free agents, {ahl.Count} API-confirmed AHL players, ported leagues {RosterWriter.Reality.PortedLeagues}");
}

if (args.Contains("--verify-only"))
{
    var api = await new NhlApi().AllRostersAsync(DateOnly.FromDateTime(DateTime.Today));
    var main = T3Csv.Load(RosterDbJob.BeforeDump(dbs[0].Name), "exhibitionplayerbiotable");
    var preferred = main.Rows.Take(main.ValidCount).Select(r => main.Get(r, "game_id")).ToHashSet();
    int errors = 0;
    foreach (var db in dbs)
    {
        var (problems, placements) = RosterDbJob.Verify(Path.Combine(RosterDbJob.WorkDir, db.Name, "after"), api, teamMap, preferred);
        errors += problems.Count;
        Console.WriteLine($"{db.Name}: {placements.Count} NHL players checked, {problems.Count} problems");
        foreach (var problem in problems) Console.WriteLine(problem);
    }
    return errors == 0 ? 0 : 1;
}

string last = "";
var result = await RosterSyncRun.RunAsync(SuiteSettings.Load(), dbs, teamMap, only, s =>
{
    if (s.StartsWith("Portraits ") && int.TryParse(s.Split(' ').Last().Split('/')[0], out var n) && n != 1 && n % 100 != 0) return;
    // Counting progress lines ("NHL player search 12/230") print once per kind.
    var head = string.Join(' ', s.Split(' ').Take(2));
    if (head != last || !s.Contains('/')) Console.WriteLine(s);
    last = head;
}, additionalProspects: extra, preparePortraits: args.Contains("--prepare-portraits"));
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
