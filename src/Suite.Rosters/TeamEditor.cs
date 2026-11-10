namespace Suite.Rosters;

/// <summary>
/// The teams / lines / strategies editing model over one roster database (a <see cref="T3Db"/>), for the Teams &amp; lines
/// editor page. Every edit is an in-place integer edit (T3Db.SetInt): the database never grows and never moves a record,
/// so the layout-guard rules (same size, same tables, same slots) hold by construction and only edited tables' section
/// CRCs change. Strings are read-only — names are for display. Not thread-safe; the UI works on the dispatcher thread.
/// </summary>
public sealed class TeamEditor
{
    public T3Db Db { get; }

    readonly T3Db.Table? _teams, _roster, _players, _bio, _strat;
    readonly Dictionary<string, (string First, string Last, int Pos)> _bioByGame = new(StringComparer.Ordinal);
    readonly Dictionary<int, string> _gameOfIndex = new();      // exhibitionplayers.index -> bio game_id
    readonly Dictionary<string, List<int>> _rowsByIndex = new(StringComparer.Ordinal); // exhibitionplayers.index -> roster rows
    readonly Dictionary<int, int> _dressShapeAtLoad = new();    // team -> count of core slots filled at load (verify compares)

    TeamEditor(T3Db db)
    {
        Db = db;
        db.Tables.TryGetValue("exhibitionteams", out _teams);
        db.Tables.TryGetValue("exhibitionrostertable", out _roster);
        db.Tables.TryGetValue("exhibitionplayers", out _players);
        db.Tables.TryGetValue("exhibitionplayerbiotable", out _bio);
        db.Tables.TryGetValue("exhibitionteamstrategies", out _strat);
        if (_bio is not null)
            for (int r = 0; r < _bio.InUse; r++)
                _bioByGame[Db.GetInt(_bio, r, "game_id").ToString()] = (Db.GetString(_bio, r, "firstname"), Db.GetString(_bio, r, "lastname"), (int)Db.GetInt(_bio, r, "position"));
        if (_players is not null)
            for (int r = 0; r < _players.InUse; r++)
                _gameOfIndex[(int)Db.GetInt(_players, r, "index")] = Db.GetInt(_players, r, "playerid").ToString();
        if (_roster is not null)
        {
            for (int r = 0; r < _roster.InUse; r++)
            {
                var idx = Db.GetInt(_roster, r, "playerindex").ToString();
                if (!_rowsByIndex.TryGetValue(idx, out var l)) _rowsByIndex[idx] = l = [];
                l.Add(r);
            }
            foreach (var t in TeamIndexes()) _dressShapeAtLoad[t] = CoreSlots.Count(s => HolderRow(t, s) is not null);
        }
    }

    public static TeamEditor Load(byte[] dbBytes, string metaXml) => new(T3Db.Load(dbBytes, metaXml));

    public bool HasTeams => _teams is not null && _roster is not null;
    public bool HasStrategies => _strat is not null;
    public bool IsDirty { get; private set; }

    // ---------------------------------------------------------------- teams

    public sealed record Team(int Index, string Name, int Row);

    public List<Team> Teams()
    {
        if (_teams is null) return [];
        var list = new List<Team>();
        for (int r = 0; r < _teams.InUse; r++)
        {
            var city = Db.GetString(_teams, r, "cityname");
            var name = Db.GetString(_teams, r, "teamname");
            list.Add(new Team((int)Db.GetInt(_teams, r, "index"), $"{city} {name}".Trim(), r));
        }
        return list;
    }

    List<int> TeamIndexes()
    {
        var set = new HashSet<int>();
        if (_roster is not null)
            for (int r = 0; r < _roster.InUse; r++) set.Add((int)Db.GetInt(_roster, r, "team"));
        return set.Order().ToList();
    }

    // ---------------------------------------------------------------- players

    public sealed record Player(int RosterRow, int PlayerIndex, string GameId, string First, string Last, int Pos,
        int Jersey, int Captain, int Style, int Dressed)
    {
        public string Name => (First + " " + Last).Trim();
    }

