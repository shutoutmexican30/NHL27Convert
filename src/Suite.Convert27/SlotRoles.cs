using System.Text.Json;
using System.Text.Json.Serialization;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>What one branding-array layer is: crest, shoulder, brand (adidas/Fanatics), shield, patch (EPW), sponsor or blank.</summary>
public sealed class LayerLabel
{
    [JsonPropertyName("layer")] public int Layer { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    /// <summary>Which logo, when it is a league-wide one (adidas, fanatics, epw...).</summary>
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    /// <summary>Set by hand; automatic labelling never changes it.</summary>
    [JsonPropertyName("manual")] public bool Manual { get; set; }
}

/// <summary>One NHL 24 layer and the NHL 27 layer that fills it (null = keep the NHL 24 layer).</summary>
public sealed class LayerLink
{
    [JsonPropertyName("nhl24")] public int Nhl24 { get; set; }
    [JsonPropertyName("nhl27")] public int? Nhl27 { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("manual")] public bool Manual { get; set; }
}

/// <summary>The slot map for one garment (branding_jersey / branding_pant) of one NHL 24 set and the NHL 27 set it takes art from.</summary>
public sealed class SlotMapEntry
{
    [JsonPropertyName("folder24")] public string Folder24 { get; set; } = "";
    [JsonPropertyName("folder27")] public string Folder27 { get; set; } = "";
    [JsonPropertyName("garment")] public string Garment { get; set; } = "";
    [JsonPropertyName("layers24")] public List<LayerLabel> Layers24 { get; set; } = [];
    [JsonPropertyName("layers27")] public List<LayerLabel> Layers27 { get; set; } = [];
    [JsonPropertyName("links")] public List<LayerLink> Links { get; set; } = [];
    /// <summary>NHL 27 layers with no NHL 24 slot (e.g. a second sponsor).</summary>
    [JsonPropertyName("noSlot27")] public List<int> NoSlot27 { get; set; } = [];
}

/// <summary>
/// Labels branding layers by role and maps NHL 24 layers to NHL 27 layers role to role. Layer order differs between
/// jerseys (St. Louis NHL 24 home: crest, shield, adidas, EPW, Stifel, shield; NHL 27 home: crest, Stifel, Fanatics,
/// shield, Stifel, shield), so indexes are never trusted on their own. Rules, in order:
/// league-wide logos by shape (data\logo_refs.json: NHL shield, adidas, adidas stripes, Fanatics, End Plastic Waste),
/// blank layers, layer 0 = crest, repeated images = shoulder pair, anything left = sponsor/wordmark.
/// The brand role links adidas (NHL 24) to Fanatics (NHL 27), so every converted kit gets the Fanatics mark.
/// </summary>
public static class SlotRoles
{
    public static readonly string[] Roles = ["crest", "shoulder", "brand", "shield", "patch", "sponsor", "blank"];

    static Dictionary<string, double[]>? _refs;

    static Dictionary<string, double[]> Refs()
    {
        if (_refs is not null) return _refs;
        var path = Path.Combine(AppContext.BaseDirectory, "data", "logo_refs.json");
        _refs = [];
        if (!File.Exists(path)) return _refs;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var p in doc.RootElement.GetProperty("refs").EnumerateObject())
            _refs[p.Name] = p.Value.EnumerateArray().Select(v => v.GetDouble()).ToArray();
        return _refs;
    }

    /// <summary>Automatic labels for a garment's layers. <paramref name="images"/>: layer → PNG/DDS path (null = no image).</summary>
    public static List<LayerLabel> Label(IReadOnlyList<(int Layer, string? Image)> images)
    {
        var sil = images.ToDictionary(i => i.Layer, i => i.Image is null ? null : LogoMatcher.Silhouette(i.Image));
        var labels = new List<LayerLabel>();
        foreach (var (layer, _) in images.OrderBy(i => i.Layer))
        {
            var s = sil[layer];
            if (s is null || IsBlank(s)) { labels.Add(new LayerLabel { Layer = layer, Role = "blank" }); continue; }
            var known = Refs().Select(r => (r.Key, d: LogoMatcher.Difference(s, r.Value))).Where(r => r.d <= LogoMatcher.AcceptMad)
                .OrderBy(r => r.d).Select(r => r.Key).FirstOrDefault();
            if (known is not null)
            {
                var role = known.Split('_')[0];
                labels.Add(new LayerLabel { Layer = layer, Role = role, Detail = known.Contains('_') ? known[(known.IndexOf('_') + 1)..] : null });
                continue;
            }
            labels.Add(new LayerLabel { Layer = layer, Role = "" });
        }
        // Layer 0 is the crest when it is not a league-wide logo.
        if (labels.FirstOrDefault(l => l.Layer == 0) is { Role: "" } first) first.Role = "crest";
        // Repeated images (left/right shoulder, two wordmarks) are a pair.
        var open = labels.Where(l => l.Role == "").ToList();
        foreach (var a in open)
            foreach (var b in open.Where(b => b != a && b.Layer > a.Layer))
                if (sil[a.Layer] is { } sa && sil[b.Layer] is { } sb && LogoMatcher.Difference(sa, sb) <= LogoMatcher.AcceptMad)
                    a.Role = b.Role = "shoulder";
        foreach (var l in labels.Where(l => l.Role == "")) l.Role = "sponsor";
        return labels;
    }

