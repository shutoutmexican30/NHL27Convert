using Suite.Core;

namespace Suite.Convert27;

/// <summary>How converted source layers occupy one NHL 24 branding array, including any retained stock slices.</summary>
public sealed record UniformLayerPlacement(IReadOnlyDictionary<int, int> Moves, IReadOnlySet<int> KeepStock)
{
    /// <summary>Transparent source layers which must not claim a physical hotspot or overwrite another logo.</summary>
    public IReadOnlySet<int> BlankSources { get; init; } = new HashSet<int>();
    public int Destination(int sourceLayer) => Moves.GetValueOrDefault(sourceLayer, sourceLayer);
}

/// <summary>Shared placement and pixel comparison rules for full-kit installation and independent installed-art audits.</summary>
public static class UniformBrandingLayers
{
    public const double SamePictureLimit = 8;

    /// <summary>Matches identical pictures before assigning unmatched layers; normal and coefficient arrays follow color.</summary>
    /// <param name="capacity">Optional shared slot count when a stock color array has extra slices its companions lack.</param>
    public static UniformLayerPlacement Plan(IReadOnlyDictionary<int, Rgba> artwork, IReadOnlyList<Rgba> stock, int? capacity = null)
    {
        int have = capacity ?? stock.Count;
        if (have < 0 || have > stock.Count) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (artwork.Keys.Any(layer => layer < 0 || layer >= have)) throw new InvalidDataException("Artwork layer is outside the shared target array capacity.");
        var moves = new Dictionary<int, int>();
        var claimed = new HashSet<int>();
        var unmatched = new List<int>();
        foreach (var (layer, source) in artwork.OrderBy(kv => kv.Key))
        {
            int match = -1;
            double best = double.MaxValue;
            for (int candidate = 0; candidate < have; candidate++)
            {
                if (claimed.Contains(candidate)) continue;
                double score = PictureDifference(source, stock[candidate]);
                if (score < best) { best = score; match = candidate; }
            }
            if (best < SamePictureLimit)
            {
                claimed.Add(match);
                if (match != layer) moves[layer] = match;
            }
            else unmatched.Add(layer);
        }
        foreach (int layer in unmatched.Where(layer => !claimed.Contains(layer)).ToList())
        {
            claimed.Add(layer);
            unmatched.Remove(layer);
        }
        foreach (int layer in unmatched)
        {
            int free = Enumerable.Range(0, have).FirstOrDefault(slot => !claimed.Contains(slot), -1);
            if (free < 0) throw new InvalidDataException("More converted layers were supplied than the target array can hold.");
            claimed.Add(free);
            moves[layer] = free;
        }
        // This artwork describes the complete converted array. A slot vacated by a picture move must be blanked:
        // retaining it would show the old crest/sponsor a second time beside the correctly moved converted artwork.
        return new UniformLayerPlacement(moves, new HashSet<int>());
    }

