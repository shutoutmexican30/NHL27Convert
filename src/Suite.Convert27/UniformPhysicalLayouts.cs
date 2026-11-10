using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>The engine-facing stamp and complete physical role map for one branding-array copy.</summary>
public sealed record UniformPhysicalLayout(string SourceStamp, string TargetStamp,
    IReadOnlyList<string> Roles, UniformHotspotLayout? Override);

/// <summary>
/// Resolves branding-array slots from the same bundle's stamp EBX, never from pixel guesses. An explicit layout is
/// required for unknown garment-specific hotspot names; it must describe every physical slot in the target array.
/// </summary>
public static class UniformPhysicalLayouts
{
    public static string StampForArray(string arraySlot)
    {
        string name = arraySlot.Trim().Replace('\\', '/').TrimStart('/');
        string leaf = name.Split('/')[^1];
        string? suffix = null;
        foreach (string ending in new[] { "_color_array", "_normal_array", "_coeff_array" })
            if (leaf.EndsWith(ending, StringComparison.Ordinal)) { suffix = ending; break; }
        if (suffix is null) throw new InvalidDataException($"{name}: not a branding-array resource.");
        string stem = leaf[..^suffix.Length];
        if (!stem.StartsWith("branding_jersey_", StringComparison.Ordinal)
            && !stem.StartsWith("branding_pant_", StringComparison.Ordinal))
            throw new InvalidDataException($"{name}: its jersey/pants stamp cannot be determined.");
        return name[..(name.Length - leaf.Length)] + "stamp_" + stem["branding_".Length..];
    }

    public static string TargetStamp(NewUniformAddition addition, string sourceStamp)
    {
        string source = addition.SourcePrefix.Trim().Replace('\\', '/').TrimEnd('/') + "/";
        if (!sourceStamp.StartsWith(source, StringComparison.Ordinal))
            throw new InvalidDataException($"{sourceStamp}: hotspot override is outside the uniform's source kit.");
        return NewUniformAdditionPlanner.MapTargetSlot(sourceStamp, source, addition.TargetPrefix);
    }

    public static UniformPhysicalLayout Resolve(BundleIndex index, NewUniformAddition addition,
        NewUniformTexture texture, int capacity, BundleIndex.Bundle? sourceBundle = null,
        BundleIndex.Bundle? targetBundle = null, bool verifyTarget = false)
    {
        if (capacity < 1) throw new InvalidDataException($"{texture.SourceSlot}: invalid physical slot capacity.");
        string sourceStamp = StampForArray(texture.SourceSlot);
        string targetStamp = TargetStamp(addition, sourceStamp);
        string expectedTargetTexture = NewUniformAdditionPlanner.MapTargetSlot(texture.SourceSlot,
            addition.SourcePrefix.Trim().Replace('\\', '/').TrimEnd('/') + "/", addition.TargetPrefix);
        if (!string.Equals(texture.TargetSlot, expectedTargetTexture, StringComparison.Ordinal))
            throw new InvalidDataException($"{texture.SourceSlot}: its target does not match the normalized source kit.");
        var overrides = addition.HotspotLayouts.Where(l => l.SourceSlot == sourceStamp).ToList();
        if (overrides.Count > 1) throw new InvalidDataException($"{sourceStamp}: duplicate hotspot overrides.");
        var replacement = overrides.SingleOrDefault();
        byte[]? sourceRaw = null;
        UniformHotspotLayout expected;
        IReadOnlyList<string> physical;
        if (replacement is not null)
        {
            physical = UniformHotspots.Roles(replacement); // Curated replacements require complete sequential ids.
            expected = replacement;
        }
        else
        {
            sourceRaw = ReadStamp(index, sourceStamp, sourceBundle);
            expected = UniformHotspots.Capture(sourceRaw, sourceStamp);
            // An untouched source can intentionally skip a slice (for example pant ids 8 and 10).
            // Preserve that sparse physical layout instead of applying the curated-replacement contiguity rule.
            physical = UniformHotspots.Roles(sourceRaw);
        }
        if (physical.Count > capacity) throw new InvalidDataException(
            $"{sourceStamp}: {physical.Count} physical logo positions do not fit {capacity} texture-array slots. "
            + "Every logo needs its real hotspot; do not discard a layer to make the count fit.");
        var roles = physical.Concat(Enumerable.Repeat("blank", capacity - physical.Count)).ToArray();
        if (texture.DonorRoles is { } declared && !declared.SequenceEqual(roles, StringComparer.Ordinal))
            throw new InvalidDataException($"{sourceStamp}: saved donor roles [{string.Join(", ", declared)}] do not agree "
                + $"with the engine hotspots [{string.Join(", ", roles)}]. Review the physical layout instead of guessing.");
        if (verifyTarget)
        {
            byte[] targetRaw = ReadStamp(index, targetStamp, targetBundle);
            var actual = UniformHotspots.Capture(targetRaw, targetStamp);
            if (!SameLayout(actual, expected)) throw new InvalidDataException(
                $"{targetStamp}: installed physical hotspots do not match the reviewed source/override layout.");
            int actualCount = replacement is null ? UniformHotspots.Roles(targetRaw).Count : physical.Count;
            if (actualCount > capacity) throw new InvalidDataException($"{targetStamp}: hotspots exceed its array capacity.");
        }
        return new UniformPhysicalLayout(sourceStamp, targetStamp, roles, replacement);
    }

