using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suite.Convert27;

/// <summary>
/// Removes unreferenced loose NHL 27 logo files and resolves roles that silhouette matching cannot determine.
/// Real source arrays remain authoritative; these rules apply only to the numbered-file fallback.
/// </summary>
internal static class UniformSourceLayouts
{
    sealed class Rule
    {
        [JsonPropertyName("omitLayers")] public int[] OmitLayers { get; set; } = [];
        [JsonPropertyName("sponsorLayers")] public int[] SponsorLayers { get; set; } = [];
    }

    static readonly Lazy<Dictionary<string, Dictionary<string, Rule>>> Rules = new(() =>
    {
        string path = Path.Combine(AppContext.BaseDirectory, "data", "uniform_source_layouts.json");
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        var store = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Rule>>>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return store is null ? new(StringComparer.Ordinal) : new(store, StringComparer.Ordinal);
    });

    public static List<LayerLabel> RefineNumbered(FolderMatch folder, string garment,
        IReadOnlyList<(int Layer, string Color, string Normal)> sources, List<LayerLabel> labels, SlotMapStore slotMaps)
    {
        if (folder.Folder27 is null) return labels;
        Rules.Value.TryGetValue(folder.Folder27, out var garments);
        Rule? rule = null;
        garments?.TryGetValue(garment, out rule);
        var omitted = new HashSet<int>(rule?.OmitLayers ?? []);
        bool shieldSeen = false;
        var refined = new List<LayerLabel>();
        foreach (var label in labels)
        {
            // These loose files also contain the retired back-hem tag and copies used by other garments. Neither is an
            // additional Fanatics jersey slot. A real NHL 27 array never comes through this fallback.
            if (label.Role == SlotRoles.EpwRole || omitted.Contains(label.Layer)) continue;
            if (label.Role == "shield" && shieldSeen) continue;
            if (label.Role == "shield") shieldSeen = true;
            refined.Add(label);
        }
        if (garment != "branding_jersey") return refined;

        var map = slotMaps.Find(folder.Folder24, folder.Folder27, garment);
        bool usableMap = map?.Layers24.Any(l => l.Role is not ("blank" or SlotRoles.EpwRole)) == true;
        var sponsorLayers = new HashSet<int>(rule?.SponsorLayers ?? []);
        // A reviewed source-layout rule owns sponsor identity; an automatic slot-map link may only add a sponsor when the
        // folder has no rule (or the link was set by hand). Carolina thirteen: the auto link paired NHL 24's sponsor slot
        // with the Stormy shoulder patch, producing a 7-role layout (two sponsors) that no NHL 24 donor can hold.
        if (usableMap)
            foreach (var link in map!.Links.Where(l => l.Role == "sponsor" && l.Nhl27 is not null && (rule is null || l.Manual)
                         && refined.Any(x => x.Layer == l.Nhl27 && x.Role == "sponsor")))
                sponsorLayers.Add(link.Nhl27!.Value);

        // A reviewed map/rule owns sponsor identity. Other non-league club marks are shoulder art, not extra sponsors.
        if (rule is not null || sponsorLayers.Count > 0)
            foreach (var label in refined.Where(l => l.Role == "sponsor" && !sponsorLayers.Contains(l.Layer)))
                label.Role = "shoulder";

        var shoulders = refined.Where(l => l.Role == "shoulder").ToList();
        if (shoulders.Count == 1)
        {
            var shoulder = shoulders[0];
            int at = refined.IndexOf(shoulder);
            // One loose file can be the shared left/right mark. Duplicating its explicit team artwork is intentional;
            // donor pixels are never involved.
            refined.Insert(at + 1, new LayerLabel { Layer = shoulder.Layer, Role = "shoulder", Detail = shoulder.Detail });
        }
        else if (shoulders.Count > 2)
        {
            throw new InvalidDataException($"{folder.Folder27} {garment}: {shoulders.Count} shoulder layers were found; "
                + "review the source-layout rule instead of guessing their placement.");
        }
        foreach (int layer in sponsorLayers)
            if (refined.All(l => l.Layer != layer))
                throw new InvalidDataException($"{folder.Folder27} {garment}: reviewed sponsor layer {layer} is absent or omitted.");
        return refined;
    }
}
