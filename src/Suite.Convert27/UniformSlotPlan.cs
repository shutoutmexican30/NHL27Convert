namespace Suite.Convert27;

/// <summary>A texture resource's name, array depth, and ordered semantic layer roles when known.</summary>
public sealed record UniformSlotState(string Name, int Layers = 1, IReadOnlyList<string>? Roles = null);

/// <summary>One eligible structural donor. Same team and kit role are preferred, never selected by depth alone.</summary>
public sealed record UniformDonor(string Prefix, IEnumerable<UniformSlotState> Resources);

/// <summary>
/// One slot the converted uniform needs. Artwork is the team's own converted image; layer artwork is used for arrays.
/// RequiredRoles, when supplied, is the complete ordered output layout (crest, shoulders, sponsor, brand, shield, tag).
/// A donor may supply missing structure, never visible content.
/// </summary>
public sealed record UniformSlotNeed(
    string Name,
    int RequiredLayers = 1,
    string? Artwork = null,
    IReadOnlyDictionary<int, string>? LayerArtwork = null,
    IReadOnlyList<string>? RequiredRoles = null);

public enum UniformSlotAction
{
    ReuseTeamSlot,
    AddMissingStructure,
    ExpandTeamArray,
}

public sealed record PlannedUniformSlot(
    string TargetName,
    string? DonorName,
    UniformSlotAction Action,
    int ExistingLayers,
    int ResultLayers,
    int PreservedTeamLayers);

