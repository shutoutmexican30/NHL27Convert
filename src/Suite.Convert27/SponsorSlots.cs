using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>
/// Adds a sponsor slot to a new kit whose NHL 27 art has a sponsor but whose stock NHL 24 jersey stamp has no sponsor
/// position. The added hotspot copies the sponsor position most stock kits use; the installer then grows every copy of
/// the branding arrays to the new slot count, so no NHL 27 logo is dropped.
/// </summary>
public static class SponsorSlots
{
    public const string Role = "sponsor";

    /// <summary>The layout with a sponsor placement appended after the last branding hotspot.</summary>
    public static UniformHotspotLayout WithSponsor(UniformHotspotLayout stock, UniformHotspotPlacement position)
    {
        var branding = stock.Branding.OrderBy(slot => slot.Id).Select(Copy).ToList();
        if (branding.Any(slot => slot.Role == Role)) throw new InvalidDataException($"{stock.SourceSlot} already has a sponsor slot.");
        var added = Copy(position);
        added.Id = branding.Count == 0 ? UniformHotspots.FirstBrandingId : branding.Max(slot => slot.Id) + 1;
        added.Name = Role;
        added.Role = Role;
        added.Enabled = true;
        branding.Add(added);
        var result = new UniformHotspotLayout { SourceSlot = stock.SourceSlot, Branding = branding };
        _ = UniformHotspots.Roles(result);
        return result;
    }

    /// <summary>The sponsor position shared by the most stock jersey stamps.</summary>
    public static UniformHotspotPlacement? StockPosition(BundleIndex index)
    {
        var positions = new List<UniformHotspotPlacement>();
        foreach (var name in index.EbxByName.Keys)
        {
            string leaf = name[(name.LastIndexOf('/') + 1)..];
            if (!leaf.StartsWith("stamp_jersey_adidas_", StringComparison.Ordinal) || leaf.EndsWith("_player_team_brt", StringComparison.Ordinal)
                || leaf.EndsWith("_blueprint", StringComparison.Ordinal) || !name.Contains("/league/nhl/", StringComparison.Ordinal)) continue;
            try
            {
                var layout = UniformHotspots.Capture(UniformPhysicalLayouts.ReadStamp(index, name), name);
                positions.AddRange(layout.Branding.Where(slot => slot.Role == Role && slot.Enabled));
            }
            catch (InvalidDataException) { }
        }
        return positions.GroupBy(p => (p.Rotation, p.ScaleX, p.ScaleY, p.TranslateX, p.TranslateY))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.ToString(), StringComparer.Ordinal).FirstOrDefault()?.First();
    }

    /// <summary>Adds a reviewed sponsor layout to every jersey array that needs one. Layouts the mod already supplies are kept.</summary>
    public static List<NewUniformAddition> AddMissing(List<NewUniformAddition> additions, BundleIndex index, Action<string>? say = null)
    {
        UniformHotspotPlacement? position = null;
        bool searched = false;
        foreach (var addition in additions)
        foreach (var texture in addition.Textures)
        {
            string source = texture.SourceSlot.Trim().Replace('\\', '/').TrimStart('/');
            string leaf = source[(source.LastIndexOf('/') + 1)..];
            if (!leaf.StartsWith("branding_jersey_", StringComparison.Ordinal) || !leaf.EndsWith("_color_array", StringComparison.Ordinal)) continue;
            int sponsorLayer = texture.RequiredRoles?.IndexOf(Role) ?? -1;
            if (sponsorLayer < 0 || texture.Artwork.Layers is not { } layers || !layers.ContainsKey(sponsorLayer)) continue;
            string stamp = UniformPhysicalLayouts.StampForArray(source);
            if (addition.HotspotLayouts.Any(l => l.SourceSlot == stamp) || !index.EbxByName.ContainsKey(stamp)) continue;
            var stock = UniformHotspots.Capture(UniformPhysicalLayouts.ReadStamp(index, stamp), stamp);
            IReadOnlyList<string> roles;
            try { roles = UniformHotspots.Roles(stock); }
            catch (InvalidDataException ex)
            {
                say?.Invoke($"{addition.Team} {addition.SourceKit}: sponsor not placed; the stock layout needs a review ({ex.Message})");
                continue;
            }
            if (roles.Contains(Role)) continue;
            if (!searched) { position = StockPosition(index); searched = true; }
            if (position is null) throw new InvalidDataException("No stock jersey has a sponsor position to copy.");
            addition.HotspotLayouts.Add(WithSponsor(stock, position));
            say?.Invoke($"{addition.Team} {addition.SourceKit}: added sponsor slot {roles.Count} to the jersey");
        }
        return additions;
    }

    static UniformHotspotPlacement Copy(UniformHotspotPlacement p) => new()
    {
        Id = p.Id, Role = p.Role, Name = p.Name, Rotation = p.Rotation, ScaleX = p.ScaleX, ScaleY = p.ScaleY,
        TranslateX = p.TranslateX, TranslateY = p.TranslateY, Enabled = p.Enabled,
    };
}
