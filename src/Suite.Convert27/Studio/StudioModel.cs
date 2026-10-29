using System.Text.RegularExpressions;

namespace Suite.Convert27.Studio;

/// <summary>
/// The parts of a texture name that say what it is, independent of team, brand and set:
/// "branding_jersey_adidas_boston_home_3_color" → garment branding_jersey, layer 3, role color.
/// </summary>
public sealed record MaterialKey(string Garment, string Role, bool IsBase, int? Layer, string Generic)
{
    static readonly HashSet<string> Brands = ["adidas", "rbk", "reebok", "nineties", "fanatics", "ccm", "nike", "bauer"];
    static readonly HashSet<string> SetWords = [.. TextureName.Numbers, "home", "away", "alt", "third", "base0", "fivegold"];
    static readonly HashSet<string> Roles = ["color", "colour", "normal", "coeff", "mask", "rough", "spec"];

    /// <summary>Parses a texture leaf or a file stem. <paramref name="teams"/> are known team words (from the index).</summary>
    public static MaterialKey Of(string leafOrStem, IReadOnlySet<string> teams)
    {
        var stem = leafOrStem.ToLowerInvariant();
        int? explicitLayer = null;
        var m = Regex.Match(stem, @"(?:#|@|_layer_?)(\d+)$");
        if (m.Success) { explicitLayer = int.Parse(m.Groups[1].Value); stem = stem[..m.Index]; }

        var tokens = stem.Split(['_', '-', ' ', '.'], StringSplitOptions.RemoveEmptyEntries).ToList();
        bool isArray = tokens.Count > 0 && tokens[^1] == "array";
        if (isArray) tokens.RemoveAt(tokens.Count - 1);
        var role = tokens.Count > 0 && Roles.Contains(tokens[^1]) ? tokens[^1] == "colour" ? "color" : tokens[^1] : "";
        if (role.Length > 0) tokens.RemoveAt(tokens.Count - 1);
        int? layer = explicitLayer;
        if (tokens.Count > 0 && int.TryParse(tokens[^1], out var n)) { layer ??= n; tokens.RemoveAt(tokens.Count - 1); }
        bool isBase = tokens.Remove("base");

        int cut = tokens.FindIndex(t => Brands.Contains(t) || teams.Contains(t) || SetWords.Contains(t));
        var garment = string.Join('_', cut < 0 ? tokens : tokens.Take(cut));
        var generic = string.Join('_', tokens.Select(t => Brands.Contains(t) ? "{brand}" : teams.Contains(t) ? "{team}" : SetWords.Contains(t) ? "{set}" : t));
        return new MaterialKey(garment, role, isBase, layer, generic);
    }

    public bool IsCoeff => Role == "coeff";
}

/// <summary>One image the user can put into a slot: a file from the replacement folder or a decoded NHL 27 texture.</summary>
public sealed class ReplacementFile
{
    public required string Path { get; init; }
    public required string Source { get; init; }            // "folder" or "NHL 27"
    public required MaterialKey Key { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Stem => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string? Preview { get; set; }
}

public sealed record Candidate(ReplacementFile File, double Score, string Reason);

/// <summary>One NHL 24 texture (or one layer of a texture array) in the studio grid.</summary>
public sealed class StudioCard : Observable
{
    public required string Slot { get; init; }
    public int? Layer { get; init; }
    public required MaterialKey Key { get; init; }
    public string Id => Layer is null ? Slot : $"{Slot}#{Layer}";
    public string Leaf => Slot[(Slot.LastIndexOf('/') + 1)..] + (Layer is null ? "" : $"  [layer {Layer}]");
    public string? OriginalPng { get; set; }
    public string Details { get; set; } = "";

    ReplacementFile? _new;
    bool _replace;
    double _score;
    string _reason = "";
    bool _manual;

    public ReplacementFile? New
    {
        get => _new;
        set { if (Set(ref _new, value)) { Raise(nameof(NewPreview)); Raise(nameof(NewName)); Raise(nameof(State)); } }
    }
    public string? NewPreview => _new?.Preview;
    public string NewName => _new is null ? "(no replacement: stock texture stays)" : $"{_new.Name}  ·  {_new.Source}";
    public bool Replace { get => _replace; set { if (Set(ref _replace, value)) Raise(nameof(State)); } }
    public double Score { get => _score; set => Set(ref _score, value); }
    public string Reason { get => _reason; set => Set(ref _reason, value); }
    /// <summary>Chosen by hand; auto-match never changes it.</summary>
    public bool Manual { get => _manual; set => Set(ref _manual, value); }