    /// <summary>The team's roster rows, in row order. Positions: 0 C, 1 LW, 2 RW, 3 D, 4 G (bio position).</summary>
    public List<Player> Players(int team)
    {
        if (_roster is null) return [];
        var list = new List<Player>();
        for (int r = 0; r < _roster.InUse; r++)
        {
            if (Db.GetInt(_roster, r, "team") != team) continue;
            var idx = (int)Db.GetInt(_roster, r, "playerindex");
            _bioByGame.TryGetValue(_gameOfIndex.GetValueOrDefault(idx, ""), out var b);
            list.Add(new Player(r, idx, _gameOfIndex.GetValueOrDefault(idx, ""), b.First, b.Last, b.Pos,
                (int)Db.GetInt(_roster, r, "jersey"), (int)Db.GetInt(_roster, r, "captain"), (int)Db.GetInt(_roster, r, "playerstyle"),
                (int)Db.GetInt(_roster, r, "rosterstatus")));
        }
        return list;
    }

    public void SetJersey(int rosterRow, int value) { Set(_roster, rosterRow, "jersey", value); }
    public void SetCaptain(int rosterRow, int value) { Set(_roster, rosterRow, "captain", value); }
    public void SetPlayerStyle(int rosterRow, int value) { Set(_roster, rosterRow, "playerstyle", value); }
    public void SetDressed(int rosterRow, bool value) { Set(_roster, rosterRow, "rosterstatus", value ? 1 : 0); }

    // ---------------------------------------------------------------- lines

    /// <summary>The 20 slots that make a dressed team: 4 lines (C/LW/RW), 3 pairs (LD/RD), 2 goalies.</summary>
    public static readonly string[] CoreSlots =
    [
        "l1c", "l1lw", "l1rw", "l2c", "l2lw", "l2rw", "l3c", "l3lw", "l3rw", "l4c", "l4lw", "l4rw",
        "l1ld", "l1rd", "l2ld", "l2rd", "l3ld", "l3rd", "g1", "g2",
    ];

    /// <summary>Every one-bit slot column of the roster table except rosterstatus, ordered by bit offset: the special
    /// teams, shootout (s1-s5), extra attackers (x1, x2) and 3-on-3 (three_*) units on top of the 20 core slots.</summary>
    public List<string> AllSlots => _roster is null ? [] : _roster.Fields.Values
        .Where(f => f.Type == 3 && f.Depth == 1 && f.Name != "rosterstatus").OrderBy(f => f.BitOffset).Select(f => f.Name).ToList();

    static string RoleOf(string slot)
    {
        if (slot.StartsWith("g", StringComparison.Ordinal) || slot.Contains("_g", StringComparison.Ordinal) || slot is "three_g1" or "three_g2") return "G";
        if (slot.Contains("ld", StringComparison.Ordinal) || slot.Contains("rd", StringComparison.Ordinal) || slot is "three_d") return "D";
        return "F"; // c/lw/rw/w and every forward-named slot
    }

    /// <summary>The group of slots one player may hold only once (a line, a power-play unit, the 3-on-3 unit...).</summary>
    static string UnitOf(string slot)
    {
        if (slot.StartsWith("three_", StringComparison.Ordinal)) return "three";
        if (slot.Length >= 2 && (slot[0] is 's' or 'x') && char.IsDigit(slot[1])) return slot;
        var m = System.Text.RegularExpressions.Regex.Match(slot, "^([a-z]+\\d?)(c|lw|rw|ld|rd|d|w|g\\d?)$");
        return m.Success ? m.Groups[1].Value : slot;
    }

    /// <summary>The roster row holding a slot for the team, or null when empty.</summary>
    public int? HolderRow(int team, string slot)
    {
        if (_roster is null) return null;
        for (int r = 0; r < _roster.InUse; r++)
            if (Db.GetInt(_roster, r, "team") == team && Db.GetInt(_roster, r, slot) != 0) return r;
        return null;
    }

