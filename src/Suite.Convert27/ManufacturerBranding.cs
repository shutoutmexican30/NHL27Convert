using Suite.Core;

namespace Suite.Convert27;

/// <summary>The manufacturer's colour/alpha and coefficient layers always change as a pair.</summary>
public sealed record BrandingLayerSet(string ColorArraySlot, int Layer, string ColorPng, string NormalPng);

public static class ManufacturerBranding
{
    public static void KeepStockLayer(ModFile mod, string colorSlot, int layer)
    {
        foreach (var role in new[] { "color", "normal", "coeff" })
        {
            var stem = colorSlot[..^"_color_array".Length];
            string slot = stem + $"_{role}_array";
            foreach (var r in mod.Replacements.Where(r => r.Slot == slot))
            {
                if (r.Source.Length > 0) throw new InvalidDataException("A whole branding array cannot retain an unmatched team-logo layer. Use per-layer replacements.");
                r.Layers?.Remove(layer);
            }
            mod.Replacements.RemoveAll(r => (r.Slot == slot && (r.Layers?.Count ?? 0) == 0)
                || r.Slot == stem + $"_{layer}_{role}");
        }
    }

    /// <summary>
    /// Drops the per-layer replacements of one branding array stem for slices the destination array does not have, so a
    /// mapped-but-impossible layer is never written into a shorter array. Empty array entries are removed afterwards.
    /// </summary>
    public static void PruneLayers(ModFile mod, string stem, IReadOnlyList<int> layers)
    {
        foreach (var r in mod.Replacements)
        {
            if (r.Layers is not { Count: > 0 } || !r.Slot.StartsWith(stem, StringComparison.Ordinal)) continue;
            foreach (var layer in layers) r.Layers.Remove(layer);
        }
        mod.Replacements.RemoveAll(r => r.Layers is { Count: 0 });
    }

    public static void ApplyToRows(IEnumerable<SlotMatch> rows, BrandingLayerSet item, string? reason = null)
    {
        var list = rows.ToList();
        foreach (var (slot, source) in new[] { (item.ColorArraySlot, item.ColorPng),
                     (item.ColorArraySlot[..^"_color_array".Length] + "_normal_array", item.NormalPng) })
        {
            var row = list.FirstOrDefault(r => r.Slot == slot && r.IsArray);
            var layer = row?.Layers.FirstOrDefault(l => l.Layer == item.Layer);
            if (layer is not null)
            {
                layer.Source27 = null; layer.FilePath = source;
                row!.Reason = reason ?? "Adidas → Fanatics: colour/alpha + matching normal/mask; coeff regenerated at export";
                row.Confidence = 1;
            }
            var role = slot.EndsWith("_color_array", StringComparison.Ordinal) ? "color" : "normal";
            var mirrorSlot = item.ColorArraySlot[..^"_color_array".Length] + $"_{item.Layer}_{role}";
            var mirror = list.FirstOrDefault(r => r.Slot == mirrorSlot);
            if (mirror is null) continue;
            mirror.Source27 = null; mirror.FilePath = source; mirror.Kind = SourceKind.File;
            mirror.Reason = reason ?? "Adidas → Fanatics: matching logo normal/mask and alpha"; mirror.Confidence = 1;
        }
    }

    public static bool IsAdidas(LayerLabel label) => label.Role == "brand"
        && label.Detail?.StartsWith("adidas", StringComparison.OrdinalIgnoreCase) == true;
    public static bool IsFanatics(LayerLabel label) => label.Role == "brand"
        && string.Equals(label.Detail, "fanatics", StringComparison.OrdinalIgnoreCase);

    public static void RequireFanaticsLinks(SlotMapEntry entry)
    {
        var sources = entry.Layers27.Where(IsFanatics).OrderBy(l => l.Layer).ToList();
        int index = 0;
        foreach (var target in entry.Layers24.Where(IsAdidas).OrderBy(l => l.Layer))
        {
            var link = entry.Links.FirstOrDefault(l => l.Nhl24 == target.Layer);
            if (link is null) { link = new LayerLink { Nhl24 = target.Layer }; entry.Links.Add(link); }
            // A user-supplied Fanatics pick is valid. Adidas/stock/wrong-logo picks cannot defeat this rule.
            if (!entry.Layers27.Any(l => l.Layer == link.Nhl27 && IsFanatics(l)))
                link.Nhl27 = sources.Count == 0 ? null : sources[Math.Min(index, sources.Count - 1)].Layer;
            link.Role = "brand";
            index++;
        }
    }

    /// <summary>Replaces the entire alpha-bearing image; never reuses an Adidas mask under Fanatics RGB.</summary>
    public static void ApplyToMod(ModFile mod, IEnumerable<BrandingLayerSet> replacements)
    {
        foreach (var item in replacements)
        {
            if (!item.ColorArraySlot.EndsWith("_color_array", StringComparison.Ordinal) || item.Layer < 0
                || string.IsNullOrWhiteSpace(item.ColorPng) || string.IsNullOrWhiteSpace(item.NormalPng))
                throw new InvalidDataException("Invalid branding array replacement (colour and normal are both required).");
            PutLayer(mod, item.ColorArraySlot, item.Layer, item.ColorPng);
            PutLayer(mod, item.ColorArraySlot[..^"_color_array".Length] + "_normal_array", item.Layer, item.NormalPng);
            // Worker rule 13 generates clean material values from the NEW Fanatics alpha, not the old Adidas shape.
            PutLayer(mod, item.ColorArraySlot[..^"_color_array".Length] + "_coeff_array", item.Layer, "coeff:" + item.ColorPng);
        }
    }

    static void PutLayer(ModFile mod, string slot, int layer, string source)
    {
        var existing = mod.Replacements.Where(r => r.Slot == slot).ToList();
        if (existing.Any(r => r.Source.Length > 0))
            throw new InvalidDataException($"{slot}: whole-array replacement conflicts with the required Fanatics layer. Use per-layer replacements.");
        var layers = new Dictionary<int, string>();
        foreach (var row in existing)
            foreach (var kv in row.Layers ?? []) layers[kv.Key] = kv.Value;
        layers[layer] = source;
        mod.Replacements.RemoveAll(r => r.Slot == slot);
        mod.Replacements.Add(new TextureReplacement(slot, "", layers));
    }
}
