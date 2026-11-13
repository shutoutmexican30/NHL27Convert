using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Suite.Core;

namespace Suite.Rosters;

/// <summary>
/// A movement clause for one season of a contract. Codes are NHL 27's contractclause0..9 values (read off its data:
/// 382 players carry one). NHL 24 has no clause field, so clauses live only in the suite's contracts file.
/// </summary>
public enum ClauseKind
{
    None = 0,
    /// <summary>Modified no-trade clause: the player names a list of <see cref="Clause.ListSize"/> teams.</summary>
    ModifiedNtc = 1,
    /// <summary>No-movement clause with a modified trade list (NHL 27 code 2, always with a list size; e.g. Malkin, Landeskog).</summary>
    NmcModifiedNtc = 2,
    /// <summary>Full no-trade clause.</summary>
    Ntc = 3,
    /// <summary>Full no-movement clause.</summary>
    Nmc = 4,
}

/// <summary>A clause in force in season <paramref name="Season"/> of the contract (0 = the current season).</summary>
public sealed record Clause(int Season, ClauseKind Kind, int ListSize = 0)
{
    public override string ToString() => $"{Season}:{Token(Kind, ListSize)}";

    public static string Token(ClauseKind k, int list) => k switch
    {
        ClauseKind.Nmc => "NMC",
        ClauseKind.Ntc => "NTC",
        ClauseKind.ModifiedNtc => $"M-NTC/{list}",
        ClauseKind.NmcModifiedNtc => $"NMC+M-NTC/{list}",
        _ => "",
    };

    /// <summary>Parses "0:NMC;1:M-NTC/10" (the CSV and grid form). Empty text is no clauses.</summary>
    public static List<Clause> ParseList(string? text)
    {
        var list = new List<Clause>();
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(':', 2);
            if (bits.Length != 2 || !int.TryParse(bits[0], out int season) || season is < 0 or > 9)
                throw new FormatException($"Clause '{part}': expected season 0-9, e.g. 0:NMC or 1:M-NTC/10.");
            string t = bits[1].Trim().ToUpperInvariant();
            int n = 0;
            int slash = t.IndexOf('/');
            if (slash >= 0) { if (!int.TryParse(t[(slash + 1)..], out n) || n is < 0 or > 32) throw new FormatException($"Clause '{part}': list size 0-32."); t = t[..slash]; }
            var kind = t switch
            {
                "NMC" => ClauseKind.Nmc,
                "NTC" => ClauseKind.Ntc,
                "M-NTC" or "MNTC" => ClauseKind.ModifiedNtc,
                "NMC+M-NTC" or "M-NMC" => ClauseKind.NmcModifiedNtc,
                _ => throw new FormatException($"Clause '{part}': kind must be NMC, NTC, M-NTC/n or NMC+M-NTC/n."),
            };
            if (list.Any(c => c.Season == season)) throw new FormatException($"Two clauses for season {season}.");
            list.Add(new Clause(season, kind, n));
        }
        return list.OrderBy(c => c.Season).ToList();
    }

    public static string FormatList(IEnumerable<Clause> clauses) => string.Join(";", clauses.OrderBy(c => c.Season).Select(c => c.ToString()));
}

/// <summary>
/// One player's contract in real-world terms (dollars, years). Years are real seasons: <see cref="YearUfa"/> 2027 means
/// the 2027 off-season. Converted to NHL 24's storage by <see cref="ContractMapping"/>.
/// </summary>
public sealed class Contract
{
    public long GameId { get; set; }
    public string First { get; set; } = "";
    public string Last { get; set; } = "";
    /// <summary>Team in the source (NHL 24 / NHL 27 numbering, 0-31 the clubs, -1 free agent). Display only; never written.</summary>
    public int Team { get; set; } = -1;
    public bool Signed { get; set; }
    /// <summary>Cap hit (AAV) in dollars.</summary>
    public int CapHit { get; set; }
    /// <summary>Seasons left including the current one.</summary>
    public int Years { get; set; }
    public bool TwoWay { get; set; }
    /// <summary>Null keeps the database's own value.</summary>
    public bool? MultiYear { get; set; }
    public int ExtCapHit { get; set; }
    public int ExtYears { get; set; }
    public bool ExtTwoWay { get; set; }
    /// <summary>The year he becomes UFA-eligible (real year). Null keeps the database's own value.</summary>
    public int? YearUfa { get; set; }
    /// <summary>Times the contract was retained in a trade (0-3).</summary>
    public int RetainedTrades { get; set; }
    public List<Clause> Clauses { get; set; } = [];
    public string Source { get; set; } = "";
    public string Note { get; set; } = "";

