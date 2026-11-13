using System.Globalization;
using System.Text.Json;
using Suite.Core;

namespace Suite.Rosters;

/// <summary>
/// One club's cap charges per season (index 0 = the game's current season, up to 24 seasons ahead), stored in NHL 24's
/// and NHL 27's stockteamsalaryextras: dead cap (cbaexpenseyearN: buyouts, recapture, buried or terminated money; there
/// is no separate buried field) and retained salary from trades (retainedtradesalaryyearN, with numretainedsalariesyearN
/// contracts). Whole dollars. Seasons are relative to the current season in both games, so NHL 27's 2026-27 row lands on
/// the game's current season whatever year that is.
/// </summary>
public sealed class TeamCap
{
    public const int Seasons = 25;
    public const string Table = "stockteamsalaryextras";

    public int Team { get; set; }
    public string Name { get; set; } = "";
    public int[] DeadCap { get; set; } = new int[Seasons];
    public int[] Retained { get; set; } = new int[Seasons];
    public int[] RetainedCount { get; set; } = new int[Seasons];
    public string Source { get; set; } = "";

    public static string FormatList(int[] values)
    {
        int n = values.Length;
        while (n > 0 && values[n - 1] == 0) n--;
        return string.Join(",", values.Take(n).Select(v => v.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>"2600000,1500000" → seasons 0, 1, … (missing seasons are 0). Throws on a bad value.</summary>
    public static int[] ParseList(string? text, int max, string what)
    {
        var parts = string.IsNullOrWhiteSpace(text) ? [] : text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length > Seasons) throw new FormatException($"{what}: at most {Seasons} seasons.");
        var v = new int[Seasons];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            if (!int.TryParse(parts[i].Replace("$", "").Replace("_", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]) || v[i] < 0 || v[i] > max)
                throw new FormatException($"{what}: season {i} value '{parts[i]}' must be 0-{max:N0}.");
        }
        return v;
    }

    public Dictionary<string, long> Fields()
    {
        var f = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < Seasons; i++)
        {
            f[$"cbaexpenseyear{i}"] = Math.Max(0, DeadCap[i]);
            f[$"retainedtradesalaryyear{i}"] = Math.Max(0, Retained[i]);
            f[$"numretainedsalariesyear{i}"] = Math.Clamp(RetainedCount[i], 0, 7);
        }
        return f;
    }

    public TeamCap Clone() => new()
    {
        Team = Team, Name = Name, DeadCap = (int[])DeadCap.Clone(), Retained = (int[])Retained.Clone(), RetainedCount = (int[])RetainedCount.Clone(), Source = Source,
    };

    static TeamCap From(int team, Func<string, long> get, string source)
    {
        var t = new TeamCap { Team = team, Source = source };
        for (int i = 0; i < Seasons; i++)
        {
            t.DeadCap[i] = (int)get($"cbaexpenseyear{i}");
            t.Retained[i] = (int)get($"retainedtradesalaryyear{i}");
            t.RetainedCount[i] = (int)get($"numretainedsalariesyear{i}");
        }
        return t;
    }

    public static List<TeamCap> Read(T3Db db, string source)
    {
        var t = db.Tables[Table];
        var list = new List<TeamCap>();
        for (int r = 0; r < t.InUse; r++)
        {
            int team = (int)db.GetInt(t, r, "index");
            list.Add(From(team, f => db.GetInt(t, r, f), source));
        }
        return list;
    }

    /// <summary>NHL 27's stockteamsalaryextras dump (same columns, same team numbers 0-31 for the clubs).</summary>
    public static List<TeamCap> LoadNhl27(string csvPath) =>
        Nhl27Contracts.ReadCsv(csvPath).Select(r => From(int.Parse(r["index"], CultureInfo.InvariantCulture),
            f => long.TryParse(r.GetValueOrDefault(f), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0, "NHL 27")).ToList();

    /// <summary>In place on stockteamsalaryextras; returns the changes (GameId = team number).</summary>
    public static List<ContractChange> Apply(string dbName, T3Db db, IEnumerable<TeamCap> teams)
    {
        var t = db.Tables[Table];
        var want = teams.GroupBy(x => x.Team).ToDictionary(g => g.Key, g => g.Last());
        var changes = new List<ContractChange>();
        for (int r = 0; r < t.InUse; r++)
        {
            int team = (int)db.GetInt(t, r, "index");
            if (!want.TryGetValue(team, out var c)) continue;
            foreach (var (field, value) in c.Fields())
            {
                if (!t.Fields.ContainsKey(field)) continue;
                long old = db.GetInt(t, r, field);
                if (old == value) continue;
                db.SetInt(t, r, field, value);
                changes.Add(new ContractChange(dbName, team, c.Name.Length > 0 ? c.Name : "team " + team, field, old, value));
            }
        }
        return changes;
    }

    /// <summary>Checks a saved database holds exactly these values; throws on the first difference.</summary>
    public static void Verify(string dbName, T3Db db, IEnumerable<TeamCap> teams)
    {
        var t = db.Tables[Table];
        var want = teams.GroupBy(x => x.Team).ToDictionary(g => g.Key, g => g.Last());
        for (int r = 0; r < t.InUse; r++)
            if (want.TryGetValue((int)db.GetInt(t, r, "index"), out var c))
                foreach (var (field, value) in c.Fields())
                    if (t.Fields.ContainsKey(field) && db.GetInt(t, r, field) != value)
                        throw new InvalidOperationException($"{dbName}: team {c.Team} {field} reads back {db.GetInt(t, r, field)}, wrote {value}.");
    }
}

/// <summary>The suite's team cap file (%APPDATA%\CenterIceModdingSuite\data\team_cap.json).</summary>
public static class TeamCapStore
{
    public static string UserPath => Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Roaming, "data")), "team_cap.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<TeamCap> Load(string? path = null)
    {
        path ??= UserPath;
        return File.Exists(path) ? JsonSerializer.Deserialize<List<TeamCap>>(File.ReadAllText(path), Json) ?? [] : [];
    }

    public static void Save(IEnumerable<TeamCap> teams, string? path = null) =>
        File.WriteAllText(path ?? UserPath, JsonSerializer.Serialize(teams.OrderBy(t => t.Team).ToList(), Json));
}
