using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

public sealed record BrandingLayerEvidence(
    string Team, string Kit, string Slot, Guid Chunk, int Layer, string ExpectedRole,
    double Difference, double Limit, bool RetainedStock);

public sealed record InstalledBrandingResult(
    int ArrayCopies, int IntendedSponsors,
    IReadOnlyList<BrandingLayerEvidence> Layers, IReadOnlyList<string> Problems);

/// <summary>Read-only verification of the actual installed color, normal and clean coefficient array slices.</summary>
public static class InstalledBrandingAudit
{
    public static InstalledBrandingResult Addition(string game, BundleIndex index, NewUniformAddition addition,
        Func<string, string> resolveArt, int workers = 0)
    {
        var problems = new List<string>();
        var evidence = new List<BrandingLayerEvidence>();
        int arrayCopies = 0, sponsors = 0;
        foreach (var color in addition.Textures.Where(t => t.SourceSlot.EndsWith("_color_array", StringComparison.Ordinal)
                     && t.Artwork.Layers is { Count: > 0 }))
        {
            string stem = color.SourceSlot[..^"_color_array".Length];
            string targetStem = color.TargetSlot[..^"_color_array".Length];
            bool jersey = TextureName.Parse(color.SourceSlot).Garment == "branding_jersey";
            var roles = color.RequiredRoles ?? throw new InvalidDataException($"{color.TargetSlot}: no ordered source roles were saved.");
            var normal = addition.Textures.SingleOrDefault(t => t.SourceSlot == stem + "_normal_array")
                ?? throw new InvalidDataException($"{color.TargetSlot}: its converted normal array is missing.");
            if (normal.RequiredRoles is null || !normal.RequiredRoles.SequenceEqual(roles))
                throw new InvalidDataException($"{color.TargetSlot}: color and normal role maps differ.");
            var sourceColors = TextureSlots.ReadAll(game, index, color.SourceSlot);
            var targetColors = TextureSlots.ReadAll(game, index, color.TargetSlot);
            var sourceNormals = TextureSlots.ReadAll(game, index, normal.SourceSlot);
            var targetNormals = TextureSlots.ReadAll(game, index, normal.TargetSlot);
            var sourceCoeffs = TextureSlots.ReadAll(game, index, stem + "_coeff_array");
            var targetCoeffs = TextureSlots.ReadAll(game, index, targetStem + "_coeff_array");

            var intended = IntendedRoles(color, roles, resolveArt, jersey, addition);
            sponsors += intended.Count(role => role == "sponsor");
            if (jersey)
            {
                RequireRoleCount(intended, "crest", 1, addition, "crest");
                RequireRoleCount(intended, "shield", 1, addition, "NHL shield");
                RequireRoleCount(intended, "brand", 1, addition, "Fanatics brand");
                int shoulders = intended.Count(role => role == "shoulder");
                if (shoulders is not (0 or 2)) problems.Add($"{addition.Team} {addition.SourceKit}: expected zero or two shoulders, found {shoulders}.");
                if (intended.Count(role => role == "sponsor") > 1) problems.Add($"{addition.Team} {addition.SourceKit}: more than one front sponsor was declared.");
            }

            foreach (var targetColor in targetColors)
            {
                var sourceColor = Matching(sourceColors, targetColor.Res, "source color", color.SourceSlot);
                var sourceNormal = Companion(sourceNormals, sourceColor.Res, "source normal", normal.SourceSlot, normal.Artwork.Layers!.Count);
                var sourceCoeff = Companion(sourceCoeffs, sourceColor.Res, "source coefficient", stem + "_coeff_array", color.Artwork.Layers!.Count);
                var targetNormal = InstalledBrandingCopyMatcher.Target(index, targetNormals, targetColor, sourceNormal.Res, "target normal", normal.TargetSlot);
                var targetCoeff = InstalledBrandingCopyMatcher.Target(index, targetCoeffs, targetColor, sourceCoeff.Res, "target coefficient", targetStem + "_coeff_array");
                int have = Ps4Codec.Slices(targetColor.Res.Header);
                int normalHave = Ps4Codec.Slices(targetNormal.Res.Header), coeffHave = Ps4Codec.Slices(targetCoeff.Res.Header);
                var stockColor = StockSlices(sourceColor, targetColor.Res, "color");
                var stockNormal = StockSlices(sourceNormal, targetNormal.Res, "normal");
                var stockCoeff = StockSlices(sourceCoeff, targetCoeff.Res, "coeff");
                var colorArt = LoadArtwork(color.Artwork.Layers!, have, targetColor.Res, resolveArt, workers);
                var normalArt = LoadArtwork(normal.Artwork.Layers!, normalHave, targetNormal.Res, resolveArt, workers);
                var coeffArt = colorArt.ToDictionary(kv => kv.Key, kv => LogoCoefficient.FromColor(kv.Value));
                int capacity = Math.Min(have, Math.Min(normalHave, coeffHave));
                var sourceBundle = UniformPhysicalLayouts.BundleForTexture(index, sourceColor, color.SourceSlot);
                var targetBundle = UniformPhysicalLayouts.BundleForTexture(index, targetColor, color.TargetSlot);
                var physical = UniformPhysicalLayouts.Resolve(index, addition, color, capacity,
                    sourceBundle, targetBundle, verifyTarget: true);
                var placement = UniformBrandingLayers.Plan(color, colorArt, stockColor, capacity, physical.Roles);
                if (colorArt.Keys.Any(source => placement.Destination(source) >= normalHave || placement.Destination(source) >= coeffHave))
                    throw new InvalidDataException($"{addition.Team} {addition.SourceKit}: a placed logo has no corresponding normal/coefficient slot.");
                var expectedColor = UniformBrandingLayers.Place(colorArt, stockColor, placement, "color");
                var expectedNormal = UniformBrandingLayers.Place(normalArt, stockNormal, placement, "normal");
                var expectedCoeff = UniformBrandingLayers.Place(coeffArt, stockCoeff, placement, "coeff");
                var actualColor = Ps4Codec.DecodeSlices(targetColor.Res, targetColor.Stock);
                var actualNormal = Ps4Codec.DecodeSlices(targetNormal.Res, targetNormal.Stock);
                var actualCoeff = Ps4Codec.DecodeSlices(targetCoeff.Res, targetCoeff.Stock);
                var outputRoles = new string[have];
                for (int layer = 0; layer < have; layer++)
                {
                    int source = colorArt.Keys.FirstOrDefault(sourceLayer => !placement.BlankSources.Contains(sourceLayer)
                        && placement.Destination(sourceLayer) == layer, -1);
                    string role = source >= 0 ? EffectiveRole(roles[source], colorArt[source])
                        : placement.KeepStock.Contains(layer) ? "stock" : "blank";
                    outputRoles[layer] = role;
                    bool retained = placement.KeepStock.Contains(layer);
                    if (retained && Visible(stockColor[layer]))
                        problems.Add($"{addition.Team} {addition.SourceKit} {targetColor.Name} chunk {targetColor.Chunk}, layer {layer}: an unconverted visible stock logo survived.");
                    Check("color", targetColor, layer, role, expectedColor[layer], actualColor[layer], expectedColor[layer], retained);
                }
                CheckCompanions("normal", targetNormal, expectedNormal, actualNormal);
                CheckCompanions("coeff", targetCoeff, expectedCoeff, actualCoeff);
                arrayCopies += 3;

                void CheckCompanions(string kind, TextureSlots.Slot target, IReadOnlyDictionary<int, Rgba> expected, IReadOnlyList<Rgba> actual)
                {
                    for (int layer = 0; layer < expected.Count; layer++)
                    {
                        int source = colorArt.Keys.FirstOrDefault(sourceLayer => !placement.BlankSources.Contains(sourceLayer)
                        && placement.Destination(sourceLayer) == layer, -1);
                        string role = source >= 0 ? EffectiveRole(roles[source], colorArt[source]) : "blank";
                        Check(kind, target, layer, role, expected[layer], actual[layer], expectedColor[layer], false);
                    }
                }
                if (jersey)
                {
                    foreach (string role in new[] { "crest", "shoulder", "shield", "brand", "sponsor" })
                    {
                        int expected = intended.Count(r => r == role);
                        int actual = outputRoles.Count(r => r == role);
                        if (expected != actual) problems.Add($"{addition.Team} {addition.SourceKit} {targetColor.Name} chunk {targetColor.Chunk}: "
                            + $"{role} count is {actual}, expected {expected}.");
                    }
                }
            }
        }
        return new InstalledBrandingResult(arrayCopies, sponsors, evidence, problems);

        void Check(string role, TextureSlots.Slot target, int layer, string expectedRole,
            Rgba expected, Rgba actual, Rgba colorMask, bool retained)
        {
            double difference, limit;
            bool pass;
            if (role == "color")
            {
                limit = UniformBrandingLayers.SamePictureLimit;
                if (!Visible(expected))
                {
                    difference = 100.0 * Coverage(actual);
                    pass = difference <= 0.01;
                    limit = 0.01;
                }
                else
                {
                    difference = UniformBrandingLayers.PictureDifference(expected, actual);
                    pass = difference <= limit;
                }
            }
            else if (role == "normal")
            {
                difference = NormalDifference(expected, actual, colorMask);
                limit = 16;
                pass = difference <= limit;
            }
            else
            {
                difference = CoefficientDifference(expected, actual);
                limit = 0.10;
                pass = difference <= limit;
            }
            evidence.Add(new BrandingLayerEvidence(addition.Team, addition.SourceKit, target.Name, target.Chunk, layer,
                expectedRole, Math.Round(difference, 4), limit, retained));
            if (!pass) problems.Add($"{addition.Team} {addition.SourceKit} {target.Name} chunk {target.Chunk}, layer {layer} "
                + $"({expectedRole}): {role} difference {difference:F3} exceeds {limit:F2}.");
        }
    }

