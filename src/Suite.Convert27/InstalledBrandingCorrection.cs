using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>One already-installed target array chunk whose final pixels were rebuilt without cloning its uniform again.</summary>
public sealed record BrandingCorrectionJob(
    string Team, string Kit, string Slot, string Role, Guid Chunk, int Layers, byte[] Before, byte[] Data);

public sealed record BrandingCorrectionCopy(
    string Team, string Kit, string Slot, Guid Chunk, int Width, int Height, int Layers,
    IReadOnlyDictionary<int, int> Moves, IReadOnlyList<string> ChangedRoles);

public sealed record InstalledBrandingCorrectionPlan(
    IReadOnlyList<BrandingCorrectionJob> Jobs, IReadOnlyList<BrandingCorrectionCopy> Copies,
    IReadOnlyList<BrandingStampJob> Stamps);

/// <summary>
/// Rebuilds final color, normal and clean coefficient pixels for existing variant 24/25 arrays. The source kit supplies
/// only the stock layout used to match pictures; every output pixel comes from the saved converted artwork or a safe blank.
/// No bundle, registry, jersey database, base-garment coefficient, or unrelated texture is written.
/// </summary>
public static class InstalledBrandingCorrection
{
    public static InstalledBrandingCorrectionPlan BuildAddition(string game, BundleIndex index,
        NewUniformAddition addition, Func<string, string> resolveArt, int workers = 0)
    {
        var jobs = new System.Collections.Concurrent.ConcurrentBag<BrandingCorrectionJob>();
        var copies = new System.Collections.Concurrent.ConcurrentBag<BrandingCorrectionCopy>();
        int parallel = Math.Clamp(workers <= 0 ? 2 : workers / 4, 1, 4);
        int codecThreads = workers <= 0 ? 0 : Math.Max(1, workers / parallel);
        foreach (var color in addition.Textures.Where(t => t.SourceSlot.EndsWith("_color_array", StringComparison.Ordinal)
                     && t.Artwork.Layers is { Count: > 0 }))
        {
            string stem = color.SourceSlot[..^"_color_array".Length];
            string targetStem = color.TargetSlot[..^"_color_array".Length];
            var normal = addition.Textures.SingleOrDefault(t => t.SourceSlot == stem + "_normal_array")
                ?? throw new InvalidDataException($"{color.TargetSlot}: its converted normal array is missing.");
            if (color.RequiredRoles is null || normal.RequiredRoles is null
                || !normal.RequiredRoles.SequenceEqual(color.RequiredRoles))
                throw new InvalidDataException($"{color.TargetSlot}: color and normal role maps differ.");
            var sourceColors = TextureSlots.ReadAll(game, index, color.SourceSlot);
            var targetColors = TextureSlots.ReadAll(game, index, color.TargetSlot);
            var sourceNormals = TextureSlots.ReadAll(game, index, normal.SourceSlot);
            var targetNormals = TextureSlots.ReadAll(game, index, normal.TargetSlot);
            var sourceCoeffs = TextureSlots.ReadAll(game, index, stem + "_coeff_array");
            var targetCoeffs = TextureSlots.ReadAll(game, index, targetStem + "_coeff_array");
            int sourceRequired = Math.Max(color.Artwork.Layers!.Keys.Max() + 1, color.RequiredRoles.Count);
            string sourceStamp = UniformPhysicalLayouts.StampForArray(color.SourceSlot);
            var hotspotLayouts = addition.HotspotLayouts.Where(l => l.SourceSlot == sourceStamp).ToList();
            if (hotspotLayouts.Count > 1) throw new InvalidDataException($"{sourceStamp}: duplicate hotspot overrides.");
            int overrideSlots = hotspotLayouts.SingleOrDefault() is { } layout ? UniformHotspots.Roles(layout).Count : 0;
            int physicalRequired = Math.Max(sourceRequired, Math.Max(color.DonorRoles?.Count ?? 0, overrideSlots));

            Parallel.ForEach(targetColors, new ParallelOptions { MaxDegreeOfParallelism = parallel }, targetColor =>
            {
                var sourceColor = InstalledBrandingAudit.Matching(sourceColors, targetColor.Res, "source color", color.SourceSlot);
                var sourceNormal = InstalledBrandingAudit.Companion(sourceNormals, sourceColor.Res, "source normal", normal.SourceSlot, normal.Artwork.Layers!.Count);
                var sourceCoeff = InstalledBrandingAudit.Companion(sourceCoeffs, sourceColor.Res, "source coefficient", stem + "_coeff_array", color.Artwork.Layers!.Count);
                var targetNormal = InstalledBrandingCopyMatcher.Target(index, targetNormals, targetColor, sourceNormal.Res, "target normal", normal.TargetSlot);
                var targetCoeff = InstalledBrandingCopyMatcher.Target(index, targetCoeffs, targetColor, sourceCoeff.Res, "target coefficient", targetStem + "_coeff_array");
                int have = Ps4Codec.Slices(targetColor.Res.Header);
                int normalHave = Ps4Codec.Slices(targetNormal.Res.Header), coeffHave = Ps4Codec.Slices(targetCoeff.Res.Header);
                if (physicalRequired > have || physicalRequired > normalHave || physicalRequired > coeffHave) throw new InvalidDataException(
                    $"{addition.Team} {addition.SourceKit}: {targetColor.Name} chunk {targetColor.Chunk} has "
                    + $"{have} color/{normalHave} normal/{coeffHave} coefficient slots, but its complete physical layout needs {physicalRequired}. "
                    + $"No layer may be dropped; expand the existing array first and preserve its real hotspots.");
                var stockColor = InstalledBrandingAudit.StockSlices(sourceColor, targetColor.Res, "color");
                var stockNormal = InstalledBrandingAudit.StockSlices(sourceNormal, targetNormal.Res, "normal");
                var stockCoeff = InstalledBrandingAudit.StockSlices(sourceCoeff, targetCoeff.Res, "coeff");
                var colorArt = InstalledBrandingAudit.LoadArtwork(color.Artwork.Layers, have, targetColor.Res, resolveArt, codecThreads);
                var normalArt = InstalledBrandingAudit.LoadArtwork(normal.Artwork.Layers!, normalHave, targetNormal.Res, resolveArt, codecThreads);
                if (colorArt.Count != sourceRequired || normalArt.Count != sourceRequired)
                    throw new InvalidDataException($"{addition.Team} {addition.SourceKit}: the converted color/normal arrays are incomplete.");
                var coeffArt = colorArt.ToDictionary(kv => kv.Key, kv => LogoCoefficient.FromColor(kv.Value));
                int capacity = Math.Min(have, Math.Min(normalHave, coeffHave));
                var sourceBundle = UniformPhysicalLayouts.BundleForTexture(index, sourceColor, color.SourceSlot);
                var physical = UniformPhysicalLayouts.Resolve(index, addition, color, capacity, sourceBundle);
                if (normal.DonorRoles is { } normalRoles && !normalRoles.SequenceEqual(physical.Roles, StringComparer.Ordinal))
                    throw new InvalidDataException($"{normal.SourceSlot}: color and normal physical roles differ.");
                var placement = UniformBrandingLayers.Plan(color, colorArt, stockColor, capacity, physical.Roles);
                if (colorArt.Keys.Any(source => placement.Destination(source) >= normalHave || placement.Destination(source) >= coeffHave))
                    throw new InvalidDataException($"{addition.Team} {addition.SourceKit}: a placed logo has no corresponding normal/coefficient slot.");
                var expectedColor = UniformBrandingLayers.Place(colorArt, stockColor, placement, "color");
                var expectedNormal = UniformBrandingLayers.Place(normalArt, stockNormal, placement, "normal");
                var expectedCoeff = UniformBrandingLayers.Place(coeffArt, stockCoeff, placement, "coeff");
                var changed = new List<string>();
                Encode("color", targetColor, expectedColor);
                Encode("normal", targetNormal, expectedNormal);
                Encode("coeff", targetCoeff, expectedCoeff);
                copies.Add(new BrandingCorrectionCopy(addition.Team, addition.SourceKit, targetColor.Name, targetColor.Chunk,
                    targetColor.Res.Width, targetColor.Res.Height, have, placement.Moves, changed));

                void Encode(string role, TextureSlots.Slot target, Dictionary<int, Rgba> expected)
                {
                    int targetHave = Ps4Codec.Slices(target.Res.Header);
                    if (expected.Count != targetHave || Enumerable.Range(0, targetHave).Any(layer => !expected.ContainsKey(layer)))
                        throw new InvalidDataException($"{target.Name}: the final {role} array does not contain every slot.");
                    byte[] raw = Ps4Codec.Build(target.Res, target.Stock, expected, codecThreads);
                    if (raw.AsSpan().SequenceEqual(target.Stock)) return;
                    jobs.Add(new BrandingCorrectionJob(addition.Team, addition.SourceKit, target.Name, role,
                        target.Chunk, targetHave, target.Stock, raw));
                    lock (changed) changed.Add(role);
                }
            });
        }
        return new InstalledBrandingCorrectionPlan(
            jobs.OrderBy(j => j.Team, StringComparer.Ordinal).ThenBy(j => j.Kit, StringComparer.Ordinal)
                .ThenBy(j => j.Slot, StringComparer.Ordinal).ThenBy(j => j.Chunk).ToArray(),
            copies.OrderBy(c => c.Team, StringComparer.Ordinal).ThenBy(c => c.Kit, StringComparer.Ordinal)
                .ThenBy(c => c.Slot, StringComparer.Ordinal).ThenBy(c => c.Chunk).ToArray(),
            InstalledHotspotCorrection.BuildAddition(index, addition));
    }