    [JsonIgnore] public string Name => (First + " " + Last).Trim();

    public Contract Clone() => new()
    {
        GameId = GameId, First = First, Last = Last, Team = Team, Signed = Signed, CapHit = CapHit, Years = Years, TwoWay = TwoWay,
        MultiYear = MultiYear, ExtCapHit = ExtCapHit, ExtYears = ExtYears, ExtTwoWay = ExtTwoWay, YearUfa = YearUfa,
        RetainedTrades = RetainedTrades, Clauses = [.. Clauses], Source = Source, Note = Note,
    };
}

/// <summary>
/// Real contract values ↔ NHL 24's exhibitionplayerbiotable columns (same units in NHL 27):
/// contractdollars / extensiondollars = dollars ÷ 5,000 (14 bits); contractlength / extensionlength = seasons (4 bits);
/// contractstatus 0 signed, -1 none; contractis2way / extensionis2way / contractismultiyear 0/1; retainedsalarytrades
/// 0-3; yearufa = year − 1900. Real years shift by <see cref="SeasonOffset"/> so a contract keeps its distance from the
/// game's current season (NHL 24 starts in 2023; the data describes <see cref="DataSeason"/>).
/// </summary>
public sealed record ContractMapping(int GameSeason = 2023, int DataSeason = 2026)
{
    public const int DollarUnit = 5000;
    public int SeasonOffset => GameSeason - DataSeason;

    public static int Units(int dollars) => (int)Math.Clamp(Math.Round(dollars / (double)DollarUnit, MidpointRounding.AwayFromZero), 0, 16383);

    /// <summary>The database values this contract asks for (field → value). Absent fields keep the database's own.</summary>
    public Dictionary<string, long> Fields(Contract c)
    {
        var f = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["contractstatus"] = c.Signed ? 0 : -1,
            ["contractdollars"] = c.Signed ? Units(c.CapHit) : 0,
            ["contractlength"] = c.Signed ? Math.Clamp(c.Years, 0, 15) : 0,
            ["contractis2way"] = c.Signed && c.TwoWay ? 1 : 0,
            ["extensiondollars"] = c.ExtYears > 0 ? Units(c.ExtCapHit) : 0,
            ["extensionlength"] = Math.Clamp(c.ExtYears, 0, 15),
            ["extensionis2way"] = c.ExtYears > 0 && c.ExtTwoWay ? 1 : 0,
            ["retainedsalarytrades"] = Math.Clamp(c.RetainedTrades, 0, 3),
        };
        if (c.MultiYear is bool m) f["contractismultiyear"] = m ? 1 : 0;
        if (c.YearUfa is int y) f["yearufa"] = Math.Clamp(y + SeasonOffset - 1900, 0, 255);
        return f;
    }

    /// <summary>Reads a bio row back into real terms (the inverse of <see cref="Fields"/>, clauses excluded).</summary>
    public Contract FromRow(Func<string, long> get, Func<string, string> str, string source) => new()
    {
        GameId = get("game_id"), First = str("firstname"), Last = str("lastname"), Team = (int)get("team"),
        Signed = get("contractstatus") >= 0, CapHit = (int)get("contractdollars") * DollarUnit, Years = (int)get("contractlength"),
        TwoWay = get("contractis2way") == 1, MultiYear = get("contractismultiyear") == 1,
        ExtCapHit = (int)get("extensiondollars") * DollarUnit, ExtYears = (int)get("extensionlength"), ExtTwoWay = get("extensionis2way") == 1,
        YearUfa = (int)get("yearufa") + 1900 - SeasonOffset, RetainedTrades = (int)get("retainedsalarytrades"), Source = source,
    };
}

public sealed record ContractChange(string Db, long GameId, string Player, string Field, long Old, long New);