    /// <summary>Matches source-role occurrences to physical donor slots. Missing visible positions are a hard error.</summary>
    public static UniformLayerPlacement PlanRoles(IReadOnlyList<string> sourceRoles, IReadOnlyList<string> donorRoles,
        IReadOnlySet<int>? blankSources = null)
    {
        if (sourceRoles.Count == 0 || sourceRoles.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Every converted artwork layer needs an ordered source role.");
        if (donorRoles.Count == 0 || donorRoles.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("The donor role map must describe every physical slot, including unused slots.");
        string[] valid = ["crest", "shoulder", "brand", "shield", "epw_tag", "sponsor", "blank"];
        foreach (string role in sourceRoles.Concat(donorRoles))
            if (!valid.Contains(role, StringComparer.Ordinal)) throw new InvalidDataException("Unknown branding role: " + role);
        var skipped = blankSources?.ToHashSet() ?? [];
        if (skipped.Any(layer => layer < 0 || layer >= sourceRoles.Count))
            throw new InvalidDataException("A transparent source layer is outside the converted role map.");
        skipped.UnionWith(Enumerable.Range(0, sourceRoles.Count).Where(layer => sourceRoles[layer] == "blank"));
        var needed = sourceRoles.Select((role, layer) => (role, layer)).Where(x => !skipped.Contains(x.layer))
            .GroupBy(x => x.role).ToDictionary(g => g.Key, g => new Queue<int>(g.Select(x => x.layer)), StringComparer.Ordinal);
        var moves = new Dictionary<int, int>();
        for (int destination = 0; destination < donorRoles.Count; destination++)
        {
            if (!needed.TryGetValue(donorRoles[destination], out var queue) || queue.Count == 0) continue;
            int source = queue.Dequeue();
            if (source != destination) moves[source] = destination;
        }
        string missing = string.Join("; ", needed.Where(g => g.Value.Count > 0)
            .Select(g => $"{g.Key} source layer(s) {string.Join(", ", g.Value)}"));
        if (missing.Length > 0)
            throw new InvalidDataException($"The NHL 24 layout has no physical position for {missing}. "
                + $"Source roles: [{string.Join(", ", sourceRoles)}]. Donor roles: [{string.Join(", ", donorRoles)}]. "
                + "Add the required hotspot or choose a role-capable donor; do not substitute another logo's position.");
        return new UniformLayerPlacement(moves, new HashSet<int>()) { BlankSources = skipped };
    }

    /// <summary>Uses explicit physical roles when present; legacy pixel matching is only an unverified fallback.</summary>
    public static UniformLayerPlacement Plan(NewUniformTexture texture, IReadOnlyDictionary<int, Rgba> artwork,
        IReadOnlyList<Rgba> stock, int? capacity = null, IReadOnlyList<string>? physicalRoles = null)
    {
        int have = capacity ?? stock.Count;
        if ((physicalRoles ?? texture.DonorRoles) is { } donor)
        {
            var source = texture.RequiredRoles ?? throw new InvalidDataException(
                $"{texture.SourceSlot}: physical donor roles require the complete converted source-role map.");
            if (source.Count != artwork.Count || Enumerable.Range(0, source.Count).Any(layer => !artwork.ContainsKey(layer)))
                throw new InvalidDataException($"{texture.SourceSlot}: source roles and converted artwork layers differ.");
            if (artwork.Keys.Any(layer => layer < 0 || layer >= have))
                throw new InvalidDataException($"{texture.SourceSlot}: converted artwork is outside the target array capacity.");
            if (donor.Count != have) throw new InvalidDataException(
                $"{texture.SourceSlot}: donor has {donor.Count} declared roles but {have} usable physical slots.");
            var blanks = new HashSet<int>();
            for (int layer = 0; layer < source.Count; layer++)
            {
                bool visible = Visible(artwork[layer]);
                if (source[layer] == "sponsor" && !visible) blanks.Add(layer);
                else if (source[layer] != "sponsor" && source[layer] != "blank" && !visible)
                    throw new InvalidDataException($"{texture.SourceSlot}: required {source[layer]} layer {layer} is transparent.");
                else if (source[layer] == "blank" && visible)
                    throw new InvalidDataException($"{texture.SourceSlot}: layer {layer} is marked blank but contains visible artwork.");
            }
            return PlanRoles(source, donor, blanks);
        }
        return Plan(artwork, stock, capacity);
    }

    public static bool Visible(Rgba image)
    {
        for (int i = 3; i < image.Pixels.Length; i += 4) if (image.Pixels[i] > 16) return true;
        return false;
    }

    /// <summary>Builds the exact expected slices after placement. Unused slices are blank, never silently borrowed.</summary>
    public static Dictionary<int, Rgba> Place(IReadOnlyDictionary<int, Rgba> artwork, IReadOnlyList<Rgba> stock,
        UniformLayerPlacement placement, string role)
    {
        if (role is not ("color" or "normal" or "coeff")) throw new ArgumentException("Unknown branding-array role.", nameof(role));
        var result = new Dictionary<int, Rgba>();
        foreach (var (source, image) in artwork)
            if (!placement.BlankSources.Contains(source)) result[placement.Destination(source)] = image;
        foreach (int layer in placement.KeepStock) result[layer] = stock[layer];
        for (int layer = 0; layer < stock.Count; layer++)
            if (!result.ContainsKey(layer)) result[layer] = Blank(stock[layer].Width, stock[layer].Height, role);
        return result;
    }

    public static Rgba Blank(int width, int height, string role)
    {
        var pixels = new byte[width * height * 4];
        for (int p = 0; p < pixels.Length; p += 4)
        {
            if (role == "normal") { pixels[p] = 128; pixels[p + 1] = 128; pixels[p + 2] = 255; }
            else if (role == "coeff") { pixels[p] = 126; pixels[p + 2] = 255; }
            else if (role != "color") throw new ArgumentException("Unknown branding-array role.", nameof(role));
        }
        return new Rgba(width, height, pixels);
    }

    /// <summary>Mean color difference where both pictures are drawn, heavily penalizing areas only one covers.</summary>
    public static double PictureDifference(Rgba a, Rgba b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return double.MaxValue;
        long both = 0, onlyOne = 0, total = 0;
        for (int i = 0; i < a.Pixels.Length; i += 4)
        {
            bool drawnA = a.Pixels[i + 3] > 16, drawnB = b.Pixels[i + 3] > 16;
            if (drawnA && drawnB)
            {
                both++;
                total += Math.Abs(a.Pixels[i] - b.Pixels[i]) + Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1])
                    + Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]);
            }
            else if (drawnA || drawnB) onlyOne++;
        }
        if (both == 0) return double.MaxValue;
        return total / (3.0 * both) + 400.0 * onlyOne / (both + onlyOne);
    }

    /// <summary>Compression-tolerant RGB difference for normal and coefficient maps, whose alpha is not coverage.</summary>
    public static double RgbDifference(Rgba a, Rgba b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return double.MaxValue;
        long total = 0;
        for (int i = 0; i < a.Pixels.Length; i += 4)
            total += Math.Abs(a.Pixels[i] - b.Pixels[i]) + Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1])
                + Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]);
        return total / (3.0 * a.Width * a.Height);
    }
}
