using Suite.Core;

namespace Suite.Convert27;

/// <summary>Builds and preflights an addition mod without saving it or writing to a game.</summary>
public static class NewUniformExport
{
    public sealed record Draft(ModFile Mod, NewUniformApply.Prepared Prepared, string Details)
    {
        public string Summary => Prepared.Summary;
        public string Save() => Mod.Save();
    }

    /// <summary>The uniforms to add, built and validated but not yet planned against the game (no targets).</summary>
    static async Task<(ModFile Unplanned, Action<string> Report)> BuildUnplannedAsync(Convert27ViewModel vm, string name, bool currentFolderOnly)
    {
        if (vm.Catalog27 is null) throw new InvalidOperationException("The texture indexes are not loaded yet.");
        if (string.IsNullOrWhiteSpace(vm.Settings.ApplyTargetDir))
            throw new InvalidOperationException("Choose the game build whose spare rows and assets should receive the new uniforms.");
        var folders = currentFolderOnly
            ? SelectFolders(vm, currentOnly: true)
            : BuildDefaultFolders(vm.Catalog24 ?? throw new InvalidOperationException("The NHL 24 texture index is not loaded yet."),
                vm.Catalog27, vm.Variants);
        // Decode/reconcile the canonical folders, but do not let the classic NHL 24 replacement matcher veto this
        // workflow: new uniforms use independent asset arrays, so an NHL 27 logo may intentionally have no old NHL 24
        // destination. The complete-array builder below is the authoritative completeness check for this mode.
        var report = await vm.RunPreflightAsync(currentFolderOnly, folders);
        SuiteLog.Write("install: classic preflight returned; decoding active logo-array slices");
        // All status updates must be marshalled to the UI thread: these callbacks fire on worker threads during the
        // long background planning stage, and a cross-thread write to a WPF-bound property can kill the app silently.
        var ui = SynchronizationContext.Current;
        void Report(string text)
        {
            SuiteLog.Write("install: " + text);
            if (ui is null || ReferenceEquals(ui, SynchronizationContext.Current)) vm.Status = text;
            else ui.Post(_ => vm.Status = text, null);
        }
        var sourceArrays = folders.Where(f => f.Folder27 is not null && vm.Catalog27.ByFolder.ContainsKey(f.Folder27))
            .SelectMany(f => vm.Catalog27.ByFolder[f.Folder27!])
            .Where(t => t.Slices > 1 && TextureName.Parse(t.Name) is { IsArrayName: true, Role: "color" or "normal" } n
                && n.Garment.StartsWith("branding", StringComparison.Ordinal))
            .ToList();
        await vm.Images.Nhl27Async(sourceArrays, s => Report("Decoding active logo-array slices: " + s));
        SuiteLog.Write("install: array slices decoded; building complete Fanatics arrays");
        vm.SlotMaps.Save();
        vm.Project.Save();
        var additions = new List<NewUniformAddition>();
        foreach (var folder in folders)
        {
            Report("Building complete Fanatics arrays: " + Convert27ViewModel.FriendlyFolder(folder.Folder24));
            additions.Add(Addition(vm, folder));
        }
        SuiteLog.Write($"install: {additions.Count} complete additions built; starting background prepare");
        vm.PreflightSummary = additions.Count == folders.Count
            ? $"{additions.Count} independent uniform asset sets passed complete-array validation; legacy replacement-map destination items are not blockers."
            : report.Summary;
        var unplanned = new ModFile
        {
            Name = name,
            Kind = NewUniformAddition.ModKind,
            Additions = additions,
        };
        return (unplanned, Report);
    }

    /// <summary>Repairs every uniform whose slot holds an incomplete earlier install (All teams, or the selected one).</summary>
    public static async Task<(bool Ok, string Message)> RepairAsync(Convert27ViewModel vm, bool currentFolderOnly)
    {
        var (unplanned, Report) = await BuildUnplannedAsync(vm, "NHL 27 repaired uniforms", currentFolderOnly);
        return await Task.Run(() => NewUniformApply.Repair(vm.Settings, unplanned, Report, rebuildComplete: currentFolderOnly));
    }

