using System.Text.RegularExpressions;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// Applies the user's saved manual matcher decisions (slot_map.json entries marked manual) to a uniform's logo arrays at
/// install time, so they win over the automatic picture labels even in mods exported before they were made.
/// A manual NHL 27 role replaces that layer's role; a manual link pins that layer to its NHL 24 slot.
/// </summary>
public static partial class MatcherOverrides
{
    [GeneratedRegex(@"_(\d+)_(?:color|normal)\.png$|@(\d+)\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex LayerNumber();

    /// <summary>The NHL 27 layer number an exported artwork file came from, or null when the name does not say.</summary>
    public static int? SourceLayer(string artworkPath)
    {
        var m = LayerNumber().Match(Path.GetFileName(artworkPath));
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
    }

    public static int Apply(IEnumerable<NewUniformAddition> additions, SlotMapStore store, Action<string>? say = null)
    {
        int changed = 0;
        foreach (var addition in additions)
        {
            if (string.IsNullOrWhiteSpace(addition.SourceFolder27)) continue;
            string folder27 = addition.SourceFolder27.Trim().TrimEnd('/');
            foreach (var texture in addition.Textures)
            {
                string leaf = texture.SourceSlot[(texture.SourceSlot.LastIndexOf('/') + 1)..];
                if (!leaf.StartsWith("branding_", StringComparison.Ordinal) || texture.RequiredRoles is not { } roles
                    || texture.Artwork.Layers is not { Count: > 0 } layers) continue;
                string garment = leaf.StartsWith("branding_jersey_", StringComparison.Ordinal) ? "branding_jersey" : "branding_pant";
                string source = addition.SourcePrefix.Trim().TrimEnd('/');
                var entry = store.Entries.Where(e => e.Folder27.TrimEnd('/') == folder27 && e.Garment == garment)
                    .OrderByDescending(e => e.Folder24.TrimEnd('/') == source).FirstOrDefault();
                if (entry is null) continue;
                var manualRoles = entry.Layers27.Where(l => l.Manual).ToDictionary(l => l.Layer, l => l.Role);
                var manualLinks = entry.Links.Where(l => l.Manual && l.Nhl27 is not null).ToDictionary(l => l.Nhl27!.Value, l => l.Nhl24);
                if (manualRoles.Count == 0 && manualLinks.Count == 0) continue;
                foreach (var (output, path) in layers)
                {
                    if (SourceLayer(path) is not { } layer27 || output >= roles.Count) continue;
                    if (manualRoles.TryGetValue(layer27, out var role) && roles[output] != role)
                    {
                        say?.Invoke($"{addition.Team} {addition.SourceKit} {garment}: your override makes NHL 27 layer {layer27} a {role} (was {roles[output]})");
                        roles[output] = role;
                        changed++;
                    }
                    if (manualLinks.TryGetValue(layer27, out int slot))
                    {
                        texture.PinnedSlots ??= [];
                        if (!texture.PinnedSlots.TryGetValue(output, out int existing) || existing != slot)
                        {
                            texture.PinnedSlots[output] = slot;
                            say?.Invoke($"{addition.Team} {addition.SourceKit} {garment}: your link puts NHL 27 layer {layer27} in slot {slot}");
                            changed++;
                        }
                    }
                }
            }
        }
        return changed;
    }
}
