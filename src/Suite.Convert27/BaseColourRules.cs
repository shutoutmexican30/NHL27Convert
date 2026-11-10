namespace Suite.Convert27;

/// <summary>
/// Keeps baked jersey preview art out of the base-colour slot. The game draws branding-array logos over that slot,
/// so using the plain NHL 27 colour map there draws every crest and shoulder logo twice.
/// </summary>
public static class BaseColourRules
{
    public const string LegacyCompositeReason = "plain art + NHL 24 fabric alpha";

    /// <summary>
    /// Repairs automatic base-colour rows, including composites saved by older versions. The exact NHL 27 base map
    /// supplies RGB while rule 11 keeps the NHL 24 fabric alpha. If an old composite has no exact base-map replacement,
    /// the slot returns to stock rather than exporting duplicate logos. Manual rows are never changed.
    /// </summary>
    public static int RepairAutomaticBaseColours(IEnumerable<SlotMatch> rows, IEnumerable<Nhl27Texture> textures27)
    {
        var byCanonical = textures27.GroupBy(t => TextureName.Parse(t.Name).Canonical)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        int changed = 0;
        foreach (var row in rows)
        {
            if (row.Manual || row.IsArray || !row.Name.IsBase || row.Name.Role != "color") continue;
            bool oldComposite = row.Kind == SourceKind.File
                && row.Reason.StartsWith(LegacyCompositeReason, StringComparison.Ordinal);
            if (byCanonical.TryGetValue(row.Name.Canonical, out var exact))
            {
                // A non-manual file is only known to be automatic when it carries the old composite marker.
                if (row.Kind == SourceKind.File && !oldComposite) continue;
                if (row.Kind == SourceKind.Nhl27 && row.Source27 == exact.Name && row.FilePath is null) continue;
                row.Kind = SourceKind.Nhl27;
                row.Source27 = exact.Name;
                row.FilePath = null;
                row.Alpha = "auto";
                row.Reason = "NHL 27 base art + NHL 24 fabric alpha; logos come only from branding layers";
                changed++;
                continue;
            }
            bool bakedPreview = row.Kind == SourceKind.Nhl27 && row.Source27 is not null
                && !row.Source27.EndsWith("_base_color", StringComparison.Ordinal);
            if (!oldComposite && !bakedPreview) continue;
            row.ClearSource();
            row.Alpha = "auto";
            row.Reason = "kept stock: no NHL 27 base map; baked preview art would duplicate the branding-layer logos";
            changed++;
        }
        return changed;
    }
}
