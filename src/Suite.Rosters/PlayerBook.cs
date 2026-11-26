namespace Suite.Rosters;

/// <summary>One player as the list shows him.</summary>
public sealed record PlayerSummary(long GameId, string First, string Last, int Position, int Team, int Jersey, int Overall, int BirthYear, int CapHit)
{
    public string Name => $"{First} {Last}".Trim();
    public bool Goalie => Position == 4;
    public string PositionName => Position switch { 0 => "C", 1 => "LW", 2 => "RW", 3 => "D", 4 => "G", _ => "?" };
    public string TeamName { get; init; } = "";
}

/// <summary>One editable value of a player (or of one of his roster rows): where it lives and how it is shown.</summary>
public sealed record PlayerField(string TableKey, T3Db.Table Table, int Row, T3Db.Field Field, FieldSpec Spec, string Label, bool ReadOnly, int? RosterTeam = null)
{
    public string Key => $"{TableKey}:{Row}:{Field.Name}";
}

/// <summary>
/// The player side of one roster database, read and edited in place: the list of players, and for one player every value of his bio, ratings,
/// gear and roster rows with the group, label and kind the <see cref="FieldCatalog"/> gives it. All edits go through <see cref="T3Db"/>, so a value
/// outside a field's range is refused and the file's layout never changes.
/// </summary>
public sealed class PlayerBook
{
    public T3Db Db { get; }
    readonly T3Db.Table _bio, _players, _roster, _skater, _goalie, _skaterGear, _goalieGear;
    readonly Dictionary<long, int> _bioRow = [], _skaterRow = [], _goalieRow = [], _skaterGearRow = [], _goalieGearRow = [];
    readonly Dictionary<long, long> _gameOfIndex = [];
    readonly Dictionary<long, List<int>> _rosterRowsOfGame = [];
    readonly Dictionary<int, string> _teamNames = [];

    public PlayerBook(T3Db db)
    {
        Db = db;
        _bio = db.Tables["exhibitionplayerbiotable"]; _players = db.Tables["exhibitionplayers"]; _roster = db.Tables["exhibitionrostertable"];
        _skater = db.Tables["exhibitionskaterai"]; _goalie = db.Tables["exhibitiongoalieai"];
        _skaterGear = db.Tables["exhibitionskaterequipment"]; _goalieGear = db.Tables["exhibitiongoalieequipment"];
        Index(_bio, _bioRow); Index(_skater, _skaterRow); Index(_goalie, _goalieRow); Index(_skaterGear, _skaterGearRow); Index(_goalieGear, _goalieGearRow);
        for (int r = 0; r < _players.InUse; r++) _gameOfIndex[db.GetInt(_players, r, "index")] = db.GetInt(_players, r, "playerid");
        for (int r = 0; r < _roster.InUse; r++)
            if (_gameOfIndex.TryGetValue(db.GetInt(_roster, r, "playerindex"), out var g))
            {
                if (!_rosterRowsOfGame.TryGetValue(g, out var list)) _rosterRowsOfGame[g] = list = [];
                list.Add(r);
            }
        if (db.Tables.TryGetValue("exhibitionteams", out var teams))
            for (int r = 0; r < teams.InUse; r++)
            {
                var name = db.GetString(teams, r, "teamname");
                if (name.Length == 0) name = db.GetString(teams, r, "abbrname");
                _teamNames[(int)db.GetInt(teams, r, "index")] = name;
            }
    }

    void Index(T3Db.Table t, Dictionary<long, int> map)
    {
        for (int r = 0; r < t.InUse; r++) map[Db.GetInt(t, r, "game_id")] = r;
    }

    /// <summary>Every team of the database as (index, name), for the team lists.</summary>
    public IReadOnlyList<(long Value, string Name)> Teams() => _teamNames.OrderBy(kv => kv.Key).Select(kv => ((long)kv.Key, kv.Value)).Prepend((-1L, "None")).ToList();

    public string TeamName(int team) => team < 0 ? "None" : _teamNames.GetValueOrDefault(team, "Team " + team);