    static List<string> IntendedRoles(NewUniformTexture color, IReadOnlyList<string> roles,
        Func<string, string> resolveArt, bool jersey, NewUniformAddition addition)
    {
        var result = new List<string>();
        for (int layer = 0; layer < roles.Count; layer++)
        {
            if (!color.Artwork.Layers!.TryGetValue(layer, out string? path)) throw new InvalidDataException(
                $"{color.TargetSlot}: role layer {layer} has no converted artwork.");
            var image = LoadImage(path, resolveArt);
            string role = EffectiveRole(roles[layer], image);
            result.Add(role);
            if (role != "blank" && role != "sponsor" && !Visible(image))
                throw new InvalidDataException($"{color.TargetSlot}: required {role} layer {layer} is transparent.");
            if (jersey && role == "brand" && SlotRoles.KnownLogo(resolveArt(path)) != "brand_fanatics")
                throw new InvalidDataException($"{color.TargetSlot}: brand layer {layer} is not verified Fanatics artwork.");
        }
        return result;
    }

    static void RequireRoleCount(IReadOnlyList<string> roles, string role, int expected,
        NewUniformAddition addition, string description)
    {
        int actual = roles.Count(r => r == role);
        if (actual != expected) throw new InvalidDataException(
            $"{addition.Team} {addition.SourceKit}: expected {expected} {description}, found {actual}.");
    }