    public string State => Replace && New is not null ? "REPLACED" : Key.IsCoeff ? "COEFF KEPT STOCK" : "UNUSED";
}

/// <summary>Matches replacement images to slots by asset name first, then by material role.</summary>
public static class StudioMatcher
{
    public static Candidate? Rate(StudioCard card, ReplacementFile f)
    {
        var slotLeaf = card.Slot[(card.Slot.LastIndexOf('/') + 1)..];
        var stem = f.Stem;
        if (string.Equals(stem, slotLeaf, StringComparison.OrdinalIgnoreCase) && card.Layer is null)
            return new(f, 1.0, "exact asset name");
        if (card.Layer is not null && f.Key.Layer == card.Layer &&
            (stem.StartsWith(slotLeaf + "#", StringComparison.OrdinalIgnoreCase) || stem.StartsWith(slotLeaf + "@", StringComparison.OrdinalIgnoreCase)))
            return new(f, 1.0, "exact asset name and layer");
        var a = card.Key;
        var b = f.Key;
        if (a.Role.Length == 0 || a.Role != b.Role) return null;
        if (a.Layer != b.Layer) return null;
        if (a.Generic == b.Generic && a.IsBase == b.IsBase) return new(f, 0.9, "same material role (team/brand/set words differ)");
        if (a.Garment == b.Garment && a.IsBase == b.IsBase) return new(f, 0.7, $"same garment and role ({a.Garment} {a.Role})");
        if (a.Garment == b.Garment) return new(f, 0.45, $"same garment and role, {(a.IsBase ? "plain" : "_base_")} texture (check)");
        return null;
    }

    public static IEnumerable<Candidate> Rank(StudioCard card, IEnumerable<ReplacementFile> files) =>
        files.Select(f => Rate(card, f)).Where(c => c is not null).Select(c => c!)
            .OrderByDescending(c => c.Score).ThenBy(c => c.File.Source == "folder" ? 0 : 1).ThenBy(c => c.File.Name);

    /// <summary>
    /// Fills every card that was not chosen by hand. Coefficient maps are never auto-replaced (keep stock coeff
    /// unless explicitly chosen). Returns the files no card uses.
    /// </summary>
    public static List<ReplacementFile> AutoMatch(IReadOnlyList<StudioCard> cards, IReadOnlyList<ReplacementFile> files)
    {
        foreach (var card in cards.Where(c => !c.Manual))
        {
            var best = card.Key.IsCoeff ? null : Rank(card, files).FirstOrDefault(c => c.Score >= 0.7);
            card.New = best?.File;
            card.Score = best?.Score ?? 0;
            card.Reason = best?.Reason ?? (card.Key.IsCoeff ? "coefficient map: kept stock unless you choose one" : "no replacement with this name or role");
            card.Replace = best is not null;
        }
        return Unused(cards, files);
    }

    public static List<ReplacementFile> Unused(IReadOnlyList<StudioCard> cards, IReadOnlyList<ReplacementFile> files)
    {
        var used = cards.Where(c => c.Replace && c.New is not null).Select(c => c.New!.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return files.Where(f => !used.Contains(f.Path)).ToList();
    }

    /// <summary>The selection as a mod-library entry; array layers are grouped back into their slot.</summary>
    public static ModFile ToMod(string name, IEnumerable<StudioCard> cards)
    {
        var mod = new ModFile { Name = name, Kind = "textures", Comment = "Uniform Studio selection." };
        foreach (var g in cards.Where(c => c.Replace && c.New is not null).GroupBy(c => c.Slot))
        {
            var alpha = g.First().Key is { IsBase: true, Role: "color" } ? "auto" : null;
            if (g.Any(c => c.Layer is not null))
                mod.Replacements.Add(new TextureReplacement(g.Key, "", g.Where(c => c.Layer is not null).ToDictionary(c => c.Layer!.Value, c => c.New!.Path), alpha));
            else
                mod.Replacements.Add(new TextureReplacement(g.Key, g.First().New!.Path, null, alpha));
        }
        return mod;
    }
}
