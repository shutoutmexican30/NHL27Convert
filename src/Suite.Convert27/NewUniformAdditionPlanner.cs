using Suite.Core;
using Suite.Rosters;

namespace Suite.Convert27;

/// <summary>A validated set of independent target assets and their coordinated spare jersey-row allocations.</summary>
public sealed record NewUniformAdditionPlan(
    IReadOnlyList<NewUniformAddition> Additions,
    JerseyRowPlan JerseyRows)
{
    public int UniformCount => Additions.Count;
    public int TextureCount => Additions.Sum(a => a.Textures.Count);
    public IEnumerable<string> TargetSlots => Additions.SelectMany(a => a.Textures.Select(t => t.TargetSlot));

    public ModFile ToMod(string name, string? comment = null) => new()
    {
        Name = name,
        Kind = NewUniformAddition.ModKind,
        Comment = comment ?? "Adds independent uniforms and spare jersey rows; never replaces an existing uniform.",
        Additions = Additions.ToList(),
    };

    public string Summary => $"{UniformCount} new uniform(s), {TextureCount} target texture(s), "
        + $"{JerseyRows.InsertedRows} new database row(s) across {JerseyRows.ChangedDatabases.Count} database(s).";
}

/// <summary>
/// Allocates unused variants and explicit target resource names without writing anything. Existing home/away/alternate
/// rows and assets are outside the plan. Any target resource that already exists is a hard failure, not an overwrite.
/// </summary>
public static class NewUniformAdditionPlanner
{
    public static NewUniformAdditionPlan Build(
        IEnumerable<NewUniformAddition> additions,
        IReadOnlyDictionary<string, byte[]> sourceDatabases,
        string metaXml,
        Func<string, bool> targetResourceExists)
    {
        var input = additions.ToList();
        if (input.Count == 0) throw new InvalidDataException("No new uniforms were requested.");
        if (sourceDatabases.Count == 0) throw new InvalidDataException("No jersey databases were supplied.");
        string primaryName = sourceDatabases.ContainsKey("nhl_game") ? "nhl_game" : sourceDatabases.Keys.Order().First();
        var primary = T3Db.Load((byte[])sourceDatabases[primaryName].Clone(), metaXml);
        if (!primary.Tables.TryGetValue(JerseyRowPlanner.TableName, out var table))
            throw new InvalidDataException($"{primaryName}: {JerseyRowPlanner.TableName} is missing.");

        var roles = new HashSet<(int Team, int Style, string Kit)>();
        var ordered = input.OrderBy(a => a.TeamId).ThenBy(a => a.IsLight).ThenBy(a => a.SourceKit, StringComparer.Ordinal).ToList();
        foreach (var addition in ordered)
        {
            addition.Team = NormalizeTeam(addition.Team);
            addition.SourcePrefix = NormalizePrefix(addition.SourcePrefix);
            addition.SourceKit = addition.SourceKit.Trim().ToLowerInvariant();
            if (addition.TeamId < 0 || addition.Team.Length == 0) throw new InvalidDataException("Every addition needs a valid team and team ID.");
            if (addition.SourceKit is not ("home" or "away"))
                throw new InvalidDataException($"{addition.Team}: SourceKit must be home or away, not '{addition.SourceKit}'.");
            if (addition.IsLight != (addition.SourceKit == "away"))
                throw new InvalidDataException($"{addition.Team} {addition.SourceKit}: the light/dark role does not match the source kit.");
            if (addition.Style != 2) throw new InvalidDataException($"{addition.Team}: only skater style 2 is supported.");
            if (!roles.Add((addition.TeamId, addition.Style, addition.SourceKit)))
                throw new InvalidDataException($"{addition.Team}: duplicate {addition.SourceKit} addition in this batch.");
            if (addition.Textures.Count == 0) throw new InvalidDataException($"{addition.Team} {addition.SourceKit}: no source textures were supplied.");
            var parts = addition.SourcePrefix.Trim('/').Split('/');
            if (parts.Length < 3 || !string.Equals(parts[^3], addition.Team, StringComparison.Ordinal)
                    && !string.Equals(addition.SourcePrefix.TrimEnd('/') + "/", KitDonor.Prefix, StringComparison.Ordinal))
                throw new InvalidDataException($"{addition.Team}: SourcePrefix is not that team's complete uniform folder.");
        }

        var used = new Dictionary<(int Team, int Style), HashSet<int>>();
        foreach (var addition in ordered)
        {
            var key = (addition.TeamId, addition.Style);
            if (!used.TryGetValue(key, out var variants))
            {
                variants = Enumerable.Range(0, table.InUse)
                    .Where(r => primary.GetInt(table, r, "teamid") == addition.TeamId
                        && primary.GetInt(table, r, "style") == addition.Style)
                    .Select(r => (int)primary.GetInt(table, r, "variant")).ToHashSet();
                used.Add(key, variants);
            }
            int variant = addition.TargetVariant;
            if (variant == 0)
            {
                variant = Enumerable.Range(2, 24).Reverse().FirstOrDefault(v => !variants.Contains(v));
                if (variant < 2) throw new InvalidDataException(
                    $"{addition.Team}: no unused variant remains in the reserved range 2–25.");
            }
            if (variant is < 2 or > 25) throw new InvalidDataException(
                $"{addition.Team}: new-uniform variant {variant} is outside the reserved range 2–25.");
            if (!variants.Add(variant)) throw new InvalidDataException(
                $"{addition.Team}: variant {variant} already has a jersey row or is allocated to another addition.");
            addition.TargetVariant = variant;
            string expectedPrefix = $"{TextureName.SkaterPrefix}nhl/{addition.Team}/adidas/{TextureName.Numbers[variant]}/";
            if (!string.IsNullOrWhiteSpace(addition.TargetPrefix)
                && !string.Equals(NormalizePrefix(addition.TargetPrefix), expectedPrefix, StringComparison.Ordinal))
                throw new InvalidDataException($"{addition.Team}: TargetPrefix does not match allocated variant {variant}.");
            addition.TargetPrefix = expectedPrefix;
        }

        var targetNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var addition in ordered)
        {
            var textureTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var texture in addition.Textures)
            {
                texture.SourceSlot = NormalizeName(texture.SourceSlot);
                string expected = MapTargetSlot(texture.SourceSlot, addition.SourcePrefix, addition.TargetPrefix);
                if (!string.IsNullOrWhiteSpace(texture.TargetSlot)
                    && !string.Equals(NormalizeName(texture.TargetSlot), expected, StringComparison.Ordinal))
                    throw new InvalidDataException($"{texture.SourceSlot}: its stored target name does not match the new uniform folder.");
                texture.TargetSlot = expected;
                if (texture.Artwork.Slot.Length != 0 && NormalizeName(texture.Artwork.Slot) != texture.SourceSlot)
                    throw new InvalidDataException($"{texture.SourceSlot}: artwork belongs to a different source slot.");
                texture.Artwork = texture.Artwork with { Slot = texture.SourceSlot };
                if (!textureTargets.Add(expected) || !targetNames.Add(expected))
                    throw new InvalidDataException($"Two source textures map to the same new resource: {expected}");
                if (targetResourceExists(expected)) throw new InvalidDataException(
                    $"Target resource already exists and will not be replaced: {expected}");
                if (texture.RequiredRoles is { } requiredRoles && (requiredRoles.Count == 0 || requiredRoles.Any(string.IsNullOrWhiteSpace)))
                    throw new InvalidDataException($"{expected}: the ordered role map is incomplete.");
            }
        }

        var requests = ordered.Select(a => new NewJerseyRowRequest(a.TeamId, a.TargetVariant, a.IsLight, a.Style)).ToList();
        var rows = JerseyRowPlanner.Build(sourceDatabases, metaXml, requests);
        return new NewUniformAdditionPlan(ordered, rows);
    }

    public static string MapTargetSlot(string sourceSlot, string sourcePrefix, string targetPrefix)
    {
        sourceSlot = NormalizeName(sourceSlot);
        sourcePrefix = NormalizePrefix(sourcePrefix);
        targetPrefix = NormalizePrefix(targetPrefix);
        if (!sourceSlot.StartsWith(sourcePrefix, StringComparison.Ordinal))
            throw new InvalidDataException($"{sourceSlot}: source texture is outside {sourcePrefix}; shared resources cannot be cloned into a team uniform.");
        string sourceIdentity = FamilyIdentity(sourcePrefix), targetIdentity = FamilyIdentity(targetPrefix);
        return targetPrefix + sourceSlot[sourcePrefix.Length..].Replace(sourceIdentity, targetIdentity, StringComparison.Ordinal);
    }

    internal static string FamilyIdentity(string prefix)
    {
        var parts = prefix.Trim('/').Split('/');
        if (parts.Length < 3) throw new InvalidDataException($"Uniform family prefix is incomplete: {prefix}");
        return parts[^3] + "_" + parts[^1];
    }

    static string NormalizeTeam(string value) => value.Trim().ToLowerInvariant();
    static string NormalizeName(string value) => value.Trim().Replace('\\', '/').TrimStart('/');
    static string NormalizePrefix(string value)
    {
        string prefix = NormalizeName(value).TrimEnd('/') + "/";
        if (prefix == "/") throw new InvalidDataException("A complete uniform folder is required.");
        return prefix;
    }
}
