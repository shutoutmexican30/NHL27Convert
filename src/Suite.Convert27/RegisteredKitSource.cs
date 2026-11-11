using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>
/// Ensures that a new uniform clones a complete, registered kit from its own team. Some NHL 24 default folders contain
/// only launch textures; their numbered siblings hold the full game layout. Pixels still come only from the selected NHL 27 set.
/// </summary>
public static class RegisteredKitSource
{
    public sealed record Candidate(string Folder, int RegistryVersions, int MappedTextures, int ArrayLayers, int MinRows,
        bool EveryCopyFits = true, bool CanExpand = false);

    /// <summary>Pure selection rule, kept separate so incomplete-source choices can be regression-tested.</summary>
    public static string Choose(string sourceFolder, int requiredTextures, int requiredArrayLayers,
        int registryVersions, IReadOnlyList<Candidate> candidates)
    {
        string source = sourceFolder.TrimEnd('/');
        string parent = source[..source.LastIndexOf('/')];
        bool AlreadyFits(Candidate candidate) => candidate.EveryCopyFits && candidate.ArrayLayers >= requiredArrayLayers;
        var eligible = candidates.Where(c => c.Folder.StartsWith(parent + "/", StringComparison.Ordinal)
                && c.RegistryVersions == registryVersions && c.MappedTextures == requiredTextures
                && (AlreadyFits(c) || c.CanExpand) && c.MinRows > 0)
            // The team's own mapped kit wins whenever it can hold the art (growing if needed): another kit's layout
            // (Boston five's second crest, Nashville twentythree's extra chest logo) puts logos in the wrong places.
            .OrderByDescending(c => c.Folder.TrimEnd('/') == source)
            .ThenByDescending(AlreadyFits)
            .ThenByDescending(c => c.MinRows)
            .ThenByDescending(c => c.Folder.EndsWith("/home", StringComparison.Ordinal))
            .ToList();
        if (eligible.Count == 0)
            throw new InvalidDataException($"{sourceFolder}: no complete registered same-team kit can hold {requiredTextures} textures and {requiredArrayLayers} branding layers, with a verified blank-slot expansion where needed.");
        var best = eligible[0];
        bool home = best.Folder.EndsWith("/home", StringComparison.Ordinal);
        if (eligible.Skip(1).Any(c => AlreadyFits(c) == AlreadyFits(best) && c.MinRows == best.MinRows
                && c.Folder.EndsWith("/home", StringComparison.Ordinal) == home))
            throw new InvalidDataException($"{sourceFolder}: more than one same-team source has the best layout; review it instead of guessing.");
        return best.Folder;
    }

