using System.Text.RegularExpressions;

namespace Suite.Rosters;

/// <summary>What one database's write did, for the per-database diff and the log.</summary>
public sealed class WriteReport
{
    public string Db { get; init; } = "";
    public List<string> Changes { get; } = [];
    public List<string> Warnings { get; } = [];
    public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);
    /// <summary>Tables whose CSV was written (only these go to db-import).</summary>
    public List<string> Tables { get; } = [];
    internal void Add(string kind, string line) { Counts[kind] = Counts.GetValueOrDefault(kind) + 1; Changes.Add(kind + "\t" + line); }
}

/// <summary>
/// Applies the NHL API rosters to one roster database dump, on that database's own terms (its own rows, slots and
/// lines), and writes the edited tables as CSV for db-import. Nothing grows: every table keeps its record slots,
/// moves reuse each team's free roster slots (key = team*40 + 0..39), and a team with no free slot is reported, not
/// overflowed. Players the API has on an NHL roster go there; NHL players no longer on one go to their club's AHL
/// affiliate when the club still lists them as a prospect, otherwise to free agency. Teams whose players changed get
/// their lines repaired so every line slot stays filled.
/// Tables: exhibitionplayerbiotable (team, proteam, jersey, position), exhibitionrostertable, exhibitionfreeagents;
/// read only: exhibitionplayers (game_id → player index), exhibitionteams (AHL affiliates).
/// </summary>
public static class RosterWriter
{
    const string Bio = "exhibitionplayerbiotable", Roster = "exhibitionrostertable", Players = "exhibitionplayers",
        FreeAgents = "exhibitionfreeagents", Teams = "exhibitionteams";

    static readonly HashSet<string> RowFields = ["playerindex", "key", "team", "rosterstatus", "captain", "playerstyle", "jersey"];
    static readonly Regex Dressed = new(@"^(l[1-4](c|lw|rw)|l[1-3](ld|rd)|g[12])$", RegexOptions.Compiled);

    enum Role { F, D, G, Skater }

    static Role RoleOf(string slot)
    {
        if (Regex.IsMatch(slot, @"(^|_)g[12]$")) return Role.G;
        if (slot.EndsWith("ld") || slot.EndsWith("rd") || slot.EndsWith("_d")) return Role.D;
        if (slot.EndsWith("lw") || slot.EndsWith("rw") || slot.EndsWith("c") || slot.EndsWith("_w")) return Role.F;
        return Role.Skater;
    }

    /// <summary>The group of slots one player may hold only once (a line, a power-play unit, the shootout list...).</summary>
    static string UnitOf(string slot)
    {
        if (slot.StartsWith("three_")) return "three";
        if (Regex.IsMatch(slot, @"^[sx]\d$")) return slot[..1];
        if (Regex.IsMatch(slot, @"^g\d$")) return "g";
        var m = Regex.Match(slot, @"^([a-z]+\d?_\d|l\d)");
        return m.Success ? m.Groups[1].Value : slot;
    }

    sealed class Member
    {
        public required string[] Row;
        public required string GameId;
        public int Pos;      // bio position 0 C, 1 LW, 2 RW, 3 D, 4 G
        public int Primary;  // bio positionprimary 0 C, 1 LW, 2 RW, 3 LD, 4 RD, 5 G
        public int Depth = 900;
        public bool Arrived;
    }