    public IReadOnlyList<PlayerSummary> Players()
    {
        var list = new List<PlayerSummary>(_bio.InUse);
        for (int r = 0; r < _bio.InUse; r++)
        {
            long g = Db.GetInt(_bio, r, "game_id"); int pos = (int)Db.GetInt(_bio, r, "position");
            int overall = pos == 4 ? (_goalieRow.TryGetValue(g, out var gr) ? (int)Db.GetInt(_goalie, gr, "skill_overall") : 0)
                : _skaterRow.TryGetValue(g, out var sr) ? (int)Db.GetInt(_skater, sr, "skill_overall") : 0;
            int team = (int)Db.GetInt(_bio, r, "team");
            list.Add(new PlayerSummary(g, Db.GetString(_bio, r, "firstname"), Db.GetString(_bio, r, "lastname"), pos, team,
                (int)Db.GetInt(_bio, r, "jersey"), overall, (int)Db.GetInt(_bio, r, "year") + 1900, (int)Db.GetInt(_bio, r, "contractdollars") * 5000) { TeamName = TeamName(team) });
        }
        return list;
    }

    public bool Has(long gameId) => _bioRow.ContainsKey(gameId);

    /// <summary>The roster rows (memberships) of a player: one per team he is on.</summary>
    public IReadOnlyList<int> RosterRows(long gameId) => _rosterRowsOfGame.TryGetValue(gameId, out var l) ? l : [];

    /// <summary>Every editable value of a player, in the catalogue's group order.</summary>
    public IReadOnlyList<PlayerField> FieldsOf(long gameId)
    {
        var list = new List<PlayerField>();
        if (!_bioRow.TryGetValue(gameId, out var bioRow)) return list;
        bool goalie = Db.GetInt(_bio, bioRow, "position") == 4;
        void From(string key, T3Db.Table table, int row)
        {
            foreach (var f in table.Fields.Values.OrderBy(f => f.BitOffset))
            {
                var spec = FieldCatalog.For(key, f);
                list.Add(new PlayerField(key, table, row, f, spec, spec.Label, FieldCatalog.IsReadOnly(key, f.Name)));
            }
        }
        From("bio", _bio, bioRow);
        if (goalie) { if (_goalieRow.TryGetValue(gameId, out var r)) From("goalie", _goalie, r); if (_goalieGearRow.TryGetValue(gameId, out r)) From("goaliegear", _goalieGear, r); }
        else { if (_skaterRow.TryGetValue(gameId, out var r)) From("skater", _skater, r); if (_skaterGearRow.TryGetValue(gameId, out r)) From("skatergear", _skaterGear, r); }
        foreach (var rr in RosterRows(gameId))
        {
            int team = (int)Db.GetInt(_roster, rr, "team");
            foreach (var f in _roster.Fields.Values.OrderBy(f => f.BitOffset))
            {
                if (f.Depth == 1 && f.Name != "rosterstatus") continue; // line slots are edited on the lines page
                var spec = FieldCatalog.For("roster", f);
                list.Add(new PlayerField("roster", _roster, rr, f, spec, $"{TeamName(team)}: {spec.Label}", FieldCatalog.IsReadOnly("roster", f.Name), team));
            }
        }
        var order = FieldCatalog.PlayerGroups.Select((g, i) => (g, i)).ToDictionary(p => p.g, p => p.i);
        var specOrder = new Dictionary<string, int>();
        foreach (var dict in new[] { FieldCatalog.Bio, FieldCatalog.Skater, FieldCatalog.Goalie, FieldCatalog.Gear, FieldCatalog.Roster })
            foreach (var k in dict.Keys) specOrder.TryAdd(k, specOrder.Count);
        return list.OrderBy(f => order.GetValueOrDefault(f.Spec.Group, order.Count)).ThenBy(f => f.RosterTeam ?? -1)
            .ThenBy(f => specOrder.GetValueOrDefault(f.Field.Name, int.MaxValue)).ThenBy(f => Natural(f.Field.Name), StringComparer.Ordinal).ToList();
    }

    static string Natural(string name) => System.Text.RegularExpressions.Regex.Replace(name, "\\d+", m => m.Value.PadLeft(4, '0'));