/// <summary>Contracts against one NHL 24 roster database: read, diff and in-place apply (exhibitionplayerbiotable only).</summary>
public static class ContractDb
{
    public const string BioTable = "exhibitionplayerbiotable";

    /// <summary>Every player row of the database as a contract (first row per game_id).</summary>
    public static List<Contract> Read(T3Db db, ContractMapping map, string source)
    {
        var t = db.Tables[BioTable];
        var seen = new HashSet<long>();
        var list = new List<Contract>();
        for (int r = 0; r < t.InUse; r++)
        {
            long gid = db.GetInt(t, r, "game_id");
            if (!seen.Add(gid)) continue;
            list.Add(map.FromRow(f => db.GetInt(t, r, f), f => db.GetString(t, r, f), source));
        }
        return list;
    }

    /// <summary>The cell changes the contracts would make (all rows of each matching game_id).</summary>
    public static List<ContractChange> Diff(string dbName, T3Db db, IEnumerable<Contract> contracts, ContractMapping map)
    {
        var t = db.Tables[BioTable];
        var want = contracts.Where(c => c.GameId > 0).GroupBy(c => c.GameId).ToDictionary(g => g.Key, g => g.Last());
        var changes = new List<ContractChange>();
        for (int r = 0; r < t.InUse; r++)
        {
            long gid = db.GetInt(t, r, "game_id");
            if (!want.TryGetValue(gid, out var c)) continue;
            foreach (var (field, value) in map.Fields(c))
            {
                if (!t.Fields.ContainsKey(field)) continue;
                long old = db.GetInt(t, r, field);
                if (old != value) changes.Add(new ContractChange(dbName, gid, c.Name, field, old, value) { });
            }
        }
        return changes;
    }

    /// <summary>Applies the contracts in place and returns the saved bytes (same length, CRCs valid) plus the changes.</summary>
    public static (byte[] Bytes, List<ContractChange> Changes) Apply(string dbName, byte[] bytes, string metaXml, IEnumerable<Contract> contracts, ContractMapping map,
        IReadOnlyList<TeamCap>? teams = null)
    {
        var db = T3Db.Load((byte[])bytes.Clone(), metaXml);
        bool doTeams = teams is { Count: > 0 } && db.Tables.ContainsKey(TeamCap.Table);
        var teamChanges = doTeams ? TeamCap.Apply(dbName, db, teams!) : [];
        var t = db.Tables[BioTable];
        var want = contracts.Where(c => c.GameId > 0).GroupBy(c => c.GameId).ToDictionary(g => g.Key, g => g.Last());
        var changes = new List<ContractChange>();
        for (int r = 0; r < t.InUse; r++)
        {
            long gid = db.GetInt(t, r, "game_id");
            if (!want.TryGetValue(gid, out var c)) continue;
            foreach (var (field, value) in map.Fields(c))
            {
                if (!t.Fields.ContainsKey(field)) continue;
                long old = db.GetInt(t, r, field);
                if (old == value) continue;
                db.SetInt(t, r, field, value);
                changes.Add(new ContractChange(dbName, gid, c.Name, field, old, value));
            }
        }
        var saved = db.Save();
        if (saved.Length != bytes.Length) throw new InvalidOperationException($"{dbName}: size changed.");
        var check = T3Db.Load((byte[])saved.Clone(), metaXml); // every CRC valid
        var ct = check.Tables[BioTable];
        for (int r = 0; r < ct.InUse; r++)
            if (want.TryGetValue(check.GetInt(ct, r, "game_id"), out var c))
                foreach (var (field, value) in map.Fields(c))
                    if (ct.Fields.ContainsKey(field) && check.GetInt(ct, r, field) != value)
                        throw new InvalidOperationException($"{dbName}: {c.Name} {field} reads back {check.GetInt(ct, r, field)}, wrote {value}.");
        if (doTeams) TeamCap.Verify(dbName, check, teams!);
        return (saved, [.. changes, .. teamChanges]);
    }
}

/// <summary>NHL 27's contracts (its exhibitionplayerbiotable CSV dump), clauses included, as real-world contracts.</summary>
public static class Nhl27Contracts
{
    public static string DefaultCsv => Path.Combine(SuitePaths.Local, "nhl27db", "exhibitionplayerbiotable.csv");

