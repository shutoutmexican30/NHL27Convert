using System.Text.Json;
using System.Text.Json.Serialization;
using Suite.Convert27;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

/// <summary>One guarded live experiment: reset V3.2, then install only Detroit away 24 and home 25.</summary>
internal static class DetroitLive
{
    const string RequiredTarget = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";

    internal static int Run(string root, bool install,
        Func<string, SuiteSettings, NewUniformApply.Prepared, Dictionary<string, byte[]>, int> verifyCommitted)
    {
        if (install) throw new InvalidOperationException("Retired: this donor-based install cannot produce a working kit. Use tools/KitInstall.");
        root = Path.GetFullPath(root);
        string target = Path.GetFullPath(RequiredTarget);
        string clean = Workspace(root, "Debugging", "live-detroit-clean-backup");
        string backupRoot = Workspace(root, "Debugging", "live-detroit-backups");
        string appData = Workspace(root, "Debugging", "detroit-live-appdata");
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(appData, "roaming"));
        Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(appData, "local"));
        string? safetyBackup = null;
        string? installBackup = null;
        string? error = null;
        bool resetVerified = false, committed = false;
        int bundles = 0, structuralRoots = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!SamePath(target, RequiredTarget)) throw new InvalidOperationException("This diagnostic is restricted to V3.2.");
            var snapshot = ApplyTarget.LoadSnapshot(clean);
            if (!SamePath(snapshot.Game, target))
                throw new InvalidDataException("The clean backup does not belong to the exact V3.2 target.");
            RefuseTargetProblems(target);
            safetyBackup = ApplyTarget.Backup(target, backupRoot);
            Console.WriteLine("Pre-reset safety backup: " + safetyBackup);
            RestoreChecked(target, clean);
            resetVerified = true;
            Console.WriteLine("V3.2 now exactly matches the clean pre-uniform backup.");
            if (!install) return 0;

            var settings = new SuiteSettings
            {
                ApplyTargetDir = target,
                Nhl24GameDir = target,
                Workers = Environment.ProcessorCount,
            };
            Oodle.Ensure(settings);
            ModFile mod = DetroitMod(root);
            string modPath = Workspace(root, "Debugging", "detroit-only-mod.json");
            File.WriteAllText(modPath, JsonSerializer.Serialize(mod, Json));
            var maps = JsonSerializer.Deserialize<SlotMapStore>(File.ReadAllText(
                Workspace(root, "Debugging", "logo-slots", "slot_map.json"))) ?? new();
            var prepared = NewUniformApply.Prepare(settings, mod,
                progress: s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}"), slotMaps: maps, planAssets: true);
            int expectedStructural = ValidatePrepared(prepared, target);
            // The staging map is resolved against the freshly reset live index during Prepare.
            bundles = prepared.CloneCount;
            Console.WriteLine($"Validated Detroit home 25 and away 24: {bundles} bundles, "
                + $"{expectedStructural} conventional roots/blueprints; equipment tables unchanged.");

            RefuseTargetProblems(target);
            installBackup = ApplyTarget.Backup(target, backupRoot);
            Console.WriteLine("Clean install backup: " + installBackup);
            string writerBackup = Workspace(root, "Debugging", "detroit-live-writer-backups",
                $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
            var files = prepared.DatabaseFiles.ToDictionary(kv => kv.Key,
                kv => Convert.FromBase64String(kv.Value), StringComparer.Ordinal);
            var clones = prepared.AssetPlans.SelectMany(p => p.Clones).ToList();
            NewUniformTransaction.Run(installBackup,
                writeAssets: () => BrandingCloneWriter.ApplyBatch(clones, target, writerBackup,
                    s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}")),
                writeDatabases: () =>
                {
                    foreach (string line in new LegacyFiles(target, settings).Replace(files)) Console.WriteLine("DB: " + line);
                },
                readBack: () => structuralRoots = verifyCommitted(target, settings, prepared, files),
                restoreAndVerify: () => RestoreChecked(target, installBackup));
            committed = expectedStructural > 0 && structuralRoots == expectedStructural;
            if (!committed) throw new InvalidDataException(
                $"Expected {expectedStructural} structural roots/blueprints; got {structuralRoots}.");
            Console.WriteLine("DETROIT FILES INSTALLED AND VERIFIED ON DISK. Runtime selection/rendering is still unverified.");
            Console.WriteLine("Launch V3.2 and explicitly choose Detroit away variant 24 and home variant 25.");
            return 0;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            Console.Error.WriteLine("DETROIT LIVE OPERATION FAILED: " + ex);
            if (installBackup is not null && !committed)
            {
                try { RestoreChecked(target, installBackup); Console.Error.WriteLine("The clean pre-install state was restored and verified."); }
                catch (Exception restoreEx) { error += "\nRESTORE FAILURE: " + restoreEx; Console.Error.WriteLine(restoreEx); }
            }
            return 1;
        }
        finally
        {
            sw.Stop();
            string report = Workspace(root, "Debugging", install ? "detroit-live-structural-install.json" : "detroit-live-reset.json");
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                Ok = error is null && resetVerified && (!install || committed),
                Target = target,
                CleanBackup = clean,
                SafetyBackup = safetyBackup,
                InstallBackup = installBackup,
                ResetVerified = resetVerified,
                UniformsInstalled = committed ? 2 : 0,
                TeamsInstalled = committed ? new[] { "detroit" } : Array.Empty<string>(),
                AwayVariant = 24,
                HomeVariant = 25,
                ClonedAssetBundles = committed ? bundles : 0,
                StructuralRootsAndBlueprintsVerified = structuralRoots,
                EquipmentRowsChanged = 0,
                ExistingHomeAwayEquipmentFallbacksPreserved = true,
                RuntimeVerified = false,
                RemainingCleanRestoreDifferences = resetVerified ? ApplyTarget.Compare(target, clean) : null,
                ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
                Error = error,
            }, Json));
            Console.WriteLine("Report: " + report);
        }
    }

    static ModFile DetroitMod(string root)
    {
        var mod = ModFile.Load(Workspace(root, "Debugging", "installed-fanatics-mod.json"))
            ?? throw new InvalidDataException("The saved Fanatics mod could not be loaded.");
        mod.Name = "NHL 27 Detroit home 25 away 24 structural test";
        mod.Kind = NewUniformAddition.ModKind;
        mod.Comment = "Isolated two-uniform V3.2 test. Existing home/away equipment and all unrelated tables remain untouched.";
        mod.Additions = (mod.Additions ?? []).Where(a => a.Team == "detroit").ToList();
        if (mod.Additions.Count != 2 || mod.Additions.Any(a => a.TeamId != 10)
            || mod.Additions.SingleOrDefault(a => a.SourceKit == "home")?.TargetVariant != 25
            || mod.Additions.SingleOrDefault(a => a.SourceKit == "away")?.TargetVariant != 24)
            throw new InvalidDataException("Expected exactly Detroit home 25 and away 24.");
        string artwork = Workspace(root, "Debugging", "new-uniform-integration", "cache", "nhl27png");
        string Local(string original)
        {
            if (original.Length == 0) throw new InvalidDataException("An artwork source is empty.");
            string path = Path.Combine(artwork, Path.GetFileName(original));
            if (!File.Exists(path)) throw new FileNotFoundException("Required Detroit artwork is missing.", path);
            return path;
        }
        foreach (var addition in mod.Additions)
        foreach (var texture in addition.Textures)
        {
            var art = texture.Artwork;
            texture.Artwork = art.Layers is { Count: > 0 }
                ? art with { Source = "", Layers = art.Layers.ToDictionary(kv => kv.Key, kv => Local(kv.Value)) }
                : art with { Source = Local(art.Source) };
        }
        if (mod.Additions.Any(a => a.Textures.Count != 19))
            throw new InvalidDataException("Detroit must retain all 19 mapped textures per uniform.");
        return mod;
    }

    static int ValidatePrepared(NewUniformApply.Prepared prepared, string target)
    {
        var plans = prepared.AssetPlans.SelectMany(a => a.Clones).ToList();
        foreach (var plan in plans)
            Console.WriteLine($"PLAN {plan.SourceBundleName} -> {plan.NewBundleName}; roots: "
                + string.Join(", ", plan.Entries.Where(e => e.IsClone && e.NewChunkId is null
                    && (e.Name.EndsWith("_player_team_brt", StringComparison.Ordinal)
                        || e.Name.EndsWith("_player_team_brt_blueprint", StringComparison.Ordinal))).Select(e => e.Name)));
        // Co-location differs between game builds, so bundle count is informational. Require the actual resources.
        if (!SamePath(prepared.Target, target) || prepared.AdditionPlan.Additions.Count != 2
            || prepared.AssetPlans.Count != 2 || prepared.CloneCount != plans.Count || plans.Count == 0
            || prepared.AdditionPlan.TextureCount != 38)
            throw new InvalidDataException($"Unexpected Detroit plan: {prepared.Summary}");
        if (prepared.AdditionPlan.JerseyRows.ChangedDatabases.Count != 2
            || prepared.AdditionPlan.JerseyRows.Rows.Count != 4
            || prepared.AdditionPlan.JerseyRows.Rows.Any(r => r.AlreadyExists))
            throw new InvalidDataException("Detroit requires two new rows in each authoritative database.");
        var expectedTextures = prepared.AdditionPlan.TargetSlots.ToHashSet(StringComparer.Ordinal);
        var clonedTextures = plans.SelectMany(c => c.Entries)
            .Where(e => e.IsClone && expectedTextures.Contains(e.Name)).Select(e => e.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!clonedTextures.SetEquals(expectedTextures))
            throw new InvalidDataException("Missing converted targets: "
                + string.Join(", ", expectedTextures.Except(clonedTextures, StringComparer.Ordinal)));

        static string ConventionalRoot(BrandingClonePlan plan) => plan.NewBundleName.StartsWith("gen4b/", StringComparison.Ordinal)
            ? plan.NewBundleName["gen4b/".Length..] : plan.NewBundleName;
        foreach (var addition in prepared.AdditionPlan.Additions)
        {
            string prefix = addition.TargetPrefix.TrimEnd('/') + "/";
            var parts = prefix.Trim('/').Split('/');
            string identity = parts[^3] + "_" + parts[^1];
            string[] leaves =
            [
                $"jersey_adidas_{identity}_base_player_team_brt",
                $"font_adidas_{identity}_player_team_brt",
                $"branding_jersey_adidas_{identity}_color_array_player_team_brt",
                $"branding_jersey_adidas_{identity}_normal_array_player_team_brt",
                $"branding_jersey_adidas_{identity}_coeff_array_player_team_brt",
                $"branding_pant_adidas_{identity}_color_array_player_team_brt",
                $"branding_pant_adidas_{identity}_normal_array_player_team_brt",
                $"branding_pant_adidas_{identity}_coeff_array_player_team_brt",
            ];
            foreach (string leaf in leaves)
            {
                string root = prefix + leaf;
                var companion = plans.SingleOrDefault(p => ConventionalRoot(p) == root)
                    ?? throw new InvalidDataException("Required Detroit companion bundle is missing: " + root);
                foreach (string name in new[] { root, root + "_blueprint" })
                    if (!companion.Entries.Any(e => e.IsClone && e.NewChunkId is null && e.Name == name))
                        throw new InvalidDataException("Required Detroit companion root/blueprint is missing: " + name);
            }
        }
        int expectedStructural = 0;
        foreach (var plan in plans)
        {
            string root = ConventionalRoot(plan);
            if (!root.EndsWith("_player_team_brt", StringComparison.Ordinal)) continue;
            foreach (string name in new[] { root, root + "_blueprint" })
                if (!plan.Entries.Any(e => e.IsClone && e.NewChunkId is null && e.Name == name))
                    throw new InvalidDataException("Conventional root/blueprint is missing: " + name);
            expectedStructural += 2;
        }
        BrandingCloneWriter.ValidatePlanSet(plans);
        return expectedStructural;
    }

    static void RefuseTargetProblems(string target)
    {
        var problems = ApplyTarget.Problems(target);
        if (problems.Count > 0) throw new InvalidOperationException("V3.2 target refused:\n" + string.Join("\n", problems));
    }

    static void RestoreChecked(string target, string backup)
    {
        ApplyTarget.RestoreFromBackup(target, backup);
        var differences = ApplyTarget.Compare(target, backup);
        if (differences.Count > 0) throw new InvalidDataException("Restore differences: " + string.Join("; ", differences));
    }

    static bool SamePath(string left, string right) =>
        string.Equals(GameLayout.Normalize(left), GameLayout.Normalize(right), StringComparison.OrdinalIgnoreCase);

    static string Workspace(string root, params string[] parts)
    {
        string path = Path.GetFullPath(Path.Combine(new[] { root }.Concat(parts).ToArray()));
        string parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A diagnostic output path escaped the workspace: " + path);
        return path;
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