    public static async Task<Draft> PlanAsync(Convert27ViewModel vm, string name, bool currentFolderOnly)
    {
        var (unplanned, Report) = await BuildUnplannedAsync(vm, name, currentFolderOnly);
        var prepared = await Task.Run(() => NewUniformApply.Prepare(vm.Settings, unplanned, progress: Report));
        SuiteLog.Write("install: background prepare finished");
        var mod = prepared.AdditionPlan.ToMod(name,
            "New selectable Fanatics home/away uniforms. Independent cloned assets and spare stockteamjerseys rows; no existing uniform is replaced.");
        var lines = new List<string>();
        for (int i = 0; i < prepared.AdditionPlan.Additions.Count; i++)
        {
            var addition = prepared.AdditionPlan.Additions[i];
            lines.Add($"{addition.Team} · {addition.SourceKit} → variant {addition.TargetVariant} ({addition.TargetPrefix.TrimEnd('/').Split('/')[^1]}); "
                + $"{addition.Textures.Count} textures on a whole clone of the team's own {addition.SourceKit} kit");
        }
        lines.Add("Databases: " + string.Join(", ", prepared.AdditionPlan.JerseyRows.ChangedDatabases));
        return new Draft(mod, prepared, string.Join("\n", lines));
    }

    static List<FolderMatch> SelectFolders(Convert27ViewModel vm, bool currentOnly)
    {
        if (!currentOnly) throw new ArgumentException("The complete set uses BuildDefaultFolders.", nameof(currentOnly));
        var current = vm.Folder24 is not null ? vm.Project.Find(vm.Folder24) : null;
        if (current is null) return [];
        if (vm.Catalog24 is null || vm.Catalog27 is null) return [current];
        return [WithJerseyStructure(current, vm.Catalog24, vm.Catalog27, vm.Variants)];
    }

    /// <summary>
    /// Builds the default Fanatics additions headlessly (no page): the same folders and complete arrays as All teams,
    /// from the NHL 24/27 indexes, the variant map and the slot map. Tools use this instead of an older saved mod.
    /// </summary>
    public static List<NewUniformAddition> BuildDefaultAdditions(Nhl24Catalog catalog24, Nhl27Catalog catalog27, VariantMap variants,
        SlotMapStore slotMaps, IReadOnlySet<string>? teams = null, string? kit = null)
    {
        var project = new MatchProject();
        return BuildDefaultFolders(catalog24, catalog27, variants)
            .Where(f => CurrentSource(f, variants) is { } s && (teams is null || teams.Contains(TextureName.Parse(f.Folder24 + "/x").Team ?? ""))
                && (kit is null || s.Kit == kit))
            .Select(f => Addition(project, variants, catalog27, slotMaps, f, TextureImages.Nhl27Png)).ToList();
    }

    /// <summary>
    /// The folder to build a single uniform from. NHL 24 keeps some teams' jersey logo arrays in another set of the same
    /// team (Boston's home uses "five"), so a picked folder without them takes the same structural folder the full set
    /// uses, keeping the picked NHL 27 art. A folder that has its own jersey arrays is used as it is.
    /// </summary>
    internal static FolderMatch WithJerseyStructure(FolderMatch current, Nhl24Catalog catalog24, Nhl27Catalog catalog27, VariantMap variants)
    {
        bool HasJersey(IEnumerable<string> names) => names.Any(n => TextureName.Parse(n) is { IsArrayName: true, Role: "color", Garment: "branding_jersey" });
        if (catalog24.ByFolder.TryGetValue(current.Folder24, out var own) && HasJersey(own.Where(s => s.Array).Select(s => s.Name)))
            return current;
        if (current.Folder27 is null || CurrentSource(current, variants) is not { } source) return current;
        string? folder24 = SelectStructuralFolder(catalog24, source.Team.Nhl24.Folder, current.Folder24, source.Kit);
        if (folder24 is null || folder24 == current.Folder24) return current;
        SuiteLog.Write($"install: {current.Folder24} has no jersey logo arrays; using {folder24.Split('/')[^1]} for its structure");
        return new FolderMatch
        {
            Folder24 = folder24,
            Folder27 = current.Folder27,
            Confidence = 0.95,
            Reason = $"{source.Kit} logos live in NHL 24 set {folder24.Split('/')[^1]}",
            Rows = Matcher.Propose(catalog24.ByFolder[folder24], current.Folder27, 0.95, catalog27),
        };
    }