    /// <summary>Opaque single-colour placeholders (the grey squares) and fully transparent layers carry no logo.</summary>
    static bool IsBlank(double[] s)
    {
        var mean = s.Average();
        return mean < 4 || s.All(v => Math.Abs(v - mean) < 2);
    }

    /// <summary>Links each NHL 24 layer to the NHL 27 layer with the same role (k-th to k-th within a role).</summary>
    public static (List<LayerLink> Links, List<int> NoSlot27) Map(IReadOnlyList<LayerLabel> l24, IReadOnlyList<LayerLabel> l27)
    {
        var links = new List<LayerLink>();
        var used = new HashSet<int>();
        foreach (var role in Roles.Where(r => r != "blank"))
        {
            var a = l24.Where(l => l.Role == role).OrderBy(l => l.Layer).ToList();
            var b = l27.Where(l => l.Role == role).OrderBy(l => l.Layer).ToList();
            for (int k = 0; k < a.Count; k++)
            {
                int? target = k < b.Count ? b[k].Layer : null;
                if (target is not null) used.Add(target.Value);
                links.Add(new LayerLink { Nhl24 = a[k].Layer, Nhl27 = target, Role = role });
            }
        }
        foreach (var blank in l24.Where(l => l.Role == "blank")) links.Add(new LayerLink { Nhl24 = blank.Layer, Nhl27 = null, Role = "blank" });
        var noSlot = l27.Where(l => l.Role != "blank" && !used.Contains(l.Layer)).Select(l => l.Layer).ToList();
        return (links.OrderBy(l => l.Nhl24).ToList(), noSlot);
    }
}

/// <summary>
/// Hand-editable store of slot maps: %APPDATA%\CenterIceModdingSuite\data\slot_map.json. Automatic labels are
/// recomputed; labels and links marked manual are kept.
/// </summary>
public sealed class SlotMapStore
{
    [JsonPropertyName("format")] public int Format { get; set; } = 1;
    [JsonPropertyName("comment")] public string Comment { get; set; } =
        "Per NHL 24 set and garment: what each branding layer is (role) in both games, and which NHL 27 layer fills each NHL 24 layer. Entries marked manual are never changed automatically.";
    [JsonPropertyName("entries")] public List<SlotMapEntry> Entries { get; set; } = [];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static string PathName => Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Roaming, "data")), "slot_map.json");

    public static SlotMapStore Load()
    {
        try { return File.Exists(PathName) ? JsonSerializer.Deserialize<SlotMapStore>(File.ReadAllText(PathName), Json) ?? new() : new(); }
        catch (Exception ex) { SuiteLog.Write("slot_map.json unreadable: " + ex.Message); return new(); }
    }

    public void Save() => File.WriteAllText(PathName, JsonSerializer.Serialize(this, Json));

    public SlotMapEntry? Find(string folder24, string folder27, string garment) =>
        Entries.FirstOrDefault(e => e.Folder24 == folder24 && e.Folder27 == folder27 && e.Garment == garment);

    /// <summary>Builds or refreshes the entry: automatic labels and links, keeping everything marked manual.</summary>
    public SlotMapEntry Resolve(string folder24, string folder27, string garment,
        IReadOnlyList<(int Layer, string? Image)> images24, IReadOnlyList<(int Layer, string? Image)> images27)
    {
        var entry = Find(folder24, folder27, garment);
        if (entry is null) { entry = new SlotMapEntry { Folder24 = folder24, Folder27 = folder27, Garment = garment }; Entries.Add(entry); }
        entry.Layers24 = MergeLabels(entry.Layers24, SlotRoles.Label(images24));
        entry.Layers27 = MergeLabels(entry.Layers27, SlotRoles.Label(images27));
        var (links, noSlot) = SlotRoles.Map(entry.Layers24, entry.Layers27);
        var manual = entry.Links.Where(l => l.Manual).ToDictionary(l => l.Nhl24);
        entry.Links = links.Select(l => manual.TryGetValue(l.Nhl24, out var m) ? m : l).ToList();
        var linked = entry.Links.Where(l => l.Nhl27 is not null).Select(l => l.Nhl27!.Value).ToHashSet();
        entry.NoSlot27 = entry.Layers27.Where(l => l.Role != "blank" && !linked.Contains(l.Layer)).Select(l => l.Layer).ToList();
        return entry;
    }

    static List<LayerLabel> MergeLabels(List<LayerLabel> saved, List<LayerLabel> auto)
    {
        var manual = saved.Where(l => l.Manual).ToDictionary(l => l.Layer);
        return auto.Select(a => manual.TryGetValue(a.Layer, out var m) ? m : a).ToList();
    }
}
