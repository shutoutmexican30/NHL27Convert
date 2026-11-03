using Suite.Core;

namespace Suite.Rosters;

/// <summary>One database's outcome in a sync run.</summary>
public sealed record DbOutcome(DbTarget Db, WriteReport Report, GuardResult Guard, List<string> Problems, string? OutDb)
{
    /// <summary>Set for a saved roster: the save it came from and the trailer that follows its database.</summary>
    public SavedRoster? Save { get; init; }
    public byte[]? SaveTrailer { get; init; }
    public bool Ok => Guard.Ok && Problems.Count == 0;
}

public sealed record SyncRunResult(DateOnly Day, List<DbOutcome> Dbs, List<string> Disagreements, List<string> Notes)
{
    public bool Ok => Dbs.All(d => d.Ok) && Disagreements.Count == 0;
}

/// <summary>
/// The "Sync rosters" run: today's NHL API rosters and prospects, applied to every roster database on that database's
/// own terms, each written to %LOCALAPPDATA%\CenterIceModdingSuite\dbwork\&lt;name&gt;\&lt;name&gt;.db (never to a game),
/// guarded, read back, and checked against the other databases. List the main database (nhl_game) first: the small
/// roster databases borrow its team table.
/// </summary>
public static class RosterSyncRun
{
    public static async Task<SyncRunResult> RunAsync(SuiteSettings settings, IReadOnlyList<DbTarget> dbs, Dictionary<string, int> teamMap,
        IReadOnlySet<string>? onlyTeams = null, Action<string>? progress = null, CancellationToken ct = default,
        IReadOnlyList<SavedRoster>? saves = null, IReadOnlyList<ApiPlayer>? additionalProspects = null, bool preparePortraits = false)
    {
        var day = DateOnly.FromDateTime(DateTime.Today);
        var additionsFile = Path.Combine(SuitePaths.Roaming, "data", "roster_additions.json");
        if (additionalProspects is null && File.Exists(additionsFile))
            additionalProspects = System.Text.Json.JsonSerializer.Deserialize<List<ApiPlayer>>(File.ReadAllText(additionsFile));
        var nhlApi = new NhlApi();
        var notes = new List<string>();
        var api = await nhlApi.AllRostersAsync(day, progress, ct);
        var prospects = await nhlApi.AllProspectsAsync(api.Keys, day, progress, ct);
        foreach (var p in additionalProspects ?? [])
        {
            if (!teamMap.ContainsKey(p.TeamAbbrev)) throw new InvalidOperationException($"Unknown club for additional prospect {p.First} {p.Last}: {p.TeamAbbrev}");
            if (!prospects.TryGetValue(p.TeamAbbrev, out var list)) prospects[p.TeamAbbrev] = list = [];
            if (!list.Any(x => x.Id == p.Id)) list.Add(p);
        }

        // Saved rosters (the sixth location) go through the same sync as the game's databases, from their own content.
        var saveOf = new Dictionary<string, (SavedRoster Save, byte[] Trailer)>();
        var all = dbs.ToList();
        foreach (var save in saves ?? [])
        {
            var home = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(save.Dir)))) ?? "user";
            var name = $"save_{home}_{save.Name}";
            var blob = save.ReadDb();
            int n = SavedRoster.DbLength(blob);
            var source = Path.Combine(SuitePaths.Ensure(Path.Combine(RosterDbJob.WorkDir, name)), "source.db");
            File.WriteAllBytes(source, blob[..n]);
            saveOf[name] = (save, blob[n..]);
            all.Add(new DbTarget(name, source, dbs[0].MetaXml));
        }
        dbs = all;

        var job = new RosterDbJob(settings);
        var tables = new Dictionary<string, System.Text.Json.JsonElement>();
        foreach (var db in dbs)
        {
            progress?.Invoke($"{db.Name}: reading");
            tables[db.Name] = await job.ReadAsync(db, ct);
        }

        // NHL players NHL 24 does not have are created in every database first (empty record slots, NHL 27 data, donor
        // assets), so the rest of the sync treats them like everyone else.
        var extraTables = new Dictionary<string, List<string>>();
        {
            var mainDb = RosterCsv.Load(dbs[0].Name, RosterDbJob.BeforeDump(dbs[0].Name));
            var byLast = mainDb.Players.ToLookup(x => RosterSync.Norm(x.Last));
            var byFull = mainDb.Players.ToLookup(x => RosterSync.Norm(x.First + x.Last));
            var requested = api.Where(kv => teamMap.ContainsKey(kv.Key)).SelectMany(kv => kv.Value)
                .Concat(additionalProspects ?? []).Where(p => onlyTeams is null || onlyTeams.Contains(p.TeamAbbrev)).DistinctBy(p => p.Id).ToList();
            var missing = requested.Where(p => RosterSync.Match(p, byLast, byFull) is null).ToList();
            var nhl27 = Directory.Exists(Ratings.Nhl27Dir) ? Ratings.Nhl27Dir : null;
            var plan = NewPlayers.Plan(dbs.Select(d => RosterDbJob.BeforeDump(d.Name)).ToList(), missing, nhl27, notes);
            File.WriteAllText(Path.Combine(RosterDbJob.WorkDir, "new_players.json"),
                System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            foreach (var db in dbs)
                extraTables[db.Name] = NewPlayers.AddTo(RosterDbJob.BeforeDump(db.Name), RosterDbJob.BeforeDump(dbs[0].Name), dbs[0].MetaXml, plan, nhl27, notes);
            foreach (var db in dbs.Skip(1))
                extraTables[db.Name] = extraTables[db.Name].Union(NewPlayers.AlignPlannedWithMain(RosterDbJob.BeforeDump(db.Name), RosterDbJob.BeforeDump(dbs[0].Name), plan)).ToList();
            // Every API player the main database now has goes into the older databases too.
            mainDb = RosterCsv.Load(dbs[0].Name, RosterDbJob.BeforeDump(dbs[0].Name));
            byLast = mainDb.Players.ToLookup(x => RosterSync.Norm(x.Last));
            byFull = mainDb.Players.ToLookup(x => RosterSync.Norm(x.First + x.Last));
            var needed = requested
                .Select(p => RosterSync.Match(p, byLast, byFull)?.GameId).OfType<string>().ToHashSet();
            foreach (var db in dbs.Skip(1))
                extraTables[db.Name] = extraTables[db.Name].Union(NewPlayers.CopyFromMain(RosterDbJob.BeforeDump(db.Name), RosterDbJob.BeforeDump(dbs[0].Name), needed, notes)).ToList();
            // Establish each explicitly requested prospect's organization. The normal roster pass then assigns him
            // to that club's AHL affiliate, using its existing capacity/number/line checks.
            foreach (var db in dbs)
            {
                var dump = RosterDbJob.BeforeDump(db.Name);
                var bio = T3Csv.Load(dump, "exhibitionplayerbiotable");
                var players = RosterCsv.Load(db.Name, dump).Players;
                var last = players.ToLookup(x => RosterSync.Norm(x.Last));
                var full = players.ToLookup(x => RosterSync.Norm(x.First + x.Last));
                bool changed = false;
                foreach (var p in additionalProspects ?? [])
                    if ((onlyTeams is null || onlyTeams.Contains(p.TeamAbbrev)) && RosterSync.Match(p, last, full) is { } m && bio.Int(bio.Rows[m.Row], "team") < 0)
                    {
                        bio.Set(bio.Rows[m.Row], "team", teamMap[p.TeamAbbrev]);
                        bio.Set(bio.Rows[m.Row], "proteam", teamMap[p.TeamAbbrev]);
                        changed = true;
                    }
                if (changed)
                {
                    bio.Save(dump);
                    if (!extraTables[db.Name].Contains("exhibitionplayerbiotable")) extraTables[db.Name].Add("exhibitionplayerbiotable");
                }
            }
        }

        // roster/<team>/current leaves out players on long-term injured reserve and players assigned to the AHL. Before
        // anyone is released, ask the player search for his club and his player page for where he is playing: AHL games
        // last → his club's AHL team; otherwise (injured) he stays on the club's NHL roster.
        var extra = await UnlistedAsync(nhlApi, day, dbs.Select(d => RosterDbJob.BeforeDump(d.Name)), api, teamMap, progress, ct);
        foreach (var (p, league) in extra)
        {
            if (league == "AHL")
            {
                if (!prospects.TryGetValue(p.TeamAbbrev, out var list)) prospects[p.TeamAbbrev] = list = [];
                list.Add(p);
                notes.Add($"{p.First} {p.Last}: {p.TeamAbbrev} player playing in the AHL → {p.TeamAbbrev}'s AHL team");
            }
            else
            {
                api[p.TeamAbbrev].Add(p);
                notes.Add($"{p.First} {p.Last}: on {p.TeamAbbrev} but not its current roster (injured?) → kept on {p.TeamAbbrev}");
            }
        }

        if (preparePortraits)
        {
            var index = Path.Combine(SuitePaths.Local, "index", "nhl24", "assets.json.gz");
            using var gz = new System.IO.Compression.GZipStream(File.OpenRead(index), System.IO.Compression.CompressionMode.Decompress);
            using var doc = System.Text.Json.JsonDocument.Parse(gz);
            var slots = doc.RootElement.GetProperty("Entries").EnumerateArray().Select(e => e.GetProperty("Path").GetString()!)
                .Where(p => p.StartsWith(Portraits.SlotFolder)).Select(p => p[Portraits.SlotFolder.Length..]).ToHashSet();
            var portraitApi = api.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
            foreach (var p in additionalProspects ?? [])
            {
                if (!portraitApi.TryGetValue(p.TeamAbbrev, out var list)) portraitApi[p.TeamAbbrev] = list = [];
                if (!list.Any(x => x.Id == p.Id)) list.Add(p);
            }
            var warnings = new List<string>();
            var plan = Portraits.Plan(dbs.Select(d => RosterDbJob.BeforeDump(d.Name)).ToList(), portraitApi, teamMap, slots, warnings);
            if (warnings.Count > 0) throw new InvalidOperationException(string.Join("; ", warnings));
            var built = await Portraits.PrepareAsync(plan, progress, ct);
            var failed = built.Where(p => p.Png is null).ToList();
            if (failed.Count > 0) throw new InvalidOperationException("Portrait preparation failed: " + string.Join(", ", failed.Select(p => p.Name)));
        }

        var overrides = LineOverrides.Load(LineOverridesPath);
        // Portrait slots planned by Portraits (portraits\plan.json): only players moved to a new slot change in the databases.
        var planFile = Path.Combine(Portraits.Dir, "plan.json");
        Dictionary<string, int>? portraitArt = null;
        if (File.Exists(planFile))
        {
            var planned = System.Text.Json.JsonSerializer.Deserialize<List<PortraitAssignment>>(File.ReadAllText(planFile)) ?? [];
            // Every planned player: a sync starts from stock, so slots given in earlier runs are set again too.
            portraitArt = planned.Where(p => p.Png is not null).ToDictionary(p => p.GameId, p => p.ArtId);
            notes.Add($"Portraits: {portraitArt.Count} players get their portrait slot ({planned.Count(p => p.NewSlot)} new in this plan)");
        }
        var ratings = Ratings.Build(Directory.Exists(Ratings.Nhl27Dir) ? Ratings.Nhl27Dir : null, RosterDbJob.BeforeDump(dbs[0].Name), dbs[0].MetaXml, notes);
        var goalieGear = GoalieGear.Build(Directory.Exists(Ratings.Nhl27Dir) ? Ratings.Nhl27Dir : null, RosterDbJob.BeforeDump(dbs[0].Name), notes);
        var outcomes = new List<DbOutcome>();
        var placements = new List<(string Db, Dictionary<long, string> P)>();
        var checkApi = onlyTeams is null ? api : api.Where(kv => onlyTeams.Contains(kv.Key)).ToDictionary();
        var mainBio = T3Csv.Load(RosterDbJob.BeforeDump(dbs[0].Name), "exhibitionplayerbiotable");
        var mainPlayerIds = mainBio.Rows.Take(mainBio.ValidCount).Select(r => mainBio.Get(r, "game_id")).ToHashSet();
        foreach (var db in dbs)
        {
            var outDb = Path.Combine(RosterDbJob.WorkDir, db.Name, db.Name + ".db");
            var teamsDump = db == dbs[0] ? null : RosterDbJob.BeforeDump(dbs[0].Name);
            var (report, guard, after) = await job.WriteAsync(db, tables[db.Name], outDb, api, prospects, teamMap, onlyTeams, teamsDump, progress, ct, overrides, ratings, portraitArt, extraTables.GetValueOrDefault(db.Name), goalieGear);
            File.WriteAllLines(Path.Combine(RosterDbJob.WorkDir, db.Name, "changes.tsv"),
                report.Changes.Concat(report.Warnings.Select(w => "Warning\t" + w)));
            var problems = new List<string>();
            if (guard.Ok)
            {
                var (p, placement) = RosterDbJob.Verify(after, checkApi, teamMap, mainPlayerIds);
                problems = p;
                placements.Add((db.Name, placement));
            }
            outcomes.Add(new DbOutcome(db, report, guard, problems, guard.Ok ? outDb : null)
            {
                Save = saveOf.TryGetValue(db.Name, out var s) ? s.Save : null,
                SaveTrailer = saveOf.TryGetValue(db.Name, out var t) ? t.Trailer : null,
            });
        }

        // Every API player present in several databases must sit on the same team with the same number and position in all of them.
        var disagree = placements.SelectMany(p => p.P.Keys).Distinct()
            .Where(id => placements.Where(p => p.P.ContainsKey(id)).Select(p => p.P[id]).Distinct().Count() > 1)
            .Select(id => $"{id}: " + string.Join(", ", placements.Where(p => p.P.ContainsKey(id)).Select(p => $"{p.Db} {p.P[id]}")))
            .ToList();
        return new SyncRunResult(day, outcomes, disagree, notes);
    }

    /// <summary>Hand-made line choices (see <see cref="LineOverrides"/>); created with an example the first time.</summary>
    public static string LineOverridesPath
    {
        get
        {
            var path = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Roaming, "data")), "lines_overrides.json");
            if (!File.Exists(path))
                File.WriteAllLines(path,
                [
                    "// Line overrides for the roster sync: team (NHL API abbreviation) -> slot -> \"First Last\".",
                    "// Slots are roster columns: l1c l1lw l1rw ... l3rd, g1 g2, pp_1c, pk4_1ld, s1 ... Example:",
                    "// { \"BOS\": { \"l1c\": \"Elias Lindholm\", \"g1\": \"Jeremy Swayman\" } }",
                    "{}",
                ]);
            return path;
        }
    }

    /// <summary>The roster databases in the game (the saved roster, the sixth, is handled apart), main database first.</summary>
    public static readonly string[] Databases = ["nhl_game", "initial_experience", "entry_draft", "expansion_draft", "threes_campaign"];

    const string MetaName = "gamemodes/database/t3db/nhl_game-meta.xml";

    /// <summary>
    /// Copies the stock roster databases (and the schema they share) out of the NHL 24 game through the read view into
    /// %LOCALAPPDATA%\CenterIceModdingSuite\dbwork\stock. A sync always starts from these, so running it twice gives the
    /// same result and the layout is always the game's own.
    /// </summary>
    public static async Task<List<DbTarget>> ExportStockAsync(SuiteSettings settings, Action<string>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(RosterDbJob.WorkDir, "stock");
        progress?.Invoke("Reading the roster databases from NHL 24");
        var view = Nhl24ReadView.Ensure(settings);
        await Worker.RunAsync(settings, ["export-legacy", view, dir, MetaName, .. Databases.Select(LegacyName)], ct);
        string Local(string legacy) => Path.Combine(dir, legacy.Replace('/', Path.DirectorySeparatorChar));
        return Databases.Select(d => new DbTarget(d, Local(LegacyName(d)), Local(MetaName))).ToList();
    }

    /// <summary>Where each roster database lives in the game, as a legacy file name.</summary>
    public static string LegacyName(string db) =>
        db == "nhl_game" ? "gamemodes/database/t3db/nhl_game.db" : $"gamemodes/roster/{db}.db";

    /// <summary>
    /// A mod for the mod list from a run that passed: the written databases are copied to
    /// %LOCALAPPDATA%\CenterIceModdingSuite\rostersync\&lt;date&gt;\ (the next run reuses dbwork) and listed as legacy
    /// file replacements. Nothing reaches a game until Apply.
    /// </summary>
    public static ModFile ToModFile(SyncRunResult result)
    {
        if (!result.Ok) throw new InvalidOperationException("The sync did not pass its checks; nothing to apply.");
        var dir = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "rostersync", result.Day.ToString("yyyy-MM-dd")));
        var mod = new ModFile
        {
            Name = $"Roster sync {result.Day:yyyy-MM-dd}",
            Kind = "rosters",
            Comment = "NHL API rosters in every roster database: " + string.Join(", ", result.Dbs.Select(d => d.Db.Name)) + ".",
            Legacy = [],
        };
        foreach (var d in result.Dbs.Where(d => d.Save is null))
        {
            var copy = Path.Combine(dir, d.Db.Name + ".db");
            File.Copy(d.OutDb!, copy, true);
            mod.Legacy.Add(new LegacyReplacement(LegacyName(d.Db.Name), copy));
        }
        return mod;
    }

    /// <summary>
    /// Writes the synced saved rosters back into their save folders (the CIMS game copy's user data only; the caller
    /// passes saves found there). Each save folder is backed up to %LOCALAPPDATA%\CenterIceModdingSuite\save-backups
    /// first (the first backup is kept). Refuses while NHL 24 is running.
    /// </summary>
    public static List<string> WriteSaves(SyncRunResult result)
    {
        if (!result.Ok) throw new InvalidOperationException("The sync did not pass its checks; nothing to write.");
        if (ApplyTarget.RunningGame() is { } running) throw new InvalidOperationException($"{running} is running. Close the game first.");
        var backup = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "save-backups"));
        var done = new List<string>();
        foreach (var d in result.Dbs.Where(d => d.Save is not null))
        {
            var db = File.ReadAllBytes(d.OutDb!);
            d.Save!.WriteDb([.. db, .. d.SaveTrailer!], Path.Combine(backup, d.Db.Name));
            done.Add(d.Save.Dir);
        }
        return done;
    }

    /// <summary>
    /// Players on an NHL team in any of the dumps whom no current roster matches, looked up in the player search.
    /// Returned for the club the search names (exact name, one active match, same goalie/skater) with the league of his
    /// latest games.
    /// </summary>
    static async Task<List<(ApiPlayer Player, string? League)>> UnlistedAsync(NhlApi nhlApi, DateOnly day, IEnumerable<string> dumps,
        Dictionary<string, List<ApiPlayer>> api, Dictionary<string, int> teamMap, Action<string>? progress, CancellationToken ct)
    {
        var nhl = teamMap.Values.ToHashSet();
        var apiIds = api.Values.SelectMany(v => v).Select(p => p.Id).ToHashSet();
        var candidates = new Dictionary<string, DbPlayer>(StringComparer.Ordinal);
        foreach (var dir in dumps)
        {
            var db = RosterCsv.Load(dir, dir);
            var byLast = db.Players.ToLookup(x => RosterSync.Norm(x.Last));
            var byFull = db.Players.ToLookup(x => RosterSync.Norm(x.First + x.Last));
            var matched = api.Values.SelectMany(v => v).Select(p => RosterSync.Match(p, byLast, byFull)?.Row).Where(r => r is not null).ToHashSet();
            foreach (var x in db.Players.Where(x => nhl.Contains(x.Team) && !matched.Contains(x.Row) && x.Last.Length > 0))
                candidates.TryAdd(RosterSync.Norm(x.First + x.Last) + "|" + x.Birth, x);
        }
        var found = new List<(ApiPlayer, string?)>();
        int n = 0;
        foreach (var x in candidates.Values)
        {
            progress?.Invoke($"NHL player search {++n}/{candidates.Count}");
            List<ApiPlayer> hits;
            try { hits = await nhlApi.SearchAsync($"{x.First} {x.Last}", day, ct); }
            catch (HttpRequestException ex) { SuiteLog.Write($"player search {x.First} {x.Last}: {ex.Message}"); continue; }
            var same = hits.Where(h => RosterSync.Norm(h.First + h.Last) == RosterSync.Norm(x.First + x.Last) && teamMap.ContainsKey(h.TeamAbbrev)).ToList();
            if (same.Count != 1 || apiIds.Contains(same[0].Id)) continue;
            if ((same[0].Position == "G") != (x.Position == 4)) continue;
            string? league;
            try { league = await nhlApi.LatestLeagueAsync(same[0].Id, day, ct); }
            catch (HttpRequestException ex) { SuiteLog.Write($"player page {x.First} {x.Last}: {ex.Message}"); continue; }
            if (league is not ("NHL" or "AHL" or null)) continue; // playing elsewhere (Europe...): released as before
            // The search has no birth date; the database player's own makes the match exact in every database.
            found.Add((same[0] with { Birth = x.Birth }, league));
            apiIds.Add(same[0].Id);
        }
        return found;
    }
}