    /// <summary>Normalizes source prefixes and source slot names; target names and NHL 27 artwork remain unchanged.</summary>
    public static List<NewUniformAddition> Normalize(IEnumerable<NewUniformAddition> additions, BundleIndex index, Action<string>? progress = null)
    {
        var tables = index.ResByName[PlayerTeamRegistry.ResourceName]
            .GroupBy(r => (r.Data.Cas, r.Data.Offset))
            .Select(g => Decode(BundleIndex.ReadBytes(g.First().Data))).ToList();
        if (tables.Count == 0) throw new InvalidDataException("No player_team_brt table was found.");
        var result = additions.ToList();
        foreach (var addition in result)
        {
            string source = addition.SourcePrefix.TrimEnd('/');
            string parent = source[..source.LastIndexOf('/')];
            string fromId = KitRegistration.Identity(source);
            (NewUniformTexture Texture, string Name)[] Mapped(string folder, string toId) => addition.Textures
                .Select(texture => (texture, Map(texture.SourceSlot, source, fromId, folder, toId))).ToArray();
            var original = Mapped(source, fromId);
            bool reviewed = addition.HotspotLayouts.Count > 0;
            var requiredTexture = original.FirstOrDefault(item => IsJerseyColor(item.Name)).Texture;
            int requiredLayers = Math.Max(RequiredLayers(requiredTexture), reviewed ? requiredTexture?.DonorRoles?.Count ?? 0 : 0);
            bool registered = tables.All(raw => PlayerTeamRegistry.Rows(raw, source).Count > 0);
            bool originalFits = Fits(index, original) && (!reviewed || FitsReviewed(index, original));
            // Prefer the exact reviewed kit. If its source is not registered everywhere, use the established same-team
            // registered fallback and remap the explicit hotspot paths with precisely the same source transformation.
            if (registered && (originalFits || reviewed && ExpandableReviewed(index, original))) continue;
            var candidates = tables.SelectMany(raw => PlayerTeamRegistry.Folders(raw, parent))
                .Distinct(StringComparer.Ordinal)
                .Where(folder => result.All(a => !string.Equals(a.TargetPrefix.TrimEnd('/'), folder, StringComparison.Ordinal)))
                .Select(folder =>
                {
                    string toId = KitRegistration.Identity(folder);
                    var mapped = Mapped(folder, toId);
                    var rows = tables.Select(raw => PlayerTeamRegistry.Rows(raw, folder).Count).ToArray();
                    int minLayers = mapped.Where(item => IsJerseyColor(item.Name))
                        .SelectMany(item => index.ResByName.GetValueOrDefault(item.Name) ?? [])
                        .Select(Layers).DefaultIfEmpty(0).Min();
                    bool fits = Fits(index, mapped) && (!reviewed || FitsReviewed(index, mapped));
                    bool complete = mapped.All(item => index.ResByName.ContainsKey(item.Name));
                    bool expandable = reviewed ? ExpandableReviewed(index, mapped) : Expandable(index, mapped);
                    return new Candidate(folder, rows.Count(count => count > 0), mapped.Count(item => index.ResByName.ContainsKey(item.Name)),
                        minLayers, rows.Min(), fits, complete && !fits && expandable);
                }).ToList();
            string selected = Choose(source, addition.Textures.Count, requiredLayers, tables.Count, candidates);
            string selectedId = KitRegistration.Identity(selected);
            foreach (var texture in addition.Textures)
            {
                string mapped = Map(texture.SourceSlot, source, fromId, selected, selectedId);
                // Before planning (Add to game) no target exists yet; the planner names it from the corrected source.
                if (TargetIsPlanned(texture.TargetSlot, addition.TargetPrefix)
                    && !string.Equals(texture.TargetSlot, NewUniformAdditionPlanner.MapTargetSlot(mapped, selected, addition.TargetPrefix), StringComparison.Ordinal))
                    throw new InvalidDataException($"{texture.SourceSlot}: the same-team source correction would change its target name.");
                texture.SourceSlot = mapped;
                texture.Artwork = texture.Artwork with { Slot = mapped };
            }
            foreach (var hotspots in addition.HotspotLayouts)
            {
                string mapped = Map(hotspots.SourceSlot, source, fromId, selected, selectedId);
                if (TargetIsPlanned(null, addition.TargetPrefix)
                    && !string.Equals(NewUniformAdditionPlanner.MapTargetSlot(mapped, selected, addition.TargetPrefix),
                        UniformPhysicalLayouts.TargetStamp(addition, hotspots.SourceSlot), StringComparison.Ordinal))
                    throw new InvalidDataException($"{hotspots.SourceSlot}: source normalization would change its target stamp.");
                hotspots.SourceSlot = mapped;
            }
            var choice = candidates.Single(c => c.Folder == selected);
            progress?.Invoke($"source layout: {addition.Team} {addition.SourceKit}: {source.Split('/')[^1]} -> {selected.Split('/')[^1]} "
                + $"(same team, registered in every table, {(choice.EveryCopyFits ? "every copy fits" : "safe blank-slot expansion verified")})");
            addition.SourcePrefix = selected;
        }
        return result;
    }

    static bool IsJerseyColor(string name) => name.Contains("/branding_jersey_", StringComparison.Ordinal)
        && name.EndsWith("_color_array", StringComparison.Ordinal);

    static int RequiredLayers(NewUniformTexture? texture) => texture is null ? 0 : texture.RequiredRoles?.Count
        ?? (texture.Artwork.Layers is { Count: > 0 } layers ? layers.Keys.Max() + 1 : 1);