    /// <summary>Puts <paramref name="rosterRow"/> (one of the team's rows, or null to clear) on a slot: the bit moves from
    /// any other row of the team. Throws when the player's position does not fit the slot.</summary>
    public void SetSlot(int team, string slot, int? rosterRow)
    {
        if (_roster is null) throw new InvalidOperationException("This database has no roster table.");
        var old = HolderRow(team, slot);
        if (old == rosterRow) return;
        if (rosterRow is not null)
        {
            if (Db.GetInt(_roster, rosterRow.Value, "team") != team) throw new ArgumentException("That player is not on this team.");
            var idx = (int)Db.GetInt(_roster, rosterRow.Value, "playerindex");
            _bioByGame.TryGetValue(_gameOfIndex.GetValueOrDefault(idx, ""), out var b);
            var need = RoleOf(slot);
            var have = b.Pos == 4 ? "G" : b.Pos == 3 ? "D" : "F";
            if (need != have) throw new ArgumentException($"{(b.First + " " + b.Last).Trim()} is a {have}, not a {need} — the game would not accept it on {slot}.");
        }
        if (old is not null) Set(_roster, old.Value, slot, 0);
        if (rosterRow is not null) Set(_roster, rosterRow.Value, slot, 1);
    }

    // ---------------------------------------------------------------- strategies

    public sealed record StrategyField(string Name, int Value, int High);
    public sealed record StrategyGroup(string Title, List<StrategyField> Fields);

    /// <summary>The strategy row's fields, grouped for the UI. The numeric values are the game's own codes; the meta XML
    /// names the fields but not the words the game shows, so the editor stays numeric (0 = the first menu entry).</summary>
    public List<StrategyGroup> StrategyGroups(int team)
    {
        if (_strat is null) return [];
        var row = StrategyRow(team);
        if (row is null) return [];
        var fields = _strat.Fields.Values.Where(f => f.Name != "index").OrderBy(f => f.BitOffset)
            .Select(f => new StrategyField(f.Name, (int)Db.GetInt(_strat, row.Value, f.Name), f.Depth >= 31 ? int.MaxValue : (1 << f.Depth) - 1)).ToList();
        var groups = new (string Title, Func<string, bool> In)[]
        {
            ("Forecheck & pressure", n => n is "oz_forecheck" or "nz_forecheck" or "dpressure" or "opressure" or "trap_forecheck_bias" or "threes_trap_forecheck_bias"),
            ("Even-strength lines", n => n is "l1_form" or "l1_ff" or "l2_form" or "l2_ff" or "l3_form" or "l3_ff" or "l4_form" or "l4_ff"),
            ("Line biases", n => n.Contains("bias", StringComparison.Ordinal)),
            ("Power play", n => n.StartsWith("pp", StringComparison.Ordinal)),
            ("Penalty kill", n => n.StartsWith("pk", StringComparison.Ordinal)),
            ("3 on 3", n => n.StartsWith("threes", StringComparison.Ordinal)),
            ("Breakouts", n => n.Contains("breakout", StringComparison.Ordinal)),
            ("Even-strength units", n => n.StartsWith("ev", StringComparison.Ordinal)),
            ("Team offense & defense", n => n.StartsWith("offense", StringComparison.Ordinal) || n.StartsWith("defense", StringComparison.Ordinal) || n is "start_fwd" or "start_def"),
        };
        var list = new List<StrategyGroup>();
        var pool = fields.ToList();   // first match wins, so the groups cannot overlap
        foreach (var (title, inGroup) in groups)
        {
            var g = pool.Where(f => inGroup(f.Name)).ToList();
            if (g.Count == 0) continue;
            list.Add(new StrategyGroup(title, g));
            pool.RemoveAll(g.Contains);
        }
        if (pool.Count > 0) list.Add(new StrategyGroup("Other", pool));
        return list;
    }

    int? StrategyRow(int team)
    {
        if (_strat is null) return null;
        for (int r = 0; r < _strat.InUse; r++)
            if (Db.GetInt(_strat, r, "index") == team) return r;
        return null;
    }