    public static bool SameLayout(UniformHotspotLayout a, UniformHotspotLayout b) =>
        a.Branding.OrderBy(x => x.Id).SequenceEqual(b.Branding.OrderBy(x => x.Id), PlacementComparer.Instance);

    /// <summary>Finds the bundle that owns a decoded texture copy, even when another RES names the same chunk.</summary>
    public static BundleIndex.Bundle BundleForTexture(BundleIndex index, TextureSlots.Slot slot, string stampSource)
    {
        var resources = (index.ResByName.GetValueOrDefault(slot.Name) ?? []).Where(r =>
            TextureRes.Parse(Decode(BundleIndex.ReadBytes(r.Data))).ChunkGuid == slot.Chunk).ToList();
        var bundles = resources.Select(r => r.Bundle).DistinctBy(b => (b.Toc, b.RecordAt)).ToList();
        if (bundles.Count == 0) throw new InvalidDataException($"{slot.Name}: no RES references chunk {slot.Chunk}.");
        string stamp = StampForArray(stampSource);
        var stampEntries = index.EbxByName.GetValueOrDefault(stamp) ?? [];
        bool ContainsStamp(BundleIndex.Bundle bundle) => stampEntries.Any(e => e.Bundle.Toc == bundle.Toc
            && e.Bundle.RecordAt == bundle.RecordAt);
        var local = bundles.Where(ContainsStamp).ToList();
        if (local.Count > 0) bundles = local;
        else
        {
            // Some stock bundles reference a shared texture chunk while the uniquely named stamp lives in a sibling
            // bundle. A single physical stamp is unambiguous; multiple stamp homes still require explicit review.
            var stampBundles = stampEntries.Select(e => e.Bundle).DistinctBy(b => (b.Toc, b.RecordAt)).ToList();
            if (stampBundles.Count == 1) return stampBundles[0];
            throw new InvalidDataException($"{slot.Name}: no containing bundle names {stamp}, and the stamp has "
                + $"{stampBundles.Count} possible physical homes.");
        }
        var first = UniformHotspots.Capture(ReadStamp(index, stamp, bundles[0]), stamp);
        foreach (var bundle in bundles.Skip(1))
            if (!SameLayout(first, UniformHotspots.Capture(ReadStamp(index, stamp, bundle), stamp)))
                throw new InvalidDataException($"{slot.Name}: resources share chunk {slot.Chunk} but their physical stamps differ; "
                    + "the ambiguous copy must be reviewed instead of merging its layouts.");
        return bundles[0];
    }

    public static byte[] ReadStamp(BundleIndex index, string name, BundleIndex.Bundle? bundle = null)
    {
        var entries = index.EbxByName.GetValueOrDefault(name) ?? [];
        if (bundle is not null) entries = entries.Where(e => e.Bundle.Toc == bundle.Toc
            && e.Bundle.RecordAt == bundle.RecordAt).ToList();
        if (entries.Count != 1) throw new InvalidDataException(
            $"{name}: expected one stamp in the array's own bundle, found {entries.Count}. Equal-depth physical copies "
            + "must be resolved by bundle, not merged by resource name or depth.");
        byte[] raw = Decode(BundleIndex.ReadBytes(entries[0].Data));
        var parsed = EbxRiff.Parse(raw);
        if (parsed.Name != name) throw new InvalidDataException($"{name}: its embedded EBX identity differs.");
        return raw;
    }

    public static byte[] Decode(byte[] stored)
    {
        try { return ChunkBlocks.Parse(stored).Decode(); }
        catch (InvalidDataException) { return stored; }
    }

    sealed class PlacementComparer : IEqualityComparer<UniformHotspotPlacement>
    {
        public static readonly PlacementComparer Instance = new();
        public bool Equals(UniformHotspotPlacement? a, UniformHotspotPlacement? b) => a is not null && b is not null
            && a.Id == b.Id && a.Role == b.Role && a.Name == b.Name && a.Rotation == b.Rotation
            && a.ScaleX == b.ScaleX && a.ScaleY == b.ScaleY && a.TranslateX == b.TranslateX
            && a.TranslateY == b.TranslateY && a.Enabled == b.Enabled;
        public int GetHashCode(UniformHotspotPlacement slot) => HashCode.Combine(slot.Id, slot.Role, slot.Name);
    }
}
