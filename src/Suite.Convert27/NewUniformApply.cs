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
        /// <summary>Uniforms left out because the game already has them, and ones whose slot needs repair first.</summary>
        public IReadOnlyList<string> Skipped { get; init; } = [];
        public IReadOnlyList<string> NeedRepair { get; init; } = [];
        public int CloneCount => AssetPlans.Sum(p => p.Clones.Count);
        public string Summary => AdditionPlan.Summary + " Each uniform is a whole clone of its own team's kit; "
            + string.Join(", ", AdditionPlan.JerseyRows.ChangedDatabases) + "."
            + (Skipped.Count > 0 ? Environment.NewLine + Environment.NewLine + "Already in this game, skipped: " + string.Join(", ", Skipped) + "." : "")
            + (NeedRepair.Count > 0 ? Environment.NewLine + Environment.NewLine
                + "Needs repair (an incomplete earlier install holds its slot), left out: " + string.Join(", ", NeedRepair) + "." : "");
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
                {
                    string actual = source.StartsWith("coeff:", StringComparison.Ordinal) ? source["coeff:".Length..] : source;
                    if (!File.Exists(actual)) problems.Add($"{mod.Name}: {addition.Team} {addition.SourceKit}, {texture.SourceSlot} layer {layer}: missing {actual}");
                }
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
        // A uniform this game already has is skipped, never added a second time under another variant.
        var skipped = InstalledKits.Load().SkipPresent(target, mod.Additions, prefix => InstalledKits.FolderHasAssets(index, prefix));
        foreach (var (a, kit) in skipped)
            progress?.Invoke($"{a.Team} {a.SourceKit}: already in this game (variant {kit.Variant}), skipped");
        // New kits always use variant 25 (home) and 24 (away), the only variants the equipment rows support. A team that
        // already has a kit there (an earlier add) is skipped instead of being moved to another variant.
        var registries = InstalledKits.Registries(index);
        var needRepair = new List<string>();
        var alreadyIn = skipped.Select(x => $"{x.Addition.Team} {x.Addition.SourceKit}").ToList();
        foreach (var a in mod.Additions.Where(a => a.TargetVariant == 0).ToList())
        {
            int variant = InstalledKits.VariantFor(a.SourceKit);
            string folder = $"{TextureName.SkaterPrefix}nhl/{a.Team}/adidas/{TextureName.Numbers[variant]}/";
            switch (InstalledKits.State(index, registries, folder))
            {
                case InstalledKits.SlotState.Complete:
                    progress?.Invoke($"{a.Team} {a.SourceKit}: this game already has a complete kit in variant {variant}, skipped");
                    alreadyIn.Add($"{a.Team} {a.SourceKit}");
                    mod.Additions.Remove(a);
                    break;
                case InstalledKits.SlotState.Broken:
                    // An earlier, incomplete install (unregistered, no jersey layout) holds the slot. It is never moved to
                    // another variant; it must be repaired in place, so it is left out of this batch and named.
                    needRepair.Add($"{a.Team} {a.SourceKit} (variant {variant})");
                    progress?.Invoke($"{a.Team} {a.SourceKit}: NEEDS REPAIR - an incomplete earlier install holds variant {variant}; left out of this batch");
                    mod.Additions.Remove(a);
                    break;
                default:
                    a.TargetVariant = variant;
                    break;
            }
        }
        if (needRepair.Count > 0) SuiteLog.Write("install: needs repair: " + string.Join(", ", needRepair));
        if (mod.Additions.Count == 0)
            throw new InvalidDataException(needRepair.Count > 0
                ? "Nothing to add: the chosen uniforms are already in this game, and these need repair first: " + string.Join(", ", needRepair)
                : "Every chosen uniform is already in this game. Use Undo last add first to add one again.");
        RegisteredKitSource.Normalize(mod.Additions, index, progress);
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
                name => Convert.ToBase64String(additionPlan.JerseyRows.Databases[name]), StringComparer.Ordinal), mod)
        { Skipped = alreadyIn, NeedRepair = needRepair };
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

    /// <summary>
    /// Repair: for each chosen uniform whose slot (25 home / 24 away) holds an incomplete earlier install, remove those
    /// leftovers (BrokenKitRemoval) and add the uniform fresh in the same slot. If the install fails, the leftovers are
    /// put back exactly as they were, so the game ends either repaired or unchanged.
    /// </summary>
    /// <param name="rebuildComplete">Also rebuild a complete kit in its slot (the user picked one uniform to repair, e.g.
    /// Anaheim home whose logos sit in the wrong spots). All teams repairs only incomplete installs.</param>
    public static (bool Ok, string Message) Repair(SuiteSettings settings, ModFile unplanned, Action<string>? progress = null,
        bool rebuildComplete = false)
    {
        var problems = ModApply.Problems(settings);
        if (problems.Count > 0) return (false, "Repair refused: " + string.Join("; ", problems));
        string target = Path.GetFullPath(settings.ApplyTargetDir).TrimEnd(Path.DirectorySeparatorChar);
        var index = BundleIndex.Build(target, progress);
        var registries = InstalledKits.Registries(index);
        var broken = new List<NewUniformAddition>();
        var plans = new List<BrokenKitRemoval.Plan>();
        foreach (var a in unplanned.Additions ?? [])
        {
            int variant = InstalledKits.VariantFor(a.SourceKit);
            string folder = $"{TextureName.SkaterPrefix}nhl/{a.Team}/adidas/{TextureName.Numbers[variant]}/";
            var state = InstalledKits.State(index, registries, folder);
            if (state == InstalledKits.SlotState.Free || state == InstalledKits.SlotState.Complete && !rebuildComplete) continue;
            var plan = BrokenKitRemoval.Build(index, folder, a.TeamId, variant);
            progress?.Invoke($"{a.Team} {a.SourceKit}: removing the {(state == InstalledKits.SlotState.Broken ? "incomplete" : "existing")} kit in variant {variant} ({plan.BundleCount} bundles, {plan.ChunkCount} chunks)");
            broken.Add(a);
            plans.Add(plan);
        }
        if (broken.Count == 0) return (false, rebuildComplete ? "Nothing to repair: this uniform is not in the game yet. Use Add to game."
            : "Nothing needs repair: no chosen uniform's slot holds an incomplete earlier install.");
        string removalBackup = BrokenKitRemoval.Apply(target, plans, settings, null,
            s => { progress?.Invoke(s); SuiteLog.Write("repair: " + s); });
        try
        {
            var mod = new ModFile { Name = unplanned.Name, Kind = NewUniformAddition.ModKind, Additions = broken };
            var prepared = Prepare(settings, mod, progress: progress);
            if (prepared.Mod?.Additions is not { Count: > 0 } ready || ready.Count != broken.Count)
                throw new InvalidDataException("After removal not every repaired uniform could be planned in its own slot.");
            var (ok, message) = Install(settings, prepared.Mod, progress);
            if (ok)
            {
                // Like Add to game, the repaired uniforms go on the mod list as a saved mod.
                // One mod per repair, named for its uniforms (short for big batches), so a repair never replaces another
                // mod on the list. The game is already repaired and read back here: a failed save never undoes it.
                string label = broken.Count <= 3 ? string.Join(", ", broken.Select(a => $"{a.Team} {a.SourceKit}"))
                    : $"{broken.Count} uniforms {DateTime.Now:yyyy-MM-dd HHmm}";
                string saved;
                try
                {
                    saved = prepared.AdditionPlan.ToMod("NHL 27 repaired " + label, "Uniforms rebuilt by Repair in their own variant slots.").Save();
                    progress?.Invoke("mod saved to " + saved);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    saved = "(not saved: " + ex.Message + ")";
                }
                return (true, $"Repaired {string.Join(", ", broken.Select(a => $"{a.Team} {a.SourceKit}"))}. " + message + " Saved mod: " + saved);
            }
            Restore(target, removalBackup);
            return (false, message + " The incomplete kit(s) were put back exactly as they were.");
        }
        catch (Exception ex)
        {
            Restore(target, removalBackup);
            return (false, ex.Message + " The incomplete kit(s) were put back exactly as they were.");
        }
    }

    static void Restore(string target, string backup)
    {
        ApplyTarget.RestoreFromBackup(target, backup);
        var left = ApplyTarget.Compare(target, backup);
        if (left.Count > 0) throw new InvalidOperationException("Restore left differences: " + string.Join("; ", left.Take(5)));
    }

    /// <summary>The one install path: whole-kit clones, every player_team_brt copy registered, rows written, all read back.</summary>
    static (bool Ok, string Message) Install(SuiteSettings settings, ModFile mod, Action<string>? progress)
    {
        string target = Path.GetFullPath(settings.ApplyTargetDir).TrimEnd(Path.DirectorySeparatorChar);
        var result = FullKitInstall.Apply(new FullKitInstall.Options(target, mod, SuitePaths.Nhl27Images, null, true,
            s => { progress?.Invoke(s); SuiteLog.Write("new-uniform: " + s); }, settings.Workers,
            JerseyName: string.IsNullOrWhiteSpace(settings.NewUniformJerseyName) ? FullKitInstall.DefaultJerseyName : settings.NewUniformJerseyName.Trim(),
            EquipmentSource: Path.Combine(AppContext.BaseDirectory, "data", "nhl27-equipment")), settings);
        if (result.Ok && result.Backup is not null) ModApply.RecordUndo(target, result.Backup);
        if (result.Ok && mod.Additions is { Count: > 0 } added)
        {
            var record = InstalledKits.Load();
            record.Record(target, added);
            record.Save();
        }
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
