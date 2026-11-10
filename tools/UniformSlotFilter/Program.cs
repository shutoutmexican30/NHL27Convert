using Suite.Convert27;
using Suite.Core;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: UniformSlotFilter <input mod.json> <output mod name>");
    return 2;
}
var input = ModFile.Load(args[0]) ?? throw new FileNotFoundException(args[0]);
var store = SlotMapStore.Load();
var output = new ModFile
{
    Name = args[1],
    Kind = "textures",
    Comment = "Slot-safe NHL 27 uniform export: role-matched branding arrays only; no numbered branding rows, no coeff maps, no no-slot NHL 27 layers.",
};

int kept = 0, filteredArrays = 0, skippedNumbered = 0, skippedCoeff = 0, skippedUnknownArray = 0, removedLayers = 0;
var removedByReason = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
foreach (var r in input.Replacements)
{
    var name = TextureName.Parse(r.Slot);
    if (name.Role == "coeff")
    {
        skippedCoeff++;
        continue;
    }
    // Numbered branding textures are not the game's logo source; the arrays are. They are also the rows whose
    // layer order differs most between games, so an old mod must not use them for automatic conversion.
    if (name.Layer is not null && name.Garment.StartsWith("branding_", StringComparison.Ordinal))
    {
        skippedNumbered++;
        continue;
    }
    // Plain garment colour/normal sheets carry logos baked into the source game's UV layout. They are exactly
    // what made NHL 27 patches/crests overlap NHL 24's own artwork, so only the base maps and branding arrays
    // are exported automatically; unmatched logos can be stamped onto the base map by hand instead.
    if (!name.IsBase && name.Role is "color" or "normal" && name.Garment is "jersey" or "pant" or "sock")
    {
        skippedNumbered++;
        continue;
    }
    if (r.Layers is { Count: > 0 })
    {
        var folder24 = r.Slot[..r.Slot.LastIndexOf('/')];
        var entry = store.Entries.FirstOrDefault(e => e.Folder24 == folder24 && e.Garment == name.Garment);
        if (entry is null)
        {
            skippedUnknownArray++;
            continue;
        }
        var (links, _) = SlotRoles.Map(entry.Layers24, entry.Layers27);
        var safeEntry = new SlotMapEntry
        {
            Folder24 = entry.Folder24,
            Folder27 = entry.Folder27,
            Garment = entry.Garment,
            Layers24 = entry.Layers24,
            Layers27 = entry.Layers27,
            Links = links,
        };
        ManufacturerBranding.RequireFanaticsLinks(safeEntry);
        SlotRoles.EnforcePantsCrestLock(safeEntry);
        SlotRoles.EnforceEpwLock(safeEntry);
        var allowed = safeEntry.Links.Where(l => l.Nhl27 is not null).ToDictionary(l => l.Nhl24, l => l.Nhl27!.Value);
        int? SourceLayer(string path)
        {
            var file = Path.GetFileNameWithoutExtension(path);
            var m = System.Text.RegularExpressions.Regex.Match(file, $"_(\\d+)_{name.Role}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }
        string? SourceFor(int sourceLayer)
        {
            foreach (var p in r.Layers.Values)
            {
                var file = Path.GetFileNameWithoutExtension(p);
                var m = System.Text.RegularExpressions.Regex.Match(file, $"_(\\d+)_{name.Role}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var candidate = Path.Combine(Path.GetDirectoryName(p)!,
                    file[..m.Groups[1].Index] + sourceLayer + "_" + name.Role + Path.GetExtension(p));
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
        var before = r.Layers.Count;
        var layers = new Dictionary<int, string>();
        foreach (var (target, sourceLayer) in allowed.OrderBy(kv => kv.Key))
        {
            var existing = r.Layers.TryGetValue(target, out var p) ? p : null;
            if (existing is not null && !existing.StartsWith("coeff:", StringComparison.Ordinal) && SourceLayer(existing) == sourceLayer)
                layers[target] = existing;
            else if (SourceFor(sourceLayer) is { } fixedPath)
                layers[target] = fixedPath;
        }
        removedLayers += before - layers.Count;
        foreach (var kv in r.Layers.Where(kv => !layers.ContainsKey(kv.Key)))
        {
            var label = entry.Layers24.FirstOrDefault(l => l.Layer == kv.Key)?.Role ?? "unknown";
            removedByReason[label] = removedByReason.GetValueOrDefault(label) + 1;
        }
        if (layers.Count == 0) continue;
        output.Replacements.Add(new TextureReplacement(r.Slot, "", layers, r.Alpha));
        filteredArrays++;
        continue;
    }
    if (r.Source.Length > 0)
    {
        output.Replacements.Add(r);
        kept++;
    }
}
var path = output.Save();
Console.WriteLine($"input: {input.Replacements.Count} replacements");
Console.WriteLine($"output: {output.Replacements.Count} replacements ({kept} single textures, {filteredArrays} filtered arrays)");
Console.WriteLine($"skipped: {skippedNumbered} numbered branding rows, {skippedCoeff} coeff rows, {skippedUnknownArray} arrays without a slot-map entry");
Console.WriteLine($"removed {removedLayers} unmatched array layers");
foreach (var (role, n) in removedByReason.OrderByDescending(kv => kv.Value))
    Console.WriteLine($"  {role,-10} {n}");
Console.WriteLine(path);
return 0;
