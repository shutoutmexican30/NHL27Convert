using System.Text.Json;
using Suite.Core;

namespace Suite.Rosters;

/// <summary>One roster database the sync writes (a copy, never the game's file).</summary>
public sealed record DbTarget(string Name, string DbPath, string MetaXml);

/// <summary>The layout guard's verdict for one written database.</summary>
public sealed record GuardResult(bool Ok, List<string> Problems)
{
    public override string ToString() => Ok ? "layout identical" : string.Join("; ", Problems);
}

/// <summary>
/// Runs the roster sync on database files through the stopgap Worker (db-dump / db-import / db-layout) and checks
/// that NHL 24's layout survived: same file size, same file header bytes, and an identical db-layout (table order,
/// record slots, record sizes, string sizes, CRC positions). Only record contents and CRC values may differ.
/// Works on copies in %LOCALAPPDATA%\CenterIceModdingSuite\dbwork; putting the result in a game is the Apply step's job.
/// </summary>
public sealed class RosterDbJob(SuiteSettings settings)
{
    /// <summary>File header bytes that must not change: the T3DB header, its table directory and the directory CRC.</summary>
    public static int HeaderBytes(byte[] db) => db.Length < 20 ? db.Length : 24 + 8 * BitConverter.ToInt32(db, 16) + 4;

