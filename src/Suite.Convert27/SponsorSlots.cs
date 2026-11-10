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
        var branding = WithRoles(stock).Branding;
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
                foreach (var layout in Copies(index, name))
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
            var copies = Copies(index, stamp);
            // A copy that already has a sponsor position (Detroit's Patch copy) is handled by growing the shorter copy.
            if (copies.Any(c => c.Branding.Any(slot => slot.Role == Role))) continue;
            if (copies.Skip(1).Any(c => !UniformPhysicalLayouts.SameLayout(c, copies[0])))
            {
                say?.Invoke($"{addition.Team} {addition.SourceKit}: sponsor not placed; the stamp copies differ and need a review");
                continue;
            }
            var stock = copies[0];
            IReadOnlyList<string> roles;
            try { roles = UniformHotspots.Roles(WithRoles(stock)); }
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

    /// <summary>The stamp's layout in each bundle that carries it (Base and Patch can differ).</summary>
    static List<UniformHotspotLayout> Copies(BundleIndex index, string stamp) =>
        (index.EbxByName.GetValueOrDefault(stamp) ?? []).Select(e => e.Bundle).DistinctBy(b => (b.Toc, b.RecordAt))
            .Select(b => UniformHotspots.Capture(UniformPhysicalLayouts.ReadStamp(index, stamp, b), stamp)).ToList();

    /// <summary>A copy in which numbered or misspelled stock slots keep their names but take their role from their position.</summary>
    public static UniformHotspotLayout WithRoles(UniformHotspotLayout stock)
    {
        var branding = stock.Branding.OrderBy(slot => slot.Id).Select(Copy).ToList();
        foreach (var slot in branding.Where(slot => slot.Role.Length == 0)) slot.Role = UniformHotspots.PositionRole(slot);
        return new UniformHotspotLayout { SourceSlot = stock.SourceSlot, Branding = branding };
    }

    static UniformHotspotPlacement Copy(UniformHotspotPlacement p) => new()
    {
        Id = p.Id, Role = p.Role, Name = p.Name, Rotation = p.Rotation, ScaleX = p.ScaleX, ScaleY = p.ScaleY,
        TranslateX = p.TranslateX, TranslateY = p.TranslateY, Enabled = p.Enabled,
    };
}