    public static WriteReport Write(string dbName, string dumpDir, string outDir,
        Dictionary<string, List<ApiPlayer>> api, Dictionary<string, List<ApiPlayer>> prospects, Dictionary<string, int> teamMap,
        IReadOnlySet<string>? onlyTeams = null, string? teamsDir = null)
    {
        var report = new WriteReport { Db = dbName };
        var bio = T3Csv.Load(dumpDir, Bio);
        var ro = T3Csv.Load(dumpDir, Roster);
        var fa = T3Csv.Load(dumpDir, FreeAgents);
        var ep = T3Csv.Load(dumpDir, Players);
        // The small roster databases have no team table; they use the main database's (teamsDir).
        var tm = T3Csv.Load(File.Exists(T3Csv.PathOf(dumpDir, Teams)) || teamsDir is null ? dumpDir : teamsDir, Teams);
        int roSlots = ro.Rows.Count, faSlots = fa.Rows.Count, bioSlots = bio.Rows.Count;

        // Who is who.
        var bioRows = bio.Rows.Take(bio.ValidCount).ToList();
        var bioByGame = new Dictionary<string, string[]>();
        foreach (var r in bioRows) bioByGame.TryAdd(bio.Get(r, "game_id"), r);
        var players = bioRows.Select((r, i) => new DbPlayer(i, bio.Get(r, "game_id"), bio.Get(r, "firstname"), bio.Get(r, "lastname"),
            RosterSync.BioDate(bio.Int(r, "year"), bio.Int(r, "month"), bio.Int(r, "day")), bio.Int(r, "team"), bio.Int(r, "jersey"),
            bio.Int(r, "position"), bio.Int(r, "hasportrait") == 1, bio.Int(r, "headid"))).ToList();
        // game_id → the player indices roster/free-agent rows use for him (many players have more than one).
        var epIndex = new Dictionary<string, List<string>>();
        var gameOfIndex = new Dictionary<string, string>();
        foreach (var r in ep.Rows.Take(ep.ValidCount))
        {
            var g = ep.Get(r, "playerid");
            if (!epIndex.TryGetValue(g, out var l)) epIndex[g] = l = [];
            l.Add(ep.Get(r, "index"));
            gameOfIndex.TryAdd(ep.Get(r, "index"), g);
        }
        // Only players the rosters can point at are matched (an old duplicate bio row without one would steal the match).
        var byLast = players.Where(x => epIndex.ContainsKey(x.GameId)).ToLookup(x => RosterSync.Norm(x.Last));
        var byFull = players.Where(x => epIndex.ContainsKey(x.GameId)).ToLookup(x => RosterSync.Norm(x.First + x.Last));
        string Name(string game) => bioByGame.TryGetValue(game, out var b) ? $"{bio.Get(b, "firstname")} {bio.Get(b, "lastname")}" : "#" + game;

        var nhl = teamMap.Values.ToHashSet();
        // A trial on some clubs: only players joining or leaving them are touched.
        var only = onlyTeams?.Select(a => teamMap.TryGetValue(a, out var t) ? t : -1).ToHashSet();
        var teamRows = tm.Rows.Take(tm.ValidCount).ToList();
        var affiliate = new Dictionary<int, int>();
        foreach (var t in teamRows)
            if (nhl.Contains(tm.Int(t, "index")) && tm.Int(t, "ahlaffiliate") >= 0) affiliate[tm.Int(t, "index")] = tm.Int(t, "ahlaffiliate");
        var clubTeams = nhl.Concat(affiliate.Values).ToHashSet();
        var abbrOf = teamRows.GroupBy(t => tm.Int(t, "index")).ToDictionary(g => g.Key, g => tm.Get(g.First(), "abbrname"));
        string Team(int t) => t < 0 ? "FA" : abbrOf.TryGetValue(t, out var a) && a.Length > 0 ? a : "#" + t;

        var rows = ro.Rows.Take(ro.ValidCount).ToList();
        var slotCols = ro.Header.Where(h => !RowFields.Contains(h)).ToList();
        string GameOf(string[] r) => gameOfIndex.GetValueOrDefault(ro.Get(r, "playerindex"), "");

        // Depth: how high a player sits in his current NHL lines (lower is better), so replacements keep the order.
        var depth = new Dictionary<string, int>();
        foreach (var r in rows.Where(r => nhl.Contains(ro.Int(r, "team"))))
        {
            int best = 900;
            foreach (var c in slotCols)
                if (ro.Get(r, c) == "1" && Dressed.IsMatch(c))
                    best = Math.Min(best, (c[1] - '0') * 100);
            int specials = slotCols.Count(c => ro.Get(r, c) == "1" && !Dressed.IsMatch(c));
            var g = GameOf(r);
            if (g.Length > 0) depth[g] = Math.Min(depth.GetValueOrDefault(g, 999), best - Math.Min(specials, 99));
        }

        // What the API wants.
        var want = new Dictionary<string, (int Team, ApiPlayer P)>();
        foreach (var (abbrev, list) in api)
        {
            if (!teamMap.TryGetValue(abbrev, out var team)) { report.Warnings.Add($"API team {abbrev} has no NHL 24 team in nhlapi_teams.tsv"); continue; }
            foreach (var p in list)
            {
                var m = RosterSync.Match(p, byLast, byFull);
                if (m is null) { report.Add("NotInDb", $"{p.First} {p.Last}\t{abbrev}"); continue; }
                if (!epIndex.ContainsKey(m.GameId)) { report.Add("NotInDb", $"{p.First} {p.Last}\t{abbrev}\tno exhibitionplayers row"); continue; }
                if (!want.TryAdd(m.GameId, (team, p))) report.Warnings.Add($"{p.First} {p.Last} matched a player already placed ({Name(m.GameId)})");
            }
        }
        var prospectOf = new Dictionary<string, int>();
        foreach (var (abbrev, list) in prospects)
            if (teamMap.TryGetValue(abbrev, out var team))
                foreach (var p in list)
                    if (RosterSync.Match(p, byLast, byFull) is { } m && !want.ContainsKey(m.GameId)) prospectOf.TryAdd(m.GameId, team);

        // Everyone whose club placement is decided now: API-rostered players, and NHL players the API no longer rosters.
        var onNhlRow = rows.Where(r => nhl.Contains(ro.Int(r, "team"))).Select(GameOf).Where(g => g.Length > 0).ToHashSet();
        var decided = new Dictionary<string, int>(); // game_id → target team (-1 free agent)
        foreach (var (g, w) in want) decided[g] = w.Team;
        foreach (var p in players)
        {
            if (decided.ContainsKey(p.GameId) || p.GameId.Length == 0) continue;
            if (!nhl.Contains(p.Team) && !onNhlRow.Contains(p.GameId)) continue;
            decided[p.GameId] = prospectOf.TryGetValue(p.GameId, out var org) && affiliate.TryGetValue(org, out var ahl) ? ahl : -1;
        }

        if (only is not null)
        {
            // Trial: everyone joining or leaving those clubs, wherever they come from or go to.
            var onlyRows = rows.Where(r => only.Contains(ro.Int(r, "team"))).Select(GameOf).ToHashSet();
            foreach (var g in decided.Keys.ToList())
                if (!only.Contains(decided[g]) && !only.Contains(bio.Int(bioByGame[g], "team")) && !onlyRows.Contains(g)) decided.Remove(g);
        }

        var changedTeams = new HashSet<int>();
        var vacatedBy = new Dictionary<(int Team, string Slot), string>(); // line slot → player who left it
        var captainLeft = new Dictionary<int, List<(string Game, string Value)>>();
        var faRows = fa.Rows.Take(fa.ValidCount).ToList();
        var faSet = faRows.Select(r => fa.Get(r, "playerindex")).ToHashSet();
        var styleByPos = rows.GroupBy(r => bioByGame.TryGetValue(GameOf(r), out var b) ? bio.Int(b, "position") : -1)
            .ToDictionary(g => g.Key, g => g.GroupBy(r => ro.Get(r, "playerstyle")).OrderByDescending(x => x.Count()).First().Key);

        // Pass 1: everyone leaves the club rosters he no longer belongs on, so arrivals find the slots departures free.
        var moves = new List<(string Game, int Target, string[]? Keep, string[]? Template, string PIndex, HashSet<string> Indices)>();
        foreach (var (game, target) in decided)
        {
            if (!epIndex.TryGetValue(game, out var idxList)) { report.Warnings.Add($"{Name(game)} has no exhibitionplayers row; skipped"); continue; }
            var indices = idxList.ToHashSet();
            var mine = rows.Where(r => indices.Contains(ro.Get(r, "playerindex"))).ToList();
            var clubRows = mine.Where(r => clubTeams.Contains(ro.Int(r, "team"))).ToList();
            var keep = clubRows.FirstOrDefault(r => ro.Int(r, "team") == target);
            var template = clubRows.FirstOrDefault(r => nhl.Contains(ro.Int(r, "team"))) ?? clubRows.FirstOrDefault() ?? mine.FirstOrDefault();
            var pIndex = template is not null ? ro.Get(template, "playerindex") : idxList[0];
            foreach (var r in clubRows.Where(r => r != keep))
            {
                int t = ro.Int(r, "team");
                changedTeams.Add(t);
                foreach (var c in slotCols)
                    if (ro.Get(r, c) == "1") vacatedBy[(t, c)] = game;
                if (ro.Get(r, "captain") is "1" or "2")
                {
                    if (!captainLeft.TryGetValue(t, out var cl)) captainLeft[t] = cl = [];
                    cl.Add((game, ro.Get(r, "captain")));
                }
                rows.Remove(r);
            }
            moves.Add((game, target, keep, template, pIndex, indices));
        }

        // Pass 2: join the target roster (or free agency) and update the bio.
        foreach (var (game, decidedTeam, keep, template, pIndex, indices) in moves)
        {
            int target = decidedTeam;
            var b = bioByGame[game];
            int oldTeam = bio.Int(b, "team");
            int jersey = target >= 0 && want.TryGetValue(game, out var w0) && w0.P.Number is { } n ? n : bio.Int(b, "jersey");
            if (target >= 0 && keep is null)
            {
                var used = rows.Where(r => ro.Int(r, "team") == target).Select(r => ro.Int(r, "key") - target * 40).ToHashSet();
                int slot = Enumerable.Range(0, 40).FirstOrDefault(s => !used.Contains(s), -1);
                if (slot < 0)
                {
                    report.Warnings.Add($"{Team(target)} has no free roster slot for {Name(game)}; left in free agency");
                    target = -1;
                }
                else
                {
                    var row = template is not null ? (string[])template.Clone() : ro.BlankRow();
                    foreach (var c in slotCols) ro.Set(row, c, 0);
                    ro.Set(row, "playerindex", pIndex);
                    ro.Set(row, "team", target);
                    ro.Set(row, "key", target * 40 + slot);
                    ro.Set(row, "captain", 0);
                    ro.Set(row, "rosterstatus", 0);
                    if (template is null) ro.Set(row, "playerstyle", styleByPos.GetValueOrDefault(bio.Int(b, "position"), "0"));
                    ro.Set(row, "jersey", jersey);
                    rows.Add(row);
                    changedTeams.Add(target);
                }
            }
            else if (keep is not null && nhl.Contains(target) && ro.Int(keep, "jersey") != jersey)
                ro.Set(keep, "jersey", jersey);

            // Free agency list.
            if (target < 0 && !indices.Overlaps(faSet))
            {
                // A free-agent record holding player index 0 would be blank and dropped by the database writer.
                if (pIndex == "0") report.Warnings.Add($"{Name(game)} uses player index 0 and cannot be listed as a free agent");
                else { faSet.Add(pIndex); var r = fa.BlankRow(); fa.Set(r, "playerindex", pIndex); faRows.Add(r); }
            }
            if (target >= 0 && indices.Overlaps(faSet))
            {
                faSet.ExceptWith(indices);
                faRows.RemoveAll(r => indices.Contains(fa.Get(r, "playerindex")));
            }

            // Bio.
            int org = target < 0 ? -1 : nhl.Contains(target) ? target : prospectOf.GetValueOrDefault(game, bio.Int(b, "proteam"));
            if (oldTeam != target)
            {
                var kind = target < 0 ? "Release" : nhl.Contains(target) ? (nhl.Contains(oldTeam) ? "Move" : "CallUp") : "ToAHL";
                report.Add(kind, $"{Name(game)}\t{Team(oldTeam)}\t{Team(target)}");
            }
            bio.Set(b, "team", target);
            if (bio.Int(b, "proteam") != org) bio.Set(b, "proteam", org);
            if (!want.TryGetValue(game, out var w)) continue;
            if (w.P.Number is { } num && bio.Int(b, "jersey") != num)
            {
                report.Add("Number", $"{Name(game)}\t{bio.Get(b, "jersey")}\t{num}");
                bio.Set(b, "jersey", num);
            }
            int pos = RosterSync.PositionCode(w.P.Position);
            if (pos >= 0 && bio.Int(b, "position") != pos)
            {
                report.Add("Position", $"{Name(game)}\t{RosterSync.PositionName(bio.Int(b, "position"))}\t{RosterSync.PositionName(pos)}");
                int oldPrimary = bio.Int(b, "positionprimary");
                int primary = pos switch
                {
                    3 when oldPrimary is 3 or 4 => oldPrimary,
                    3 => w.P.Shoots == "R" || (w.P.Shoots is null && bio.Int(b, "handedness") == 1) ? 4 : 3,
                    4 => 5,
                    _ => pos,
                };
                bio.Set(b, "position", pos);
                bio.Set(b, "positionprimary", primary);
            }
        }

        // A slot nobody on the team can fill (an AHL club stripped of its goalies by call-ups...) gets a free agent.
        var orgOfAhl = affiliate.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
        var placed = moves.Where(m => m.Target >= 0).Select(m => m.Game).ToHashSet();
        var released = moves.Where(m => m.Target < 0).Select(m => m.Game).ToHashSet();
        Member? Sign(int team, Role role)
        {
            var pick = bioRows.Where(r => bio.Int(r, "team") == -1 && epIndex.ContainsKey(bio.Get(r, "game_id")) && !placed.Contains(bio.Get(r, "game_id")))
                .Where(r => role switch { Role.G => bio.Int(r, "position") == 4, Role.D => bio.Int(r, "position") == 3, Role.F => bio.Int(r, "position") is >= 0 and <= 2, _ => bio.Int(r, "position") is >= 0 and <= 3 })
                // Young free agents first (the pool is full of retired players), youngest first; players the sync
                // just released (no NHL club, often retired or abroad) only when nobody else fits.
                .OrderBy(r => released.Contains(bio.Get(r, "game_id")) ? 1 : 0)
                .ThenBy(r => DateTime.Today.Year - 1900 - bio.Int(r, "year") > 30 ? 1 : 0)
                .ThenByDescending(r => bio.Int(r, "year")).FirstOrDefault();
            if (pick is null) return null;
            var game = bio.Get(pick, "game_id");
            var indices = epIndex[game].ToHashSet();
            var used = rows.Where(r => ro.Int(r, "team") == team).Select(r => ro.Int(r, "key") - team * 40).ToHashSet();
            int slot = Enumerable.Range(0, 40).FirstOrDefault(s => !used.Contains(s), -1);
            if (slot < 0) return null;
            var template = rows.FirstOrDefault(r => indices.Contains(ro.Get(r, "playerindex")));
            var row = template is not null ? (string[])template.Clone() : ro.BlankRow();
            foreach (var c in slotCols) ro.Set(row, c, 0);
            ro.Set(row, "playerindex", template is not null ? ro.Get(template, "playerindex") : epIndex[game][0]);
            ro.Set(row, "team", team);
            ro.Set(row, "key", team * 40 + slot);
            ro.Set(row, "captain", 0);
            ro.Set(row, "rosterstatus", 0);
            if (template is null) ro.Set(row, "playerstyle", styleByPos.GetValueOrDefault(bio.Int(pick, "position"), "0"));
            ro.Set(row, "jersey", bio.Int(pick, "jersey"));
            rows.Add(row);
            placed.Add(game);
            bio.Set(pick, "team", team);
            bio.Set(pick, "proteam", nhl.Contains(team) ? team : orgOfAhl.GetValueOrDefault(team, -1));
            faSet.ExceptWith(indices);
            faRows.RemoveAll(r => indices.Contains(fa.Get(r, "playerindex")));
            report.Add("Signed", $"{Name(game)}	FA	{Team(team)}");
            return new Member { Row = row, GameId = game, Pos = bio.Int(pick, "position"), Primary = bio.Int(pick, "positionprimary"), Depth = depth.GetValueOrDefault(game, 900), Arrived = true };
        }

        foreach (var t in changedTeams.Order()) RepairLines(t, rows, ro, slotCols, vacatedBy, captainLeft, depth, GameOf, bioByGame, bio, report, Team, Sign);

        // Numbers the API did not give (signings, players it lists without one) must not clash on an NHL team.
        foreach (var t in changedTeams.Where(nhl.Contains))
        {
            var mine = rows.Where(r => ro.Int(r, "team") == t).ToList();
            // Current-roster numbers first; numbers from the player search (injured players) give way when they clash.
            var fromApi = mine.Where(r => want.TryGetValue(GameOf(r), out var w) && w.P.Number is not null && !w.P.FromSearch).ToList();
            var taken = fromApi.Select(r => ro.Int(r, "jersey")).ToHashSet();
            foreach (var r in mine.Except(fromApi).OrderBy(r => want.ContainsKey(GameOf(r)) ? 0 : 1).ThenBy(r => depth.GetValueOrDefault(GameOf(r), 900)))
            {
                int j = ro.Int(r, "jersey");
                if (taken.Add(j)) continue;
                int free = Enumerable.Range(2, 97).First(n => !taken.Contains(n));
                taken.Add(free);
                ro.Set(r, "jersey", free);
                if (bioByGame.TryGetValue(GameOf(r), out var b)) bio.Set(b, "jersey", free);
                report.Add("Number", $"{Name(GameOf(r))}	{j}	{free}	{Team(t)} already has {j}");
            }
        }

        // Write back: in-use rows first, roster keys in order, every table at its original slot count.
        ro.ReplaceUsed(rows.OrderBy(r => ro.Int(r, "key")).ToList());
        fa.ReplaceUsed(faRows.OrderBy(r => fa.Int(r, "playerindex")).ToList());
        if (ro.Rows.Count != roSlots || fa.Rows.Count != faSlots || bio.Rows.Count != bioSlots)
            throw new InvalidOperationException("A table changed its record slot count; nothing written.");
        var keys = ro.Rows.Take(ro.ValidCount).Select(r => ro.Get(r, "key")).ToList();
        if (keys.Distinct().Count() != keys.Count) throw new InvalidOperationException("Two roster rows ended up with the same key; nothing written.");
        bio.Save(outDir); ro.Save(outDir); fa.Save(outDir);
        report.Tables.AddRange([Bio, Roster, FreeAgents]);
        return report;
    }