    public static string WorkDir => SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "dbwork"));

    public async Task<JsonElement> DumpAsync(DbTarget db, string outDir, CancellationToken ct = default)
    {
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        var json = await Worker.RunAsync(settings, ["db-dump", db.DbPath, db.MetaXml, outDir], ct);
        File.WriteAllText(Path.Combine(outDir, "tables.json"), json);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public Task<string> LayoutAsync(string dbPath, string metaXml, CancellationToken ct = default) =>
        Worker.RunAsync(settings, ["db-layout", dbPath, metaXml], ct);

    /// <summary>
    /// Same size, same header bytes, and every db-layout entry identical. The one thing allowed to move is a table's
    /// records CRC, and only by (in-use records after - before) x record size: the table keeps its area, the CRC
    /// follows the last in-use record. <paramref name="validBefore"/>/<paramref name="validAfter"/> come from db-dump.
    /// </summary>
    public static GuardResult Guard(string before, string after, string layoutBefore, string layoutAfter,
        IReadOnlyDictionary<string, int> validBefore, IReadOnlyDictionary<string, int> validAfter)
    {
        var problems = new List<string>();
        var a = File.ReadAllBytes(before);
        var b = File.ReadAllBytes(after);
        if (a.Length != b.Length) problems.Add($"size {a.Length} → {b.Length}");
        int n = Math.Min(HeaderBytes(a), Math.Min(a.Length, b.Length));
        for (int i = 0; i < n; i++)
            if (a[i] != b[i]) { problems.Add($"header byte {i} changed"); break; }
        var la = JsonDocument.Parse(layoutBefore).RootElement.EnumerateArray().ToList();
        var lb = JsonDocument.Parse(layoutAfter).RootElement.EnumerateArray().ToList();
        if (la.Count != lb.Count) problems.Add($"{la.Count} tables → {lb.Count}");
        for (int i = 0; i < Math.Min(la.Count, lb.Count); i++)
        {
            var x = la[i]; var y = lb[i];
            var table = x.GetProperty("table").GetString()!;
            foreach (var f in new[] { "table", "headerCrcAt", "records", "recordSize", "strings" })
                if (x.GetProperty(f).GetRawText() != y.GetProperty(f).GetRawText())
                    problems.Add($"{table}: {f} {x.GetProperty(f).GetRawText()} → {y.GetProperty(f).GetRawText()}");
            long shift = y.GetProperty("recordsCrcAt").GetInt64() - x.GetProperty("recordsCrcAt").GetInt64();
            long expected = (long)(validAfter.GetValueOrDefault(table) - validBefore.GetValueOrDefault(table)) * x.GetProperty("recordSize").GetInt32();
            if (shift != expected) problems.Add($"{table}: records CRC moved {shift} bytes, in-use records account for {expected}");
        }
        return new GuardResult(problems.Count == 0, problems);
    }

    static Dictionary<string, int> Valid(JsonElement tables) =>
        tables.EnumerateArray().ToDictionary(t => t.GetProperty("table").GetString()!, t => t.GetProperty("valid").GetInt32());

    /// <summary>The dump folder of a database before the sync (written by <see cref="ReadAsync"/>).</summary>
    public static string BeforeDump(string name) => Path.Combine(WorkDir, name, "before");

    /// <summary>Dumps a database to its "before" folder. Returns the table list (records, in use, slots).</summary>
    public Task<JsonElement> ReadAsync(DbTarget db, CancellationToken ct = default) =>
        DumpAsync(db, BeforeDump(db.Name), ct);

    /// <summary>
    /// Applies the sync to the database's "before" dump (see <see cref="ReadAsync"/>), imports the edited tables into
    /// <paramref name="outDb"/>, re-dumps the result for <see cref="Verify"/> and runs the layout guard.
    /// The output file is deleted again when the guard fails.
    /// </summary>
    public async Task<(WriteReport Report, GuardResult Guard, string AfterDump)> WriteAsync(DbTarget db, JsonElement beforeTables, string outDb,
        Dictionary<string, List<ApiPlayer>> api, Dictionary<string, List<ApiPlayer>> prospects, Dictionary<string, int> teamMap,
        IReadOnlySet<string>? onlyTeams = null, string? teamsDump = null, Action<string>? progress = null, CancellationToken ct = default,
        LineOverrides? lineOverrides = null, Ratings? ratings = null)
    {
        var work = SuitePaths.Ensure(Path.Combine(WorkDir, db.Name));
        string before = BeforeDump(db.Name), edit = Path.Combine(work, "edit"), after = Path.Combine(work, "after");
        progress?.Invoke($"{db.Name}: applying the NHL API rosters");
        if (Directory.Exists(edit)) Directory.Delete(edit, true);
        var report = RosterWriter.Write(db.Name, before, edit, api, prospects, teamMap, onlyTeams, teamsDump, lineOverrides, ratings);
        progress?.Invoke($"{db.Name}: writing");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outDb))!);
        await Worker.RunAsync(settings, ["db-import", db.DbPath, db.MetaXml, edit, outDb], ct);
        progress?.Invoke($"{db.Name}: checking");
        var afterTables = await DumpAsync(db with { DbPath = outDb }, after, ct);
        var guard = Guard(db.DbPath, outDb, await LayoutAsync(db.DbPath, db.MetaXml, ct), await LayoutAsync(outDb, db.MetaXml, ct),
            Valid(beforeTables), Valid(afterTables));
        if (!guard.Ok) { File.Delete(outDb); return (report, guard, ""); }
        return (report, guard, after);
    }

    /// <summary>
    /// Reads a written database back and checks that every API-rostered player it has is on his NHL team with his
    /// number, exactly once among NHL rosters, and that every NHL team's line slots are each held by one player.
    /// Returns the problems (empty when all is well) and the placement per API id, for the cross-database check.
    /// </summary>
    public static (List<string> Problems, Dictionary<long, string> Placement) Verify(string dumpDir,
        Dictionary<string, List<ApiPlayer>> api, Dictionary<string, int> teamMap)
    {
        var problems = new List<string>();
        var placement = new Dictionary<long, string>();
        var bio = T3Csv.Load(dumpDir, "exhibitionplayerbiotable");
        var ro = T3Csv.Load(dumpDir, "exhibitionrostertable");
        var ep = T3Csv.Load(dumpDir, "exhibitionplayers");
        var players = bio.Rows.Take(bio.ValidCount).Select((r, i) => new DbPlayer(i, bio.Get(r, "game_id"), bio.Get(r, "firstname"), bio.Get(r, "lastname"),
            RosterSync.BioDate(bio.Int(r, "year"), bio.Int(r, "month"), bio.Int(r, "day")), bio.Int(r, "team"), bio.Int(r, "jersey"),
            bio.Int(r, "position"), false, 0)).ToList();
        var gameOf = ep.Rows.Take(ep.ValidCount).GroupBy(r => ep.Get(r, "index")).ToDictionary(g => g.Key, g => ep.Get(g.First(), "playerid"));
        var withRow = gameOf.Values.ToHashSet();
        var byLast = players.Where(x => withRow.Contains(x.GameId)).ToLookup(x => RosterSync.Norm(x.Last));
        var byFull = players.Where(x => withRow.Contains(x.GameId)).ToLookup(x => RosterSync.Norm(x.First + x.Last));
        var nhl = teamMap.Values.ToHashSet();
        var rows = ro.Rows.Take(ro.ValidCount).ToList();
        var nhlRows = rows.Where(r => nhl.Contains(ro.Int(r, "team"))).ToLookup(r => gameOf.GetValueOrDefault(ro.Get(r, "playerindex"), ""));
        foreach (var (abbrev, list) in api)
        {
            if (!teamMap.TryGetValue(abbrev, out var team)) continue;
            foreach (var p in list)
            {
                var m = RosterSync.Match(p, byLast, byFull);
                if (m is null) continue;
                var mine = nhlRows[m.GameId].ToList();
                // A number from the player search (injured players) may give way to a current-roster number.
                placement[p.Id] = p.Number is null || p.FromSearch ? $"{m.Team}/-/{m.Position}" : $"{m.Team}/{m.Jersey}/{m.Position}";
                if (m.Team != team) problems.Add($"{p.First} {p.Last}: bio team {m.Team}, API {abbrev}");
                if (mine.Count != 1 || ro.Int(mine[0], "team") != team) problems.Add($"{p.First} {p.Last}: {mine.Count} NHL roster rows");
                else if (p.Number is { } n && !p.FromSearch && ro.Int(mine[0], "jersey") != n) problems.Add($"{p.First} {p.Last}: roster jersey {ro.Get(mine[0], "jersey")}, API {n}");
            }
        }
        var slotCols = ro.Header.Where(h => h is not ("playerindex" or "key" or "team" or "rosterstatus" or "captain" or "playerstyle" or "jersey")).ToList();
        foreach (var team in nhl)
        {
            var mine = rows.Where(r => ro.Int(r, "team") == team).ToList();
            if (mine.Count == 0) continue;
            foreach (var c in slotCols)
            {
                int holders = mine.Count(r => ro.Get(r, c) == "1");
                if (holders > 1) problems.Add($"team {team}: {holders} players in {c}");
            }
        }
        return (problems, placement);
    }
}
