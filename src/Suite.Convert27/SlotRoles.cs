using System.Text.Json;
using System.Text.Json.Serialization;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>What one branding-array layer is: crest, shoulder, brand (adidas/Fanatics), shield, epw_tag, sponsor or blank.</summary>
public sealed class LayerLabel
{
    [JsonPropertyName("layer")] public int Layer { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    /// <summary>Which logo, when it is a league-wide one (adidas, fanatics, epw...).</summary>
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    /// <summary>Where the game draws this layer on the jersey, when known (e.g. "back hem tag").</summary>
    [JsonPropertyName("placement")] public string? Placement { get; set; }
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
    public static readonly string[] Roles = ["crest", "shoulder", "brand", "shield", "epw_tag", "sponsor", "blank"];

    /// <summary>
    /// Global rule (user, 2026-11-01): every End Plastic Waste layer, on every team's jersey, is drawn as the small tag on
    /// the very back hem. Placement is fixed per layer, so only an End Plastic Waste (back tag) image may go into it.
    /// </summary>
    public const string EpwRole = "epw_tag";
    public const string EpwPlacement = "back hem tag";

    static Dictionary<string, double[]>? _refs;
    public static bool HasManufacturerReferences => Refs().ContainsKey("brand_adidas") && Refs().ContainsKey("brand_fanatics");

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
    /// <summary>
    /// The league-wide logo a decoded image matches ("brand_fanatics", "patch_epw", ...), or null. Used to find the
    /// Fanatics mark in another NHL 27 set when the paired set has no brand layer of its own.
    /// </summary>
    public static string? KnownLogo(string? png)
    {
        if (png is null) return null;
        var s = LogoMatcher.Silhouette(png);
        return s is null || IsBlank(s) ? null : KnownLogo(s);
    }

    /// <summary>
    /// How close a silhouette must be to a league-wide reference to BE that logo.  League logos are the same picture in
    /// every set of both games: the NHL shield, adidas, Fanatics and End Plastic Waste score 0 (measured on DET, LA, MIN,
    /// NYR and NHL 27 LA).  Club crests shaped like the shield score 18-40 (Kings 21, Rangers pants 18, Wild 38), so the
    /// old cut-off (24, pants 40) read them as the shield and paired the NHL shield onto them; sponsors score 50+.
    /// </summary>
    public const double LeagueLogoMad = 10;

    public static string? KnownLogo(double[] s) => Refs().Select(r => (r.Key, d: LogoMatcher.Difference(s, r.Value)))
        .Where(r => r.d <= LeagueLogoMad).OrderBy(r => r.d).Select(r => r.Key).FirstOrDefault();

    /// <summary>Automatic labels for a garment's layers. <paramref name="images"/>: layer → PNG/DDS path (null = no image).</summary>
    public static List<LayerLabel> Label(IReadOnlyList<(int Layer, string? Image)> images, string? garment = null)
    {
        var sil = images.ToDictionary(i => i.Layer, i => i.Image is null ? null : LogoMatcher.Silhouette(i.Image));
        var labels = new List<LayerLabel>();
        foreach (var (layer, _) in images.OrderBy(i => i.Layer))
        {
            var s = sil[layer];
            if (s is null || IsBlank(s)) { labels.Add(new LayerLabel { Layer = layer, Role = "blank" }); continue; }
            var known = KnownLogo(s);
            if (known == "patch_epw")
            {
                labels.Add(new LayerLabel { Layer = layer, Role = EpwRole, Detail = "End Plastic Waste", Placement = EpwPlacement });
                continue;
            }
            if (known is not null)
            {
                var role = known.Split('_')[0];
                labels.Add(new LayerLabel { Layer = layer, Role = role, Detail = known.Contains('_') ? known[(known.IndexOf('_') + 1)..] : null });
                continue;
            }
            labels.Add(new LayerLabel { Layer = layer, Role = "" });
        }
        // Layer 0 is the crest when it is not a league-wide logo.
        if (garment == "branding_pant")
        {
            // Pants reorder the league shield and club mark between games (e.g. STL: shield, SL, shield).
            // The first actual club image is the crest, not whichever image happens to be layer zero.
            if (labels.FirstOrDefault(l => l.Role == "") is { } club) club.Role = "crest";
        }
        else if (labels.FirstOrDefault(l => l.Layer == 0) is { Role: "" } first) first.Role = "crest";
        // Repeated images (left/right shoulder, two wordmarks) are a pair.
        var open = labels.Where(l => l.Role == "").ToList();
        foreach (var a in open)
            foreach (var b in open.Where(b => b != a && b.Layer > a.Layer))
                if (sil[a.Layer] is { } sa && sil[b.Layer] is { } sb && LogoMatcher.Difference(sa, sb) <= LogoMatcher.AcceptMad)
                    a.Role = b.Role = "shoulder";
        foreach (var l in labels.Where(l => l.Role == "")) l.Role = "sponsor";
        return labels;
    }

    /// <summary>
    /// The End Plastic Waste lock: an NHL 24 back-tag layer only takes an NHL 27 back-tag layer, and no other NHL 24 layer
    /// takes the back tag. Any link that breaks this (automatic or hand-made) is reset to "keep NHL 24". Returns the count reset.
    /// </summary>
    public static int EnforceEpwLock(SlotMapEntry entry)
    {
        int reset = 0;
        foreach (var link in entry.Links)
        {
            if (link.Nhl27 is not { } src) continue;
            bool target24IsTag = entry.Layers24.Any(l => l.Layer == link.Nhl24 && l.Role == EpwRole);
            bool source27IsTag = entry.Layers27.Any(l => l.Layer == src && l.Role == EpwRole);
            if (target24IsTag != source27IsTag)
            {
                SuiteLog.Write($"slot map: refused NHL 27 layer {src} -> NHL 24 layer {link.Nhl24} in {entry.Folder24} {entry.Garment} (End Plastic Waste back tag must stay a back tag)");
                link.Nhl27 = null;
                reset++;
            }
        }
        foreach (var l in entry.Links.Where(l => entry.Layers24.Any(x => x.Layer == l.Nhl24 && x.Role == EpwRole))) l.Role = EpwRole;
        return reset;
    }

    /// <summary>
    /// True when an image is the NHL shield.  It was looser than <see cref="KnownLogo"/> (40, "rendered a little
    /// differently by either game"), but both games' shields score 0 against the reference and shield-shaped club logos
    /// score 18-40: at 40 the Kings' and Wild's pants logos counted as the shield and were kept stock.
    /// </summary>
    public static bool LooksLikeShield(string? png)
    {
        if (png is null || !File.Exists(png) || !Refs().TryGetValue("shield", out var shield)) return false;
        var s = LogoMatcher.Silhouette(png);
        return s is not null && !IsBlank(s) && LogoMatcher.Difference(s, shield) <= LeagueLogoMad;
    }

    /// <summary>
    /// Reconciles an automatic colour-array row with its role map. Older saved projects can contain a wrong automatic
    /// pick (or no pick) even after the map has been corrected, so filling only empty layers is not enough. A manual
    /// array row is never changed. Returns the number of layer picks changed.
    /// </summary>
    public static int ReconcileColourArray(SlotMatch row, SlotMapEntry entry, IReadOnlyDictionary<int, string> sources27)
    {
        if (row.Manual) return 0;
        int changed = 0;
        foreach (var link in entry.Links)
        {
            var layer = row.Layers.FirstOrDefault(l => l.Layer == link.Nhl24);
            if (layer is null) continue;
            string? source = link.Nhl27 is { } n ? sources27.GetValueOrDefault(n) : null;
            // A missing decoded source must not erase an existing pick; it can appear after the next decode pass.
            if (link.Nhl27 is not null && source is null) continue;
            if (layer.Source27 == source && (source is not null || layer.FilePath is null)) continue;
            layer.Source27 = source;
            layer.FilePath = null;
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Which numbered texture (…_N_color) shows the same stock picture as each layer of a colour array, from the folder's
    /// stock images (<paramref name="stock"/>: slot name → PNG, array layers as "slot#layer").  NHL 24 keeps its branding
    /// arrays in its own order (Detroit pants: array 0 = NHL shield, 1 = club; numbered 0 = club, 1 = empty grey,
    /// 2 = shield), so pairing by number put the Red Wings on the empty layer and turned sponsor patches into crests.
    /// Pairs by picture instead: closest pairs first, each texture used once (two identical shoulder patches get one
    /// each), only within the logo matcher's acceptance.  A layer with no partner is absent from the result.
    /// </summary>
    public static Dictionary<int, string> PairNumbered(string colorArraySlot, IReadOnlyDictionary<string, string> stock, Dictionary<string, double[]?>? cache = null)
    {
        cache ??= new(StringComparer.Ordinal);
        double[]? Sil(string png)
        {
            if (!cache.TryGetValue(png, out var s)) cache[png] = s = LogoMatcher.Silhouette(png);
            return s;
        }
        var prefix = colorArraySlot[..^"_color_array".Length] + "_";
        var numbered = stock.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Key.EndsWith("_color", StringComparison.Ordinal)
                && int.TryParse(kv.Key[prefix.Length..^"_color".Length], out _))
            .Select(kv => (Slot: kv.Key, Sil: Sil(kv.Value))).Where(x => x.Sil is not null).ToList();
        var slices = stock.Where(kv => kv.Key.StartsWith(colorArraySlot + "#", StringComparison.Ordinal) && int.TryParse(kv.Key[(colorArraySlot.Length + 1)..], out _))
            .Select(kv => (Layer: int.Parse(kv.Key[(colorArraySlot.Length + 1)..]), Sil: Sil(kv.Value))).Where(x => x.Sil is not null).ToList();
        var pairs = new Dictionary<int, string>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (layer, slot, _) in slices.SelectMany(s => numbered.Select(n => (s.Layer, n.Slot, D: LogoMatcher.Difference(s.Sil!, n.Sil!))))
                     .Where(x => x.D <= LogoMatcher.AcceptMad).OrderBy(x => x.D))
            if (!pairs.ContainsKey(layer) && taken.Add(slot)) pairs[layer] = slot;
        return pairs;
    }

    /// <summary>
    /// Pants rule (user, 2026-11-02): every NHL 24 pants set already carries the NHL shield on the back, so no pants layer
    /// ever takes the shield from NHL 27 and the shield layers themselves stay stock. A team-logo layer is only ever
    /// filled by a club mark.
    /// </summary>
    public static int EnforcePantsCrestLock(SlotMapEntry entry)
    {
        if (entry.Garment != "branding_pant") return 0;
        int reset = 0;
        foreach (var link in entry.Links)
        {
            bool shield24 = entry.Layers24.Any(l => l.Layer == link.Nhl24 && l.Role == "shield");
            bool shield27 = link.Nhl27 is { } src27 && entry.Layers27.Any(l => l.Layer == src27 && l.Role == "shield");
            if (link.Nhl27 is not null && (shield24 || shield27))
            {
                link.Nhl27 = null;
                reset++;
                continue;
            }
            if (!entry.Layers24.Any(l => l.Layer == link.Nhl24 && l.Role == "crest")) continue;
            link.Role = "crest";
            if (link.Nhl27 is not { } source || entry.Layers27.Any(l => l.Layer == source && l.Role == "crest")) continue;
            link.Nhl27 = null;
            reset++;
        }
        return reset;
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
        entry.Layers24 = MergeLabels(entry.Layers24, SlotRoles.Label(images24, garment), garment == "branding_pant");
        entry.Layers27 = MergeLabels(entry.Layers27, SlotRoles.Label(images27, garment), garment == "branding_pant");
        var (links, noSlot) = SlotRoles.Map(entry.Layers24, entry.Layers27);
        var manual = entry.Links.Where(l => l.Manual).ToDictionary(l => l.Nhl24);
        entry.Links = links.Select(l => manual.TryGetValue(l.Nhl24, out var m) ? m : l).ToList();
        ManufacturerBranding.RequireFanaticsLinks(entry);
        SlotRoles.EnforcePantsCrestLock(entry);
        SlotRoles.EnforceEpwLock(entry);
        var linked = entry.Links.Where(l => l.Nhl27 is not null).Select(l => l.Nhl27!.Value).ToHashSet();
        entry.NoSlot27 = entry.Layers27.Where(l => l.Role != "blank" && !linked.Contains(l.Layer)).Select(l => l.Layer).ToList();
        return entry;
    }

    static List<LayerLabel> MergeLabels(List<LayerLabel> saved, List<LayerLabel> auto, bool protectPantsCrests)
    {
        var manual = saved.Where(l => l.Manual).ToDictionary(l => l.Layer);
        // A layer recognised as End Plastic Waste stays the back tag even if it was relabelled by hand.
        return auto.Select(a => a.Role != SlotRoles.EpwRole && !ManufacturerBranding.IsAdidas(a)
            && !(protectPantsCrests && a.Role == "crest") && manual.TryGetValue(a.Layer, out var m) ? m : a).ToList();
    }
}
