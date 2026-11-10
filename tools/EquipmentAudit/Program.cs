// EquipmentAudit [test-game-folder] [report.json]
// Read-only reference comparison. It never writes a game file or a database.
using System.Text.Json;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

string root = Directory.GetCurrentDirectory();
string report = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(root, "Debugging", "equipment-color-reference.json"));
if (!report.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("The report must stay inside the workspace.");
string reference = Path.Combine(root, "reference", "nhl24-full-csv", "gamemodes", "database", "t3db");
var stock24 = T3Db.Load(File.ReadAllBytes(Path.Combine(reference, "nhl_game.db")), Path.Combine(reference, "nhl_game-meta.xml"));
string dump27 = Path.Combine(root, "reference", "nhl27-full-csv");
var equipment27 = T3Csv.Load(dump27, "stockteamequipmenttable");
var goalie27 = T3Csv.Load(dump27, "stockgoalieequipment");
var jerseys27 = T3Csv.Load(dump27, "stockteamjerseys");

T3Db current = stock24;
if (args.Length > 0)
{
    string target = Path.GetFullPath(args[0]);
    var settings = new SuiteSettings { Nhl24GameDir = target, ApplyTargetDir = target };
    current = T3Db.Load(new LegacyFiles(target, settings).Read(RosterSyncRun.LegacyName("nhl_game")), Path.Combine(reference, "nhl_game-meta.xml"));
}

var teams = new Dictionary<int, string>();
var teamTable = stock24.Tables["exhibitionteams"];
for (int r = 0; r < teamTable.InUse; r++)
{
    int id = (int)stock24.GetInt(teamTable, r, "index");
    if (id is >= 0 and < 32) teams[id] = $"{stock24.GetString(teamTable, r, "cityname")} {stock24.GetString(teamTable, r, "teamname")}".Trim();
}

static List<Dictionary<string, long>> Goalie24(T3Db db, int team)
{
    var t = db.Tables["stockgoalieequipment"];
    var rows = Enumerable.Range(0, t.InUse).Where(r => db.GetInt(t, r, "team") == team && db.GetInt(t, r, "league") == 0).ToList();
    if (rows.Count == 0) throw new InvalidDataException($"NHL 24 team {team}: no stock NHL goalie-equipment row.");
    return rows.Select(row => t.Fields.Values.Where(f => f.Name.Contains("zone", StringComparison.Ordinal) && f.Type == 3)
        .OrderBy(f => f.Name, StringComparer.Ordinal).ToDictionary(f => f.Name, f => db.GetInt(t, row, f.Name))).ToList();
}

static Dictionary<string, string> CsvRow(T3Csv table, string[] row) =>
    table.Header.Where(h => h.Contains("zone", StringComparison.Ordinal) || h is "helmetcolorpreset" or "glovescolorpreset"
            or "helmetvariant" or "glovesvariant" or "helmetlogo" or "helmetlogotype")
        .ToDictionary(h => h, h => table.Get(row, h), StringComparer.Ordinal);

static List<Dictionary<string, long>> Equipment24(T3Db db, int team)
{
    var t = db.Tables["stockteamequipmenttable"];
    var result = new List<Dictionary<string, long>>();
    for (int r = 0; r < t.InUse; r++)
    {
        if (db.GetInt(t, r, "team") != team || db.GetInt(t, r, "era") is not (-1 or 0 or 2)
            || db.GetInt(t, r, "variant") is < -1 or > 25 || db.GetInt(t, r, "equipment") is not (4 or 7)) continue;
        result.Add(t.Fields.Values.Where(f => f.Type == 3 && f.Name is not "index").OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToDictionary(f => f.Name, f => db.GetInt(t, r, f.Name), StringComparer.Ordinal));
    }
    return result;
}

static List<Dictionary<string, string>> Equipment27(T3Csv table, int team)
{
    return table.Rows.Take(table.ValidCount).Where(r => table.Get(r, "team") == team.ToString()
        && table.Get(r, "era") is "-1" or "0" or "2" && int.Parse(table.Get(r, "variant")) is >= -1 and <= 25 && table.Get(r, "equipment") is "4" or "7")
        .Select(r => table.Header.Where(h => h.StartsWith("zone", StringComparison.Ordinal) || h is "equipment" or "era" or "variant" or "brand")
            .ToDictionary(h => h, h => table.Get(r, h), StringComparer.Ordinal)).ToList();
}

var result = new List<object>();
foreach (int team in Enumerable.Range(0, 32))
{
    var g24 = Goalie24(stock24, team);
    var g27 = goalie27.Rows.Take(goalie27.ValidCount).Where(r => goalie27.Get(r, "team") == team.ToString()
        && goalie27.Get(r, "league") == "0").Select(r => CsvRow(goalie27, r)).ToList();
    if (g27.Count == 0) throw new InvalidDataException($"NHL 27 team {team}: no NHL goalie-equipment row.");
    var e24 = Equipment24(current, team);
    var e27 = Equipment27(equipment27, team);
    var j27 = jerseys27.Rows.Take(jerseys27.ValidCount).Where(r => jerseys27.Get(r, "teamid") == team.ToString()
        && jerseys27.Get(r, "style") == "2" && jerseys27.Get(r, "variant") is "0" or "1").Select(r => CsvRow(jerseys27, r)
            .Concat([new KeyValuePair<string, string>("variant", jerseys27.Get(r, "variant"))])
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)).ToList();
    result.Add(new { Team = team, Name = teams.GetValueOrDefault(team, $"Team {team}"), Goalie24 = g24,
        Goalie27 = g27, Equipment24 = e24, Equipment27 = e27, Jerseys27 = j27 });
    string Color(string field) => g24[0].TryGetValue(field, out long n) ? $"#{n:X6}" : "-";
    int palettes = g24.Select(x => JsonSerializer.Serialize(x)).Distinct(StringComparer.Ordinal).Count();
    Console.WriteLine($"{team,2} {teams.GetValueOrDefault(team, ""),-28} goalie pads {Color("padszone1color")} {Color("padszone2color")} {Color("padszone3color")}; "
        + $"blocker {Color("blockerzone1color")}; trapper {Color("trapperzone1color")}; palettes={palettes}; equipment rows 24={e24.Count} 27={e27.Count}");
}
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
File.WriteAllText(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine("Read-only equipment reference report: " + report);
