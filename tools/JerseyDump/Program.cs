// JerseyDump <game>: prints stockteamjerseys (adidas style 2) per NHL team: variant word, isdefault, islightcolor.
// Read only. Used to see which kit folder the game actually dresses each team in.
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

var target = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar);
var settings = SuiteSettings.Load();
var meta = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes", "database", "t3db", "nhl_game-meta.xml");
var localMeta = Path.GetFullPath(Path.Combine("reference", "nhl24-full-csv", "gamemodes", "database", "t3db", "nhl_game-meta.xml"));
if (File.Exists(localMeta)) meta = localMeta;
string[] words = "home away two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty twentyone twentytwo twentythree twentyfour twentyfive".Split(' ');
var dbName = args.Length > 1 ? args[1] : "nhl_game";
// "--file <path>": read a raw T3Db file (e.g. a saved roster's DATA) instead of a game's legacy file.
byte[] raw = dbName == "--file" ? File.ReadAllBytes(args[2]) : new LegacyFiles(target, settings).Read(RosterSyncRun.LegacyName(dbName));
var db = T3Db.Load(raw, meta);
var t = db.Tables["stockteamjerseys"];
Console.WriteLine($"{dbName}: stockteamjerseys {t.InUse} rows, {t.Slots} slots, {t.Slots - t.InUse} spare");
int equipmentArg = Array.IndexOf(args, "--equipment");
if (equipmentArg >= 0 && equipmentArg + 1 < args.Length)
{
    int wanted = int.Parse(args[equipmentArg + 1]);
    var equipment = db.Tables["stockteamequipmenttable"];
    for (int r = 0; r < equipment.InUse; r++)
    {
        if (db.GetInt(equipment, r, "team") != wanted) continue;
        var values = equipment.Fields.Values.Where(f => f.Type == 3).OrderBy(f => f.BitOffset)
            .Select(f => $"{f.Name}={db.GetInt(equipment, r, f.Name)}");
        Console.WriteLine($"row {r}: {string.Join(", ", values)}");
    }
    return;
}
int teamArg = Array.IndexOf(args, "--team");
if (teamArg >= 0 && teamArg + 1 < args.Length)
{
    int wanted = int.Parse(args[teamArg + 1]);
    for (int r = 0; r < t.InUse; r++)
    {
        if (db.GetInt(t, r, "teamid") != wanted || db.GetInt(t, r, "style") != 2) continue;
        var values = t.Fields.Values.Where(f => f.Type == 3).OrderBy(f => f.BitOffset)
            .Select(f => $"{f.Name}={db.GetInt(t, r, f.Name)}");
        Console.WriteLine($"row {r}: {string.Join(", ", values)}");
    }
    return;
}
var byTeam = new Dictionary<long, List<string>>();
for (int r = 0; r < t.InUse; r++)
{
    if (db.GetInt(t, r, "style") != 2) continue;
    var team = db.GetInt(t, r, "teamid");
    var v = db.GetInt(t, r, "variant");
    var word = v >= 0 && v < words.Length ? words[v] : $"#{v}";
    if (!byTeam.TryGetValue(team, out var l)) byTeam[team] = l = [];
    l.Add($"{word}{(db.GetInt(t, r, "isdefault") == 1 ? "*" : "")}{(db.GetInt(t, r, "islightcolor") == 1 ? "(light)" : "")}");
}
foreach (var (team, list) in byTeam.OrderBy(kv => kv.Key))
    Console.WriteLine($"team {team,3}: {string.Join(" ", list)}");
Console.WriteLine("* = isdefault (what the team wears)");