    static string EffectiveRole(string role, Rgba image) => role == "sponsor" && !Visible(image) ? "blank" : role;

    internal static Dictionary<int, Rgba> LoadArtwork(Dictionary<int, string> layers, int have, TextureRes target,
        Func<string, string> resolveArt, int workers)
    {
        var result = new Dictionary<int, Rgba>();
        foreach (var (layer, path) in layers.Where(kv => kv.Key < have).OrderBy(kv => kv.Key))
        {
            var image = LoadImage(path, resolveArt);
            result[layer] = image.Width == target.Width && image.Height == target.Height
                ? image : ImageIO.ResizeSmooth(image, target.Width, target.Height, workers);
        }
        return result;
    }

    internal static List<Rgba> StockSlices(TextureSlots.Slot source, TextureRes target, string role)
    {
        var stock = Ps4Codec.DecodeSlices(source.Res, source.Stock);
        int needed = Ps4Codec.Slices(target.Header);
        if (stock.Count > needed || stock.Any(image => image.Width != target.Width || image.Height != target.Height))
            throw new InvalidDataException($"{source.Name}: its stock slices cannot provide the expanded target layout.");
        while (stock.Count < needed) stock.Add(UniformBrandingLayers.Blank(target.Width, target.Height, role));
        return stock;
    }

