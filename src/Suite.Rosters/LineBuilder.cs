using System.Text.Json;
using System.Text.RegularExpressions;

namespace Suite.Rosters;

/// <summary>
/// Hand-editable line overrides: data\lines_overrides.json, {"BOS": {"l1c": "Elias Lindholm", "g1": "Jeremy Swayman"}}.
/// Keys are NHL API team abbreviations and roster slot columns; values are "First Last" as in the database. An override
/// is used when that player is on the team and healthy; everything else is built around it.
/// </summary>
public sealed class LineOverrides
{
    public Dictionary<string, Dictionary<string, string>> Teams { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static LineOverrides Load(string? path)
    {
        var o = new LineOverrides();
        if (path is null || !File.Exists(path)) return o;
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        foreach (var team in doc.RootElement.EnumerateObject())
        {
            if (team.Value.ValueKind != JsonValueKind.Object) continue;
            o.Teams[team.Name] = team.Value.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
        }
        return o;
    }
}

/// <summary>
/// Builds a whole NHL team's lines from ratings, the same way in every database, so every database that has the same
/// players gets the same lines. Injured players are never dressed (AHL players are not on the NHL roster at all).
/// Overrides (any slot) are placed first and everything else is built around them.
/// - Even strength: the 12 best healthy forwards by OVR (skill_overall) in four lines, best first: each line takes the best
///   natural C / LW / RW left; a missing wing is the best forward left shooting on that side when possible. The 6 best
///   healthy D in three pairs: k-th best left side (LD, else left shot) with k-th best right side. Best OVR goalie starts.
/// - PP1 / PP2: the dressed players with the best offence (offensive awareness, passing, hands, shot, puck control; for D
///   the point shot): C, a left- and a right-shot wing, two point men; PP1 first. 5-on-3 units lean on the same PP unit.
/// - PK (three 4-man and three 3-man units): best defence (defensive awareness, stick checking, shot blocking, discipline;
///   centres also faceoffs): C, a forward, a left and a right D; unit 1 first, no forward on two 4-man units.
/// - 4-on-4 / 3-on-3 overtime and the 3-on-3 mode: best skating and puck skill. Shootout: the five best shooters.
///   Extra attackers: the two best attackers outside PP1's forwards.
/// Letters (C/A), jerseys and roster keys are not touched.
/// </summary>
public static class LineBuilder
{
    public sealed record Player(string[] Row, string Name, int Pos, int Primary, int Hand, int Ovr, IReadOnlyDictionary<string, int> R, bool Injured)
    {
        int A(params string[] k) => (int)Math.Round(k.Average(x => R.TryGetValue(x, out var v) ? v : Ovr));
        public bool IsF => Pos is >= 0 and <= 2;
        public bool IsD => Pos == 3;
        public bool IsG => Pos == 4;
        public bool LeftSide => IsD ? Primary == 3 || (Primary != 4 && Hand == 0) : Hand == 0;
        public int Offence => IsD ? A("c_offawareness", "c_passing", "c_slapshotpower", "c_slapaccuracy", "c_puckcontrol")
                                  : A("c_offawareness", "c_passing", "c_handeye", "c_wristaccuracy", "c_puckcontrol", "c_deking");
        public int Defence => Pos == 0 ? A("c_defawareness", "c_stickchecking", "c_shotblocking", "c_discipline", "c_faceoffs")
                                       : A("c_defawareness", "c_stickchecking", "c_shotblocking", "c_discipline");
        public int Skill => A("c_speed", "c_agility", "c_puckcontrol", "c_deking", "c_passing");
        public int Shootout => A("c_deking", "c_wristaccuracy", "c_handeye", "c_puckcontrol", "c_poise");
    }

