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
        RememberStandardTag(additions);
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
            // Numbers, name and captain letter sit where the team's own NHL 24 kit puts them (Florida's sleeve numbers are
            // inside its shoulder patches, Anaheim's outside); without a team stamp the donor's spots stay.
            a.FontPlacement.Clear();
            foreach (string garment in new[] { "jersey", "pant" })
            {
                string donorStamp = $"{Prefix}stamp_{garment}_adidas_{toId}";
                string teamStamp = $"content/character/skater/league/nhl/{a.Team}/adidas/{a.SourceKit}/stamp_{garment}_adidas_{a.Team}_{a.SourceKit}";
                if (!index.EbxByName.ContainsKey(donorStamp)) continue;
                // NHL 27's own spots for the kit we paint (Ottawa's sleeve numbers sit lower on the arm); else the team's NHL 24 kit.
                if (garment == "jersey" && a.SourceFolder27 is { } stampFolder27 && Nhl27Stamps.Xml(stampFolder27) is { } xml27) a.FontPlacement[donorStamp] = xml27;
                else if (index.EbxByName.ContainsKey(teamStamp)) a.FontPlacement[donorStamp] = teamStamp;
            }
            if (dropped.Count > 0) say?.Invoke($"{a.Team} {a.SourceKit}: the donor has no slot for {string.Join(", ", dropped)}; left out");
            if (PaintedLogos) UsePaintedTextures(a, donorRes, array => SponsorSlots.PhysicalSpots(index, array.Replace("_normal_array", "_color_array").Replace("_coeff_array", "_color_array")), say);
            if (HemFit.Enabled) HemFit.Apply(a, HemReference(index, a), say);
            if (HemFit.Enabled) SockFit.Apply(a, OwnTeamImage(index, a, $"{Prefix}sock_adidas_{toId}_color") ?? OwnTeamImage(index, a, $"{Prefix}sock_adidas_{toId}_base_color"), say);
            if (PaintedLogos) ConvertToNhl24(a, index, donorRes, say);
            int fitted = FitToSlots(a, array => SponsorSlots.PhysicalRoles(index, array));
            if (fitted > 0) say?.Invoke($"{a.Team} {a.SourceKit}: {fitted} logo layer(s) have no spot of their kind on the donor and were left off");
            var covered = kept.Select(t => t.SourceSlot).ToHashSet(StringComparer.Ordinal);
            // Team pieces NHL 27 has no art for (the helmet logo) come from the team's own NHL 24 kit, never the donor's.
            // Number/letter fonts: NHL 27's sheets place the glyphs differently (Detroit: the captain's C and A sit further
            // right, extra ' . - glyphs), so on NHL 24's lettering they come out clipped. Every team keeps its own NHL 24 font.
            // NHL 27's own glyphs are cut out and fitted into the NHL 24 sheet's glyph boxes (FontRemap), so a team whose
            // lettering changed (Utah: Coyotes to Mammoth) still gets NHL 27's numbers; if the sheets cannot be matched
            // glyph for glyph, the team's NHL 24 font is used.
            var nhl27Fonts = kept.Where(t => t.SourceSlot[(t.SourceSlot.LastIndexOf('/') + 1)..].StartsWith("font_", StringComparison.Ordinal)).ToList();
            foreach (var t in nhl27Fonts) { kept.Remove(t); covered.Remove(t.SourceSlot); }
            // Only a team whose lettering changed (Arizona → Utah Mammoth) takes NHL 27's glyphs. Everyone else keeps the
            // NHL 24 font sheets byte for byte: fitting NHL 27's colour glyphs over NHL 24's stitch (normal) sheet put
            // Florida's digits above their top stitching and a gold sliver under the "1".
            bool renamed = a.SourceFolder27 is { } folder27 && !folder27.Contains($"/nhl/{a.Team}/", StringComparison.Ordinal);
            if (renamed && RemappedFont(index, a, nhl27Fonts) is { Count: > 0 } remapped)
            {
                kept.AddRange(remapped);
                foreach (var t in remapped) covered.Add(t.SourceSlot);
                say?.Invoke($"{a.Team} {a.SourceKit}: NHL 27 numbers and letters fitted into NHL 24's font layout ({remapped.Count} maps)");
            }
            foreach (var name in donorRes.Where(n => !covered.Contains(n) && (n[(n.LastIndexOf('/') + 1)..].StartsWith("helmetlogo_", StringComparison.Ordinal)
                         || n[(n.LastIndexOf('/') + 1)..].StartsWith("font_", StringComparison.Ordinal))).ToList())
                if (OwnTeamTexture(index, a, name) is { } own)
                {
                    if (name.EndsWith("_color", StringComparison.Ordinal) && name[(name.LastIndexOf('/') + 1)..].StartsWith("font_", StringComparison.Ordinal))
                        RecolorFont(a, own, nhl27Fonts, say);
                    kept.Add(own); covered.Add(name); say?.Invoke($"{a.Team} {a.SourceKit}: {name[(name.LastIndexOf('/') + 1)..]} uses the team's own NHL 24 art");
                }
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

    static void UsePaintedTextures(NewUniformAddition a, IReadOnlySet<string> donorRes, Func<string, IReadOnlyList<UnpaintedLayers.Spot>?> physical, Action<string>? say)
    {
        string toId = KitRegistration.Identity(Prefix);
        int swapped = 0;
        var plainJersey = new Dictionary<string, string>(StringComparer.Ordinal);
        var sheets = new Dictionary<string, (string Finished, string Plain)>(StringComparer.Ordinal);
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
            if (garment == "jersey" && target?.Artwork.Source is { } plainPng) plainJersey[kind] = plainPng;
            if (kind == "color" && target?.Artwork.Source is { } plainColor)
            {
                sheets[garment] = (png, plainColor);
                var finishedArt = ImageIO.Load(png);
                var zones = ImageIO.Load(plainColor);
                if (zones.Width == finishedArt.Width && zones.Height == finishedArt.Height)
                {
                    var px = (byte[])finishedArt.Pixels.Clone();
                    for (int i = 3; i < px.Length; i += 4) px[i] = zones.Pixels[i];
                    string withZones = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "nhl27-zones")), $"{a.Team}_{a.SourceKit}_{garment}_color.png");
                    ImageIO.SavePng(new Rgba(finishedArt.Width, finishedArt.Height, px), withZones);
                    png = withZones;
                    if (finished is not null) finished.Artwork = finished.Artwork with { Source = withZones };
                }
            }
            if (target is null) a.Textures.Add(target = new NewUniformTexture { SourceSlot = baseSlot, Artwork = new TextureReplacement(baseSlot, png) });
            else target.Artwork = target.Artwork with { Source = png };
            swapped++;
        }
        SponsorSide.Apply(a, plainJersey, say);
        // Every donor logo slot gets an explicit transparent layer: an array with no art would keep the donor's own
        // (Anaheim) logos.
        string clear = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "donor-team-art")), "transparent-512.png");
        if (!File.Exists(clear)) ImageIO.SavePng(new Rgba(512, 512, new byte[512 * 512 * 4]), clear);
        int emptied = 0, unpainted = 0;
        var placements = new Dictionary<(bool, string), Dictionary<int, int>>();
        foreach (var t in a.Textures.Where(t => t.SourceSlot[(t.SourceSlot.LastIndexOf('/') + 1)..].StartsWith("branding_", StringComparison.Ordinal)
                     && t.SourceSlot.EndsWith("_array", StringComparison.Ordinal)))
        {
            var spots = physical(t.SourceSlot);
            int slots = spots?.Count ?? 0;
            if (slots == 0) throw new InvalidDataException($"{t.SourceSlot}: the donor's logo slot count is unknown.");
            // The back-hem tag is a decal on the back hem; NHL 27's finished textures do not paint it, so its layer stays
            // (End Plastic Waste, or Chicago's 100th-season tag that the owner put in that spot).
            var tag = t.RequiredRoles is { } roles && t.Artwork.Layers is { } art
                ? roles.Select((r, i) => (r, i)).Where(x => x.r == SlotRoles.EpwRole && art.ContainsKey(x.i)).Select(x => art[x.i]).FirstOrDefault()
                : null;
            // Every kit carries the End Plastic Waste back-hem tag (Chicago's 100 tag replaces it through the slot map).
            // NHL 27's Florida kits lack the layer but keep its stitched outline, which showed as an empty rectangle.
            bool jerseyArray = t.SourceSlot.Contains("branding_jersey_", StringComparison.Ordinal);
            if (tag is null && jerseyArray)
            {
                // The matcher leaves NHL 27's tag layer out of the addition (no NHL 24 Adidas counterpart), so it is
                // read from the kit's own NHL 27 layers; a kit without one (Florida) gets the standard tag.
                tag = OwnNhl27Tag(a, ArrayKind(t.SourceSlot)) ?? StandardTag(ArrayKind(t.SourceSlot));
                if (tag is not null && ArrayKind(t.SourceSlot) == "color")
                    say?.Invoke($"{a.Team} {a.SourceKit}: End Plastic Waste tag on the back hem ({(tag == StandardTag("color") ? "standard tag" : "NHL 27's own")})");
            }
            var layers = Enumerable.Range(0, slots).ToDictionary(i => i, _ => clear);
            var newRoles = Enumerable.Repeat("blank", slots).ToList();
            int tagSlot = spots?.ToList().FindIndex(x => x.Role == SlotRoles.EpwRole) ?? -1;
            if (tag is not null && tagSlot >= 0) { layers[tagSlot] = tag; newRoles[tagSlot] = SlotRoles.EpwRole; }
            // Logos NHL 27 keeps only as layers (Seattle's and Toronto's chest patches, LA's pants logo) go in the
            // donor's slot of the same role. Normal and coefficient arrays follow the colour array's choice.
            bool pants = t.SourceSlot.Contains("branding_pant_", StringComparison.Ordinal);
            bool colorArray = t.SourceSlot.EndsWith("_color_array", StringComparison.Ordinal);
            if (t.RequiredRoles is { } srcRoles && t.Artwork.Layers is { } srcArt && spots is not null
                && sheets.TryGetValue(pants ? "pant" : "jersey", out var sheet) && File.Exists(sheet.Finished) && File.Exists(sheet.Plain))
            {
                var key = (pants, sheet.Finished);
                if (!placements.TryGetValue(key, out var place))
                {
                    var colors = a.Textures.FirstOrDefault(x => x.SourceSlot.Contains(pants ? "branding_pant_" : "branding_jersey_", StringComparison.Ordinal)
                        && x.SourceSlot.EndsWith("_color_array", StringComparison.Ordinal));
                    placements[key] = place = UnpaintedLayers.Place(pants, spots, colors?.RequiredRoles ?? srcRoles,
                        (colors?.Artwork.Layers ?? srcArt).Keys.ToList(), ImageIO.Load(sheet.Finished), ImageIO.Load(sheet.Plain));
                }
                foreach (var (slot, layer) in place)
                {
                    if (!srcArt.TryGetValue(layer, out var layerArt) || (tag is not null && slot == tagSlot)) continue;
                    layers[slot] = layerArt;
                    newRoles[slot] = spots[slot].Role;
                    if (!colorArray) continue;
                    unpainted++;
                    say?.Invoke($"{a.Team} {a.SourceKit}: NHL 27's {(pants ? "pants" : "jersey")} {spots[slot].Role} is a separate layer, not painted on the sheet; placed in the donor's {spots[slot].Role} spot");
                }
            }
            t.Artwork = t.Artwork with { Layers = layers };
            t.RequiredRoles = newRoles;
            t.PinnedSlots = null;
            emptied++;
        }
        say?.Invoke($"{a.Team} {a.SourceKit}: logos painted on from NHL 27's finished textures ({swapped} textures); {emptied} logo arrays left empty");
    }

    static string ArrayKind(string slot) => slot.EndsWith("_normal_array", StringComparison.Ordinal) ? "normal"
        : slot.EndsWith("_coeff_array", StringComparison.Ordinal) ? "coeff" : "color";

    static string StandardTagPath(string kind) => Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "standard-epw")), $"epw_{kind}.png");

    static string? StandardTag(string kind) => File.Exists(StandardTagPath(kind)) ? StandardTagPath(kind) : null;

    /// <summary>Keeps one NHL 27 End Plastic Waste tag (colour, normal, coefficient layers) for kits that lack it.</summary>
    static void RememberStandardTag(IEnumerable<NewUniformAddition> additions)
    {
        var found = FindStandardTag(additions);
        if (found is null)
            foreach (var a in additions.Where(a => a.Team != "chicago"))
                if (OwnNhl27Tag(a, "color") is { } color)
                {
                    found = new() { ["color"] = color };
                    foreach (string kind in new[] { "normal", "coeff" }) if (OwnNhl27Tag(a, kind) is { } other) found[kind] = other;
                    break;
                }
        if (found is not null)
            foreach (var (kind, png) in found) File.Copy(png, StandardTagPath(kind), overwrite: true);
    }

    /// <summary>The kit's own NHL 27 End Plastic Waste layer (decoded PNG) for an array kind, found by its picture.</summary>
    static string? OwnNhl27Tag(NewUniformAddition a, string kind)
    {
        if (a.SourceFolder27 is not { } folder) return null;
        var parts = folder.TrimEnd('/').Split('/');
        if (parts.Length < 3) return null;
        string team27 = parts[^3], variant = parts[^1];
        string dir = Path.Combine(SuitePaths.Local, "nhl27png");
        string stem = folder.TrimEnd('/').Replace('/', '_') + $"_branding_jersey_adidas_{team27}_{variant}_";
        for (int i = 0; i < 12; i++)
        {
            string color = Path.Combine(dir, $"{stem}{i}_color.png");
            if (!File.Exists(color)) continue;
            if (SlotRoles.KnownLogo(color) != "patch_epw") continue;
            string wanted = Path.Combine(dir, $"{stem}{i}_{kind}.png");
            return File.Exists(wanted) ? wanted : null;
        }
        return null;
    }

    /// <summary>The first kit's End Plastic Waste tag layers by array kind (never Chicago's 100 tag); null if none.</summary>
    public static Dictionary<string, string>? FindStandardTag(IEnumerable<NewUniformAddition> additions)
    {
        foreach (var a in additions)
        {
            if (a.Team == "chicago") continue; // Chicago's tag slot holds its 100th-season tag
            var arrays = a.Textures.Where(t => t.SourceSlot.Contains("branding_jersey_", StringComparison.Ordinal) && t.SourceSlot.EndsWith("_array", StringComparison.Ordinal)).ToList();
            var found = new Dictionary<string, string>();
            foreach (var t in arrays)
            {
                if (t.RequiredRoles is not { } roles || t.Artwork.Layers is not { } art) continue;
                int i = roles.IndexOf(SlotRoles.EpwRole);
                if (i >= 0 && art.TryGetValue(i, out var png) && File.Exists(png)) found[ArrayKind(t.SourceSlot)] = png;
            }
            if (found.ContainsKey("color")) return found;
        }
        return null;
    }

    /// <summary>The team's own NHL 24 jersey sheet (plain if it has one, else its painted sheet) for the hem edges.</summary>
    static Rgba? HemReference(BundleIndex index, NewUniformAddition a)
    {
        string toId = KitRegistration.Identity(Prefix);
        return OwnTeamImage(index, a, $"{Prefix}jersey_adidas_{toId}_base_color") ?? OwnTeamImage(index, a, $"{Prefix}jersey_adidas_{toId}_color");
    }

    static Rgba? OwnTeamImage(BundleIndex index, NewUniformAddition a, string donorName)
    {
        string own = $"content/character/skater/league/nhl/{a.Team}/adidas/{a.SourceKit}/";
        string ownName = own + donorName[Prefix.Length..].Replace(KitRegistration.Identity(Prefix), KitRegistration.Identity(own), StringComparison.Ordinal);
        if (!index.ResByName.ContainsKey(ownName)) return null;
        try { var slot = TextureSlots.Read(index.Game, index, ownName); return Ps4Codec.DecodeSlices(slot.Res, slot.Stock)[0]; }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>NHL 27's font maps redrawn in the team's NHL 24 font layout; empty when the glyphs cannot be matched.</summary>
    static List<NewUniformTexture> RemappedFont(BundleIndex index, NewUniformAddition a, List<NewUniformTexture> nhl27Fonts)
    {
        var result = new List<NewUniformTexture>();
        var color27 = nhl27Fonts.FirstOrDefault(t => t.SourceSlot.EndsWith("_color", StringComparison.Ordinal));
        if (color27 is null || !File.Exists(color27.Artwork.Source) || OwnTeamImage(index, a, color27.SourceSlot) is not { } own24) return result;
        var pairs = FontRemap.Map(ImageIO.Load(color27.Artwork.Source), own24);
        if (pairs is null) return result;
        string dir = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "font-remap"));
        foreach (var t in nhl27Fonts)
        {
            if (!File.Exists(t.Artwork.Source) || OwnTeamImage(index, a, t.SourceSlot) is not { } fill) continue;
            var source = ImageIO.Load(t.Artwork.Source);
            if (source.Width != ImageIO.Load(color27.Artwork.Source).Width) continue;
            string png = Path.Combine(dir, $"{a.Team}_{a.SourceKit}_" + Path.GetFileName(t.Artwork.Source));
            ImageIO.SavePng(FontRemap.Apply(source, fill, pairs, fill), png);
            result.Add(new NewUniformTexture { SourceSlot = t.SourceSlot, Artwork = new TextureReplacement(t.SourceSlot, png) });
        }
        return result;
    }

    /// <summary>
    /// NHL 27's garment maps converted to how NHL 24 reads them (NJ away's grey marbling, 2026-11-12):
    /// colour alpha is NHL 24's fabric-zone map (rule 11; NHL 27 uses other values and zones), so the donor's alpha is put
    /// back; NHL 27 normals keep only X/Y and leave blue at 255, where NHL 24 keeps its fold shading, so the donor's blue
    /// (and alpha) go back in; coefficient maps stay the donor's stock base coefficient (rule 10).
    /// </summary>
    static void ConvertToNhl24(NewUniformAddition a, BundleIndex index, IReadOnlySet<string> donorRes, Action<string>? say)
    {
        string toId = KitRegistration.Identity(Prefix);
        string dir = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "nhl24-maps"));
        Rgba? Donor(string slot)
        {
            if (!donorRes.Contains(slot)) return null;
            try { var s = TextureSlots.Read(index.Game, index, slot); return Ps4Codec.DecodeSlices(s.Res, s.Stock)[0]; }
            catch (InvalidDataException) { return null; }
        }
        int converted = 0;
        foreach (string garment in new[] { "jersey", "pant", "sock" })
        foreach (string variant in new[] { "base_", "" })
        {
            foreach (string kind in new[] { "color", "normal" })
            {
                string slot = $"{Prefix}{garment}_adidas_{toId}_{variant}{kind}";
                var t = a.Textures.FirstOrDefault(x => x.SourceSlot == slot);
                if (t is null || !File.Exists(t.Artwork.Source) || Donor(slot) is not { } donor) continue;
                var art = ImageIO.Load(t.Artwork.Source);
                if (art.Width != donor.Width || art.Height != donor.Height) continue;
                string png = Path.Combine(dir, $"{a.Team}_{a.SourceKit}_{garment}_{variant}{kind}.png");
                ImageIO.SavePng(ConvertMap(art, donor, kind == "normal", flattenShading: garment == "jersey"), png);
                // The converted alpha is final: rule 11's "auto" would put the donor's Adidas zones back at encode time.
                t.Artwork = t.Artwork with { Source = png, Alpha = null };
                converted++;
            }
            // Rule 10: coefficient maps stay stock. The donor's base coefficient (fabric only, no donor logos) fills both.
            string coeffSlot = $"{Prefix}{garment}_adidas_{toId}_{variant}coeff";
            if (!donorRes.Contains(coeffSlot)) continue;
            // Per-kit switch (nhl27_shine.json in the user data folder, default off): the finished coefficient is NHL 27's own. It is packed
            // like NHL 24's (finished 127/27/0/255 vs NHL 27 129/26/23/255) and carries the metallic areas (Vegas gold).
            if (variant == "" && UsesNhl27Shine(a) && a.Textures.FirstOrDefault(x => x.SourceSlot == coeffSlot) is { } own27
                && File.Exists(own27.Artwork.Source))
            {
                own27.Artwork = own27.Artwork with { Alpha = null };
                say?.Invoke($"{a.Team} {a.SourceKit}: {garment} shine (finished coefficient) is NHL 27's own");
                continue;
            }
            a.Textures.RemoveAll(x => x.SourceSlot == coeffSlot);
            // The finished coefficient is packed differently from the base one (NHL 24 Anaheim: base 126/127/255/0,
            // finished 127/27/0/255), so it keeps the donor's own stock finished map, with the donor's baked-in logo
            // areas set back to plain fabric so no Anaheim logo shows in the shine.
            if (variant == "" && Donor(coeffSlot) is { } stockFinished
                && Donor($"{Prefix}{garment}_adidas_{toId}_color") is { } donorColor && Donor($"{Prefix}{garment}_adidas_{toId}_base_color") is { } donorPlain)
            {
                string png = Path.Combine(dir, $"{garment}_donor_coeff_without_logos.png");
                if (!File.Exists(png)) ImageIO.SavePng(WithoutLogos(stockFinished, donorColor, donorPlain), png);
                a.Textures.Add(new NewUniformTexture { SourceSlot = coeffSlot, Artwork = new TextureReplacement(coeffSlot, png) });
            }
        }
        say?.Invoke($"{a.Team} {a.SourceKit}: {converted} NHL 27 colour/normal maps converted to NHL 24's fabric alpha and fold shading; coefficient maps stock");
    }

    /// <summary>
    /// NHL 27 map in NHL 24's packing. Colour keeps RGB; its alpha keeps NHL 27's fabric-zone shapes with NHL 24's zone
    /// values (<see cref="ZoneMap"/>), so a Fanatics jersey has no Adidas shoulder-yoke zone (the dimples) and no marbling.
    /// A normal keeps X/Y (NHL 27's seams and hem stitching) and takes NHL 24's alpha and blue; with
    /// <paramref name="flattenShading"/> the blue is the donor's plain-fabric level everywhere, so the Adidas yoke seams
    /// baked into the donor's fold shading do not show on a Fanatics jersey.
    /// </summary>
    public static Rgba ConvertMap(Rgba nhl27, Rgba nhl24, bool normal, bool flattenShading = false)
    {
        if (nhl27.Width != nhl24.Width || nhl27.Height != nhl24.Height) throw new ArgumentException("map sizes differ");
        var px = (byte[])nhl27.Pixels.Clone();
        var zones = normal ? null : ZoneMap(nhl27, nhl24);
        // A sheet without fabric zones (Vegas home's NHL 27 plain sheet is alpha 255 everywhere) keeps NHL 24's zone
        // shapes, with the Adidas shoulder yoke turned into plain body fabric (no dimples on a Fanatics kit).
        bool noZones = !normal && HasNoZones(nhl27);
        byte body24 = Mode(nhl24, 3);
        byte flat = Mode(nhl24, 2);
        for (int i = 0; i < px.Length; i += 4)
        {
            if (normal)
            {
                px[i + 2] = flattenShading ? flat : nhl24.Pixels[i + 2];
                px[i + 3] = nhl24.Pixels[i + 3];
            }
            else if (noZones) px[i + 3] = nhl24.Pixels[i + 3] == AdidasYoke ? body24 : nhl24.Pixels[i + 3];
            else px[i + 3] = nhl24.Pixels[i + 3] == 255 ? (byte)255 : zones![px[i + 3]];
        }
        return new Rgba(nhl27.Width, nhl27.Height, px);
    }

    /// <summary>
    /// <paramref name="coeff"/> with every pixel where the donor's finished colour shows a logo (it differs from its plain
    /// colour) set to the map's most common value, i.e. plain fabric.
    /// </summary>
    public static Rgba WithoutLogos(Rgba coeff, Rgba finished, Rgba plain)
    {
        if (coeff.Width != finished.Width || coeff.Height != finished.Height || plain.Width != finished.Width || plain.Height != finished.Height) return coeff;
        var px = (byte[])coeff.Pixels.Clone();
        var fabric = new byte[] { Mode(coeff, 0), Mode(coeff, 1), Mode(coeff, 2), Mode(coeff, 3) };
        for (int i = 0; i < px.Length; i += 4)
        {
            int d = Math.Abs(finished.Pixels[i] - plain.Pixels[i]) + Math.Abs(finished.Pixels[i + 1] - plain.Pixels[i + 1]) + Math.Abs(finished.Pixels[i + 2] - plain.Pixels[i + 2]);
            if (d > 60) fabric.CopyTo(px, i);
        }
        return new Rgba(coeff.Width, coeff.Height, px);
    }

    static HashSet<string>? _nhl27Shine;

    /// <summary>Kits ("vegas home") whose finished coefficient comes from NHL 27, from nhl27_shine.json in the user data folder.</summary>
    public static bool UsesNhl27Shine(NewUniformAddition a)
    {
        if (_nhl27Shine is null)
        {
            string path = Path.Combine(SuitePaths.Roaming, "data", "nhl27_shine.json");
            _nhl27Shine = File.Exists(path)
                ? System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? []
                : [];
        }
        return _nhl27Shine.Contains("all") || _nhl27Shine.Contains($"{a.Team} {a.SourceKit}");
    }

    /// <summary>Test hook: replaces the per-kit NHL 27 shine list.</summary>
    public static void SetNhl27Shine(IEnumerable<string>? kits) => _nhl27Shine = kits is null ? null : [.. kits];

    /// <summary>NHL 24's Adidas shoulder-yoke fabric zone (the dimpled mesh); Fanatics jerseys have none.</summary>
    public const byte AdidasYoke = 73;

    static bool HasNoZones(Rgba image)
    {
        var counts = new int[256];
        for (int i = 3; i < image.Pixels.Length; i += 4) counts[image.Pixels[i]]++;
        return counts.Count(c => c >= image.Width * image.Height * 0.003) < 2;
    }

    static byte Mode(Rgba image, int channel)
    {
        var counts = new int[256];
        for (int i = channel; i < image.Pixels.Length; i += 4) counts[image.Pixels[i]]++;
        return (byte)Array.IndexOf(counts, counts.Max());
    }

    /// <summary>
    /// NHL 27 fabric-zone value → NHL 24 zone value. Each real NHL 27 zone (at least 0.3% of the sheet) takes the NHL 24
    /// zone it overlaps most on the same sheet layout: body → body, side panels → side panels, collar → collar, 255 → 255.
    /// The two games use different value scales (NHL 27 bodies are 64, 42, 51 or 36; NHL 24's is 36) and NHL 27's
    /// Fanatics jerseys have no shoulder-yoke zone. Edge pixels between zones take the nearest real zone's value.
    /// </summary>
    public static byte[] ZoneMap(Rgba nhl27, Rgba nhl24)
    {
        int n = nhl27.Width * nhl27.Height;
        var count27 = new int[256];
        var overlap = new int[256, 256];
        var count24 = new int[256];
        for (int i = 3; i < nhl27.Pixels.Length; i += 4) { count27[nhl27.Pixels[i]]++; count24[nhl24.Pixels[i]]++; overlap[nhl27.Pixels[i], nhl24.Pixels[i]]++; }
        var levels27 = Enumerable.Range(0, 256).Where(v => count27[v] >= n * 0.003).ToList();
        var levels24 = Enumerable.Range(0, 256).Where(v => count24[v] >= n * 0.003).ToList();
        int body27 = Array.IndexOf(count27, count27.Max()), body24 = Array.IndexOf(count24, count24.Max());
        var target = new Dictionary<int, int>();
        foreach (int v in levels27)
        {
            if (v == 255) target[v] = 255;
            else if (v == body27) target[v] = body24;
            else
            {
                // The non-body NHL 24 zone this NHL 27 zone overlaps most; plain body when it overlaps none enough.
                var best = levels24.Where(z => z != body24 && z != 255).OrderByDescending(z => overlap[v, z]).FirstOrDefault(-1);
                target[v] = best >= 0 && overlap[v, best] >= count27[v] * 0.2 ? best : body24;
            }
        }
        var map = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            int nearest = levels27.Count == 0 ? body27 : levels27.OrderBy(l => Math.Abs(l - v)).First();
            map[v] = (byte)(target.TryGetValue(nearest, out int t) ? t : body24);
        }
        return map;
    }

    /// <summary>The same texture from the team's regular NHL 24 kit (home or away), decoded to a PNG the install can use.</summary>
    /// <summary>
    /// The owner's number colours (font_colours.json, <see cref="FontRecolor"/>) on the team's NHL 24 font sheet. NHL 27's
    /// colours are not taken automatically: its sheets differ from NHL 24's in layout and edge shading too much to read
    /// reliably, so a kit whose numbers changed colour (Ottawa) is listed in the table.
    /// </summary>
    static void RecolorFont(NewUniformAddition a, NewUniformTexture own, List<NewUniformTexture> nhl27Fonts, Action<string>? say)
    {
        var sheet24 = ImageIO.Load(own.Artwork.Source);
        var forced = FontRecolor.Override($"{a.Team} {a.SourceKit}");
        // Only the owner's table: reading NHL 27's colours automatically mixed up fill and outline on double-outlined
        // numbers (Florida away came out gold on navy instead of navy on gold), so changed kits are listed by hand.
        var target = forced;
        if (target is null) return;
        var recolored = FontRecolor.Recolor(sheet24, target);
        string png = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "font-recolor")), $"{a.Team}_{a.SourceKit}_font_color.png");
        ImageIO.SavePng(recolored, png);
        own.Artwork = own.Artwork with { Source = png };
        say?.Invoke($"{a.Team} {a.SourceKit}: number colours {(forced is null ? "from NHL 27" : "from font_colours.json")} on the NHL 24 number sheet");
    }

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
