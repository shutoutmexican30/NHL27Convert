using Suite.Core;

namespace Suite.Convert27;

/// <summary>The manufacturer's colour/alpha and coefficient layers always change as a pair.</summary>
public sealed record BrandingLayerSet(string ColorArraySlot, int Layer, string ColorPng, string NormalPng);

public static class ManufacturerBranding
{
    /// <param name="numberedColorSlot">The numbered texture showing this layer's picture (<see cref="SlotRoles.PairNumbered"/>),
    /// whose colour/normal/coeff replacements go too; null = no numbered texture shows it, so none is touched.</param>
    public static void KeepStockLayer(ModFile mod, string colorSlot, int layer, string? numberedColorSlot)
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
            var numbered = numberedColorSlot is null ? null : numberedColorSlot[..^"_color".Length] + "_" + role;
            mod.Replacements.RemoveAll(r => (r.Slot == slot && (r.Layers?.Count ?? 0) == 0) || r.Slot == numbered);
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

    /// <param name="numbered">
    /// The numbered texture (…_N_color) that shows the same picture as array layer <c>item.Layer</c>, or null when none
    /// does.  NHL 24 does not keep its arrays in the numbered textures' order (Detroit pants: array 0 = shield, 1 = club;
    /// numbered 0 = club, 1 = empty, 2 = shield), so the mirror is never found by number.  The normal goes to the same N.
    /// </param>
    public static void ApplyToRows(IEnumerable<SlotMatch> rows, BrandingLayerSet item, Func<int, string?> numbered, string? reason = null)
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
            if (numbered(item.Layer) is not { } colorSlot) continue;
            var mirrorSlot = colorSlot[..^"_color".Length] + "_" + role;
            var mirror = list.FirstOrDefault(r => r.Slot == mirrorSlot && !r.Manual);
            if (mirror is null) continue;
            mirror.Source27 = null; mirror.FilePath = source; mirror.Kind = SourceKind.File;
            mirror.Reason = reason ?? "Adidas → Fanatics: matching logo normal/mask and alpha"; mirror.Confidence = 1;
        }
    }

    /// <summary>
    /// Every colour pick also sets the matching normal: layer N of a <c>…_color_array</c> fills layer N of its
    /// <c>…_normal_array</c> with the source's normal sibling (…_color → …_normal), and a numbered <c>…_N_color</c>
    /// row fills its <c>…_N_normal</c> sibling the same way. A colour layer that keeps its NHL 24 image keeps the
    /// stock normal, so the new crest never sits on the old crest's relief (Utah colour over Coyotes stitching).
    /// Automatic array picks are reconciled, not just filled when empty: an old saved normal cannot survive a corrected
    /// colour pick, and a colour layer returned to stock returns its normal to stock too. A manual normal row or an
    /// explicit file supplied by <see cref="ApplyToRows"/> is never overridden. Returns how many picks changed.
    /// </summary>
    public static int PropagateNormals(IEnumerable<SlotMatch> rows, Func<string, bool> nhl27PngExists)
    {
        var list = rows.ToList();
        var touched = new HashSet<SlotMatch>();
        int changed = 0;
        string? Sibling(string? source) =>
            source is not null && source.EndsWith("_color", StringComparison.Ordinal)
                ? source[..^"_color".Length] + "_normal" : null;

        foreach (var color in list.Where(r => r.Role == "color"))
        {
            if (color.IsArray)
            {
                if (!color.Slot.EndsWith("_color_array", StringComparison.Ordinal)) continue;
                var normal = list.FirstOrDefault(r => r.IsArray && !r.Manual
                    && r.Slot == color.Slot[..^"_color_array".Length] + "_normal_array");
                if (normal is null) continue;
                foreach (var layer in color.Layers)
                {
                    var target = normal.Layers.FirstOrDefault(l => l.Layer == layer.Layer);
                    if (target is null) continue;
                    if (layer.Source27 is null)
                    {
                        // ApplyToRows supplies an explicit colour/normal file pair; that pair owns this layer.
                        if (layer.FilePath is not null || target.Source27 is null && target.FilePath is null) continue;
                        target.Source27 = null;
                        target.FilePath = null;
                        touched.Add(normal);
                        changed++;
                        continue;
                    }
                    // A Fanatics file pair is selected as a unit and must not be replaced by a source-path guess.
                    if (target.FilePath is not null) continue;
                    if (Sibling(layer.Source27) is not { } sib || !nhl27PngExists(sib) || target.Source27 == sib) continue;
                    target.Source27 = sib;
                    touched.Add(normal);
                    changed++;
                }
            }
            else
            {
                if (color.Source27 is null || !color.Slot.EndsWith("_color", StringComparison.Ordinal)) continue;
                var normal = list.FirstOrDefault(r => !r.IsArray && !r.Manual && r.Kind == SourceKind.Stock
                    && r.Slot == color.Slot[..^"_color".Length] + "_normal");
                if (normal is null) continue;
                if (Sibling(color.Source27) is not { } sib || !nhl27PngExists(sib)) continue;
                normal.Kind = SourceKind.Nhl27;
                normal.Source27 = sib;
                normal.Confidence = color.Confidence;
                normal.Reason = "normal follows the colour pick; the stock relief would show through";
                changed++;
            }
        }
        foreach (var row in touched)
        {
            row.Confidence = 1;
            row.Reason = "normal layers follow the colour layers of the same logo (never the stock layer order)";
        }
        return changed;
    }

    /// <summary>Adds the matching clean coefficient array to a full-kit addition; base-garment coeff maps stay stock.</summary>
    public static NewUniformTexture CoefficientFor(NewUniformTexture color)
    {
        if (!color.SourceSlot.EndsWith("_color_array", StringComparison.Ordinal) || color.Artwork.Layers is not { Count: > 0 })
            throw new InvalidDataException("A coefficient array needs its complete converted colour-array layers.");
        string source = color.SourceSlot[..^"_color_array".Length] + "_coeff_array";
        string target = color.TargetSlot.Length == 0 ? "" : color.TargetSlot[..^"_color_array".Length] + "_coeff_array";
        return new NewUniformTexture
        {
            SourceSlot = source,
            TargetSlot = target,
            RequiredRoles = color.RequiredRoles?.ToList(),
            Artwork = new TextureReplacement(source, "", color.Artwork.Layers.ToDictionary(kv => kv.Key, kv => "coeff:" + kv.Value)),
        };
    }

    /// <summary>Older saved addition mods did not carry coefficient arrays. Supply them deterministically on install.</summary>
    public static void EnsureCoefficientArrays(NewUniformAddition addition)
    {
        foreach (var color in addition.Textures.Where(t => t.SourceSlot.EndsWith("_color_array", StringComparison.Ordinal)
                     && t.Artwork.Layers is { Count: > 0 }).ToList())
        {
            var coefficient = CoefficientFor(color);
            addition.Textures.RemoveAll(t => t.SourceSlot == coefficient.SourceSlot);
            addition.Textures.Add(coefficient);
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