    /// <summary>
    /// Creates a fresh, canonical plan for every current NHL team's default home/away artwork. Old manual folder pairs
    /// cannot leak into this batch. The NHL 24 folders supply only structure; all pixels come from the mapped NHL 27 set.
    /// </summary>
    internal static List<FolderMatch> BuildDefaultFolders(Nhl24Catalog catalog24, Nhl27Catalog catalog27, VariantMap variants)
    {
        var teams = variants.Teams.Where(t => t.Index is >= 0 and <= 31 && UniformViewerReference.TeamKey(t.Nhl24.Folder) is not null)
            .OrderBy(t => t.Index).ToList();
        var folders = new List<FolderMatch>();
        var missing = new List<string>();
        foreach (var team in teams)
        foreach (var home in new[] { true, false })
        {
            string kit = home ? "home" : "away";
            string? set24 = home ? team.Nhl24.Home : team.Nhl24.Away;
            string? set27 = home ? team.Nhl27.Home : team.Nhl27.Away;
            if (set24 is null || set27 is null) { missing.Add($"{team.Name27} {kit}: incomplete variant map"); continue; }
            string default24 = $"{TextureName.SkaterPrefix}nhl/{team.Nhl24.Folder}/adidas/{set24}";
            string folder27 = $"{TextureName.SkaterPrefix}nhl/{team.Nhl27.Folder}/adidas/{set27}";
            string? folder24 = SelectStructuralFolder(catalog24, team.Nhl24.Folder, default24, kit);
            if (folder24 is null || !catalog27.ByFolder.ContainsKey(folder27))
            {
                missing.Add($"{team.Name27} {kit}: {(folder24 is null ? default24 + " has no complete jersey-array structure" : folder27 + " is absent from the texture index")}");
                continue;
            }
            var slots = catalog24.ByFolder[folder24];
            folders.Add(new FolderMatch
            {
                Folder24 = folder24,
                Folder27 = folder27,
                Confidence = 0.95,
                Reason = $"canonical default {kit}; NHL 24 structure: {folder24.Split('/')[^1]}",
                Rows = Matcher.Propose(slots, folder27, 0.95, catalog27),
            });
        }
        if (missing.Count > 0 || folders.Count != 64)
            throw new InvalidDataException($"The complete set needs 64 default home/away uniforms; {folders.Count} are available. "
                + string.Join("; ", missing));
        return folders;
    }

