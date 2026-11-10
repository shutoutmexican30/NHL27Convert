using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>Resolves companion copies by layout, then by the bundles that physically group the arrays.</summary>
internal static class InstalledBrandingCopyMatcher
{
    public static TextureSlots.Slot Target(BundleIndex index, IReadOnlyList<TextureSlots.Slot> slots,
        TextureSlots.Slot color, TextureRes sourceLayout, string description, string name)
    {
        var matches = slots.Where(slot => SameLayout(slot.Res, sourceLayout)).ToList();
        // An expanded target can be deeper than every remaining source copy. Its own color layout then identifies
        // the normal/coefficient companions; bundle association resolves equal-depth duplicate copies.
        if (matches.Count == 0) matches = slots.Where(slot => SameLayout(slot.Res, color.Res)).ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
        {
            var colorBundles = (index.ChunksById.GetValueOrDefault(color.Chunk) ?? [])
                .Select(chunk => (chunk.Bundle.Toc, chunk.Bundle.RecordAt)).ToHashSet();
            var associated = matches.Where(candidate => (index.ChunksById.GetValueOrDefault(candidate.Chunk) ?? [])
                .Any(chunk => colorBundles.Contains((chunk.Bundle.Toc, chunk.Bundle.RecordAt)))).ToList();
            if (associated.Count == 1) return associated[0];
            matches = associated;
        }
        string available = string.Join("; ", slots.Select(slot => $"{slot.Chunk}: {slot.Res.Width}x{slot.Res.Height}, "
            + $"{Ps4Codec.Slices(slot.Res.Header)} layers, {slot.Res.MipCount} mips"));
        throw new InvalidDataException($"{name}: {matches.Count} {description} copies match the required layout and color-array bundle. "
            + $"Available: {available}");
    }

    static bool SameLayout(TextureRes a, TextureRes b) => a.Width == b.Width && a.Height == b.Height
        && a.PayloadSize == b.PayloadSize && a.MipCount == b.MipCount && a.MipSizes.SequenceEqual(b.MipSizes)
        && Ps4Codec.Slices(a.Header) == Ps4Codec.Slices(b.Header);
}
