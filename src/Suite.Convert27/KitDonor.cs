using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>
/// One donor kit for every new uniform (owner, 2026-11-11): Anaheim's regular home. It is the plain current-season kit,
/// has the modern jersey shape every NHL 27 texture is painted for, and carries every logo spot a Fanatics kit needs:
/// jersey crest, NHL shield, brand, back-hem tag, right and left shoulder, sponsor; pants NHL shield and team logo, in
/// one clean copy whose spots line up 1:1 with its logo layers. Each team's kit is a renamed clone of it with that team's
/// NHL 27 textures and logos on top by role. No per-team base, no added spots, no grown arrays: a logo with no spot of
/// its kind is left off. (Per-team bases put Boston on its Winter Classic jersey: wrong stripes, tiny crest, a crash.)
/// </summary>
public static class KitDonor
{
    public const string Team = "anaheim";
    public const string Prefix = "content/character/skater/league/nhl/anaheim/adidas/home/";

    /// <summary>Points every addition at the donor: texture names follow the donor's, art layers fit the donor's spots.</summary>
    public static List<NewUniformAddition> Retarget(List<NewUniformAddition> additions, BundleIndex index, Action<string>? say = null)
    {
        string toId = KitRegistration.Identity(Prefix);
        var donorRes = index.ResByName.Keys.Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (donorRes.Count == 0) throw new InvalidDataException("The donor kit (Anaheim home) is not in this game.");
        foreach (var a in additions)
        {
            string source = a.SourcePrefix.Trim().TrimEnd('/') + "/";
            string fromId = KitRegistration.Identity(source);
            var kept = new List<NewUniformTexture>();
            var dropped = new List<string>();
            foreach (var t in a.Textures)
            {
                string slot = t.SourceSlot.Trim().Replace('\\', '/').TrimStart('/');
                string mapped = slot.StartsWith(source, StringComparison.Ordinal)
                    ? Prefix + slot[source.Length..].Replace(fromId, toId, StringComparison.Ordinal) : slot;
                if (!donorRes.Contains(mapped)) { dropped.Add(slot[(slot.LastIndexOf('/') + 1)..]); continue; }
                t.SourceSlot = mapped;
                t.Artwork = t.Artwork with { Slot = mapped };
                t.TargetSlot = "";
                t.DonorRoles = null;
                kept.Add(t);
            }
            a.Textures = kept;
            a.HotspotLayouts.Clear();
            a.SourcePrefix = Prefix;
            if (dropped.Count > 0) say?.Invoke($"{a.Team} {a.SourceKit}: the donor has no slot for {string.Join(", ", dropped)}; left out");
            if (PaintedLogos) UsePaintedTextures(a, donorRes, array => SponsorSlots.PhysicalRoles(index, array.Replace("_normal_array", "_color_array").Replace("_coeff_array", "_color_array"))?.Count, say);
            if (HemFit.Enabled) HemFit.Apply(a, say);
            int fitted = FitToSlots(a, array => SponsorSlots.PhysicalRoles(index, array));
            if (fitted > 0) say?.Invoke($"{a.Team} {a.SourceKit}: {fitted} logo layer(s) have no spot of their kind on the donor and were left off");
            var covered = kept.Select(t => t.SourceSlot).ToHashSet(StringComparer.Ordinal);
            // Team pieces NHL 27 has no art for (the helmet logo) come from the team's own NHL 24 kit, never the donor's.
            foreach (var name in donorRes.Where(n => !covered.Contains(n) && n[(n.LastIndexOf('/') + 1)..].StartsWith("helmetlogo_", StringComparison.Ordinal)).ToList())
                if (OwnTeamTexture(index, a, name) is { } own) { kept.Add(own); covered.Add(name); say?.Invoke($"{a.Team} {a.SourceKit}: {name[(name.LastIndexOf('/') + 1)..]} uses the team's own NHL 24 art"); }
            var stock = donorRes.Where(n => !covered.Contains(n) && (n.EndsWith("_color", StringComparison.Ordinal) || n.EndsWith("_color_array", StringComparison.Ordinal))
                    && !n[(n.LastIndexOf('/') + 1)..].StartsWith("branding_", StringComparison.Ordinal))
                .Select(n => n[(n.LastIndexOf('/') + 1)..]).Order(StringComparer.Ordinal).ToList();
            if (stock.Count > 0) say?.Invoke($"{a.Team} {a.SourceKit}: WARNING keeps the donor's own {string.Join(", ", stock)} (no NHL 27 or team art for them)");
        }
        return additions;
    }

    /// <summary>
    /// Owner, 2026-11-12: "follow the NHL 27 layout to a T". NHL 27 ships every jersey and pants twice: plain, and
    /// finished with each logo painted in NHL 27's own spot and size (same 2048 layout, plus finished normal and
    /// coefficient maps so the logos keep their stitched look). The donor's base textures take the finished ones and its
    /// logo layers are left empty, so every logo sits exactly where NHL 27 puts it and nothing is painted twice.
    /// </summary>
    public static bool PaintedLogos = true;

