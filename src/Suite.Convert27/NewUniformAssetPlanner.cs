using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>Complete clone plans for one addition. Building reads the source game but never writes to it.</summary>
public sealed record NewUniformAssetPlan(
    IReadOnlyList<BrandingClonePlan> Clones,
    IReadOnlyList<PlannedUniformSlot> Slots,
    IReadOnlyDictionary<string, byte[]> Artwork)
{
    public void ThrowIfIncomplete()
    {
        foreach (var clone in Clones) clone.ThrowIfIncomplete();
    }
}

/// <summary>
/// Builds independent texture assets from team artwork and role-matched structural donors. Donor uniforms, fabrics and
/// pixels are never a content fallback. A full converted array can be rebuilt in a different team's layout; stock bytes
/// are reused only for another resource from the same team's own source uniform with an identical binary layout.
/// </summary>
public static class NewUniformAssetPlanner
{
    public static NewUniformAssetPlan Build(
        BundleIndex index,
        NewUniformAddition addition,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? donorRoles = null,
        int workers = 0)
    {
        donorRoles ??= new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        string sourcePrefix = Prefix(addition.SourcePrefix), targetPrefix = Prefix(addition.TargetPrefix);
        if (sourcePrefix == targetPrefix) throw new InvalidDataException("Source and target uniform folders must differ.");
        var artwork = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var slots = new List<PlannedUniformSlot>();
        var donors = new Dictionary<string, string>(StringComparer.Ordinal);

        // Encode every explicitly supplied resource first. A dependency that is also explicit therefore uses converted
        // artwork even when it is reached through another root's EBX dependency closure.
        foreach (var texture in addition.Textures)
        {
            string target = Name(texture.TargetSlot), source = Name(texture.SourceSlot);
            string expected = NewUniformAdditionPlanner.MapTargetSlot(source, sourcePrefix, targetPrefix);
            if (target != expected) throw new InvalidDataException($"{source}: target name does not match the new uniform folder.");
            var replacement = texture.Artwork with { Slot = source };
            bool isArray = IsArray(target);
            int sourceLayers = index.ResByName.ContainsKey(source)
                ? Ps4Codec.Slices(TextureSlots.Read(index.Game, index, source).Res.Header) : 0;
            int required = texture.RequiredRoles?.Count
                ?? (replacement.Layers is { Count: > 0 } ? replacement.Layers.Keys.Max() + 1 : Math.Max(1, sourceLayers));
            if (required < 1) throw new InvalidDataException($"{target}: invalid layer count.");
            if (texture.RequiredRoles is { } roles && roles.Count != required)
                throw new InvalidDataException($"{target}: every output layer needs an ordered semantic role.");

            Dictionary<int, string>? layerPaths = null;
            if (isArray)
            {
                layerPaths = Enumerable.Range(0, required).ToDictionary(layer => layer, layer =>
                    replacement.Layers is not null && replacement.Layers.TryGetValue(layer, out string? path) && File.Exists(path)
                        ? path : "");
                // New target arrays are complete rebuilds. This deliberately refuses an old partial replacement mod:
                // a blank output slice would otherwise expose the structural donor's crest, sponsor or shoulder art.
                foreach (var (layer, path) in layerPaths)
                    if (path.Length == 0) throw new InvalidDataException(
                        $"{target} layer {layer}: converted team artwork is required; donor pixels cannot fill this slice.");
            }
            var need = new UniformSlotNeed(target, required,
                Artwork: !isArray && (File.Exists(replacement.Source) || index.ResByName.ContainsKey(source))
                    ? (File.Exists(replacement.Source) ? replacement.Source : "stock:" + source) : null,
                LayerArtwork: layerPaths,
                RequiredRoles: texture.RequiredRoles);
            var candidates = Donors(index, targetPrefix, target, donorRoles).ToList();
            string BlankArtwork(int layer)
            {
                string basis = replacement.Layers!.Values.First(File.Exists);
                var image = ImageIO.Load(basis);
                bool normal = TextureName.Parse(target).Role == "normal";
                string path = Path.Combine(Path.GetDirectoryName(basis)!, ".cims-blanks", $"{image.Width}x{image.Height}-{(normal ? "normal" : "color")}.png");
                if (!File.Exists(path))
                {
                    var pixels = new byte[image.Width * image.Height * 4];
                    if (normal)
                        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 128; pixels[i + 1] = 128; pixels[i + 2] = 255; }
                    ImageIO.SavePng(new Rgba(image.Width, image.Height, pixels), path);
                }
                return path;
            }
            var mapped = UniformSlotPlan.BuildRoleMapped(targetPrefix, candidates, [], need, BlankArtwork);
            mapped.Plan.ThrowIfInvalid();
            replacement = replacement with { Layers = mapped.Need.LayerArtwork?.ToDictionary(kv => kv.Key, kv => kv.Value) };
            var slotPlan = mapped.Plan;
            var slot = slotPlan.Slots.Single();
            string donorName = slot.DonorName ?? throw new InvalidDataException($"{target}: a new target needs a structural donor.");
            var donor = TextureSlots.Read(index.Game, index, donorName);
            byte[] raw;
            if (isArray)
            {
                // Use the same alpha-preservation path as ordinary texture writes; donor stock supplies only the fabric
                // mask when requested, never RGB logo content. Every output slice still has explicit team artwork above.
                raw = TextureSlots.Build(donor, replacement with { Slot = donorName }, workers);
            }
            else if (File.Exists(replacement.Source))
            {
                raw = TextureSlots.Build(donor, replacement with { Slot = donorName }, workers);
            }
            else
            {
                var team = TextureSlots.Read(index.Game, index, source);
                SameLayout(donor.Res, team.Res, target);
                raw = TextureSlots.Build(team, replacement, workers);
            }
            if (raw.Length != donor.Res.PayloadSize * Ps4Codec.Slices(donor.Res.Header))
                throw new InvalidDataException($"{target}: encoded texture does not fill the donor layout.");
            artwork.Add(target, raw);
            donors.Add(target, donorName);
            slots.Add(slot);
        }

