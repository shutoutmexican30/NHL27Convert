using Suite.Core;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Suite.Convert27;

public enum SourceKind { Stock, Nhl27, File }

/// <summary>Source for one layer of an NHL 24 texture array (logos/branding come from arrays, rule 12).</summary>
public sealed class LayerMatch : Observable
{
    public int Layer { get; init; }
    string? _source27;
    string? _file;
    public string? Source27 { get => _source27; set => Set(ref _source27, value); }
    public string? FilePath { get => _file; set => Set(ref _file, value); }
    public string Display => FilePath is not null ? System.IO.Path.GetFileName(FilePath) : Source27?.Split('/')[^1] ?? "(stock)";
}

/// <summary>One NHL 24 slot and what goes into it. Every field is editable by hand in the matcher.</summary>
public sealed class SlotMatch : Observable
{
    public required string Slot { get; init; }
    public bool IsArray { get; init; }
    public TextureName Name => TextureName.Parse(Slot);
    public string Leaf => Name.Leaf;
    public string Role => Name.Role;
    public string Garment => Name.Garment;

    SourceKind _kind;
    string? _source27;
    string? _file;
    double _confidence;
    string _reason = "";
    bool _manual;
    string? _alpha;

    public SourceKind Kind { get => _kind; set { if (Set(ref _kind, value)) Raise(nameof(SourceDisplay)); } }
    public string? Source27 { get => _source27; set { if (Set(ref _source27, value)) Raise(nameof(SourceDisplay)); } }
    public string? FilePath { get => _file; set { if (Set(ref _file, value)) Raise(nameof(SourceDisplay)); } }
    public double Confidence { get => _confidence; set => Set(ref _confidence, value); }
    public string Reason { get => _reason; set => Set(ref _reason, value); }
    /// <summary>Set once the user has touched this row; re-proposing never overwrites a manual row.</summary>
    public bool Manual { get => _manual; set => Set(ref _manual, value); }
    /// <summary>"auto" keeps the game's fabric-region alpha (rule 11), "nodimples" also maps 102 to 51, null = image alpha.</summary>
    public string? Alpha { get => _alpha; set => Set(ref _alpha, value); }
    public List<LayerMatch> Layers { get; set; } = [];

    public string SourceDisplay => Kind switch
    {
        SourceKind.Nhl27 => Source27?.Split('/')[^1] ?? "",
        SourceKind.File => System.IO.Path.GetFileName(FilePath ?? ""),
        _ => IsArray && Layers.Any(l => l.Source27 is not null || l.FilePath is not null)
            ? $"{Layers.Count(l => l.Source27 is not null || l.FilePath is not null)} layers" : "(keep stock)",
    };

    /// <summary>Editable text of the Source column; the page resolves it (NHL 27 path, image file, or empty for stock).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SourceText
    {
        get => Kind switch { SourceKind.Nhl27 => Source27, SourceKind.File => FilePath, _ => "(keep stock)" };
        set { SourceTextChanged?.Invoke(this, value); Raise(); }
    }

    [System.Text.Json.Serialization.JsonIgnore] public Action<SlotMatch, string?>? SourceTextChanged { get; set; }

    string? _thumb24, _thumbSource;
    [System.Text.Json.Serialization.JsonIgnore] public string? Thumb24 { get => _thumb24; set => Set(ref _thumb24, value); }
    [System.Text.Json.Serialization.JsonIgnore] public string? ThumbSource { get => _thumbSource; set => Set(ref _thumbSource, value); }

    public void ClearSource()
    {
        Kind = SourceKind.Stock;
        Source27 = null;
        FilePath = null;
    }
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed record FolderProposal(string Folder27, double Confidence, string Reason);

/// <summary>
/// Proposes NHL 27 sources for NHL 24 slots from the two games' folder layouts and names.
/// Folder level: team (with the hand-editable team map), brand, set. Slot level: same role in the folder
/// (garment, branding layer, _base_, color/normal/coeff, array), compared on the canonical leaf name.
/// </summary>
public static class Matcher
{
    /// <summary>NHL 24 team folder → NHL 27 team folder where they differ. Arizona's slots take Utah art (artid 22 kept).</summary>
    public static readonly Dictionary<string, string> DefaultTeamMap = new(StringComparer.Ordinal) { ["arizona"] = "utah" };