    /// <summary>Writes a completely prepared correction, reads every reference back, and proves restoration on failure.</summary>
    public static string? Apply(string game, InstalledBrandingCorrectionPlan plan, string? backupRoot,
        bool restoreAfterVerify, Action<string> say, int workers = 0)
    {
        if (ApplyTarget.RunningGame() is { } running) throw new InvalidOperationException(running + " is running. Close the game first.");
        if (ApplyTarget.Problems(game) is { Count: > 0 } problems)
            throw new InvalidOperationException("Unsafe apply target: " + string.Join("; ", problems));
        var grouped = plan.Jobs.GroupBy(j => j.Chunk).ToList();
        var conflict = grouped.FirstOrDefault(g => g.Any(j => !j.Data.AsSpan().SequenceEqual(g.First().Data)));
        if (conflict is not null) throw new InvalidDataException($"Conflicting final payloads for shared chunk {conflict.Key}.");
        var jobs = grouped.Select(g => g.First()).ToList();
        bool stampChanges = plan.Stamps.Any(j => !j.Before.AsSpan().SequenceEqual(j.Data));
        if (jobs.Count == 0 && !stampChanges)
        {
            say("All final branding pixels and physical hotspots are already current. No game files changed.");
            return null;
        }
        string backup = ApplyTarget.Backup(game, backupRoot);
        say("Backup of the current uniform state: " + backup);
        var index = BundleIndex.Build(game);
        NewUniformTransaction.Run(backup,
            writeAssets: () =>
            {
                InstalledHotspotCorrection.Write(game, index, plan.Stamps, say, workers);
                // EBX is written first. ChunkReplace reads the latest TOC bytes before merging its own chunk references.
                foreach (string line in ChunkReplace.ReplaceMany(game, index,
                    jobs.Select(j => (j.Chunk, j.Data)).ToList(), workers)) SuiteLog.Write("branding correction: " + line);
            },
            writeDatabases: () => { },
            readBack: () =>
            {
                VerifyChunks(game, jobs, before: false);
                InstalledHotspotCorrection.Verify(game, plan.Stamps, before: false);
            },
            restoreAndVerify: () => RestoreAndVerify(game, backup, jobs, plan.Stamps));
        say($"BRANDING INSTALLED AND READ BACK: {jobs.Count} independent chunks, {plan.Stamps.Count} stamp copies; "
            + $"{plan.Copies.Count} complete array copies checked.");
        if (restoreAfterVerify)
        {
            RestoreAndVerify(game, backup, jobs, plan.Stamps);
            say("ISOLATED BRANDING WRITE, READ-BACK AND RESTORE PASSED: the previous uniform state was restored exactly.");
        }
        return backup;
    }