    /// <summary>Folders an NHL 27 table dump may be in: the suite's nhl27db cache, then the full CSV dumps the other tools use.</summary>
    public static readonly string[] DumpDirs =
    [
        Path.Combine(SuitePaths.Local, "nhl27db"),
        @"C:\dev\NHL27Convert\reference\nhl27-full-csv",
        @"C:\Users\devin\Documents\Codex\2026-10-20\where-are-the-imported-rosters-located\RosterConversion\nhl27-csv",
    ];

    public static string? FindTable(string table) => DumpDirs.Select(d => Path.Combine(d, table + ".csv")).FirstOrDefault(File.Exists);

    public static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path);
        static string[] Split(string l) => l.Split(',').Select(v => v.Trim('"')).ToArray();
        var head = Split(lines[0].TrimStart('﻿'));
        return lines.Skip(1).Where(l => l.Length > 0).Select(l => { var v = Split(l); return head.Select((h, i) => (h, v.ElementAtOrDefault(i) ?? "")).ToDictionary(x => x.h, x => x.Item2); }).ToList();
    }

    /// <summary>NHL 27 rows as contracts. NHL 27 stores the same units and year − 1900 as NHL 24, for its 2026-27 season.</summary>
    public static List<Contract> Load(string csvPath)
    {
        var source = new ContractMapping(GameSeason: 2026, DataSeason: 2026); // no shift: read NHL 27's own years
        var list = new List<Contract>();
        foreach (var r in ReadCsv(csvPath))
        {
            long L(string f) => long.TryParse(r.GetValueOrDefault(f), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            var c = source.FromRow(L, f => r.GetValueOrDefault(f, ""), "NHL 27");
            for (int i = 0; i < 10; i++)
            {
                int code = (int)L($"contractclause{i}");
                if (code is >= 1 and <= 4) c.Clauses.Add(new Clause(i, (ClauseKind)code, (int)L($"contractclausemntcnumteams{i}")));
            }
            c.Note = $"born {L("year") + 1900}-{L("month") + 1:00}-{L("day") + 1:00}";
            list.Add(c);
        }
        return list;
    }

    public sealed record PrefillResult(List<Contract> Contracts, int Nhl27Signed, int Matched, int ByName, List<Contract> Unmatched);

    /// <summary>
    /// NHL 27 contracts for the players of an NHL 24 database: matched by game_id (shared by both games), else by
    /// first + last name and birth date. Only NHL 27 players with a contract (contractstatus 0) are taken.
    /// </summary>
    public static PrefillResult Prefill(IReadOnlyList<Contract> nhl27, T3Db nhl24)
    {
        var t = nhl24.Tables[ContractDb.BioTable];
        var ids = new HashSet<long>();
        var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var dup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int r = 0; r < t.InUse; r++)
        {
            long gid = nhl24.GetInt(t, r, "game_id");
            ids.Add(gid);
            string key = $"{nhl24.GetString(t, r, "firstname")}|{nhl24.GetString(t, r, "lastname")}|{nhl24.GetInt(t, r, "year")}|{nhl24.GetInt(t, r, "month")}|{nhl24.GetInt(t, r, "day")}";
            if (byName.TryGetValue(key, out var other) && other != gid) dup.Add(key); else byName[key] = gid;
        }
        var signed = nhl27.Where(c => c.Signed).ToList();
        var outList = new List<Contract>(); var unmatched = new List<Contract>();
        int named = 0;
        foreach (var c in signed)
        {
            var copy = c.Clone();
            if (!ids.Contains(c.GameId))
            {
                var born = c.Note.Replace("born ", "").Split('-');
                string key = born.Length == 3
                    ? $"{c.First}|{c.Last}|{int.Parse(born[0]) - 1900}|{int.Parse(born[1]) - 1}|{int.Parse(born[2]) - 1}" : "";
                if (key.Length > 0 && !dup.Contains(key) && byName.TryGetValue(key, out var gid24)) { copy.GameId = gid24; named++; }
                else { unmatched.Add(c); continue; }
            }
            copy.Source = "NHL 27";
            outList.Add(copy);
        }
        return new PrefillResult(outList, signed.Count, outList.Count, named, unmatched);
    }
}