    static string? SelectStructuralFolder(Nhl24Catalog catalog, string team, string defaultFolder, string kit)
    {
        string prefix = $"{TextureName.SkaterPrefix}nhl/{team}/adidas/";
        return catalog.ByFolder.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)
                && kv.Value.Any(s => s.Array && TextureName.Parse(s.Name) is { Role: "color", Garment: "branding_jersey" }))
            .OrderBy(kv => kv.Key == defaultFolder ? 0 : 1)
            .ThenBy(kv => kv.Key.EndsWith("/" + kit, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(kv => TextureName.SetOrder(kv.Key.Split('/')[^1]))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key).FirstOrDefault();
    }

    internal static (VariantMap.Team Team, string Kit)? CurrentSource(FolderMatch folder, VariantMap variants)
    {
        if (folder.Folder27 is null) return null;
        var source = TextureName.Parse(folder.Folder27 + "/x");
        if (source.Team is null || source.Set is null) return null;
        var team = variants.Teams.Where(t => t.Index is >= 0 and <= 31 && UniformViewerReference.TeamKey(t.Nhl24.Folder) is not null
                && t.Nhl27.Folder == source.Team)
            .OrderBy(t => t.Index).FirstOrDefault();
        if (team is null) return null;
        if (source.Set == team.Nhl27.Home) return (team, "home");
        if (source.Set == team.Nhl27.Away) return (team, "away");
        return null;
    }

    static NewUniformAddition Addition(Convert27ViewModel vm, FolderMatch folder) =>
        Addition(vm.Project, vm.Variants, vm.Catalog27!, vm.SlotMaps, folder, TextureImages.Nhl27Png);

    internal static NewUniformAddition Addition(MatchProject project, VariantMap variants, Nhl27Catalog catalog,
        SlotMapStore slotMaps, FolderMatch folder, Func<string, string> png)
    {
        var parsed = TextureName.Parse(folder.Folder24 + "/x");
        if (parsed.Team is null || CurrentSource(folder, variants) is not { } source)
            throw new InvalidDataException($"{folder.Folder24}: no current-team NHL 27 default home/away mapping exists.");
        string kit = source.Kit;
        var sourceMod = project.ToModFile("addition-source", [folder]);
        ModApply.RepairLegacyJerseyBases([sourceMod]);
        var textures = sourceMod.Replacements.Where(r => r.Slot.StartsWith(folder.Folder24 + "/", StringComparison.Ordinal)
                && !r.Slot.EndsWith("_color_array", StringComparison.Ordinal)
                && !r.Slot.EndsWith("_normal_array", StringComparison.Ordinal))
            .Select(r => new NewUniformTexture { SourceSlot = r.Slot, Artwork = r }).ToList();
        int jerseyArrays = 0;
        foreach (var array in folder.Rows.Where(r => r.IsArray && r.Role == "color"
                     && r.Name.Garment.StartsWith("branding", StringComparison.Ordinal)))
        {
            var (color, normal) = CompleteArray(catalog, slotMaps, folder, array, png);
            textures.Add(color);
            textures.Add(normal);
            textures.Add(ManufacturerBranding.CoefficientFor(color));
            if (array.Garment == "branding_jersey") jerseyArrays++;
        }
        if (jerseyArrays == 0) throw new InvalidDataException($"{folder.Folder24}: no jersey branding array was matched; the Fanatics set cannot be verified.");
        return new NewUniformAddition
        {
            TeamId = source.Team.Index,
            Team = parsed.Team,
            SourceKit = kit,
            IsLight = kit == "away",
            SourcePrefix = folder.Folder24,
            SourceFolder27 = folder.Folder27,
            Textures = textures,
        };
    }

    internal static (NewUniformTexture Color, NewUniformTexture Normal) CompleteArray(
        Nhl27Catalog catalog, SlotMapStore slotMaps, FolderMatch folder, SlotMatch array, Func<string, string> png)
    {
        if (folder.Folder27 is null || !catalog.ByFolder.TryGetValue(folder.Folder27, out var all))
            throw new InvalidDataException($"{folder.Folder24}: no NHL 27 source textures are available.");
        var colors = all.Where(t => TextureName.Parse(t.Name) is { Role: "color", Layer: not null } n
                && !n.IsArrayName && n.Garment == array.Garment)
            .Select(t => (Layer: TextureName.Parse(t.Name).Layer!.Value, Png: png(t.Name)))
            .OrderBy(c => c.Layer).ToList();
        List<(int Layer, string Color, string Normal)> sources = [];
        List<LayerLabel> labels = [];
        var sourceArray = all.FirstOrDefault(t => TextureName.Parse(t.Name) is { Role: "color", IsArrayName: true } n
            && n.Garment == array.Garment && t.Slices > 1);
        var sourceNormal = sourceArray is null ? null : all.FirstOrDefault(t => t.Name == sourceArray.Name[..^"_color_array".Length] + "_normal_array"
            && t.Slices == sourceArray.Slices);
        bool arrayReady = false;
        if (sourceArray is not null && sourceNormal is not null)
        {
            var arraySources = Enumerable.Range(0, sourceArray.Slices)
                .Select(i => (Layer: i, Color: png($"{sourceArray.Name}#{i}"), Normal: png($"{sourceNormal.Name}#{i}"))).ToList();
            var arrayLabels = SlotRoles.Label(arraySources.Select(s => (s.Layer, (string?)s.Color)).ToList(), array.Garment);
            // A shared one-purpose array (for example a shield-only pants resource) is not the team's complete array.
            if (arraySources.All(s => File.Exists(s.Color) && File.Exists(s.Normal)) && arrayLabels.Any(l => l.Role == "crest"))
            {
                sources = arraySources;
                labels = arrayLabels;
                arrayReady = true;
            }
        }
        if (!arrayReady)
        {
            if (colors.Count == 0) throw new InvalidDataException($"{array.Slot}: no numbered NHL 27 colour layers were found.");
            sources = colors.Select(c =>
            {
                string normal = all.Where(t => TextureName.Parse(t.Name) is { Role: "normal", Layer: not null } n
                        && !n.IsArrayName && n.Garment == array.Garment && n.Layer == c.Layer)
                    .Select(t => png(t.Name)).FirstOrDefault() ?? "";
                return (c.Layer, c.Png, normal);
            }).ToList();
            labels = SlotRoles.Label(colors.Select(c => (c.Layer, (string?)c.Png)).ToList(), array.Garment);
            labels = UniformSourceLayouts.RefineNumbered(folder, array.Garment, sources, labels, slotMaps);
        }
        labels = labels.Where(l =>
        {
            if (l.Role != "blank") return true;
            var source = sources.FirstOrDefault(s => s.Layer == l.Layer);
            // A missing file is not a transparent design choice; retain it so the completeness check below blocks export.
            return !File.Exists(source.Color) || !File.Exists(source.Normal);
        }).ToList();
        (string Color, string Normal)? fallback = null;
        if (array.Garment == "branding_jersey" && !labels.Any(ManufacturerBranding.IsFanatics))
        {
            fallback = FanaticsFallback(catalog, array.Garment, png);
            if (fallback is null)
                throw new InvalidDataException($"{array.Slot}: no Fanatics colour/normal pair exists in NHL 27.");
            var map = slotMaps.Find(folder.Folder24, folder.Folder27, array.Garment);
            int at = map?.Layers24.TakeWhile(l => l.Role != "brand").Count() ?? labels.Count;
            labels.Insert(Math.Clamp(at, 0, labels.Count), new LayerLabel { Layer = -1, Role = "brand", Detail = "fanatics" });
        }
        var colorLayers = new Dictionary<int, string>();
        var normalLayers = new Dictionary<int, string>();
        for (int output = 0; output < labels.Count; output++)
        {
            var label = labels[output];
            bool useFallback = label.Layer < 0 || label.Role == "brand" && !ManufacturerBranding.IsFanatics(label);
            if (useFallback && fallback is null) fallback = FanaticsFallback(catalog, array.Garment, png);
            var source = sources.FirstOrDefault(s => s.Layer == label.Layer);
            string? color = useFallback ? fallback?.Color : source.Color;
            string? normal = useFallback ? fallback?.Normal : source.Normal;
            if (color is null || normal is null || !File.Exists(color) || !File.Exists(normal))
                throw new InvalidDataException($"{array.Slot}, output layer {output} ({label.Role}): the complete team colour and normal artwork is required; no donor fallback is allowed.");
            colorLayers.Add(output, color);
            normalLayers.Add(output, normal);
        }
        if (array.Garment == "branding_jersey" && !labels.Any(ManufacturerBranding.IsFanatics))
            throw new InvalidDataException($"{array.Slot}: Fanatics branding could not be verified.");
        string normalSlot = array.Slot[..^"_color_array".Length] + "_normal_array";
        string[] roles = [.. labels.Select(l => l.Role)];
        return (
            new NewUniformTexture { SourceSlot = array.Slot, Artwork = new TextureReplacement(array.Slot, "", colorLayers), RequiredRoles = [.. roles] },
            new NewUniformTexture { SourceSlot = normalSlot, Artwork = new TextureReplacement(normalSlot, "", normalLayers), RequiredRoles = [.. roles] });
    }

    static (string Color, string Normal)? FanaticsFallback(Nhl27Catalog catalog, string garment, Func<string, string> png)
    {
        foreach (var group in catalog.ByFolder)
        {
            var colors = group.Value.Where(t => TextureName.Parse(t.Name) is { Role: "color", Layer: not null } n
                    && !n.IsArrayName && n.Garment == garment)
                .Select(t => (Layer: TextureName.Parse(t.Name).Layer!.Value, Png: png(t.Name))).ToList();
            var fanatics = SlotRoles.Label(colors.Select(c => (c.Layer, (string?)c.Png)).ToList(), garment)
                .FirstOrDefault(ManufacturerBranding.IsFanatics);
            if (fanatics is null) continue;
            string color = colors.First(c => c.Layer == fanatics.Layer).Png;
            string? normal = group.Value.Where(t => TextureName.Parse(t.Name) is { Role: "normal", Layer: not null } n
                    && n.Garment == garment && n.Layer == fanatics.Layer)
                .Select(t => png(t.Name)).FirstOrDefault();
            if (File.Exists(color) && normal is not null && File.Exists(normal)) return (color, normal);
        }
        return null;
    }
}