    static bool Fits(BundleIndex index, IEnumerable<(NewUniformTexture Texture, string Name)> mapped) => mapped.All(item =>
        index.ResByName.GetValueOrDefault(item.Name) is { Count: > 0 } copies
        && copies.All(copy => Layers(copy) >= RequiredLayers(item.Texture)));

    static bool FitsReviewed(BundleIndex index, IEnumerable<(NewUniformTexture Texture, string Name)> mapped) => mapped.All(item =>
        index.ResByName.GetValueOrDefault(item.Name) is { Count: > 0 } copies
        && copies.All(copy => Layers(copy) >= Math.Max(RequiredLayers(item.Texture), item.Texture.DonorRoles?.Count ?? 0)));

    static bool ExpandableReviewed(BundleIndex index, IEnumerable<(NewUniformTexture Texture, string Name)> mapped)
    {
        foreach (var item in mapped)
        {
            int required = Math.Max(RequiredLayers(item.Texture), item.Texture.DonorRoles?.Count ?? 0);
            var resources = index.ResByName.GetValueOrDefault(item.Name) ?? [];
            if (resources.Count == 0) return false;
            if (resources.All(copy => Layers(copy) >= required)) continue;
            string role = item.Name.EndsWith("_color_array", StringComparison.Ordinal) ? "color"
                : item.Name.EndsWith("_normal_array", StringComparison.Ordinal) ? "normal"
                : item.Name.EndsWith("_coeff_array", StringComparison.Ordinal) ? "coeff" : "";
            if (role.Length == 0) return false;
            var slots = TextureSlots.ReadAll(index.Game, index, item.Name);
            foreach (var slot in slots.Where(s => Ps4Codec.Slices(s.Res.Header) < required))
                if (InstalledArrayExpansion.StructuralDonor(index.Game, index, slot, slots, role, required) is null) return false;
        }
        return true;
    }

    static bool Expandable(BundleIndex index, IEnumerable<(NewUniformTexture Texture, string Name)> mapped)
    {
        foreach (var item in mapped)
        {
            int required = RequiredLayers(item.Texture);
            var resources = index.ResByName.GetValueOrDefault(item.Name) ?? [];
            if (resources.Count == 0) return false;
            if (resources.All(copy => Layers(copy) >= required)) continue;
            string role = item.Name.EndsWith("_color_array", StringComparison.Ordinal) ? "color"
                : item.Name.EndsWith("_normal_array", StringComparison.Ordinal) ? "normal"
                : item.Name.EndsWith("_coeff_array", StringComparison.Ordinal) ? "coeff" : "";
            if (role.Length == 0) return false;
            var slots = TextureSlots.ReadAll(index.Game, index, item.Name);
            foreach (var slot in slots.Where(s => Ps4Codec.Slices(s.Res.Header) < required))
                if (InstalledArrayExpansion.StructuralDonor(index.Game, index, slot, slots, role, required) is null) return false;
        }
        return true;
    }

    static int Layers(BundleIndex.Res resource) => Ps4Codec.Slices(Decode(BundleIndex.ReadBytes(resource.Data)));

    static string Map(string sourceSlot, string source, string fromId, string folder, string toId)
    {
        string prefix = source + "/";
        if (!sourceSlot.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException($"{sourceSlot}: texture is outside its uniform source folder {source}.");
        return folder + "/" + sourceSlot[prefix.Length..].Replace(fromId, toId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a target name exists to compare against. Add to game normalizes sources before the planner allocates any
    /// target, so an empty prefix means "not planned yet", never a broken folder (All teams once failed on exactly that).
    /// </summary>
    internal static bool TargetIsPlanned(string? targetSlot, string? targetPrefix) =>
        !string.IsNullOrWhiteSpace(targetPrefix) && (targetSlot is null || !string.IsNullOrWhiteSpace(targetSlot));

    static byte[] Decode(byte[] stored)
    {
        try { return ChunkBlocks.Parse(stored).Decode(); }
        catch (InvalidDataException) { return stored; }
    }
}
