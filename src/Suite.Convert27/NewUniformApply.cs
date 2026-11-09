using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

namespace Suite.Convert27;

/// <summary>Coordinates new assets and new jersey rows under one outer game backup. Planning is read-only.</summary>
public static class NewUniformApply
{
    public sealed record Prepared(
        string Target,
        NewUniformAdditionPlan AdditionPlan,
        IReadOnlyList<NewUniformAssetPlan> AssetPlans,
        IReadOnlyDictionary<string, string> DatabaseFiles,
        ModFile? Mod = null)
    {
        public int CloneCount => AssetPlans.Sum(p => p.Clones.Count);
        public string Summary => AdditionPlan.Summary + " Each uniform is a whole clone of its own team's kit; "
            + string.Join(", ", AdditionPlan.JerseyRows.ChangedDatabases) + ".";
    }

    public static List<string> SourceProblems(IEnumerable<ModFile> mods)
    {
        var problems = new List<string>();
        foreach (var mod in mods.Where(m => m.Kind == NewUniformAddition.ModKind))
        foreach (var addition in mod.Additions ?? [])
        foreach (var texture in addition.Textures)
        {
            var art = texture.Artwork;
            if (art.Layers is { Count: > 0 })
            {
                foreach (var (layer, source) in art.Layers)
                    if (!File.Exists(source)) problems.Add($"{mod.Name}: {addition.Team} {addition.SourceKit}, {texture.SourceSlot} layer {layer}: missing {source}");
            }
            else if (art.Source.Length > 0 && !File.Exists(art.Source))
                problems.Add($"{mod.Name}: {addition.Team} {addition.SourceKit}, {texture.SourceSlot}: missing {art.Source}");
        }
        return problems;
    }

    public static Prepared Prepare(
        SuiteSettings settings,
        ModFile mod,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? roleMaps = null,
        Action<string>? progress = null,
        SlotMapStore? slotMaps = null,
        bool planAssets = false)
    {
        if (mod.Kind != NewUniformAddition.ModKind || mod.Additions is not { Count: > 0 })
            throw new InvalidDataException("The selected mod contains no new uniforms.");
        var missing = SourceProblems([mod]);
        if (missing.Count > 0) throw new InvalidDataException("New-uniform artwork is missing:\n" + string.Join("\n", missing.Take(20)));
        string target = Path.GetFullPath(settings.ApplyTargetDir).TrimEnd(Path.DirectorySeparatorChar);
        progress?.Invoke("Reading jersey databases and the target asset index...");
        var legacy = new LegacyFiles(target, settings);
        string metaPath = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "meta-cache")), "nhl_game-meta.xml");
        File.WriteAllBytes(metaPath, legacy.Read("gamemodes/database/t3db/nhl_game-meta.xml"));
        var sources = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["nhl_game"] = legacy.Read(RosterSyncRun.LegacyName("nhl_game")),
            ["initial_experience"] = legacy.Read(RosterSyncRun.LegacyName("initial_experience")),
        };
        var index = BundleIndex.Build(target, progress);
        bool Exists(string name) => index.ResByName.ContainsKey(name) || index.EbxByName.ContainsKey(name);
        var additionPlan = NewUniformAdditionPlanner.Build(mod.Additions, sources, metaPath, Exists);
        var assets = new List<NewUniformAssetPlan>();
        if (planAssets)
        {
            // Only the retired donor-based diagnostics ask for this. Installs use FullKitInstall.
            roleMaps ??= BuildRoleMaps(index, slotMaps ?? SlotMapStore.Load());
            foreach (var addition in additionPlan.Additions)
            {
                progress?.Invoke($"Planning {addition.Team} {addition.SourceKit}: independent assets and ordered logo roles...");
                var plan = NewUniformAssetPlanner.Build(index, addition, roleMaps, settings.Workers);
                plan.ThrowIfIncomplete();
                assets.Add(plan);
            }
            BrandingCloneWriter.ValidatePlanSet(assets.SelectMany(p => p.Clones));
        }
        return new Prepared(target, additionPlan, assets,
            additionPlan.JerseyRows.ChangedDatabases.ToDictionary(RosterSyncRun.LegacyName,
                name => Convert.ToBase64String(additionPlan.JerseyRows.Databases[name]), StringComparer.Ordinal), mod);
    }

    public static async Task<(bool Ok, string Message)> ApplyAsync(
        SuiteSettings settings,
        ModFile mod,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        var problems = ModApply.Problems(settings);
        if (problems.Count > 0) return (false, "Apply refused:\n" + string.Join("\n", problems));
        return await Task.Run(() => Install(settings, mod, progress), ct);
    }

    /// <summary>Commits an already completed plan. The guided install uses this to avoid preparing all 64 twice.</summary>
    public static async Task<(bool Ok, string Message)> ApplyPreparedAsync(
        SuiteSettings settings,
        Prepared prepared,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        var problems = ModApply.Problems(settings);
        if (problems.Count > 0) return (false, "Apply refused:\n" + string.Join("\n", problems));
        if (!string.Equals(Path.GetFullPath(prepared.Target).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(settings.ApplyTargetDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return (false, "Apply refused: the selected game build changed after this uniform plan was prepared.");
        if (prepared.Mod is null) return (false, "Apply refused: this plan has no uniform list. Prepare it again.");
        return await Task.Run(() => Install(settings, prepared.Mod, progress), ct);
    }

    /// <summary>The one install path: whole-kit clones, every player_team_brt copy registered, rows written, all read back.</summary>
    static (bool Ok, string Message) Install(SuiteSettings settings, ModFile mod, Action<string>? progress)
    {
        string target = Path.GetFullPath(settings.ApplyTargetDir).TrimEnd(Path.DirectorySeparatorChar);
        settings.Save();
        var result = FullKitInstall.Apply(new FullKitInstall.Options(target, mod, SuitePaths.Nhl27Images, null, true,
            s => { progress?.Invoke(s); SuiteLog.Write("new-uniform: " + s); }, settings.Workers,
            JerseyName: string.IsNullOrWhiteSpace(settings.NewUniformJerseyName) ? FullKitInstall.DefaultJerseyName : settings.NewUniformJerseyName.Trim()), settings);
        if (result.Ok && result.Backup is not null) ModApply.RecordUndo(target, result.Backup);
        return (result.Ok, result.Message);
    }

    public static Dictionary<string, IReadOnlyList<string>> BuildRoleMaps(BundleIndex index, SlotMapStore store)
    {
        var maps = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entry in store.Entries)
        {
            string[] roles = [.. entry.Layers24.OrderBy(l => l.Layer).Select(l => l.Role)];
            foreach (string name in index.ResByName.Keys.Where(n => n.StartsWith(entry.Folder24 + "/", StringComparison.Ordinal)))
            {
                var parsed = TextureName.Parse(name);
                if (parsed.Garment != entry.Garment || parsed.Role is not ("color" or "normal") || !parsed.IsArrayName) continue;
                if (Ps4Codec.Slices(ReadHeader(index.ResByName[name][0])) == roles.Length) maps[name] = roles;
            }
        }
        return maps;
    }

    static byte[] ReadHeader(BundleIndex.Res resource)
    {
        byte[] stored = BundleIndex.ReadBytes(resource.Data);
        try { return ChunkBlocks.Parse(stored).Decode(); } catch (InvalidDataException) { return stored; }
    }
}