        var clones = new List<BrandingClonePlan>();
        var clonedTextures = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        // Discover every root's donor bundle first. Roots that share one donor bundle must ship in ONE target
        // bundle: the engine resolves a uniform by its conventional bundle name (target folder + target family
        // identity + platform suffix), so sibling resources in a second, differently named bundle would never load.
        var groups = new List<(string SourceBundle, string DonorPrefix,
            List<(string DonorName, string TargetName)> Roots)>();
        foreach (var texture in addition.Textures)
        {
            string target = Name(texture.TargetSlot);
            if (clonedTextures.ContainsKey(target)) continue;
            string donorName = donors[target];
            string donorPrefix = donorName[..(donorName.LastIndexOf('/') + 1)];
            var initial = BrandingClonePlan.Build(index, donorPrefix, targetPrefix, donorName, target,
                new Dictionary<string, byte[]>(StringComparer.Ordinal) { [target] = artwork[target] });
            int at = groups.FindIndex(g => g.SourceBundle == initial.SourceBundleName);
            if (at < 0)
            {
                groups.Add((initial.SourceBundleName, donorPrefix, []));
                at = groups.Count - 1;

                // A conventional bundle is resolved by its blueprint/root asset, not just by its TOC name.
                // Include every team root in this donor bundle; their GUID imports pull in the corresponding
                // _hq_asp objects, blueprints and texture EBX. Previously only texture EBX were renamed,
                // leaving a twentyfour/twentyfive bundle full of home/away blueprint objects.
                string structuralRoot = initial.SourceBundleName.StartsWith("gen4b/", StringComparison.Ordinal)
                    ? initial.SourceBundleName["gen4b/".Length..] : initial.SourceBundleName;
                var structural = index.EbxByName.Values.SelectMany(entries => entries)
                    .Where(e => e.Bundle.Toc == initial.SourceBundle.Toc
                        && e.Bundle.RecordAt == initial.SourceBundle.RecordAt
                        && e.Name.StartsWith(donorPrefix, StringComparison.Ordinal)
                        && (e.Name.EndsWith("_player_team_brt", StringComparison.Ordinal)
                            || e.Name.EndsWith("_player_team_brt_blueprint", StringComparison.Ordinal)))
                    .Select(e => e.Name).Distinct(StringComparer.Ordinal).Order().ToList();
                if (!structural.Contains(structuralRoot, StringComparer.Ordinal))
                    throw new InvalidDataException($"The donor bundle's conventional root is missing: {structuralRoot}");
                foreach (string root in structural)
                    groups[at].Roots.Add((root, NewUniformAdditionPlanner.MapTargetSlot(root, donorPrefix, targetPrefix)));
            }
            if (!groups[at].Roots.Any(r => r.DonorName == donorName)) groups[at].Roots.Add((donorName, target));
        }
        foreach (var group in groups)
        {
            // Conventional target bundle name. Bundle names carry a "gen4b/" root that resource names do not,
            // so map beneath that root when present.
            string bundleName = group.SourceBundle.StartsWith(group.DonorPrefix, StringComparison.Ordinal)
                ? NewUniformAdditionPlanner.MapTargetSlot(group.SourceBundle, group.DonorPrefix, targetPrefix)
                : group.SourceBundle.StartsWith("gen4b/" + group.DonorPrefix, StringComparison.Ordinal)
                    ? "gen4b/" + NewUniformAdditionPlanner.MapTargetSlot(group.SourceBundle["gen4b/".Length..], group.DonorPrefix, targetPrefix)
                    : throw new InvalidDataException($"Donor bundle {group.SourceBundle} is outside {group.DonorPrefix}; cannot derive the conventional target bundle name.");
            // Discover the structural closure before filling dependencies. Supply every explicitly converted
            // texture here so two bundles carrying the same resource never disagree over converted versus stock bytes.
            var discovery = BrandingClonePlan.Build(index, group.DonorPrefix, targetPrefix, group.Roots, artwork, bundleName);
            var complete = new Dictionary<string, byte[]>(artwork, StringComparer.Ordinal);
            foreach (string missing in discovery.MissingArtwork)
            {
                if (complete.ContainsKey(missing)) continue;
                string donorDependency = NewUniformAdditionPlanner.MapTargetSlot(missing, targetPrefix, group.DonorPrefix);
                string teamDependency = NewUniformAdditionPlanner.MapTargetSlot(donorDependency, group.DonorPrefix, sourcePrefix);
                if (!index.ResByName.ContainsKey(teamDependency)) throw new InvalidDataException(
                    $"{missing}: the source team has no corresponding texture ({teamDependency}); donor artwork is not a fallback.");
                var team = TextureSlots.Read(index.Game, index, teamDependency);
                var structural = TextureSlots.Read(index.Game, index, donorDependency);
                SameLayout(structural.Res, team.Res, missing);
                complete[missing] = team.Stock;
            }
            var plan = BrandingClonePlan.Build(index, group.DonorPrefix, targetPrefix, group.Roots, complete, bundleName);
            plan.ThrowIfIncomplete();
            foreach (var (_, target) in group.Roots)
                if (!plan.Entries.Any(e => e.IsClone && e.Name == target))
                    throw new InvalidDataException($"{target}: it is not inside its donor bundle's shared clone plan.");
            foreach (var entry in plan.Entries.Where(e => e.NewChunkId is not null))
            {
                if (clonedTextures.TryGetValue(entry.Name, out byte[]? already))
                {
                    byte[] raw = ChunkBlocks.Parse(entry.Replacement!).Decode();
                    if (!already.AsSpan().SequenceEqual(raw)) throw new InvalidDataException(
                        $"{entry.Name}: two dependency roots produced different texture bytes.");
                    continue;
                }
                clonedTextures.Add(entry.Name, ChunkBlocks.Parse(entry.Replacement!).Decode());
            }
            clones.Add(plan);
        }
        // Texture donors supply binary layouts, not the source kit's complete engine-facing bundle layout. Clone every
        // source-kit family that is not already represented: base garments, fonts, numbered branding materials, stamps,
        // helmet logos, hotspots and placements are all potential runtime dependencies, not optional decoration.
        var sourceKitRoots = index.EbxByName.Keys.Where(name => name.StartsWith(sourcePrefix, StringComparison.Ordinal)
                && name.EndsWith("_player_team_brt", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Order().ToList();
        static string ConventionalRoot(string bundleName) => bundleName.StartsWith("gen4b/", StringComparison.Ordinal)
            ? bundleName["gen4b/".Length..] : bundleName;
        var plannedRoots = clones.Select(c => ConventionalRoot(c.NewBundleName)).ToHashSet(StringComparer.Ordinal);
        var companionRoots = sourceKitRoots.Where(root => !plannedRoots.Contains(
            NewUniformAdditionPlanner.MapTargetSlot(root, sourcePrefix, targetPrefix))).ToList();
        var companionGroups = new Dictionary<(string Toc, int RecordAt),
            List<(string DonorName, string TargetName)>>();
        foreach (string root in companionRoots)
        {
            string blueprint = root + "_blueprint";
            if (!index.EbxByName.ContainsKey(blueprint))
                throw new InvalidDataException($"The source companion has no blueprint: {root}");
            var source = index.EbxByName[root][0].Bundle;
            var key = (source.Toc, source.RecordAt);
            if (!companionGroups.TryGetValue(key, out var roots))
                companionGroups.Add(key, roots = []);
            roots.Add((root, NewUniformAdditionPlanner.MapTargetSlot(root, sourcePrefix, targetPrefix)));
            roots.Add((blueprint, NewUniformAdditionPlanner.MapTargetSlot(blueprint, sourcePrefix, targetPrefix)));
        }
        foreach (var ((toc, recordAt), roots) in companionGroups)
        {
            var archive = TocArchive.Parse(File.ReadAllBytes(toc));
            string sourceName = archive.Bundles.Single(b => b.RecordOffset == (ulong)(recordAt - TocFile.HeaderSize)).Name;
            string bundleName = sourceName.StartsWith(sourcePrefix, StringComparison.Ordinal)
                ? NewUniformAdditionPlanner.MapTargetSlot(sourceName, sourcePrefix, targetPrefix)
                : sourceName.StartsWith("gen4b/" + sourcePrefix, StringComparison.Ordinal)
                    ? "gen4b/" + NewUniformAdditionPlanner.MapTargetSlot(sourceName["gen4b/".Length..], sourcePrefix, targetPrefix)
                    : throw new InvalidDataException($"Source companion bundle {sourceName} is outside {sourcePrefix}.");

            // This is a same-team source-kit clone. Non-visible dependencies that were not explicitly converted may
            // reuse only this kit's own stock. BrandingClonePlan.Build checks the complete source chunk extent, so a
            // coeff array can be reused only in its original same-kit binary layout, never with another team's pixels.
            // Visible branding is rejected below unless it was explicitly supplied in the converted artwork set.
            var discovery = BrandingClonePlan.Build(index, sourcePrefix, targetPrefix, roots, artwork, bundleName);
            var complete = new Dictionary<string, byte[]>(artwork, StringComparer.Ordinal);
            foreach (string missing in discovery.MissingArtwork)
            {
                if (complete.ContainsKey(missing)) continue;
                string teamDependency = NewUniformAdditionPlanner.MapTargetSlot(missing, targetPrefix, sourcePrefix);
                if (!index.ResByName.ContainsKey(teamDependency)) throw new InvalidDataException(
                    $"{missing}: the source companion has no corresponding texture ({teamDependency}).");
                var team = TextureSlots.Read(index.Game, index, teamDependency);
                complete[missing] = team.Stock;
            }
            var plan = BrandingClonePlan.Build(index, sourcePrefix, targetPrefix, roots, complete, bundleName);
            plan.ThrowIfIncomplete();
            foreach (var (_, target) in roots)
                if (!plan.Entries.Any(e => e.IsClone && e.NewChunkId is null && e.Name == target))
                    throw new InvalidDataException($"{target}: its source-kit companion plan omitted the root or blueprint.");
            clones.Add(plan);
        }
        plannedRoots = clones.Select(c => ConventionalRoot(c.NewBundleName)).ToHashSet(StringComparer.Ordinal);
        string[] missingFamilies = sourceKitRoots.Select(root =>
                NewUniformAdditionPlanner.MapTargetSlot(root, sourcePrefix, targetPrefix))
            .Where(root => !plannedRoots.Contains(root)).ToArray();
        if (missingFamilies.Length > 0) throw new InvalidDataException(
            "The target does not reproduce every source-kit bundle family:\n" + string.Join("\n", missingFamilies));

        // Different donor bundles can map to one conventional garment bundle (for example base textures plus branding).
        // Merge their manifests, records and chunk metadata instead of writing colliding bundles the engine cannot load.
        clones = clones.GroupBy(c => c.NewBundleName, StringComparer.Ordinal)
            .Select(g => BrandingClonePlan.Merge(g)).ToList();
        BrandingCloneWriter.ValidatePlanSet(clones);

        // A root and its blueprint can live in different source bundles. If such a dependency was cloned elsewhere,
        // its importing object must use the new GUID too; checking only same-bundle leftovers would miss this.
        var globalGuidMap = new Dictionary<Guid, Guid>();
        foreach (var plan in clones)
        foreach (var (oldGuid, newGuid) in plan.EbxGuidRemap)
        {
            if (globalGuidMap.TryGetValue(oldGuid, out var existing) && existing != newGuid)
                throw new InvalidDataException($"Two target bundles remap source EBX GUID {oldGuid} differently.");
            globalGuidMap[oldGuid] = newGuid;
        }
        foreach (var plan in clones)
        {
            int ebxCount = BundleManifest.Parse(plan.Manifest).Ebx.Count;
            foreach (var entry in plan.Entries.Take(ebxCount).Where(e => e.IsClone))
            {
                byte[] stored = entry.Replacement!;
                byte[] raw;
                try { raw = ChunkBlocks.Parse(stored).Decode(); } catch (InvalidDataException) { raw = stored; }
                var parsed = EbxRiff.Parse(raw);
                foreach (var imported in parsed.ImportedFileGuids)
                    if (globalGuidMap.TryGetValue(imported, out var mapped) && imported != mapped)
                        throw new InvalidDataException($"{entry.Name}: cross-bundle dependency {imported} still points to the source kit; expected {mapped}.");
            }
        }

        var clonedTextureNames = clones.SelectMany(c => c.Entries.Where(e => e.NewChunkId is not null).Select(e => e.Name))
            .ToHashSet(StringComparer.Ordinal);
        var oldVisibleBranding = clonedTextureNames.Where(name => !artwork.ContainsKey(name)
            && TextureName.Parse(name) is { Garment: var garment, Role: "color" or "normal" }
            && garment.StartsWith("branding", StringComparison.Ordinal)).ToArray();
        if (oldVisibleBranding.Length > 0)
            throw new InvalidDataException("Visible branding dependencies require explicit new artwork; old stock logos cannot survive:\n"
                + string.Join("\n", oldVisibleBranding));
        return new NewUniformAssetPlan(clones, slots, artwork);
    }

    static IEnumerable<UniformDonor> Donors(BundleIndex index, string targetPrefix, string target,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roleMaps)
    {
        string relative = target[targetPrefix.Length..];
        string targetIdentity = NewUniformAdditionPlanner.FamilyIdentity(targetPrefix);
        foreach (string name in index.ResByName.Keys)
        {
            int slash = name.LastIndexOf('/');
            if (slash < 0) continue;
            string prefix = name[..(slash + 1)];
            if (prefix == targetPrefix || !prefix.Contains("/adidas/", StringComparison.Ordinal)) continue;
            string donorIdentity;
            try { donorIdentity = NewUniformAdditionPlanner.FamilyIdentity(prefix); }
            catch (InvalidDataException) { continue; }
            string donorRelative = name[(slash + 1)..];
            if (donorRelative.Replace(donorIdentity, targetIdentity, StringComparison.Ordinal) != relative) continue;
            byte[] stored = BundleIndex.ReadBytes(index.ResByName[name][0].Data);
            byte[] header;
            try { header = ChunkBlocks.Parse(stored).Decode(); } catch (InvalidDataException) { header = stored; }
            int layers = Ps4Codec.Slices(header);
            roleMaps.TryGetValue(name, out var roles);
            yield return new UniformDonor(prefix,
                [new UniformSlotState(name, layers, roles?.Count == layers ? roles : null)]);
        }
    }

    static void SameLayout(TextureRes donor, TextureRes team, string target)
    {
        if (donor.Width != team.Width || donor.Height != team.Height || donor.BlockBytes != team.BlockBytes
            || donor.MipCount != team.MipCount || !donor.MipSizes.SequenceEqual(team.MipSizes)
            || Ps4Codec.Slices(donor.Header) != Ps4Codec.Slices(team.Header))
            throw new InvalidDataException($"{target}: the source team's stock texture has a different binary layout; converted artwork is required.");
    }

    static bool IsArray(string name) => name.EndsWith("_color_array", StringComparison.Ordinal)
        || name.EndsWith("_normal_array", StringComparison.Ordinal) || name.EndsWith("_coeff_array", StringComparison.Ordinal);
    static string Name(string value) => value.Trim().Replace('\\', '/').TrimStart('/');
    static string Prefix(string value) => Name(value).TrimEnd('/') + "/";
}