    static void RestoreAndVerify(string game, string backup, IReadOnlyList<BrandingCorrectionJob> jobs,
        IReadOnlyList<BrandingStampJob> stamps)
    {
        ApplyTarget.RestoreFromBackup(game, backup);
        var left = ApplyTarget.Compare(game, backup);
        if (left.Count > 0) throw new InvalidOperationException("Rollback differences: " + string.Join("; ", left.Take(10)));
        // TOC/CAS comparison alone is not enough: independently resolve original texture chunks and stamp EBX payloads.
        VerifyChunks(game, jobs, before: true);
        InstalledHotspotCorrection.Verify(game, stamps, before: true);
    }

    static void VerifyChunks(string game, IReadOnlyList<BrandingCorrectionJob> jobs, bool before)
    {
        var fresh = BundleIndex.Build(game);
        foreach (var job in jobs)
        {
            byte[] expected = before ? job.Before : job.Data;
            var references = ChunkReplace.ReadAll(game, fresh, job.Chunk);
            if (references.Count == 0) throw new InvalidDataException($"No readable reference remains for {job.Slot} chunk {job.Chunk}.");
            foreach (var reference in references)
            {
                int at = checked((int)reference.LogicalOffset);
                if (at + reference.Raw.Length > expected.Length
                    || !reference.Raw.AsSpan().SequenceEqual(expected.AsSpan(at, reference.Raw.Length)))
                    throw new InvalidDataException($"{(before ? "Restored" : "Installed")} bytes differ for {job.Slot} via {reference.Where}.");
            }
        }
    }
}