/// <summary>The suite's contracts file (%APPDATA%\CenterIceModdingSuite\data\contracts.json) and its CSV form.</summary>
public static class ContractStore
{
    public static string UserPath => Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Roaming, "data")), "contracts.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static List<Contract> Load(string? path = null)
    {
        path ??= UserPath;
        return File.Exists(path) ? JsonSerializer.Deserialize<List<Contract>>(File.ReadAllText(path), Json) ?? [] : [];
    }

    public static void Save(IEnumerable<Contract> contracts, string? path = null) =>
        File.WriteAllText(path ?? UserPath, JsonSerializer.Serialize(contracts.OrderBy(c => c.Last).ThenBy(c => c.First).ToList(), Json));

    public static readonly string[] CsvColumns =
        ["game_id", "first", "last", "team", "signed", "cap_hit", "years", "two_way", "multi_year", "ext_cap_hit", "ext_years", "ext_two_way",
         "year_ufa", "retained_trades", "clauses", "source", "note"];

    static string Q(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string ToCsv(IEnumerable<Contract> contracts)
    {
        var sb = new StringBuilder(string.Join(",", CsvColumns)).Append('\n');
        foreach (var c in contracts)
            sb.Append(string.Join(",", new[]
            {
                c.GameId.ToString(CultureInfo.InvariantCulture), Q(c.First), Q(c.Last), c.Team.ToString(CultureInfo.InvariantCulture), B(c.Signed),
                c.CapHit.ToString(CultureInfo.InvariantCulture), c.Years.ToString(CultureInfo.InvariantCulture), B(c.TwoWay), c.MultiYear is bool m ? B(m) : "",
                c.ExtCapHit.ToString(CultureInfo.InvariantCulture), c.ExtYears.ToString(CultureInfo.InvariantCulture), B(c.ExtTwoWay),
                c.YearUfa?.ToString(CultureInfo.InvariantCulture) ?? "", c.RetainedTrades.ToString(CultureInfo.InvariantCulture),
                Q(Clause.FormatList(c.Clauses)), Q(c.Source), Q(c.Note),
            })).Append('\n');
        return sb.ToString();
        static string B(bool b) => b ? "1" : "0";
    }

    /// <summary>Parses the CSV form (header required, columns in any order; unknown columns ignored). Errors name the line.</summary>
    public static List<Contract> FromCsv(string text)
    {
        var rows = ParseCsv(text);
        if (rows.Count == 0) return [];
        var head = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string n) => head.IndexOf(n);
        if (Col("game_id") < 0) throw new FormatException("The CSV needs a game_id column.");
        var list = new List<Contract>();
        for (int i = 1; i < rows.Count; i++)
        {
            var v = rows[i];
            if (v.All(string.IsNullOrWhiteSpace)) continue;
            string S(string n) => Col(n) is int k and >= 0 && k < v.Count ? v[k].Trim() : "";
            int I(string n, int dflt = 0) => S(n) is { Length: > 0 } s ? int.Parse(s.Replace("$", "").Replace(",", "").Replace("_", ""), CultureInfo.InvariantCulture) : dflt;
            bool Bo(string n) => S(n) is "1" or "true" or "True" or "yes" or "y";
            try
            {
                list.Add(new Contract
                {
                    GameId = long.Parse(S("game_id"), CultureInfo.InvariantCulture), First = S("first"), Last = S("last"), Team = I("team", -1),
                    Signed = Bo("signed"), CapHit = I("cap_hit"), Years = I("years"), TwoWay = Bo("two_way"),
                    MultiYear = S("multi_year").Length == 0 ? null : Bo("multi_year"),
                    ExtCapHit = I("ext_cap_hit"), ExtYears = I("ext_years"), ExtTwoWay = Bo("ext_two_way"),
                    YearUfa = S("year_ufa").Length == 0 ? null : I("year_ufa"), RetainedTrades = I("retained_trades"),
                    Clauses = Clause.ParseList(S("clauses")), Source = S("source"), Note = S("note"),
                });
            }
            catch (Exception e) when (e is FormatException or OverflowException) { throw new FormatException($"CSV line {i + 1}: {e.Message}", e); }
        }
        return list;
    }

    static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); bool q = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (q)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else q = false; }
                else cell.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = [];
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}

