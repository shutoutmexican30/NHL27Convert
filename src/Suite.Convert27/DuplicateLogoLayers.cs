using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// Removes converted logo layers that repeat an earlier layer's NHL 27 picture in the same role. The export fills an NHL 24
/// template, so a kit with one real shoulder patch (Nashville's 2026 home) arrived with the same picture in two shoulder
/// layers, and the installer then gave it a second shoulder. The NHL 27 art is the authority: one picture, one logo.
/// A logo the user really wants twice can still be pinned in the slot map.
/// </summary>
public static class DuplicateLogoLayers
{
    static readonly string[] Suffixes = ["_color_array", "_normal_array", "_coeff_array"];

    /// <summary>The layers to drop from one colour array: later layers whose role and picture both repeat an earlier one.</summary>
    public static List<int> Duplicates(NewUniformTexture color)
    {
        var drop = new List<int>();
        if (color.RequiredRoles is not { } roles || color.Artwork.Layers is not { Count: > 0 } layers) return drop;
        var seen = new HashSet<(string Role, string Picture)>();
        foreach (var (layer, path) in layers.OrderBy(kv => kv.Key))
        {
            if (layer >= roles.Count || roles[layer] == "blank") continue;
            if (color.PinnedSlots?.ContainsKey(layer) == true) continue;
            if (!seen.Add((roles[layer], Path.GetFileName(path).ToLowerInvariant()))) drop.Add(layer);
        }
        return drop;
    }

    public static int Apply(IEnumerable<NewUniformAddition> additions, Action<string>? say = null)
    {
        int removed = 0;
        foreach (var addition in additions)
        foreach (var color in addition.Textures.Where(t => t.SourceSlot.EndsWith("_color_array", StringComparison.Ordinal)
                     && t.SourceSlot[(t.SourceSlot.LastIndexOf('/') + 1)..].StartsWith("branding_", StringComparison.Ordinal)).ToList())
        {
            var drop = Duplicates(color);
            if (drop.Count == 0) continue;
            string stem = color.SourceSlot[..^"_color_array".Length];
            foreach (var texture in addition.Textures.Where(t => Suffixes.Any(s => t.SourceSlot == stem + s)))
                Remove(texture, drop);
            removed += drop.Count;
            say?.Invoke($"{addition.Team} {addition.SourceKit} {stem[(stem.LastIndexOf('/') + 1)..]}: layer(s) {string.Join(", ", drop)} repeat an earlier logo's picture and were not added again");
        }
        return removed;
    }

    /// <summary>Drops the given layers and renumbers the rest, keeping roles and pins aligned.</summary>
    internal static void Remove(NewUniformTexture texture, IReadOnlyCollection<int> drop)
    {
        int NewIndex(int old) => old - drop.Count(d => d < old);
        if (texture.Artwork.Layers is { } layers)
            texture.Artwork = texture.Artwork with
            {
                Layers = layers.Where(kv => !drop.Contains(kv.Key)).ToDictionary(kv => NewIndex(kv.Key), kv => kv.Value),
            };
        if (texture.RequiredRoles is { } roles)
            texture.RequiredRoles = roles.Where((_, i) => !drop.Contains(i)).ToList();
        if (texture.PinnedSlots is { } pins)
            texture.PinnedSlots = pins.Where(kv => !drop.Contains(kv.Key)).ToDictionary(kv => NewIndex(kv.Key), kv => kv.Value);
    }
}
