using System.Text.Json;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

namespace Suite.Convert27;

/// <summary>
/// Makes each team's Fanatics 26-27 kits (style 2, variants 25 home / 24 away) its default home and away kits, and keeps
/// the original NHL 24 regular kits (variants 0 / 1) as selectable alternates named "23-24 Adidas" (owner, 2026-11-16).
/// Only the stockteamjerseys rows change (isdefault, orderid, and the old kits' name label) in every roster database that
/// has them (nhl_game and initial_experience, both layers), plus the name keys in loc/eng_us; no variant is renumbered
/// and no asset moves. The previous values are saved so the change can be undone exactly.
/// Defaults are read when a new league, season or franchise copies the stock table; a saved roster or an existing
/// franchise keeps the jersey table it was saved with.
/// </summary>
public static class KitDefaults
{
    public const int Style = 2, FanaticsHome = 25, FanaticsAway = 24, OldHome = 0, OldAway = 1, VintageLabel = 6;
    public const string OldLabel = "23-24 Adidas";
    static readonly string[] Databases = ["nhl_game", "initial_experience"];
    const string English = "loc/eng_us.db";

    /// <summary>
    /// Name the old kits "23-24 Adidas" (per-kit VINTAGE keys, 64 new language entries). Off: the English table holds
    /// 65,535 entries and has 37 free, so 64 names do not fit until space is freed; the old kits keep "Home"/"Away".
    /// </summary>
    public static bool NameOldKits = false;

    public sealed record RowState(int TeamId, int Variant, long IsDefault, long OrderId, long LocString);