    static void UsePaintedTextures(NewUniformAddition a, IReadOnlySet<string> donorRes, Func<string, int?> physical, Action<string>? say)
    {
        string toId = KitRegistration.Identity(Prefix);
        int swapped = 0;
        foreach (string garment in new[] { "jersey", "pant" })
        foreach (string kind in new[] { "color", "normal", "coeff" })
        {
            string baseSlot = $"{Prefix}{garment}_adidas_{toId}_base_{kind}", finishedSlot = $"{Prefix}{garment}_adidas_{toId}_{kind}";
            if (!donorRes.Contains(baseSlot)) continue;
            var finished = a.Textures.FirstOrDefault(t => t.SourceSlot == finishedSlot);
            string? png = finished?.Artwork.Source;
            if (png is null)
            {
                var plain = a.Textures.FirstOrDefault(t => t.SourceSlot == baseSlot)?.Artwork.Source;
                png = plain?.Replace($"_base_{kind}.png", $"_{kind}.png", StringComparison.Ordinal);
            }
            if (png is null || !File.Exists(png)) continue;
            var target = a.Textures.FirstOrDefault(t => t.SourceSlot == baseSlot);
            if (target is null) a.Textures.Add(target = new NewUniformTexture { SourceSlot = baseSlot, Artwork = new TextureReplacement(baseSlot, png) });
            else target.Artwork = target.Artwork with { Source = png };
            swapped++;
        }
        // Every donor logo slot gets an explicit transparent layer: an array with no art would keep the donor's own
        // (Anaheim) logos.
        string clear = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "donor-team-art")), "transparent-512.png");
        if (!File.Exists(clear)) ImageIO.SavePng(new Rgba(512, 512, new byte[512 * 512 * 4]), clear);
        int emptied = 0;
        foreach (var t in a.Textures.Where(t => t.SourceSlot[(t.SourceSlot.LastIndexOf('/') + 1)..].StartsWith("branding_", StringComparison.Ordinal)
                     && t.SourceSlot.EndsWith("_array", StringComparison.Ordinal)))
        {
            int slots = physical(t.SourceSlot) ?? 0;
            if (slots == 0) throw new InvalidDataException($"{t.SourceSlot}: the donor's logo slot count is unknown.");
            t.Artwork = t.Artwork with { Layers = Enumerable.Range(0, slots).ToDictionary(i => i, _ => clear) };
            t.RequiredRoles = Enumerable.Repeat("blank", slots).ToList();
            t.PinnedSlots = null;
            emptied++;
        }
        say?.Invoke($"{a.Team} {a.SourceKit}: logos painted on from NHL 27's finished textures ({swapped} textures); {emptied} logo arrays left empty");
    }

    /// <summary>The same texture from the team's regular NHL 24 kit (home or away), decoded to a PNG the install can use.</summary>
    static NewUniformTexture? OwnTeamTexture(BundleIndex index, NewUniformAddition a, string donorName)
    {
        string own = $"content/character/skater/league/nhl/{a.Team}/adidas/{a.SourceKit}/";
        string ownName = own + donorName[Prefix.Length..].Replace(KitRegistration.Identity(Prefix), KitRegistration.Identity(own), StringComparison.Ordinal);
        if (!index.ResByName.ContainsKey(ownName)) return null;
        try
        {
            var slot = TextureSlots.Read(index.Game, index, ownName);
            var image = Ps4Codec.DecodeSlices(slot.Res, slot.Stock)[0];
            string png = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "donor-team-art")), SuitePaths.Flatten(ownName) + ".png");
            ImageIO.SavePng(image, png);
            return new NewUniformTexture { SourceSlot = donorName, Artwork = new TextureReplacement(donorName, png) };
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>
    /// Drops art layers beyond the number of donor spots of their role (later layers first), in a colour array and its
    /// normal/coefficient siblings. Returns how many layers were dropped.
    /// </summary>
    public static int FitToSlots(NewUniformAddition addition, Func<string, IReadOnlyList<string>?> physicalRoles)
    {
        int dropped = 0;
        foreach (var color in addition.Textures.Where(t => t.SourceSlot.EndsWith("_color_array", StringComparison.Ordinal)
                     && t.SourceSlot[(t.SourceSlot.LastIndexOf('/') + 1)..].StartsWith("branding_", StringComparison.Ordinal)).ToList())
        {
            if (color.RequiredRoles is not { } roles || color.Artwork.Layers is not { } layers || physicalRoles(color.SourceSlot) is not { } physical) continue;
            var room = physical.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
            var drop = new List<int>();
            foreach (var layer in layers.Keys.Order())
            {
                if (layer >= roles.Count || roles[layer] is "blank" or "") continue;
                string role = roles[layer];
                if (room.TryGetValue(role, out int left) && left > 0) room[role] = left - 1;
                else drop.Add(layer);
            }
            if (drop.Count == 0) continue;
            string stem = color.SourceSlot[..^"_color_array".Length];
            foreach (var t in addition.Textures.Where(t => t.SourceSlot.StartsWith(stem + "_", StringComparison.Ordinal) && t.SourceSlot.EndsWith("_array", StringComparison.Ordinal)))
                DuplicateLogoLayers.Remove(t, drop);
            dropped += drop.Count;
        }
        return dropped;
    }
}