    internal static TextureSlots.Slot Matching(IReadOnlyList<TextureSlots.Slot> slots, TextureRes layout, string description, string name)
    {
        var matches = slots.Where(slot => SameLayout(slot.Res, layout)).ToList();
        if (matches.Count == 0)
        {
            // Expansion can make the installed target deeper than its original source. Use the deepest compatible
            // source as the picture-matching layout; StockSlices supplies only safe blanks for the added capacity.
            int layers = Ps4Codec.Slices(layout.Header);
            var compatible = slots.Where(slot => Ps4Codec.Slices(slot.Res.Header) <= layers
                && slot.Res.Width == layout.Width && slot.Res.Height == layout.Height
                && slot.Res.PayloadSize == layout.PayloadSize && slot.Res.MipCount == layout.MipCount
                && slot.Res.MipSizes.SequenceEqual(layout.MipSizes)).ToList();
            if (compatible.Count > 0)
            {
                int deepest = compatible.Max(slot => Ps4Codec.Slices(slot.Res.Header));
                matches = compatible.Where(slot => Ps4Codec.Slices(slot.Res.Header) == deepest).ToList();
            }
        }
        if (matches.Count != 1) throw new InvalidDataException($"{name}: {matches.Count} {description} copies match the required layout.");
        return matches[0];
    }

    internal static TextureSlots.Slot Companion(IReadOnlyList<TextureSlots.Slot> slots, TextureRes color, string description, string name,
        int artworkLayers)
    {
        // Color, normal and coefficient arrays are logical companions, not necessarily identical binary layouts.
        // Prefer the color's slice count; a stock copy can carry an extra blank slice (Montreal away), in which case
        // the complete converted artwork's layer count identifies the real normal/coefficient companion.
        int layers = Ps4Codec.Slices(color.Header);
        var matches = slots.Where(slot => Ps4Codec.Slices(slot.Res.Header) == layers).ToList();
        if (matches.Count == 0 && artworkLayers != layers)
            matches = slots.Where(slot => Ps4Codec.Slices(slot.Res.Header) == artworkLayers).ToList();
        if (matches.Count > 1)
        {
            var sameDimensions = matches.Where(slot => slot.Res.Width == color.Width && slot.Res.Height == color.Height).ToList();
            if (sameDimensions.Count == 1) matches = sameDimensions;
        }
        if (matches.Count != 1)
        {
            string available = string.Join("; ", slots.Select(slot => $"{slot.Chunk}: {slot.Res.Width}x{slot.Res.Height}, "
                + $"{Ps4Codec.Slices(slot.Res.Header)} layers, {slot.Res.MipCount} mips"));
            throw new InvalidDataException($"{name}: {matches.Count} {description} copies match the color array's {layers} layers. "
                + $"Color is {color.Width}x{color.Height}. Available: {available}");
        }
        return matches[0];
    }

    static bool SameLayout(TextureRes a, TextureRes b) => a.Width == b.Width && a.Height == b.Height
        && a.PayloadSize == b.PayloadSize && a.MipCount == b.MipCount && a.MipSizes.SequenceEqual(b.MipSizes)
        && Ps4Codec.Slices(a.Header) == Ps4Codec.Slices(b.Header);