    public static string UndoPath => Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Roaming, "data")), "kit_defaults_undo.json");

    /// <summary>The teams that have both Fanatics kits and both original kits in this table.</summary>
    public static List<int> Teams(T3Db db)
    {
        var t = db.Tables["stockteamjerseys"];
        var variants = Enumerable.Range(0, t.InUse).Where(r => db.GetInt(t, r, "style") == Style)
            .GroupBy(r => (int)db.GetInt(t, r, "teamid")).ToDictionary(g => g.Key, g => g.Select(r => (int)db.GetInt(t, r, "variant")).ToHashSet());
        return variants.Where(kv => kv.Value.IsSupersetOf([FanaticsHome, FanaticsAway, OldHome, OldAway])).Select(kv => kv.Key).Order().ToList();
    }

    /// <summary>The kit's row; a team listed twice for the same kit (an older test install) uses its first row.</summary>
    static int Row(T3Db db, T3Db.Table t, int team, int variant) => Enumerable.Range(0, t.InUse)
        .First(r => db.GetInt(t, r, "teamid") == team && db.GetInt(t, r, "style") == Style && db.GetInt(t, r, "variant") == variant);

    /// <summary>Applies the swap to one database; returns the rows' previous values.</summary>
    public static List<RowState> MakeFanaticsDefault(T3Db db, out List<string> labelKeys)
    {
        var t = db.Tables["stockteamjerseys"];
        var before = new List<RowState>();
        labelKeys = [];
        foreach (int team in Teams(db))
        {
            int home = Row(db, t, team, FanaticsHome), away = Row(db, t, team, FanaticsAway), oldHome = Row(db, t, team, OldHome), oldAway = Row(db, t, team, OldAway);
            foreach (var (row, v) in new[] { (home, FanaticsHome), (away, FanaticsAway), (oldHome, OldHome), (oldAway, OldAway) })
                before.Add(new RowState(team, v, db.GetInt(t, row, "isdefault"), db.GetInt(t, row, "orderid"), db.GetInt(t, row, "locstring")));
            if (db.GetInt(t, home, "isdefault") == 1 && db.GetInt(t, oldHome, "isdefault") == 0) continue; // already done
            long oHome = db.GetInt(t, oldHome, "orderid"), oAway = db.GetInt(t, oldAway, "orderid");
            long fHome = db.GetInt(t, home, "orderid"), fAway = db.GetInt(t, away, "orderid");
            db.SetInt(t, home, "isdefault", 1); db.SetInt(t, away, "isdefault", 1);
            db.SetInt(t, oldHome, "isdefault", 0); db.SetInt(t, oldAway, "isdefault", 0);
            db.SetInt(t, home, "orderid", oHome); db.SetInt(t, away, "orderid", oAway);
            db.SetInt(t, oldHome, "orderid", fHome); db.SetInt(t, oldAway, "orderid", fAway);
            if (!NameOldKits) continue;
            db.SetInt(t, oldHome, "locstring", VintageLabel); db.SetInt(t, oldAway, "locstring", VintageLabel);
            long art = db.GetInt(t, oldHome, "teamartid");
            labelKeys.Add($"VINTAGE_{Style}_{art}_{OldHome}");
            labelKeys.Add($"VINTAGE_{Style}_{db.GetInt(t, oldAway, "teamartid")}_{OldAway}");
        }
        return before;
    }

    /// <summary>Puts the saved previous values back into one database.</summary>
    public static int Restore(T3Db db, IEnumerable<RowState> before)
    {
        var t = db.Tables["stockteamjerseys"];
        int n = 0;
        foreach (var s in before)
        {
            int row = Row(db, t, s.TeamId, s.Variant);
            db.SetInt(t, row, "isdefault", s.IsDefault); db.SetInt(t, row, "orderid", s.OrderId); db.SetInt(t, row, "locstring", s.LocString);
            n++;
        }
        return n;
    }

    /// <summary>Applies (or undoes) the change on a game folder: backup first, every copy written together and read back.</summary>
    public static (bool Ok, string Message) Run(SuiteSettings settings, bool undo, Action<string>? say = null)
    {
        var problems = ModApply.Problems(settings);
        if (problems.Count > 0) return (false, "Refused: " + string.Join("; ", problems));
        string game = Path.GetFullPath(settings.ApplyTargetDir).TrimEnd(Path.DirectorySeparatorChar);
        if (ApplyTarget.RunningGame() is { } running) return (false, "Close the game first (" + running + ").");
        var legacy = new LegacyFiles(game, settings);
        string metaPath = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "meta-cache")), "nhl_game-meta.xml");
        File.WriteAllBytes(metaPath, legacy.Read("gamemodes/database/t3db/nhl_game-meta.xml"));
        var saved = undo
            ? File.Exists(UndoPath) ? JsonSerializer.Deserialize<Dictionary<string, List<RowState>>>(File.ReadAllText(UndoPath)) : null
            : new Dictionary<string, List<RowState>>();
        if (saved is null) return (false, "Nothing to undo: no saved defaults.");
        // Every copy (base and update layer) is edited from its own bytes: the roster databases also carry contracts
        // written per copy, so one copy must never overwrite another.
        var edits = new Dictionary<string, Func<byte[], byte[]>>(StringComparer.Ordinal);
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string name in Databases)
        {
            string file = RosterSyncRun.LegacyName(name);
            var first = T3Db.Load(legacy.Read(file), metaPath);
            if (undo)
            {
                if (!saved.TryGetValue(name, out var rows)) continue;
                edits[file] = bytes => { var db = T3Db.Load(bytes, metaPath); Restore(db, rows); return db.Save(); };
            }
            else
            {
                saved[name] = MakeFanaticsDefault(first, out var k);
                foreach (var key in k) keys.Add(key);
                say?.Invoke($"{name}: Fanatics default for {Teams(first).Count} teams");
                edits[file] = bytes => { var db = T3Db.Load(bytes, metaPath); MakeFanaticsDefault(db, out _); return db.Save(); };
            }
        }
        if (!undo && keys.Count > 0)
            edits[English] = bytes =>
            {
                var strings = LanguageStrings.Load(bytes);
                bool added = false;
                foreach (string key in keys) if (strings.Find(key) is null) { strings.Add(key, OldLabel); added = true; }
                return added ? strings.Build() : bytes;
            };
        string backup = ApplyTarget.Backup(game, null);
        say?.Invoke("backup: " + backup);
        try
        {
            foreach (string line in legacy.ReplaceEach(edits)) say?.Invoke("DB: " + line);
            // Read every copy back and check the defaults are what was asked for.
            var check = new LegacyFiles(game, settings);
            foreach (string name in Databases)
                foreach (var copy in check.ReadAll(RosterSyncRun.LegacyName(name), everyToc: true))
                {
                    var db = T3Db.Load(copy, metaPath);
                    var t = db.Tables["stockteamjerseys"];
                    foreach (int team in Teams(db))
                    {
                        bool fanatics = db.GetInt(t, Row(db, t, team, FanaticsHome), "isdefault") == 1 && db.GetInt(t, Row(db, t, team, OldHome), "isdefault") == 0;
                        if (fanatics == undo) throw new InvalidDataException($"{name}: team {team} defaults did not read back.");
                    }
                }
            if (!undo)
            {
                var strings = LanguageStrings.Load(check.Read(English));
                foreach (string key in keys) if (strings.Find(key) != OldLabel) throw new InvalidDataException("Label did not read back: " + key);
            }
        }
        catch (Exception e)
        {
            ApplyTarget.RestoreFromBackup(game, backup);
            return (false, "Not done, game restored: " + e.Message);
        }
        if (undo) File.Delete(UndoPath);
        else File.WriteAllText(UndoPath, JsonSerializer.Serialize(saved));
        return (true, undo ? "Original kits are the defaults again." : $"Fanatics kits are the defaults; the old kits are named '{OldLabel}'. Backup: {backup}");
    }
}