    public void SetStrategy(int team, string field, int value)
    {
        if (_strat is null) throw new InvalidOperationException("This database has no strategy table.");
        var row = StrategyRow(team) ?? throw new InvalidOperationException($"Team {team} has no strategy row in this database.");
        Set(_strat, row, field, value);
    }

    // ---------------------------------------------------------------- save & verify

    void Set(T3Db.Table? t, int row, string field, long value)
    {
        if (t is null) throw new InvalidOperationException("This database misses the table that field belongs to.");
        Db.SetInt(t, row, field, value);
        IsDirty = true;
    }

    /// <summary>The edited database bytes (dirty tables' section CRCs recomputed; same length as loaded).</summary>
    public byte[] Save()
    {
        var bytes = Db.Save();
        IsDirty = false;
        return bytes;
    }

    /// <summary>Every rule the game is known to enforce, checked against the edited database. A team that was incomplete
    /// at load is not blamed for being incomplete (the flaw predates the edit); anything the edit broke is reported.</summary>
    public List<string> Verify()
    {
        var problems = new List<string>();
        if (_roster is null) { problems.Add("This database has no roster table."); return problems; }
        foreach (var team in TeamIndexes())
        {
            var rows = new List<int>();
            for (int r = 0; r < _roster.InUse; r++) if (Db.GetInt(_roster, r, "team") == team) rows.Add(r);
            string Who(int r)
            {
                var idx = (int)Db.GetInt(_roster, r, "playerindex");
                _bioByGame.TryGetValue(_gameOfIndex.GetValueOrDefault(idx, ""), out var b);
                return (b.First + " " + b.Last).Trim();
            }

            var holders = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var slot in AllSlots)
                for (int i = 0; i < rows.Count; i++)
                    if (Db.GetInt(_roster, rows[i], slot) != 0)
                    {
                        if (holders.TryGetValue(slot, out var first))
                            problems.Add($"Team {team}: {slot} is held by both {Who(first)} and {Who(rows[i])}.");
                        else holders[slot] = rows[i];
                    }

            var dressed = CoreSlots.Count(s => holders.ContainsKey(s));
            var atLoad = _dressShapeAtLoad.GetValueOrDefault(team);
            if (dressed != atLoad && dressed is > 0 and < 20)
                problems.Add($"Team {team}: {dressed} of {CoreSlots.Length} dressed slots are filled (this edit changed it from {atLoad}).");
            if (holders.TryGetValue("g1", out var g1) && holders.TryGetValue("g2", out var g2) && g1 == g2)
                problems.Add($"Team {team}: one goalie is listed twice (g1 and g2).");

            var coreDoubles = CoreSlots.GroupBy(s => holders.GetValueOrDefault(s, -1)).Where(g => g.Key >= 0 && g.Count() > 1);
            foreach (var g in coreDoubles)
                problems.Add($"Team {team}: {Who(g.Key)} holds two dressed slots ({string.Join(", ", g)}).");

            var units = holders.GroupBy(h => UnitOf(h.Key)).SelectMany(g => g.GroupBy(h => h.Value).Where(x => x.Count() > 1));
            foreach (var g in units)
                problems.Add($"Team {team}: {Who(g.First().Value)} appears {g.Count()} times in unit {g.First().Key}.");

            var c = rows.Count(r => Db.GetInt(_roster, r, "captain") == 1);
            var letters = rows.Count(r => Db.GetInt(_roster, r, "captain") != 0);
            if (c > 1) problems.Add($"Team {team}: {c} captains (the game allows one).");
            if (letters > 3) problems.Add($"Team {team}: {letters} letter wearers (the game allows three).");
            foreach (var r in rows.Where(r => Db.GetInt(_roster, r, "captain") != 0 && Db.GetInt(_roster, r, "rosterstatus") == 0))
                problems.Add($"Team {team}: {Who(r)} wears a letter but is scratched.");

            var numbers = rows.GroupBy(r => Db.GetInt(_roster, r, "jersey")).Where(g => g.Key != 0 && g.Count() > 1);
            foreach (var g in numbers) problems.Add($"Team {team}: number {g.Key} is worn by {g.Count()} players.");
        }
        return problems;
    }
}