    /// <summary>Every editable value of one club (a row of exhibitionteams), grouped by the catalogue's team groups.</summary>
    public IReadOnlyList<PlayerField> TeamFields(int teamIndex)
    {
        var list = new List<PlayerField>();
        if (!Db.Tables.TryGetValue("exhibitionteams", out var teams)) return list;
        for (int r = 0; r < teams.InUse; r++)
        {
            if (Db.GetInt(teams, r, "index") != teamIndex) continue;
            foreach (var f in teams.Fields.Values.OrderBy(f => f.BitOffset))
            {
                var spec = FieldCatalog.For("team", f);
                list.Add(new PlayerField("team", teams, r, f, spec, spec.Label, FieldCatalog.IsReadOnly("team", f.Name)));
            }
            break;
        }
        var order = FieldCatalog.TeamGroups.Select((g, i) => (g, i)).ToDictionary(p => p.g, p => p.i);
        var specOrder = new Dictionary<string, int>();
        foreach (var k in FieldCatalog.Team.Keys) specOrder.TryAdd(k, specOrder.Count);
        return list.OrderBy(f => order.GetValueOrDefault(f.Spec.Group, order.Count)).ThenBy(f => specOrder.GetValueOrDefault(f.Field.Name, int.MaxValue))
            .ThenBy(f => Natural(f.Field.Name), StringComparer.Ordinal).ToList();
    }

    public long GetInt(PlayerField f) => Db.GetInt(f.Table, f.Row, f.Field.Name);
    public string GetText(PlayerField f) => Db.GetString(f.Table, f.Row, f.Field.Name);

    /// <summary>The range a numeric field accepts (low, high), from its bit width and range low.</summary>
    public static (long Low, long High) Range(T3Db.Field f) => (f.RangeLow, f.Depth >= 63 ? long.MaxValue : f.RangeLow + (1L << f.Depth) - 1);

    public void SetInt(PlayerField f, long value)
    {
        if (f.ReadOnly) throw new InvalidOperationException($"{f.Label} cannot be changed here.");
        Db.SetInt(f.Table, f.Row, f.Field.Name, value);
    }

    public void SetText(PlayerField f, string text)
    {
        if (f.ReadOnly) throw new InvalidOperationException($"{f.Label} cannot be changed here.");
        Db.SetString(f.Table, f.Row, f.Field.Name, text);
    }
}

/// <summary>
/// Keeps the five roster databases the same. The player data of nhl_game (bios, player index, roster rows, ratings, gear, free agents) is copied
/// into the other databases table for table, and nhl_game's online roster copy is made equal to its roster again. A mismatch between the five
/// crashes the game, so a studio save of the player tables always goes through here.
/// </summary>
public static class PlayerMirror
{
    public static readonly string[] Tables =
    [
        "exhibitionplayerbiotable", "exhibitionplayers", "exhibitionrostertable", "exhibitionskaterai", "exhibitionskaterequipment",
        "exhibitiongoalieai", "exhibitiongoalieequipment", "exhibitionfreeagents",
    ];

    /// <summary>Copies the player tables of <paramref name="main"/> into <paramref name="other"/>. Roster rows of a team the other database does not have are left out.</summary>
    public static void CopyInto(T3Db main, T3Db other)
    {
        foreach (var name in Tables)
        {
            if (!main.Tables.TryGetValue(name, out var src) || !other.Tables.TryGetValue(name, out var dst)) continue;
            Func<int, bool>? keep = null;
            if (name == "exhibitionrostertable" && other.Tables.TryGetValue("exhibitionteams", out var teams))
            {
                var have = Enumerable.Range(0, teams.InUse).Select(r => other.GetInt(teams, r, "index")).ToHashSet();
                keep = r => have.Contains(main.GetInt(src, r, "team"));
            }
            other.CopyRecords(dst, main, src, keep);
        }
    }

    /// <summary>nhl_game's customonlinerostertable is a row-for-row copy of its exhibitionrostertable (stock keeps it so).</summary>
    public static void SyncOnlineRoster(T3Db db)
    {
        if (db.Tables.TryGetValue("customonlinerostertable", out var online) && db.Tables.TryGetValue("exhibitionrostertable", out var roster))
            db.CopyRecords(online, db, roster);
    }
}
