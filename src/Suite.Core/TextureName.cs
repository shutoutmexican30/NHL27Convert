using System.Text.RegularExpressions;

namespace Suite.Core;

/// <summary>
/// Parsed texture asset path. Both games use the same layout, e.g.
/// content/character/skater/league/nhl/boston/adidas/home/branding_jersey_adidas_boston_home_3_color.
/// </summary>
public sealed record TextureName(string Path, string Folder, string Leaf, string? League, string? Team, string? Brand, string? Set)
{
    public const string SkaterPrefix = "content/character/skater/league/";

    /// <summary>color / normal / coeff (or the last token when it is something else).</summary>
    public string Role
    {
        get
        {
            var t = Leaf.Split('_');
            var last = t[^1] == "array" && t.Length > 1 ? t[^2] : t[^1];
            return last;
        }
    }

    public bool IsArrayName => Leaf.EndsWith("_array", StringComparison.Ordinal);
    public bool IsBase => Leaf.Contains("_base_", StringComparison.Ordinal);

    /// <summary>What part of the uniform this is: jersey, pant, sock, font, helmetlogo, branding_jersey, branding_pant, ...</summary>
    public string Garment
    {
        get
        {
            if (Brand is null) return Leaf.Split('_')[0];
            int i = Leaf.IndexOf("_" + Brand + "_", StringComparison.Ordinal);
            return i > 0 ? Leaf[..i] : Leaf.Split('_')[0];
        }
    }

    /// <summary>Branding layer index (…_3_color → 3), if any.</summary>
    public int? Layer
    {
        get
        {
            var m = Regex.Match(Leaf, @"_(\d+)_(color|normal|coeff)$");
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }
    }

    /// <summary>
    /// The leaf with this folder's own team/brand/set words replaced by placeholders, so
    /// "jersey_adidas_boston_home_base_color" and "jersey_adidas_utah_ten_base_color" both become
    /// "jersey_{brand}_{team}_{set}_base_color". Two textures with the same canonical leaf play the same role.
    /// </summary>
    public string Canonical
    {
        get
        {
            var tokens = Leaf.Split('_');
            for (int i = 0; i < tokens.Length; i++)
            {
                if (Team is not null && tokens[i] == Team) tokens[i] = "{team}";
                else if (Brand is not null && tokens[i] == Brand) tokens[i] = "{brand}";
                else if (Set is not null && tokens[i] == Set) tokens[i] = "{set}";
            }
            return string.Join('_', tokens);
        }
    }

    public static TextureName Parse(string path)
    {
        int slash = path.LastIndexOf('/');
        var folder = slash < 0 ? "" : path[..slash];
        var leaf = path[(slash + 1)..];
        string? league = null, team = null, brand = null, set = null;
        if (path.StartsWith(SkaterPrefix, StringComparison.Ordinal))
        {
            var p = path[SkaterPrefix.Length..].Split('/');
            // league/team/brand/set/leaf
            if (p.Length >= 5) { league = p[0]; team = p[1]; brand = p[2]; set = p[3]; }
        }
        else
        {
            // Generic team folders elsewhere, e.g. .../goalie/blocker/nhl/arizona/textures/leaf or common/logo/arizona/leaf
            var parts = folder.Split('/');
            int nhl = Array.IndexOf(parts, "nhl");
            if (nhl >= 0 && nhl + 1 < parts.Length) { league = "nhl"; team = parts[nhl + 1]; }
        }
        return new TextureName(path, folder, leaf, league, team, brand, set);
    }

    /// <summary>Sorts jersey sets the way the games name them: home, away, then word-numbers in order.</summary>
    public static int SetOrder(string set) => set switch
    {
        "home" => 0,
        "away" => 1,
        _ => Array.IndexOf(Numbers, set) is var n && n >= 0 ? 2 + n : 100,
    };

    public static readonly string[] Numbers =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen",
        "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty", "twentyone", "twentytwo", "twentythree",
        "twentyfour", "twentyfive",
    ];
}
