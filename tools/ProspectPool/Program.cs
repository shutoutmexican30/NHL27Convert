// ProspectPool <nhlapi_teams.tsv> [dump-dir] [report.tsv]
// Every club's NHL API prospect pool (signed/drafted, not on the NHL roster) and - with a roster database dump -
// how many of those names the game already carries. Read only; API JSON is cached per day.
using Suite.Rosters;

if (args.Length < 1) { Console.Error.WriteLine("usage: ProspectPool <nhlapi_teams.tsv> [dump-dir] [report.tsv]"); return 2; }
var day = DateOnly.FromDateTime(DateTime.Today);
var api = new NhlApi();
var teams = await api.TeamsAsync(day);
Console.WriteLine($"NHL API: {teams.Count} clubs");
var pools = await api.AllProspectsAsync(teams, day, s => Console.Error.WriteLine(s));

RosterDb? db = null;
if (args.Length > 1 && File.Exists(Path.Combine(args[1], "exhibitionplayerbiotable.csv")))
{
    db = RosterCsv.Load("nhl_game", args[1]);
    Console.WriteLine($"game db: {db.Players.Count} players");
}
var teamMap = RosterSync.LoadTeamMap(args[0]);
static string Key(string first, string last) => (first + " " + last).ToUpperInvariant().Replace("'", "").Replace("-", " ").Replace(".", "");
var dbNames = db is null ? [] : db.Players.Select(p => Key(p.First, p.Last)).ToHashSet(StringComparer.Ordinal);
var dbByTeam = db is null ? [] : db.Players.GroupBy(p => p.Team).ToDictionary(g => g.Key, g => g.Count());

var report = new List<string> { "team\tin_game_db\tapi_prospects\talready_in_db\tmissing" };
int totalApi = 0, totalHave = 0;
foreach (var team in teams.OrderBy(t => t))
{
    if (!pools.TryGetValue(team, out var pool)) pool = [];
    totalApi += pool.Count;
    var have = pool.Count(p => dbNames.Contains(Key(p.First, p.Last)));
    totalHave += have;
    var inDb = teamMap.TryGetValue(team, out var idx) && dbByTeam.TryGetValue(idx, out var n) ? n : 0;
    Console.WriteLine($"{team,-4} db players {inDb,4} · api prospects {pool.Count,4} · already in db {have,4} · missing {pool.Count - have,4}");
    report.Add($"{team}\t{inDb}\t{pool.Count}\t{have}\t{pool.Count - have}");
}
Console.WriteLine($"total: {totalApi} API prospects, {totalHave} already in the game's db, {totalApi - totalHave} would be added");
if (args.Length > 2)
{
    report.Add("");
    report.Add("== missing names ==");
    foreach (var team in teams.OrderBy(t => t))
        if (pools.TryGetValue(team, out var pool))
            foreach (var p in pool.Where(p => !dbNames.Contains(Key(p.First, p.Last))))
                report.Add($"{team}\t{p.Position}\t{p.First} {p.Last}\t{p.Birth}");
    File.WriteAllLines(args[2], report);
    Console.WriteLine("report: " + args[2]);
}
return 0;