    public static IEnumerable<FolderProposal> ProposeFolders(string folder24, Nhl27Catalog c27, IReadOnlyDictionary<string, string>? teamMap = null, VariantMap? variants = null)
    {
        var n = TextureName.Parse(folder24 + "/x");
        if (n.Team is null || n.Brand is null || n.Set is null)
        {
            if (c27.ByFolder.ContainsKey(folder24)) yield return new(folder24, 0.9, "same folder in NHL 27");
            yield break;
        }
        var vt = variants?.ByNhl24Folder(n.Team);
        var map = teamMap ?? DefaultTeamMap;
        var team27 = vt?.Nhl27.Folder ?? (map.TryGetValue(n.Team, out var t) ? t : n.Team);
        var prefix = $"{TextureName.SkaterPrefix}{n.League}/{team27}/";
        var candidates = c27.ByFolder.Keys.Where(f => f.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        var given = new HashSet<string>();

        // 1. Default home/away paired through the variant map (stockteamjerseys isdefault/islightcolor), never by folder name.
        if (variants?.Pair(n.Team, n.Set) is { } pair)
        {
            var target = candidates.Where(f => f.EndsWith("/" + pair.Set27, StringComparison.Ordinal))
                .OrderBy(f => f.Split('/')[^2] == n.Brand ? 0 : 1).FirstOrDefault();
            if (target is not null)
            {
                var why = $"default {pair.Role}";
                if (team27 != n.Team) why = $"{n.Team} → {team27}; " + why;
                yield return new(target, 0.95, why);
                given.Add(target);
            }
        }

        // 2. Same set word (alternates and retro sets; numbering differs between games, so lower confidence).
        var same = $"{prefix}{n.Brand}/{n.Set}";
        if (candidates.Contains(same) && given.Add(same))
        {
            var isDefault = vt is not null && (n.Set == vt.Nhl24.Home || n.Set == vt.Nhl24.Away);
            var conf = isDefault ? 0.3 : 0.6;
            var why = isDefault
                ? $"same folder name '{n.Set}', but NHL 27's default {(n.Set == vt!.Nhl24.Home ? "home" : "away")} is elsewhere"
                : $"same set name '{n.Set}'; NHL 24 and NHL 27 number alternates separately, check it is the same jersey";
            yield return new(same, conf, why);
        }
        foreach (var f in candidates.Where(f => !given.Contains(f))
                     .OrderBy(f => f.Split('/')[^2] == n.Brand ? 0 : 1)
                     .ThenBy(f => TextureName.SetOrder(f.Split('/')[^1])))
        {
            var parts = f.Split('/');
            var conf = parts[^2] == n.Brand ? 0.2 : 0.1;
            yield return new(f, conf, $"other set of {team27}: {parts[^2]}/{parts[^1]}");
        }
    }
    public static List<SlotMatch> Propose(IReadOnlyList<Nhl24Slot> slots24, string folder27, double folderConfidence, Nhl27Catalog c27)
    {
        var textures27 = c27.ByFolder.TryGetValue(folder27, out var list) ? list : [];
        var byCanon27 = textures27.GroupBy(t => TextureName.Parse(t.Name).Canonical).ToDictionary(g => g.Key, g => g.First());
        var rows = new List<SlotMatch>();
        var siblings24 = slots24.Select(s => TextureName.Parse(s.Name)).ToList();

        foreach (var slot in slots24.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var name = TextureName.Parse(slot.Name);
            var row = new SlotMatch { Slot = slot.Name, IsArray = slot.Array };
            if (name.IsBase && name.Role == "color") row.Alpha = "auto";
            rows.Add(row);

            if (name.IsBase && name.Role == "coeff")
            {
                row.Reason = "rule 10: keep base coeff maps stock (NHL 26/27 base coeffs turn gameplay jerseys black)";
                continue;
            }

            // Numbered logo layers (branding crests, helmet logos) are in a different order in the other game:
            // NHL 24 layer 0 can be the NHL shield while NHL 27 layer 0 is the team crest. Leave them stock and
            // let the user pick each one by hand instead of silently putting the wrong logo in the slot.
            if (name.Layer is not null)
            {
                row.Reason = "logo layer: layer order differs between games, choose it by hand";
                continue;
            }

            if (slot.Array)
            {
                ProposeLayers(row, name, siblings24, byCanon27, folderConfidence);
                continue;
            }

            if (byCanon27.TryGetValue(name.Canonical, out var exact))
            {
                Use(row, exact, folderConfidence * SizeFactor(exact, name), "same role in the matched NHL 27 set");
                continue;
            }
            // _base_ ↔ plain fallback: skaters draw _base_ (rule 1), so a plain source for a base slot is a guess.
            var swapped = name.IsBase ? name.Canonical.Replace("_base_", "_") : Regex.Replace(name.Canonical, "_(color|normal|coeff)$", "_base_$1");
            if (byCanon27.TryGetValue(swapped, out var alt))
            {
                Use(row, alt, folderConfidence * 0.5, name.IsBase ? "no NHL 27 _base_ texture; plain one used (check)" : "only a _base_ texture in NHL 27 (check)");
                continue;
            }
            row.Reason = "no NHL 27 texture with this role in the matched set";
        }
        return rows;
    }

    static void ProposeLayers(SlotMatch row, TextureName arrayName, List<TextureName> siblings24, Dictionary<string, Nhl27Texture> byCanon27, double folderConfidence)
    {
        // branding_jersey_{brand}_{team}_{set}_color_array → layers branding_jersey_{brand}_{team}_{set}_<i>_color
        var stem = arrayName.Canonical[..^"_array".Length];
        var role = arrayName.Role;
        var layerStem = stem[..^(role.Length + 1)];
        var count24 = siblings24.Count(s => s.Layer is not null && s.Canonical == $"{layerStem}_{s.Layer}_{role}");
        var available27 = byCanon27.Keys.Select(k => Regex.Match(k, "^" + Regex.Escape(layerStem) + @"_(\d+)_" + role + "$"))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
        int count = count24 > 0 ? count24 : available27.Count == 0 ? 0 : available27.Max() + 1;
        // Layers are listed so each one can be picked by hand, but none is filled automatically: the branding
        // layers are ordered differently in the other game, so layer i is not the same logo (rule 12 only holds
        // in-game, where the array order is authored for that game).
        for (int i = 0; i < count; i++) row.Layers.Add(new LayerMatch { Layer = i });
        row.Confidence = 0;
        row.Reason = count == 0
            ? "no layers found"
            : $"{count} logo layers: layer order differs between games, choose each one by hand (logos come from arrays, rule 12)";
    }

    static void Use(SlotMatch row, Nhl27Texture t, double confidence, string reason)
    {
        row.Kind = SourceKind.Nhl27;
        row.Source27 = t.Name;
        row.Confidence = Math.Round(confidence, 2);
        row.Reason = reason;
    }

    // Size differences are fine (the writer resizes to the slot at FirstMip), aspect changes are suspicious.
    static double SizeFactor(Nhl27Texture t, TextureName _) => t.Width == t.Height || t.Width == 2 * t.Height || t.Height == 2 * t.Width ? 1.0 : 0.8;
}