    /// <summary>Resolves a saved absolute artwork path, then the same flattened NHL 27 PNG cache used by installation.</summary>
    public static string ResolveArtwork(string path, string artDir)
    {
        string prefix = path.StartsWith("coeff:", StringComparison.Ordinal) ? "coeff:"
            : path.StartsWith("stock:", StringComparison.Ordinal) ? "stock:" : "";
        string source = path[prefix.Length..];
        if (source.Length == 0 || File.Exists(source)) return path;
        string local = Path.Combine(artDir, Path.GetFileName(source));
        if (!File.Exists(local)) throw new FileNotFoundException("Converted artwork required by the audit is missing.", source);
        return prefix + local;
    }

    static Rgba LoadImage(string path, Func<string, string> resolveArt)
    {
        string resolved = resolveArt(path);
        if (resolved.StartsWith("stock:", StringComparison.Ordinal))
            throw new InvalidDataException("A stock texture cannot stand in for converted branding artwork: " + resolved[6..]);
        return resolved.StartsWith("coeff:", StringComparison.Ordinal)
            ? LogoCoefficient.FromColor(ImageIO.Load(resolved[6..])) : ImageIO.Load(resolved);
    }

    static bool Visible(Rgba image)
    {
        for (int i = 3; i < image.Pixels.Length; i += 4) if (image.Pixels[i] > 16) return true;
        return false;
    }

    static double Coverage(Rgba image)
    {
        int drawn = 0;
        for (int i = 3; i < image.Pixels.Length; i += 4) if (image.Pixels[i] > 16) drawn++;
        return (double)drawn / (image.Pixels.Length / 4);
    }

    static double NormalDifference(Rgba expected, Rgba actual, Rgba colorMask)
    {
        long total = 0, count = 0;
        for (int i = 0; i < expected.Pixels.Length; i += 4)
        {
            if (colorMask.Pixels[i + 3] <= 16) continue;
            total += Math.Abs(expected.Pixels[i] - actual.Pixels[i]) + Math.Abs(expected.Pixels[i + 1] - actual.Pixels[i + 1]);
            count += 2;
        }
        if (count > 0) return total / (double)count;
        // Blank layers must be flat normals. Compare RG everywhere; the corresponding color coverage check catches old art.
        for (int i = 0; i < expected.Pixels.Length; i += 4)
            total += Math.Abs(expected.Pixels[i] - actual.Pixels[i]) + Math.Abs(expected.Pixels[i + 1] - actual.Pixels[i + 1]);
        return total / (double)(expected.Pixels.Length / 2);
    }

    /// <summary>Percentage of pixels with the wrong material class or RGB values too far from that class's clean values.</summary>
    internal static double CoefficientDifference(Rgba expected, Rgba actual)
    {
        const int channelErrorLimit = 36; // Tolerate block-compression rounding, not same-shaped legacy/noisy material data.
        int wrong = 0;
        for (int i = 0; i < expected.Pixels.Length; i += 4)
        {
            bool wanted = Inside(expected.Pixels, i);
            bool found = Inside(actual.Pixels, i);
            int cleanR = wanted ? 128 : 126, cleanG = wanted ? 54 : 0, cleanB = wanted ? 225 : 255;
            int cleanError = Math.Abs(actual.Pixels[i] - cleanR) + Math.Abs(actual.Pixels[i + 1] - cleanG)
                + Math.Abs(actual.Pixels[i + 2] - cleanB);
            if (wanted != found || cleanError > channelErrorLimit) wrong++;
        }
        return 100.0 * wrong / (expected.Pixels.Length / 4);
    }

    static bool Inside(byte[] p, int i)
    {
        int inside = Math.Abs(p[i] - 128) + Math.Abs(p[i + 1] - 54) + Math.Abs(p[i + 2] - 225);
        int outside = Math.Abs(p[i] - 126) + Math.Abs(p[i + 1]) + Math.Abs(p[i + 2] - 255);
        return inside < outside;
    }
}