    static void RepairLines(int team, List<string[]> rows, T3Csv ro, List<string> slotCols,
        Dictionary<(int, string), string> vacatedBy, Dictionary<int, List<(string Game, string Value)>> captainLeft,
        Dictionary<string, int> depth, Func<string[], string> gameOf, Dictionary<string, string[]> bioByGame, T3Csv bio,
        WriteReport report, Func<int, string> teamName, Func<int, Role, Member?> sign)
    {
        var members = rows.Where(r => ro.Int(r, "team") == team).Select(r =>
        {
            var g = gameOf(r);
            var b = bioByGame.GetValueOrDefault(g);
            return new Member
            {
                Row = r, GameId = g,
                Pos = b is null ? -1 : bio.Int(b, "position"),
                Primary = b is null ? -1 : bio.Int(b, "positionprimary"),
                Depth = depth.GetValueOrDefault(g, 900),
                Arrived = slotCols.All(c => ro.Get(r, c) != "1"),
            };
        }).ToList();
        if (members.Count == 0) return;
        bool Holds(Member m, string c) => ro.Get(m.Row, c) == "1";
        bool InUnit(Member m, string unit) => slotCols.Any(c => UnitOf(c) == unit && Holds(m, c));
        bool DressedNow(Member m) => slotCols.Any(c => Dressed.IsMatch(c) && Holds(m, c));
        bool Fits(Member m, Role role) => role switch
        {
            Role.G => m.Pos == 4,
            Role.D => m.Pos == 3,
            Role.F => m.Pos is >= 0 and <= 2,
            _ => m.Pos is >= 0 and <= 3,
        };
        int Natural(Member m, string slot) => slot switch
        {
            _ when slot.EndsWith("lw") => m.Pos == 1 ? 0 : 1,
            _ when slot.EndsWith("rw") => m.Pos == 2 ? 0 : 1,
            _ when slot.EndsWith("ld") => m.Primary == 3 ? 0 : 1,
            _ when slot.EndsWith("rd") => m.Primary == 4 ? 0 : 1,
            _ when slot.EndsWith("c") => m.Pos == 0 ? 0 : 1,
            _ => 0,
        };
        bool Taken(string c) => members.Any(m => Holds(m, c));

        // Line slots first (top line first), then every other slot the leaving players held.
        var successor = new Dictionary<string, Member>();
        var vacated = slotCols.Where(c => vacatedBy.ContainsKey((team, c)) && !Taken(c))
            .OrderBy(c => Dressed.IsMatch(c) ? 0 : 1).ThenBy(c => c).ToList();
        foreach (var c in vacated)
        {
            var role = RoleOf(c);
            var unit = UnitOf(c);
            var left = vacatedBy[(team, c)];
            Member? pick;
            if (Dressed.IsMatch(c))
            {
                var free = members.Where(m => !DressedNow(m)).ToList();
                pick = free.Where(m => Fits(m, role)).OrderBy(m => Natural(m, c)).ThenBy(m => m.Arrived ? 0 : 1).ThenBy(m => m.Depth).FirstOrDefault()
                    ?? (role == Role.G ? null : free.Where(m => Fits(m, Role.Skater)).OrderBy(m => m.Depth).FirstOrDefault());
                if (pick is null && sign(team, role) is { } signed) { members.Add(signed); pick = signed; }
                if (pick is not null) successor.TryAdd(left, pick);
            }
            else
            {
                pick = successor.TryGetValue(left, out var s) && Fits(s, role) && !InUnit(s, unit) ? s : null;
                pick ??= members.Where(m => DressedNow(m) && Fits(m, role) && !InUnit(m, unit)).OrderBy(m => Natural(m, c)).ThenBy(m => m.Depth).FirstOrDefault()
                    ?? members.Where(m => Fits(m, role) && !InUnit(m, unit)).OrderBy(m => m.Depth).FirstOrDefault();
            }
            if (pick is null) { report.Warnings.Add($"{teamName(team)}: nobody can fill {c}"); continue; }
            ro.Set(pick.Row, c, 1);
        }

        // Captaincy: a captain who leaves is replaced by the team's top alternate, and the team keeps as many letters
        // as it had (stock teams have a C and two A's, or three A's). New letters go to the best-placed dressed skater
        // who stayed (an arrival only when nobody did).
        var gone = captainLeft.GetValueOrDefault(team) ?? [];
        Member? Best(Func<Member, bool> ok) => members.Where(m => ok(m) && DressedNow(m) && m.Pos is >= 0 and <= 3)
            .OrderBy(m => m.Arrived ? 1 : 0).ThenBy(m => m.Depth).FirstOrDefault();
        int Letters() => members.Count(m => ro.Get(m.Row, "captain") != "0");
        int wantLetters = Letters() + gone.Count;
        if (gone.Any(l => l.Value == "1") && !members.Any(m => ro.Get(m.Row, "captain") == "1"))
            if ((Best(m => ro.Get(m.Row, "captain") == "2") ?? Best(m => ro.Get(m.Row, "captain") == "0")) is { } c) ro.Set(c.Row, "captain", 1);
        while (Letters() < wantLetters && Best(m => ro.Get(m.Row, "captain") == "0") is { } a) ro.Set(a.Row, "captain", 2);

        foreach (var m in members) ro.Set(m.Row, "rosterstatus", DressedNow(m) ? 1 : 0);
        int dressed = members.Count(DressedNow);
        if (dressed < 20) report.Warnings.Add($"{teamName(team)}: only {dressed} players dressed after the sync");
    }
}