/// <summary>
/// Writes contracts into every roster database of a game copy, in place (only contract cells change; size, tables and
/// slots stay NHL 24's own). Same write path as the other in-place database tools: LegacyFiles replaces every copy the
/// game reads (Data and Patch), after an ApplyTarget backup; each database is read back and a mismatch restores the
/// backup. Refuses the original game and a running NHL 24.
/// </summary>
public static class ContractRun
{
    public static string MetaPath => Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes", "database", "t3db", "nhl_game-meta.xml");

    public sealed record DbPlan(string Db, byte[] Bytes, List<ContractChange> Changes);

    /// <summary>The planned databases (only those that change) from the given database bytes, keyed by database name.</summary>
    public static List<DbPlan> Plan(IReadOnlyDictionary<string, byte[]> dbs, IReadOnlyList<Contract> contracts, ContractMapping map, string? metaXml = null,
        IReadOnlyList<TeamCap>? teams = null)
    {
        var plans = new List<DbPlan>();
        foreach (var name in RosterSyncRun.Databases)
        {
            if (!dbs.TryGetValue(name, out var bytes)) continue;
            var (saved, changes) = ContractDb.Apply(name, bytes, metaXml ?? MetaPath, contracts, map, teams);
            if (changes.Count > 0) plans.Add(new DbPlan(name, saved, changes));
        }
        return plans;
    }

    public static Dictionary<string, byte[]> ReadGame(string target, SuiteSettings settings)
    {
        var lf = new Suite.Core.Frostbite.LegacyFiles(target, settings);
        return RosterSyncRun.Databases.ToDictionary(d => d, d => lf.Read(RosterSyncRun.LegacyName(d)));
    }

    public static string? Refusal(string target, SuiteSettings settings)
    {
        var full = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        if (!settings.InPlaceWriteAllowed(full))
        foreach (var original in new[] { settings.Nhl24GameDir, settings.Nhl24BaseDir, settings.Nhl24UpdateDir, settings.Nhl24ReadView })
            if (!string.IsNullOrWhiteSpace(original) && string.Equals(Path.GetFullPath(original).TrimEnd(Path.DirectorySeparatorChar), full, StringComparison.OrdinalIgnoreCase))
                return "That is the original game. Tick \"Allow in-place writes to this game folder\" on the Contracts page to write it anyway.";
        if (ApplyTarget.RunningGame() is { } running) return running + " is running; close it first.";
        return null;
    }

    /// <summary>Backs up, writes the planned databases, reads them back; restores the backup on any mismatch.</summary>
    public static List<string> Write(string target, SuiteSettings settings, IReadOnlyList<DbPlan> plans)
    {
        if (Refusal(target, settings) is { } why) throw new InvalidOperationException(why);
        var report = new List<string>();
        if (plans.Count == 0) { report.Add("Nothing to change."); return report; }
        var backup = ApplyTarget.Backup(target);
        report.Add("Backup: " + backup);
        // Also the five databases as the game held them (every copy reads the same through LegacyFiles), timestamped.
        var dbBackup = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "contracts", "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        foreach (var (name, bytes) in ReadGame(target, settings)) File.WriteAllBytes(Path.Combine(dbBackup, name + ".db"), bytes);
        report.Add("Databases before: " + dbBackup);
        var files = plans.ToDictionary(p => RosterSyncRun.LegacyName(p.Db), p => p.Bytes);
        try
        {
            foreach (var line in new Suite.Core.Frostbite.LegacyFiles(target, settings).Replace(files)) SuiteLog.Write("contracts: " + line);
            var check = new Suite.Core.Frostbite.LegacyFiles(target, settings);
            var wrong = files.Where(kv => !check.Read(kv.Key).AsSpan().SequenceEqual(kv.Value)).Select(kv => kv.Key).ToList();
            if (wrong.Count > 0) throw new InvalidOperationException("Read-back differs: " + string.Join(", ", wrong));
        }
        catch
        {
            ApplyTarget.RestoreFromBackup(target, backup);
            report.Add("FAILED; the backup was restored.");
            throw;
        }
        foreach (var p in plans) report.Add($"{p.Db}: {p.Changes.Count} cells, {p.Changes.Select(c => c.GameId).Distinct().Count()} players; read back identical.");
        return report;
    }
}
