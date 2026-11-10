using System.Text.Json;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

namespace Suite.Convert27;

/// <summary>Reads and transactionally applies the constrained equipment editor to both runtime databases.</summary>
public static class InstalledEquipmentEditor
{
    public sealed record Session(string Game, EquipmentEditorDocument Document);

    public static Session Load(string game, SuiteSettings settings)
    {
        var legacy = new LegacyFiles(game, settings);
        string meta = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "meta-cache")), "nhl_game-meta.xml");
        File.WriteAllBytes(meta, legacy.Read("gamemodes/database/t3db/nhl_game-meta.xml"));
        return new Session(GameLayout.Normalize(game), EquipmentEditorDocument.Load(ReadDatabases(legacy), meta));
    }

    public static string? Apply(SuiteSettings settings, Session session, EquipmentEditPlan plan, Action<string> say,
        bool verifyRestore = false)
    {
        if (!string.Equals(GameLayout.Normalize(settings.ApplyTargetDir), session.Game, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected build changed after this edit was loaded. Reload before applying.");
        if (verifyRestore && !string.Equals(session.Game.TrimEnd(Path.DirectorySeparatorChar), @"D:\CIMS-test\play", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The automatic write/read-back/restore test is restricted to the isolated test copy.");
        if (ApplyTarget.Problems(session.Game) is { Count: > 0 } problems)
            throw new InvalidOperationException("Apply refused: " + string.Join("; ", problems));
        var legacy = new LegacyFiles(session.Game, settings);
        var original = plan.OriginalDatabases.ToDictionary(kv => RosterSyncRun.LegacyName(kv.Key), kv => kv.Value, StringComparer.Ordinal);
        VerifyFiles(legacy, original, "Current");
        if (!plan.HasChanges)
        {
            plan.Verify(ReadDatabases(legacy));
            say("Everything is already current. No game files changed.");
            return null;
        }
        string backup = ApplyTarget.Backup(session.Game);
        var corrected = plan.Databases.ToDictionary(kv => RosterSyncRun.LegacyName(kv.Key), kv => kv.Value, StringComparer.Ordinal)
            .Where(kv => !kv.Value.AsSpan().SequenceEqual(original[kv.Key]))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        void RestoreAndVerify()
        {
            ApplyTarget.RestoreFromBackup(session.Game, backup);
            if (ApplyTarget.Compare(session.Game, backup) is { Count: > 0 } differences)
                throw new InvalidDataException("Rollback differs: " + string.Join("; ", differences.Take(10)));
            VerifyFiles(new LegacyFiles(session.Game, settings), original, "Restored");
        }
        NewUniformTransaction.Run(backup,
            writeAssets: () => { },
            writeDatabases: () => { foreach (string line in new LegacyFiles(session.Game, settings).Replace(corrected)) say(line); },
            readBack: () =>
            {
                plan.Verify(ReadDatabases(new LegacyFiles(session.Game, settings)));
                VerifyFiles(new LegacyFiles(session.Game, settings), corrected, "Installed");
            },
            restoreAndVerify: RestoreAndVerify);
        if (verifyRestore)
        {
            RestoreAndVerify();
            say("EDITOR ISOLATED WRITE, READ-BACK AND RESTORE PASSED: the original 64-kit state was restored exactly.");
        }
        string report = Path.Combine(backup, "equipment-edit-report.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            target = session.Game, restoredAfterVerification = verifyRestore,
            insertedRows = plan.Inserted.Count, changedRows = plan.ChangedRows,
            databases = corrected.Keys, rows = plan.Rows,
            verification = "Both logical databases matched exactly; edited keys, all 15 zones, unrelated tables and stock rows verified.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!verifyRestore) ModApply.RecordUndo(session.Game, backup);
        say($"Equipment edit verified: {plan.ChangedRows} recolored rows and {plan.Inserted.Count} duplicated rows across both databases. Backup: {backup}");
        return backup;
    }

    static Dictionary<string, byte[]> ReadDatabases(LegacyFiles legacy) => new(StringComparer.Ordinal)
    {
        ["nhl_game"] = legacy.Read(RosterSyncRun.LegacyName("nhl_game")),
        ["initial_experience"] = legacy.Read(RosterSyncRun.LegacyName("initial_experience")),
    };

    static void VerifyFiles(LegacyFiles legacy, IReadOnlyDictionary<string, byte[]> expected, string stage)
    {
        foreach (var (file, bytes) in expected)
            if (!legacy.Read(file).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException($"{stage} database differs: {file}. No newer state was overwritten.");
    }
}

/// <summary>Read-only ordered-role and pixel inspection of every installed independent uniform array copy.</summary>
public static class UniformSlotInspection
{
    public sealed record Row(string Team, string Kit, string Slot, int Depth, int RequiredDepth,
        IReadOnlyList<string> Roles, string Status, Guid? Chunk = null, IReadOnlyList<string>? Issues = null)
    {
        public string TeamName => Team == "arizona" ? "Utah"
            : System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(Team.Replace('_', ' '));
        public string RoleOrder => string.Join("  ·  ", Roles.Select((role, index) => $"{index} {role}"));
        public string Copy => Chunk?.ToString("N")[..12] ?? "";
        public IReadOnlyList<string> Problems => Issues ?? [];
    }

    public sealed record Result(IReadOnlyList<Row> Rows, IReadOnlyList<string> Errors, int IntendedSponsors = 0)
    {
        public bool IsValid => Rows.Count > 0 && Errors.Count == 0 && Rows.All(r => r.Status == "Validated");
    }

    /// <summary>
    /// Uses the same converted-art placement and color/normal/coefficient read-back as the proven full-set audit.
    /// Structural capacity is not an artwork role map: a larger installed array is valid only when every extra slice
    /// is explicitly blank, and every converted logo has been found in its expected placed slot.
    /// </summary>
    public static Result Build(string game, BundleIndex index, IEnumerable<NewUniformAddition> additions,
        Func<string, string> resolveArt, int workers = 0, Action<string>? progress = null)
    {
        var wanted = RegisteredKitSource.Normalize(additions, index, progress);
        var evidence = new List<BrandingLayerEvidence>();
        var errors = new List<string>();
        int sponsors = 0;
        foreach (var addition in wanted)
        {
            progress?.Invoke($"Verifying placed artwork: {addition.Team} {addition.SourceKit}");
            try
            {
                var audit = InstalledBrandingAudit.Addition(game, index, addition, resolveArt, workers);
                evidence.AddRange(audit.Layers);
                errors.AddRange(audit.Problems);
                sponsors += audit.IntendedSponsors;
            }
            catch (Exception ex)
            {
                // A failed read is never represented as a successful or depth-only inspection.
                errors.Add($"{addition.Team} {addition.SourceKit}: {ex.Message}");
            }
        }
        return InspectCopies(wanted, evidence, errors, sponsors);
    }

    /// <summary>Groups the actual placed role of every slice by its individual installed resource copy.</summary>
    internal static Result InspectCopies(IEnumerable<NewUniformAddition> additions,
        IEnumerable<BrandingLayerEvidence> evidence, IReadOnlyList<string> auditProblems, int intendedSponsors = 0)
    {
        var rows = new List<Row>();
        var errors = new List<string>();
        var uniforms = additions.ToList();
        foreach (var group in evidence.Where(e => e.Slot.EndsWith("_color_array", StringComparison.Ordinal)
                     || e.Slot.EndsWith("_normal_array", StringComparison.Ordinal))
                     .GroupBy(e => (e.Team, e.Kit, e.Slot, e.Chunk)).OrderBy(g => g.Key.Team).ThenBy(g => g.Key.Kit)
                     .ThenBy(g => g.Key.Slot, StringComparer.Ordinal).ThenBy(g => g.Key.Chunk))
        {
            var addition = uniforms.SingleOrDefault(a => a.Team == group.Key.Team && a.SourceKit == group.Key.Kit);
            if (addition is null)
            {
                errors.Add($"{group.Key.Team} {group.Key.Kit}: installed array evidence has no matching uniform addition.");
                continue;
            }
            string identity = $"{addition.Team} {addition.SourceKit} {group.Key.Slot} chunk {group.Key.Chunk}";
            var local = auditProblems.Where(p => p.StartsWith(identity, StringComparison.Ordinal)
                || p.StartsWith($"{addition.Team} {addition.SourceKit}:", StringComparison.Ordinal)).ToList();
            var observations = group.GroupBy(e => e.Layer).OrderBy(g => g.Key).ToList();
            // A normal/coefficient companion can be reached through two color resources (Montreal away). Identical
            // repeated evidence describes the same installed chunk, not two copies. Conflicts must remain visible.
            if (observations.Any(g => g.Distinct().Count() != 1))
                local.Add(identity + ": conflicting observations were read for the same installed array layer.");
            if (observations.Any(g => g.Key < 0))
                local.Add(identity + ": the installed layer evidence has an invalid negative layer index.");
            var layers = observations.Select(g => g.First()).ToList();
            int depth = layers.Max(e => e.Layer) + 1;
            var roles = Enumerable.Repeat("missing", Math.Max(0, depth)).ToArray();
            foreach (var layer in layers.Where(e => e.Layer >= 0)) roles[layer.Layer] = layer.ExpectedRole;
            if (layers.Select(e => e.Layer).Distinct().Count() != depth)
                local.Add(identity + ": not every installed array slice has a placed semantic role.");
            if (layers.Any(e => e.Difference > e.Limit))
                local.Add(identity + ": one or more installed pixels differ from the verified converted artwork.");
            if (layers.Any(e => e.RetainedStock || e.ExpectedRole == "stock"))
                local.Add(identity + ": retained stock artwork is not an accepted converted uniform layer.");

            var texture = addition.Textures.SingleOrDefault(t => t.TargetSlot == group.Key.Slot);
            var declared = texture?.RequiredRoles;
            if (declared is not { Count: > 0 })
                local.Add(identity + ": the saved source-art role map is missing.");
            string prefix = addition.TargetPrefix.Trim().Replace('\\', '/').Trim('/') + "/";
            try
            {
                // Validate the complete observed layout, including blank spare slices. Do not substitute the shorter
                // source-art list and then assume that its order survived installation.
                var plan = UniformSlotPlan.BuildRoleAware(prefix, [],
                    [new UniformSlotState(group.Key.Slot, roles.Length, roles)],
                    [new UniformSlotNeed(group.Key.Slot, roles.Length, RequiredRoles: roles)]);
                foreach (string error in plan.Errors) local.Add(identity + ": " + error);
                if (plan.Slots.SingleOrDefault()?.Action != UniformSlotAction.ReuseTeamSlot)
                    local.Add(identity + ": the installed resource did not match its complete observed role layout.");
            }
            catch (Exception ex) { local.Add(identity + ": " + ex.Message); }

            if (group.Key.Slot.Contains("_branding_jersey_", StringComparison.Ordinal))
            {
                foreach (string role in new[] { "crest", "shield", "brand" })
                {
                    int actual = roles.Count(r => r == role);
                    if (actual != 1) local.Add($"{identity}: a jersey must have exactly one {role} in its installed role map; found {actual}.");
                }
                int shoulders = roles.Count(r => r == "shoulder");
                if (shoulders is not (0 or 2)) local.Add($"{identity}: expected zero or two shoulders; found {shoulders}.");
                if (roles.Count(r => r == "sponsor") > 1) local.Add(identity + ": more than one front sponsor is installed.");
                if (declared is not null)
                    foreach (string role in new[] { "crest", "shoulder", "shield", "brand" })
                    {
                        int expected = declared.Count(r => r == role), actual = roles.Count(r => r == role);
                        if (actual != expected) local.Add($"{identity}: installed {role} count is {actual}; converted artwork declares {expected}.");
                    }
            }
            errors.AddRange(local);
            rows.Add(new Row(addition.Team, addition.SourceKit, group.Key.Slot, roles.Length, declared?.Count ?? 0,
                roles, local.Count == 0 ? "Validated" : "Needs artwork/role repair", group.Key.Chunk, local));
        }
        return new Result(rows, auditProblems.Concat(errors).ToArray(), intendedSponsors);
    }

    /// <summary>Cheap synthetic depth/role check for unit tests; not a live installed-art audit.</summary>
    public static Result Build(IReadOnlyDictionary<string, int> installedDepths, IEnumerable<NewUniformAddition> additions)
    {
        var rows = new List<Row>(); var errors = new List<string>();
        foreach (var addition in additions)
        {
            string prefix = addition.TargetPrefix.Trim().Replace('\\', '/').Trim('/') + "/";
            var declared = addition.Textures.Where(t => t.RequiredRoles is { Count: > 0 })
                .ToDictionary(t => t.TargetSlot, t => t.RequiredRoles!, StringComparer.Ordinal);
            var states = installedDepths.Where(e => e.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(e => new UniformSlotState(e.Key, e.Value,
                    declared.TryGetValue(e.Key, out var roles) && roles.Count == e.Value ? roles : null)).ToList();
            foreach (var texture in addition.Textures.Where(t => t.RequiredRoles is { Count: > 0 }))
            {
                var roles = texture.RequiredRoles!;
                int actual = installedDepths.GetValueOrDefault(texture.TargetSlot);
                var need = new UniformSlotNeed(texture.TargetSlot, roles.Count, RequiredRoles: roles);
                UniformSlotPlan plan;
                try { plan = UniformSlotPlan.BuildRoleAware(prefix, [], states, [need]); }
                catch (Exception ex) { errors.Add($"{addition.Team} {addition.SourceKit}: {ex.Message}"); continue; }
                foreach (string error in plan.Errors) errors.Add($"{addition.Team} {addition.SourceKit}: {error}");
                bool crestProblem = texture.TargetSlot.Contains("_branding_jersey_", StringComparison.Ordinal) && roles.Count(r => r == "crest") != 1;
                if (crestProblem) errors.Add($"{texture.TargetSlot}: a jersey must have exactly one crest in its ordered role map.");
                if (actual != roles.Count) errors.Add($"{texture.TargetSlot}: {actual} installed layers, expected {roles.Count}.");
                bool valid = plan.IsValid && !crestProblem && actual == roles.Count
                    && plan.Slots.SingleOrDefault()?.Action == UniformSlotAction.ReuseTeamSlot;
                rows.Add(new Row(addition.Team, addition.SourceKit, texture.TargetSlot, actual, roles.Count, roles,
                    valid ? "Validated" : "Needs role-map repair"));
            }
        }
        return new Result(rows, errors);
    }
}
