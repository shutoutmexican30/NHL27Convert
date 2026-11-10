using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suite.Core;

/// <summary>
/// Each team's default home (dark) and away (light) jersey variant in both games, from stockteamjerseys
/// (isdefault = 1, islightcolor). NHL 27 moved the defaults (most teams 10/11, Boston 13/14, Utah 12/13,
/// St. Louis 14/15) while NHL 24 mostly uses 0/1, so home/away is paired through this map, never by folder name.
/// The user's copy (%APPDATA%\CenterIceModdingSuite\data\variant_map.json) is hand-editable; it is seeded from data\.
/// </summary>
public sealed class VariantMap
{
    public sealed class Side
    {
        [JsonPropertyName("folder")] public string Folder { get; set; } = "";
        [JsonPropertyName("home")] public string? Home { get; set; }
        [JsonPropertyName("away")] public string? Away { get; set; }
        [JsonPropertyName("homeVariant")] public int? HomeVariant { get; set; }
        [JsonPropertyName("awayVariant")] public int? AwayVariant { get; set; }
    }

    public sealed class Team
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("name24")] public string Name24 { get; set; } = "";
        [JsonPropertyName("name27")] public string Name27 { get; set; } = "";
        [JsonPropertyName("nhl24")] public Side Nhl24 { get; set; } = new();
        [JsonPropertyName("nhl27")] public Side Nhl27 { get; set; } = new();
        /// <summary>NHL 27 kits with no NHL 24 folder and the NHL 24 set each would replace (a suggestion; null = skip).</summary>
        [JsonPropertyName("extraKits")] public List<ExtraKit> ExtraKits { get; set; } = [];
    }

    public sealed class ExtraKit
    {
        [JsonPropertyName("nhl27")] public string Nhl27 { get; set; } = "";
        [JsonPropertyName("variant27")] public int? Variant27 { get; set; }
        [JsonPropertyName("replaces24")] public string? Replaces24 { get; set; }
    }

    [JsonPropertyName("format")] public int Format { get; set; } = 1;
    [JsonPropertyName("comment")] public string Comment { get; set; } = "";
    [JsonPropertyName("teams")] public List<Team> Teams { get; set; } = [];

    public static string UserPath => Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Roaming, "data")), "variant_map.json");
    public static string ShippedPath => Path.Combine(AppContext.BaseDirectory, "data", "variant_map.json");

    public static VariantMap Load()
    {
        if (!File.Exists(UserPath) && File.Exists(ShippedPath)) File.Copy(ShippedPath, UserPath);
        // A newer shipped format (2 adds extraKits) replaces an older user copy; the old one is kept beside it.
        if (File.Exists(UserPath) && File.Exists(ShippedPath) && Parse(File.ReadAllText(ShippedPath)).Format > Parse(File.ReadAllText(UserPath)).Format)
        {
            File.Copy(UserPath, UserPath + $".before-format{Parse(File.ReadAllText(ShippedPath)).Format}", true);
            File.Copy(ShippedPath, UserPath, true);
        }
        var user = File.Exists(UserPath) ? Parse(File.ReadAllText(UserPath)) : new VariantMap();
        if (!File.Exists(ShippedPath)) return user;
        var shipped = Parse(File.ReadAllText(ShippedPath));
        int repaired = RepairMissingCurrentTeams(user, shipped);
        if (repaired > 0)
        {
            File.Copy(UserPath, UserPath + ".before-current-team-repair", true);
            File.WriteAllText(UserPath, JsonSerializer.Serialize(user, new JsonSerializerOptions { WriteIndented = true }));
            SuiteLog.Write($"variant map: restored {repaired} missing current NHL team(s); custom mappings preserved");
        }
        return user;
    }

    /// <summary>Adds required current NHL teams absent from a hand-edited user map without replacing any custom entry.</summary>
    public static int RepairMissingCurrentTeams(VariantMap user, VariantMap shipped)
    {
        var missing = shipped.Teams.Where(t => t.Index is >= 0 and <= 31)
            .Where(s => !user.Teams.Any(u => u.Index == s.Index)).ToList();
        if (missing.Count == 0) return 0;
        user.Teams.AddRange(missing);
        user.Teams = user.Teams.OrderBy(t => t.Index).ToList();
        return missing.Count;
    }

    public static VariantMap Parse(string json) => JsonSerializer.Deserialize<VariantMap>(json) ?? new VariantMap();

    /// <summary>The team whose NHL 24 texture folder is <paramref name="folder24"/> (e.g. "arizona"), NHL teams first.</summary>
    public Team? ByNhl24Folder(string folder24) => Teams.Where(t => t.Nhl24.Folder == folder24).OrderBy(t => t.Index).FirstOrDefault();

    /// <summary>Pairs an NHL 24 set folder with the NHL 27 set that plays the same role (default home or away).</summary>
    public (string Team27, string Set27, string Role)? Pair(string team24, string set24)
    {
        var t = ByNhl24Folder(team24);
        if (t is null) return null;
        if (set24 == t.Nhl24.Home && t.Nhl27.Home is not null) return (t.Nhl27.Folder, t.Nhl27.Home, $"home: NHL 24 variant {t.Nhl24.HomeVariant} ↔ NHL 27 variant {t.Nhl27.HomeVariant}");
        if (set24 == t.Nhl24.Away && t.Nhl27.Away is not null) return (t.Nhl27.Folder, t.Nhl27.Away, $"away: NHL 24 variant {t.Nhl24.AwayVariant} ↔ NHL 27 variant {t.Nhl27.AwayVariant}");
        return null;
    }
}
