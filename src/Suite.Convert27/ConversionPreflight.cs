namespace Suite.Convert27;

public enum PreflightSeverity { Ready, Repaired, Warning, Error }

/// <summary>One plain-English result shown before a converted uniform is exported.</summary>
public sealed record PreflightItem(PreflightSeverity Severity, string Title, string Detail)
{
    public string Symbol => Severity switch
    {
        PreflightSeverity.Ready => "✓",
        PreflightSeverity.Repaired => "↻",
        PreflightSeverity.Warning => "!",
        _ => "×",
    };
}

/// <summary>The complete preflight result. Warnings allow export; errors do not.</summary>
public sealed record PreflightReport(IReadOnlyList<PreflightItem> Items)
{
    public int ErrorCount => Items.Count(i => i.Severity == PreflightSeverity.Error);
    public int WarningCount => Items.Count(i => i.Severity == PreflightSeverity.Warning);
    public int RepairCount => Items.Count(i => i.Severity == PreflightSeverity.Repaired);
    public bool CanExport => ErrorCount == 0;
    public string Summary => ErrorCount > 0
        ? $"{ErrorCount} problem(s) need attention before export"
        : WarningCount > 0
            ? $"Ready to export with {WarningCount} warning(s)"
            : RepairCount > 0
                ? $"Ready — {RepairCount} automatic repair(s) completed"
                : "Ready to export — all checks passed";
}

/// <summary>
/// Validates the final texture relationships, after automatic role/base/normal reconciliation. These checks are pure so
/// the exact same rules can be tested without the game, its asset indexes, or the WPF page.
/// </summary>
public static class ConversionPreflight
{
    public static IEnumerable<PreflightItem> InspectRows(IEnumerable<SlotMatch> rows,
        Func<string, bool> nhl27PngExists, Func<string, bool> fileExists)
    {
        var list = rows.ToList();
        foreach (var row in list)
        {
            if (!row.IsArray && row.Name.IsBase && row.Name.Role == "color" && row.Kind == SourceKind.Nhl27
                && row.Source27 is not null && !row.Source27.EndsWith("_base_color", StringComparison.Ordinal))
            {
                yield return new(row.Manual ? PreflightSeverity.Warning : PreflightSeverity.Error,
                    "Base artwork may duplicate the jersey logos",
                    $"{row.Leaf} uses the baked preview instead of unbaked base art. " +
                    (row.Manual ? "This is a manual choice; confirm that its artwork does not already contain the crest."
                        : "Use the exact NHL 27 base texture or keep the NHL 24 base."));
            }

            if (!row.IsArray)
            {
                if (Missing(row.Kind, row.Source27, row.FilePath, nhl27PngExists, fileExists))
                    yield return MissingSource(row.Leaf);
                continue;
            }

            foreach (var layer in row.Layers)
                if (layer.Source27 is not null && !nhl27PngExists(layer.Source27)
                    || layer.FilePath is not null && !fileExists(layer.FilePath))
                    yield return MissingSource($"{row.Leaf}, layer {layer.Layer}");
        }

        foreach (var color in list.Where(r => r.IsArray && r.Role == "color"
                     && r.Slot.EndsWith("_color_array", StringComparison.Ordinal)))
        {
            var normal = list.FirstOrDefault(r => r.IsArray
                && r.Slot == color.Slot[..^"_color_array".Length] + "_normal_array");
            if (normal is null) continue;
            foreach (var layer in color.Layers)
            {
                var target = normal.Layers.FirstOrDefault(l => l.Layer == layer.Layer);
                if (target is null || target.FilePath is not null) continue;
                string? expected = layer.Source27 is not null && layer.Source27.EndsWith("_color", StringComparison.Ordinal)
                    ? layer.Source27[..^"_color".Length] + "_normal" : null;
                bool mismatch = expected is not null && nhl27PngExists(expected) && target.Source27 != expected
                    || expected is null && layer.FilePath is null && target.Source27 is not null;
                if (!mismatch) continue;
                bool manual = color.Manual || normal.Manual;
                yield return new(manual ? PreflightSeverity.Warning : PreflightSeverity.Error,
                    "A logo and its surface relief do not match",
                    $"{color.Garment}, layer {layer.Layer}: the normal map belongs to a different logo or a stock layer. " +
                    (manual ? "A manual override is preserving this combination; check it in Advanced mode."
                        : "Re-run Check & convert to align the pair automatically."));
            }
        }
    }

    /// <summary>Reports important NHL 27 logos that have no destination, without treating optional sponsors as fatal.</summary>
    public static IEnumerable<PreflightItem> InspectUnplacedLogos(SlotMapEntry entry)
    {
        foreach (var layer in entry.NoSlot27)
        {
            var label = entry.Layers27.FirstOrDefault(l => l.Layer == layer);
            if (label is null) continue;
            // Shoulders follow the Shoulder patches choice (see InspectShoulders); a repeated picture is one logo.
            if (label.Role is "sponsor" or "crest" or "shield" or "brand")
            {
                string name = SlotNames.Name(label, entry.Garment);
                yield return new(PreflightSeverity.Repaired, $"{name}: a slot will be added",
                    $"{(entry.Garment == "branding_pant" ? "Pants" : "Jersey")}: the stock kit has no {name.ToLowerInvariant()} position, " +
                    "so Add to game gives the new kit one at the usual stock position. Change it in the slot map if this logo should go somewhere else.");
            }
        }
    }

    /// <summary>States the jersey's shoulder patches, so the check shows what Add to game will write.</summary>
    public static PreflightItem InspectShoulders(SlotMapEntry jersey)
    {
        int art = jersey.Layers27.Count(l => l.Role == "shoulder");
        string label = ShoulderPatches.Label(jersey.Shoulders);
        return new(PreflightSeverity.Ready, "Shoulder patches: " + label,
            jersey.Shoulders is null
                ? $"Following the NHL 27 jersey ({(art == 0 ? "no shoulder logo" : art == 1 ? "one shoulder logo" : "shoulder logos; a repeated picture counts once")}). Change it with Shoulder patches."
                : jersey.Shoulders == ShoulderPatches.None ? "No shoulder patches will be drawn."
                : "Only the chosen side(s) get the patch; any other shoulder slot is switched off.");
    }

    static bool Missing(SourceKind kind, string? source27, string? file,
        Func<string, bool> nhl27PngExists, Func<string, bool> fileExists) => kind switch
    {
        SourceKind.Nhl27 => source27 is null || !nhl27PngExists(source27),
        SourceKind.File => file is null || !fileExists(file),
        _ => false,
    };

    static PreflightItem MissingSource(string slot) => new(PreflightSeverity.Error, "Artwork is unavailable",
        $"{slot}: the selected image has not been decoded or the file no longer exists. Check the source and try again.");
}
