using System.Text.Json;

namespace Suite.Core;

/// <summary>
/// General UI colors for the viewer. The 32 NHL clubs use data\team_goalie_colors_24.tsv, deterministically derived
/// from NHL 24 stockgoalieequipment pad zones. NHL 24/27 stockteamcolorstable files are fallbacks for other teams;
/// NHL 27 never overwrites an NHL 24 club palette. Existing team overrides, including Utah, are applied last.
/// Fanatics variant 24/25 helmet and glove materials are separate and retain all 15 NHL 27 equipment zones.
/// </summary>
public static class TeamColors
{
    static Dictionary<int, string[]>? _cache;

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "data");
    public static string ShippedPath27 => Path.Combine(Dir, "team_colors_27.tsv");
    public static string ShippedPath24 => Path.Combine(Dir, "team_colors.tsv");
    public static string ShippedGoaliePath24 => Path.Combine(Dir, "team_goalie_colors_24.tsv");

    /// <summary>The team's general UI colors for a team index, or null when the tables have none.</summary>
    public static string[]? For(int index)
    {
        _cache ??= Load();
        return _cache.TryGetValue(index, out var colours) && colours.Length > 0 ? colours : null;
    }

    /// <summary>The team's general UI primary color as #RRGGBB.</summary>
    public static string Primary(int? index, string fallback = "#14223A") =>
        index is not null && For(index.Value) is [var first, ..] ? "#" + first : fallback;

    /// <summary>The team's general UI secondary color as #RRGGBB.</summary>
    public static string Secondary(int? index, string fallback = "#202226")
    {
        if (index is null) return fallback;
        var colours = For(index.Value);
        return colours is { Length: > 1 } ? "#" + colours[1] : fallback;
    }

    static Dictionary<int, string[]> Load()
    {
        var result = new Dictionary<int, string[]>();
        // Fallbacks first: an NHL 27 entry can fill a missing non-NHL team, never replace an NHL 24 team.
        ReadInto(ShippedPath27, result);
        ReadInto(ShippedPath24, result);
        ReadInto(ShippedGoaliePath24, result);
        ApplyOverrides(result);
        return result;
    }

    static void ApplyOverrides(Dictionary<int, string[]> target)
    {
        string path = Path.Combine(Dir, "teams_overrides.json");
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var team in doc.RootElement.GetProperty("teams").EnumerateArray())
            {
                if (!team.TryGetProperty("index", out var id) || !team.TryGetProperty("colors", out var colors)) continue;
                string? primary = Color(colors, "primary"), secondary = Color(colors, "secondary");
                if (primary is null && secondary is null) continue;
                var existing = target.GetValueOrDefault(id.GetInt32()) ?? [];
                var merged = existing.ToList();
                while (merged.Count < 2) merged.Add("202226");
                if (primary is not null) merged[0] = primary;
                if (secondary is not null) merged[1] = secondary;
                target[id.GetInt32()] = merged.ToArray();
            }
        }
        catch (Exception ex) { SuiteLog.Write("viewer team colors: teams_overrides.json not read: " + ex.Message); }
    }

    static string? Color(JsonElement colors, string name)
    {
        if (!colors.TryGetProperty(name, out var value) || value.GetString() is not { } text) return null;
        string color = text.Trim().TrimStart('#');
        return color.Length == 6 && color.All(Uri.IsHexDigit) ? color.ToUpperInvariant() : null;
    }

    static void ReadInto(string path, Dictionary<int, string[]> target)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split('\t');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var index)) continue;
            var colours = parts.Skip(2).Select(s => s.Trim().TrimStart('#')).Where(s => s.Length == 6).ToArray();
            if (colours.Length > 0) target[index] = colours;
        }
    }
}
