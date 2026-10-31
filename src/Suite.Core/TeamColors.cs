namespace Suite.Core;

/// <summary>
/// Team colours baked from each game's stockteamcolorstable into data\team_colors_27.tsv (NHL 27) and
/// data\team_colors.tsv (NHL 24), so the viewer can dress the helmet/gloves in the colours of the game whose
/// art it is showing. Colour 0 is the primary, 1 the secondary, and so on (RRGGBB, hand-editable).
/// The NHL 27 table wins where both have an index, because the viewer shows NHL 27 art.
/// </summary>
public static class TeamColors
{
    static Dictionary<int, string[]>? _cache;

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "data");
    public static string ShippedPath27 => Path.Combine(Dir, "team_colors_27.tsv");
    public static string ShippedPath24 => Path.Combine(Dir, "team_colors.tsv");

    /// <summary>The team's colours for a team index, or null when the tables have none.</summary>
    public static string[]? For(int index)
    {
        _cache ??= Load();
        return _cache.TryGetValue(index, out var colours) && colours.Length > 0 ? colours : null;
    }

    /// <summary>The team's primary colour as #RRGGBB.</summary>
    public static string Primary(int? index, string fallback = "#14223A") =>
        index is not null && For(index.Value) is [var first, ..] ? "#" + first : fallback;

    /// <summary>The team's secondary colour as #RRGGBB.</summary>
    public static string Secondary(int? index, string fallback = "#202226")
    {
        if (index is null) return fallback;
        var colours = For(index.Value);
        return colours is { Length: > 1 } ? "#" + colours[1] : fallback;
    }

    static Dictionary<int, string[]> Load()
    {
        var result = new Dictionary<int, string[]>();
        // NHL 24 first, then NHL 27 overwrites: the viewer shows NHL 27 art, so its palette wins.
        ReadInto(ShippedPath24, result);
        ReadInto(ShippedPath27, result);
        return result;
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