    /// <summary>Clears and refills every slot column of <paramref name="team"/>'s rows. Returns warnings.</summary>
    public static List<string> Build(string teamName, List<Player> team, T3Csv ro, IReadOnlyList<string> slotCols,
        IReadOnlyDictionary<string, string>? overrides)
    {
        var warnings = new List<string>();
        foreach (var p in team) foreach (var c in slotCols) ro.Set(p.Row, c, 0);
        var slot = new Dictionary<string, Player>();
        var dressed = new HashSet<Player>();
        var healthy = team.Where(p => !p.Injured).ToList();
        bool Has(string col) => slotCols.Contains(col);

        foreach (var (col, name) in overrides ?? new Dictionary<string, string>())
        {
            var p = healthy.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (!Has(col)) { warnings.Add($"{teamName}: override slot {col} does not exist"); continue; }
            if (p is null) { warnings.Add($"{teamName}: override {col} = {name} is not a healthy player on the team"); continue; }
            if (slot.Any(s => UnitOf(s.Key) == UnitOf(col) && s.Value == p)) { warnings.Add($"{teamName}: override puts {name} twice in {UnitOf(col)}"); continue; }
            slot[col] = p;
            if (IsEvenStrength(col)) dressed.Add(p);
        }

        // Even strength.
        var fPool = healthy.Where(p => p.IsF && !dressed.Contains(p)).OrderByDescending(p => p.Ovr)
            .Take(Math.Max(0, 12 - slot.Keys.Count(IsForwardSlot))).ToList();
        var dPool = healthy.Where(p => p.IsD && !dressed.Contains(p)).OrderByDescending(p => p.Ovr)
            .Take(Math.Max(0, 6 - slot.Keys.Count(IsDefenseSlot))).ToList();
        Player? Take(List<Player> pool, params Func<Player, bool>[] prefs)
        {
            foreach (var pref in prefs.Append(_ => true))
                if (pool.FirstOrDefault(pref) is { } p) { pool.Remove(p); return p; }
            return null;
        }
        Player? Spare(Func<Player, bool> ok) => healthy.Where(p => !dressed.Contains(p) && !fPool.Contains(p) && !dPool.Contains(p) && ok(p))
            .OrderByDescending(p => p.Ovr).FirstOrDefault();
        void Put(string col, Player? p)
        {
            if (p is null) { warnings.Add($"{teamName}: nobody for {col}"); return; }
            slot[col] = p; dressed.Add(p);
        }
        bool Open(string col) => Has(col) && !slot.ContainsKey(col);
        for (int k = 1; k <= 4; k++)
        {
            if (Open($"l{k}c")) Put($"l{k}c", Take(fPool, x => x.Pos == 0) ?? Spare(x => x.IsF) ?? Spare(x => x.IsD));
            if (Open($"l{k}lw")) Put($"l{k}lw", Take(fPool, x => x.Pos == 1, x => x.Hand == 0) ?? Spare(x => x.IsF) ?? Spare(x => x.IsD));
            if (Open($"l{k}rw")) Put($"l{k}rw", Take(fPool, x => x.Pos == 2, x => x.Hand == 1) ?? Spare(x => x.IsF) ?? Spare(x => x.IsD));
        }
        for (int k = 1; k <= 3; k++)
        {
            if (Open($"l{k}ld")) Put($"l{k}ld", Take(dPool, x => x.LeftSide) ?? Spare(x => x.IsD) ?? Spare(x => x.IsF));
            if (Open($"l{k}rd")) Put($"l{k}rd", Take(dPool, x => !x.LeftSide) ?? Spare(x => x.IsD) ?? Spare(x => x.IsF));
        }
        foreach (var col in new[] { "g1", "g2" })
            if (Open(col)) Put(col, healthy.Where(p => p.IsG && !dressed.Contains(p)).OrderByDescending(p => p.Ovr).FirstOrDefault());

        // Units, from the dressed skaters.
        var skaters = dressed.Where(p => !p.IsG).ToList();
        List<Player> In(string prefix) => slot.Where(s => s.Key.StartsWith(prefix)).Select(s => s.Value).ToList();
        void Unit(string prefix, Func<Player, int> score, IEnumerable<Player>? exclude, params (string Pos, Func<Player, bool> Fits)[] members)
        {
            var unit = UnitOf(prefix + members[0].Pos);
            var used = slot.Where(s => UnitOf(s.Key) == unit).Select(s => s.Value).ToHashSet();
            var excluded = exclude?.ToHashSet() ?? [];
            foreach (var (pos, fits) in members)
            {
                var col = prefix + pos;
                if (!Open(col)) continue;
                var p = skaters.Where(x => !used.Contains(x) && !excluded.Contains(x))
                    .OrderByDescending(x => fits(x) ? 1 : 0).ThenByDescending(score).ThenByDescending(x => x.Ovr).FirstOrDefault()
                    ?? skaters.Where(x => !used.Contains(x)).OrderByDescending(score).FirstOrDefault();
                if (p is null) continue;
                slot[col] = p; used.Add(p);
            }
        }
        Func<Player, bool> C = x => x.Pos == 0, LW = x => x.IsF && x.Hand == 0, RW = x => x.IsF && x.Hand == 1,
            Fw = x => x.IsF, LD = x => x.IsD && x.LeftSide, RD = x => x.IsD && !x.LeftSide, Dm = x => x.IsD;

        Unit("pp_1", p => p.Offence, null, ("c", C), ("lw", LW), ("rw", RW), ("ld", Dm), ("rd", Dm));
        Unit("pp_2", p => p.Offence, In("pp_1"), ("c", C), ("lw", LW), ("rw", RW), ("ld", Dm), ("rd", Dm));
        for (int k = 1; k <= 2; k++)
        {
            var pp = In($"pp_{k}").ToHashSet();
            Unit($"pp4_{k}", p => p.Offence + (pp.Contains(p) ? 100 : 0), k == 2 ? In("pp4_1") : null, ("c", C), ("lw", Fw), ("ld", Dm), ("rd", Dm));
        }
        var pkForwards = new List<Player>();
        for (int k = 1; k <= 3; k++)
        {
            Unit($"pk4_{k}", p => p.Defence, pkForwards, ("c", C), ("lw", Fw), ("ld", LD), ("rd", RD));
            pkForwards.AddRange(In($"pk4_{k}").Where(p => p.IsF));
            var pk = In($"pk4_{k}").ToHashSet();
            Unit($"pk3_{k}", p => p.Defence + (pk.Contains(p) ? 100 : 0), null, ("c", C), ("ld", LD), ("rd", RD));
        }
        var ev4Used = new List<Player>();
        var ev3Used = new List<Player>();
        for (int k = 1; k <= 3; k++)
        {
            Unit($"ev4_{k}", p => p.Skill, ev4Used, ("c", C), ("lw", Fw), ("ld", LD), ("rd", RD));
            ev4Used.AddRange(In($"ev4_{k}"));
            Unit($"ev3_{k}", p => p.Skill, ev3Used, ("c", C), ("ld", Fw), ("rd", Dm));
            ev3Used.AddRange(In($"ev3_{k}"));
        }
        Unit("three_", p => p.Skill, null, ("c", C), ("w", Fw), ("d", Dm), ("s1", Fw), ("s2", Dm));
        if (Open("three_g1") && slot.GetValueOrDefault("g1") is { } g1) slot["three_g1"] = g1;
        if (Open("three_g2") && slot.GetValueOrDefault("g2") is { } g2) slot["three_g2"] = g2;
        var shooters = skaters.OrderByDescending(p => p.Shootout).ThenByDescending(p => p.Ovr).ToList();
        for (int i = 1; i <= 5; i++)
            if (Open($"s{i}") && shooters.FirstOrDefault(p => !slot.Any(s => UnitOf(s.Key) == "s" && s.Value == p)) is { } shooter) slot[$"s{i}"] = shooter;
        Unit("x", p => p.Offence, In("pp_1").Where(p => p.IsF), ("1", Fw), ("2", Fw));

        foreach (var (col, p) in slot) ro.Set(p.Row, col, 1);
        foreach (var p in team) ro.Set(p.Row, "rosterstatus", dressed.Contains(p) ? 1 : 0);
        foreach (var col in slotCols.Where(c => !slot.ContainsKey(c))) warnings.Add($"{teamName}: {col} left empty");
        return warnings;
    }

    static bool IsForwardSlot(string c) => Regex.IsMatch(c, "^l[1-4](c|lw|rw)$");
    static bool IsDefenseSlot(string c) => Regex.IsMatch(c, "^l[1-3](ld|rd)$");
    static bool IsEvenStrength(string c) => IsForwardSlot(c) || IsDefenseSlot(c) || c is "g1" or "g2";

    public static string UnitOf(string slot)
    {
        if (slot.StartsWith("three_")) return "three";
        if (slot.Length == 2 && slot[0] is 's' or 'x' or 'g' && char.IsDigit(slot[1])) return slot[..1];
        var m = Regex.Match(slot, @"^([a-z]+\d?_\d|l\d)");
        return m.Success ? m.Groups[1].Value : slot;
    }
}