/// <summary>
/// Plans only the structure a converted uniform is missing. Existing team uniforms, fabrics and unrelated resources are
/// outside the plan by construction. If a target resource is absent, the same relative resource must exist beneath the
/// donor prefix and every output layer must have team artwork; otherwise preflight fails rather than showing donor art.
/// </summary>
public sealed class UniformSlotPlan
{
    public IReadOnlyList<PlannedUniformSlot> Slots { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool IsValid => Errors.Count == 0;
    public IEnumerable<PlannedUniformSlot> StructuralChanges => Slots.Where(s => s.Action != UniformSlotAction.ReuseTeamSlot);

    UniformSlotPlan(List<PlannedUniformSlot> slots, List<string> errors) { Slots = slots; Errors = errors; }

    public static UniformSlotPlan Build(
        string donorPrefix,
        string targetPrefix,
        IEnumerable<UniformSlotState> donorResources,
        IEnumerable<UniformSlotState> targetResources,
        IEnumerable<UniformSlotNeed> requiredSlots)
    {
        donorPrefix = NormalizePrefix(donorPrefix);
        targetPrefix = NormalizePrefix(targetPrefix);
        if (string.Equals(donorPrefix, targetPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Donor and target prefixes must differ.");

        var donor = Index(donorPrefix, donorResources, "donor");
        var target = Index(targetPrefix, targetResources, "target");
        string donorIdentity = FamilyIdentity(donorPrefix);
        string targetIdentity = FamilyIdentity(targetPrefix);
        var slots = new List<PlannedUniformSlot>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var need in requiredSlots)
        {
            string name = need.Name.Trim().Replace('\\', '/').TrimStart('/');
            if (!name.StartsWith(targetPrefix, StringComparison.Ordinal))
            {
                errors.Add($"{name}: required slot is outside the target uniform folder; shared resources cannot be cloned.");
                continue;
            }
            if (!seen.Add(name))
            {
                errors.Add($"{name}: required slot is listed more than once.");
                continue;
            }
            if (need.RequiredLayers < 1)
            {
                errors.Add($"{name}: at least one layer is required.");
                continue;
            }

            string relative = name[targetPrefix.Length..];
            string donorName = donorPrefix + relative.Replace(targetIdentity, donorIdentity, StringComparison.Ordinal);
            target.TryGetValue(name, out int existing);
            donor.TryGetValue(donorName, out int donorLayers);
            bool isArray = IsArray(name) || need.LayerArtwork is not null || need.RequiredLayers > 1;

            if (existing >= need.RequiredLayers)
            {
                slots.Add(new PlannedUniformSlot(name, null, UniformSlotAction.ReuseTeamSlot,
                    existing, existing, existing));
                continue;
            }
            if (existing > 0 && !isArray)
            {
                errors.Add($"{name}: only branding arrays can be expanded; the team's texture remains untouched.");
                continue;
            }
            if (donorLayers < need.RequiredLayers)
            {
                errors.Add(donorLayers == 0
                    ? $"{name}: no matching structural donor exists at {donorName}."
                    : $"{name}: donor has {donorLayers} layers but {need.RequiredLayers} are required.");
                continue;
            }

            int preserved = existing > 0 ? existing : 0;
            if (isArray)
            {
                for (int layer = preserved; layer < need.RequiredLayers; layer++)
                    if (need.LayerArtwork is null || !need.LayerArtwork.TryGetValue(layer, out string? art) || string.IsNullOrWhiteSpace(art))
                        errors.Add($"{name} layer {layer}: team artwork is required; donor artwork cannot be used as a fallback.");
            }
            else if (string.IsNullOrWhiteSpace(need.Artwork))
            {
                errors.Add($"{name}: team artwork is required; donor artwork cannot be used as a fallback.");
            }

            slots.Add(new PlannedUniformSlot(name, donorName,
                existing > 0 ? UniformSlotAction.ExpandTeamArray : UniformSlotAction.AddMissingStructure,
                existing, need.RequiredLayers, preserved));
        }

        return new UniformSlotPlan(slots, errors);
    }

    /// <summary>
    /// Selects structure by an exact ordered role layout. Existing team layers must be a prefix of that layout, so an
    /// expansion cannot duplicate or shift a crest. Donors are ranked same team + kit, same team, same kit, then others.
    /// </summary>
    public static UniformSlotPlan BuildRoleAware(
        string targetPrefix,
        IEnumerable<UniformDonor> donors,
        IEnumerable<UniformSlotState> targetResources,
        IEnumerable<UniformSlotNeed> requiredSlots)
    {
        targetPrefix = NormalizePrefix(targetPrefix);
        var targetList = targetResources.ToList();
        _ = Index(targetPrefix, targetList, "target");
        var targetStates = targetList.GroupBy(r => r.Name.Trim().Replace('\\', '/').TrimStart('/'), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Layers).First(), StringComparer.Ordinal);
        var candidates = donors.Select(d =>
        {
            string prefix = NormalizePrefix(d.Prefix);
            var resources = d.Resources.ToList();
            return (Prefix: prefix, Resources: resources, Index: Index(prefix, resources, "donor"),
                Team: FamilyParts(prefix).Team, Kit: FamilyParts(prefix).Kit);
        }).Where(d => d.Prefix != targetPrefix).ToList();
        var targetParts = FamilyParts(targetPrefix);
        var slots = new List<PlannedUniformSlot>();
        var errors = new List<string>();

        foreach (var need in requiredSlots)
        {
            string name = need.Name.Trim().Replace('\\', '/').TrimStart('/');
            if (need.RequiredRoles is { } roles)
            {
                if (roles.Count == 0 || roles.Count != need.RequiredLayers || roles.Any(string.IsNullOrWhiteSpace))
                {
                    errors.Add($"{name}: RequiredRoles must describe every output layer in order.");
                    continue;
                }
                if (targetStates.TryGetValue(name, out var existingState))
                {
                    if (existingState.Roles is null || existingState.Roles.Count != existingState.Layers)
                    {
                        errors.Add($"{name}: the existing team's ordered role map is missing; depth-only expansion is refused.");
                        continue;
                    }
                    if (!existingState.Roles.SequenceEqual(roles.Take(existingState.Layers), StringComparer.Ordinal))
                    {
                        errors.Add($"{name}: existing team roles would be duplicated or shifted; rebuild the map instead of appending by depth.");
                        continue;
                    }
                }
            }
            targetStates.TryGetValue(name, out var targetState);
            int existing = targetState?.Layers ?? 0;
            if (existing >= need.RequiredLayers)
            {
                slots.Add(new PlannedUniformSlot(name, null, UniformSlotAction.ReuseTeamSlot, existing, existing, existing));
                continue;
            }

            string relative = name.StartsWith(targetPrefix, StringComparison.Ordinal) ? name[targetPrefix.Length..] : "";
            if (relative.Length == 0)
            {
                errors.Add($"{name}: required slot is outside the target uniform folder; shared resources cannot be cloned.");
                continue;
            }
            string targetIdentity = FamilyIdentity(targetPrefix);
            var eligible = candidates.Select(d =>
                {
                    string donorIdentity = FamilyIdentity(d.Prefix);
                    string donorName = d.Prefix + relative.Replace(targetIdentity, donorIdentity, StringComparison.Ordinal);
                    d.Index.TryGetValue(donorName, out int layers);
                    var state = d.Resources.Where(r => r.Name.Trim().Replace('\\', '/').TrimStart('/') == donorName)
                        .OrderBy(r => r.Layers).FirstOrDefault();
                    bool rolesMatch = need.RequiredRoles is null
                        || (state?.Roles is not null && state.Roles.SequenceEqual(need.RequiredRoles, StringComparer.Ordinal));
                    int rank = d.Team == targetParts.Team
                        ? (d.Kit == targetParts.Kit ? 0 : 1)
                        : (d.Kit == targetParts.Kit ? 2 : 3);
                    return (d.Prefix, d.Resources, DonorName: donorName, Layers: layers, RolesMatch: rolesMatch, Rank: rank);
                })
                .Where(d => d.Layers >= need.RequiredLayers && d.RolesMatch)
                .OrderBy(d => d.Rank).ThenBy(d => d.Prefix, StringComparer.Ordinal).ToList();
            if (eligible.Count == 0)
            {
                errors.Add($"{name}: no donor provides {need.RequiredLayers} layers with the required ordered semantic roles; apply is blocked.");
                continue;
            }
            var selected = eligible[0];
            var one = Build(selected.Prefix, targetPrefix, selected.Resources, targetList, [need]);
            slots.AddRange(one.Slots);
            errors.AddRange(one.Errors);
        }
        return new UniformSlotPlan(slots, errors);
    }

    /// <summary>
    /// Fits the team's complete artwork into a donor's actual slot order. Every required role must have a distinct donor
    /// slot. Unused optional donor slots (sponsor, shoulder, obsolete back tag or existing blank) receive explicit
    /// transparent artwork; required team roles can never be padded. The exact-order validator checks the returned need.
    /// </summary>
    public static (UniformSlotPlan Plan, UniformSlotNeed Need) BuildRoleMapped(
        string targetPrefix,
        IEnumerable<UniformDonor> donors,
        IEnumerable<UniformSlotState> targetResources,
        UniformSlotNeed need,
        Func<int, string> blankArtwork)
    {
        targetPrefix = NormalizePrefix(targetPrefix);
        var fallback = BuildRoleAware(targetPrefix, donors, targetResources, [need]);
        if (need.RequiredRoles is not { } required || need.LayerArtwork is not { } artwork) return (fallback, need);
        string relative = need.Name.StartsWith(targetPrefix, StringComparison.Ordinal) ? need.Name[targetPrefix.Length..] : "";
        if (relative.Length == 0) return (fallback, need);
        string targetIdentity = FamilyIdentity(targetPrefix), targetTeam = FamilyParts(targetPrefix).Team, targetKit = FamilyParts(targetPrefix).Kit;
        var options = new List<(UniformDonor Donor, UniformSlotState State, int[] Sources, int Padding, int Exact, int Rank)>();
        foreach (var donor in donors)
        {
            string prefix = NormalizePrefix(donor.Prefix);
            if (prefix == targetPrefix) continue;
            string donorName = prefix + relative.Replace(targetIdentity, FamilyIdentity(prefix), StringComparison.Ordinal);
            var state = donor.Resources.FirstOrDefault(r => r.Name == donorName && r.Roles is not null);
            if (state?.Roles is not { } donorRoles || donorRoles.Count < required.Count) continue;
            var queues = required.Select((role, index) => (role, index)).GroupBy(x => x.role)
                .ToDictionary(g => g.Key, g => new Queue<int>(g.Select(x => x.index)), StringComparer.Ordinal);
            var sources = new int[donorRoles.Count];
            Array.Fill(sources, -1);
            bool valid = true;
            for (int i = 0; i < donorRoles.Count; i++)
            {
                if (queues.TryGetValue(donorRoles[i], out var queue) && queue.Count > 0) sources[i] = queue.Dequeue();
                else if (donorRoles[i] is not (SlotRoles.EpwRole or "sponsor" or "shoulder" or "blank")) { valid = false; break; }
            }
            if (!valid || queues.Values.Any(q => q.Count > 0)) continue;
            var parts = FamilyParts(prefix);
            int rank = parts.Team == targetTeam ? (parts.Kit == targetKit ? 0 : 1) : (parts.Kit == targetKit ? 2 : 3);
            options.Add((donor, state, sources, sources.Count(i => i < 0), donorRoles.SequenceEqual(required) ? 0 : 1, rank));
        }
        var selected = options.OrderBy(o => o.Padding).ThenBy(o => o.Exact).ThenBy(o => o.Rank)
            .ThenBy(o => NormalizePrefix(o.Donor.Prefix), StringComparer.Ordinal).FirstOrDefault();
        if (selected.State is null) return (fallback, need);
        var mappedArt = new Dictionary<int, string>();
        var mappedRoles = new List<string>();
        for (int i = 0; i < selected.Sources.Length; i++)
        {
            int source = selected.Sources[i];
            string role = selected.State.Roles![i];
            string path = source >= 0 ? artwork[source] : blankArtwork(i);
            if (string.IsNullOrWhiteSpace(path)) return (fallback, need);
            mappedArt[i] = path;
            mappedRoles.Add(source >= 0 ? role : "blank");
        }
        var mappedNeed = need with { RequiredLayers = mappedRoles.Count, RequiredRoles = mappedRoles, LayerArtwork = mappedArt };
        var validatedDonor = selected.Donor with
        {
            Resources = selected.Donor.Resources.Select(r => r.Name == selected.State.Name ? r with { Roles = mappedRoles } : r).ToList(),
        };
        var one = BuildRoleAware(targetPrefix, [validatedDonor], targetResources, [mappedNeed]);
        return (one, mappedNeed);
    }

    public void ThrowIfInvalid()
    {
        if (!IsValid) throw new InvalidOperationException("Uniform slot preflight failed:\n" + string.Join("\n", Errors));
    }

    static Dictionary<string, int> Index(string prefix, IEnumerable<UniformSlotState> resources, string label)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            string name = resource.Name.Trim().Replace('\\', '/').TrimStart('/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                throw new ArgumentException($"{label} resource {name} is outside {prefix}.");
            if (resource.Layers < 1) throw new ArgumentException($"{label} resource {name} has an invalid layer count.");
            // Duplicate Data/Patch declarations can differ. The shallowest copy determines whether every copy is sufficient.
            result[name] = result.TryGetValue(name, out int old) ? Math.Min(old, resource.Layers) : resource.Layers;
        }
        return result;
    }

    static bool IsArray(string name) => name.EndsWith("_color_array", StringComparison.Ordinal)
        || name.EndsWith("_normal_array", StringComparison.Ordinal)
        || name.EndsWith("_coeff_array", StringComparison.Ordinal);

    static string FamilyIdentity(string prefix)
    {
        var parts = prefix.Trim('/').Split('/');
        if (parts.Length < 3) throw new ArgumentException($"Uniform family prefix is incomplete: {prefix}");
        // …/<team>/<manufacturer>/<variant>/; resource leaves repeat <team>_<variant>.
        return parts[^3] + "_" + parts[^1];
    }

    static (string Team, string Kit) FamilyParts(string prefix)
    {
        var parts = prefix.Trim('/').Split('/');
        if (parts.Length < 3) throw new ArgumentException($"Uniform family prefix is incomplete: {prefix}");
        return (parts[^3], parts[^1]);
    }

    static string NormalizePrefix(string prefix)
    {
        prefix = prefix.Trim().Replace('\\', '/').Trim('/') + "/";
        if (prefix.Length == 1) throw new ArgumentException("A resource-folder prefix is required.", nameof(prefix));
        return prefix;
    }
}
