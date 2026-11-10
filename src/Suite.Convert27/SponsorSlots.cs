using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>
/// Adds the logo slots a new kit's NHL 27 art needs but its stock NHL 24 jersey stamp lacks (a sponsor patch, a second
/// shoulder, ...). An added shoulder mirrors the kit's own shoulder; anything else copies the position most stock kits
/// use for that role. The installer then grows every copy of the branding arrays to the new slot count, so no logo is
/// dropped or forced into another logo's position.
/// </summary>
public static class SponsorSlots
{
    public const string Role = "sponsor";
    static readonly string[] Addable = ["crest", "shield", "brand", "shoulder", "sponsor"];

    /// <summary>The layout with a sponsor placement appended after the last branding hotspot.</summary>
    public static UniformHotspotLayout WithSponsor(UniformHotspotLayout stock, UniformHotspotPlacement position)
    {
        if (stock.Branding.Any(slot => slot.Role == Role)) throw new InvalidDataException($"{stock.SourceSlot} already has a sponsor slot.");
        return WithAdded(stock, [(Role, position)]);
    }

    /// <summary>The layout with each given role appended, in order, after the last branding hotspot.</summary>
    public static UniformHotspotLayout WithAdded(UniformHotspotLayout stock, IReadOnlyList<(string Role, UniformHotspotPlacement Position)> added)
    {
        var branding = WithRoles(stock).Branding;
        int next = branding.Count == 0 ? UniformHotspots.FirstBrandingId : branding.Max(slot => slot.Id) + 1;
        var c = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var (role, position) in added)
        {
            var slot = Copy(position);
            slot.Id = next++;
            slot.Name = role == "shoulder"
                ? (double.TryParse(slot.TranslateX, c, out double tx) && tx > 0.5 ? "left_shoulder" : "right_shoulder")
                : role == Role ? Role : "added_" + role;
            slot.Role = role;
            slot.Enabled = true;
            branding.Add(slot);
        }
        var result = new UniformHotspotLayout { SourceSlot = stock.SourceSlot, Branding = branding };
        _ = UniformHotspots.Roles(result);
        return result;
    }

    /// <summary>The other shoulder: the same patch reflected across the jersey's centre line.</summary>
    public static UniformHotspotPlacement Mirror(UniformHotspotPlacement shoulder)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        var m = Copy(shoulder);
        m.TranslateX = (1 - double.Parse(shoulder.TranslateX, c)).ToString("0.0000", c);
        m.Rotation = ((360 - double.Parse(shoulder.Rotation, c)) % 360).ToString("0.0000", c);
        return m;
    }

    /// <summary>The position shared by the most stock jersey stamps for one role.</summary>
    public static UniformHotspotPlacement? StockPosition(BundleIndex index, string role = Role)
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
                    positions.AddRange(layout.Branding.Where(slot => slot.Role == role && slot.Enabled));
            }
            catch (InvalidDataException) { }
        }
        return positions.GroupBy(p => (p.Rotation, p.ScaleX, p.ScaleY, p.TranslateX, p.TranslateY))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.ToString(), StringComparer.Ordinal).FirstOrDefault()?.First();
    }

    /// <summary>Adds every logo slot a jersey's art needs and its stamp lacks. Layouts the mod already supplies are kept.</summary>
    public static List<NewUniformAddition> AddMissing(List<NewUniformAddition> additions, BundleIndex index, Action<string>? say = null)
    {
        var stockPositions = new Dictionary<string, UniformHotspotPlacement?>(StringComparer.Ordinal);
        foreach (var addition in additions)
        foreach (var texture in addition.Textures)
        {
            string source = texture.SourceSlot.Trim().Replace('\\', '/').TrimStart('/');
            string leaf = source[(source.LastIndexOf('/') + 1)..];
            if (!leaf.StartsWith("branding_jersey_", StringComparison.Ordinal) || !leaf.EndsWith("_color_array", StringComparison.Ordinal)
                || texture.RequiredRoles is not { } artRoles || texture.Artwork.Layers is not { Count: > 0 } layers) continue;
            string stamp = UniformPhysicalLayouts.StampForArray(source);
            if (addition.HotspotLayouts.Any(l => l.SourceSlot == stamp) || !index.EbxByName.ContainsKey(stamp)) continue;
            var copies = Copies(index, stamp);
            if (copies.Skip(1).Any(c => !UniformPhysicalLayouts.SameLayout(c, copies[0])))
            {
                // Base and Patch differ (Detroit's Patch copy gained a sponsor); the installer grows the shorter copy.
                if (copies.Any(c => c.Branding.Any(slot => slot.Role == Role))) continue;
                say?.Invoke($"{addition.Team} {addition.SourceKit}: slots not added; the stamp copies differ and need a review");
                continue;
            }
            var stock = WithRoles(copies[0]);
            IReadOnlyList<string> physical;
            try { physical = UniformHotspots.Roles(stock); }
            catch (InvalidDataException ex)
            {
                say?.Invoke($"{addition.Team} {addition.SourceKit}: slots not added; the stock layout needs a review ({ex.Message})");
                continue;
            }
            var needed = artRoles.Where((role, layer) => layers.ContainsKey(layer) && Addable.Contains(role))
                .GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
            var added = new List<(string Role, UniformHotspotPlacement Position)>();
            foreach (var (role, count) in needed)
            {
                int missing = count - physical.Count(r => r == role);
                for (int i = 0; i < missing; i++)
                {
                    UniformHotspotPlacement? position = null;
                    if (role == "shoulder")
                    {
                        // A second shoulder mirrors the one the kit (or this step) already has.
                        var existing = added.Where(a => a.Role == "shoulder").Select(a => a.Position)
                            .Concat(stock.Branding.Where(slot => slot.Role == "shoulder")).FirstOrDefault();
                        if (existing is not null) position = Mirror(existing);
                    }
                    if (position is null)
                    {
                        if (!stockPositions.TryGetValue(role, out position)) stockPositions[role] = position = StockPosition(index, role);
                        if (position is null) throw new InvalidDataException($"No stock jersey has a {role} position to copy.");
                    }
                    added.Add((role, position));
                }
            }
            if (added.Count == 0) continue;
            addition.HotspotLayouts.Add(WithAdded(copies[0], added));
            say?.Invoke($"{addition.Team} {addition.SourceKit}: added {string.Join(", ", added.Select(a => a.Role))} slot(s) to the jersey");
        }
        return additions;
    }

    /// <summary>A copy in which numbered or misspelled stock slots keep their names but take their role from their position.</summary>
    public static UniformHotspotLayout WithRoles(UniformHotspotLayout stock)
    {
        var branding = stock.Branding.OrderBy(slot => slot.Id).Select(Copy).ToList();
        foreach (var slot in branding.Where(slot => slot.Role.Length == 0)) slot.Role = UniformHotspots.PositionRole(slot);
        return new UniformHotspotLayout { SourceSlot = stock.SourceSlot, Branding = branding };
    }

    /// <summary>The stamp's layout in each bundle that carries it (Base and Patch can differ).</summary>
    static List<UniformHotspotLayout> Copies(BundleIndex index, string stamp) =>
        (index.EbxByName.GetValueOrDefault(stamp) ?? []).Select(e => e.Bundle).DistinctBy(b => (b.Toc, b.RecordAt))
            .Select(b => UniformHotspots.Capture(UniformPhysicalLayouts.ReadStamp(index, stamp, b), stamp)).ToList();

    static UniformHotspotPlacement Copy(UniformHotspotPlacement p) => new()
    {
        Id = p.Id, Role = p.Role, Name = p.Name, Rotation = p.Rotation, ScaleX = p.ScaleX, ScaleY = p.ScaleY,
        TranslateX = p.TranslateX, TranslateY = p.TranslateY, Enabled = p.Enabled,
    };
}
