using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

namespace Suite.Convert27;

/// <summary>
/// A complete equipment-only edit for already-installed variants 24/25. Logical database names remain separate from
/// legacy file names, and both the original and corrected payloads are retained for byte-exact read-back and rollback.
/// </summary>
public sealed record InstalledEquipmentCorrectionPlan(
    IReadOnlyDictionary<string, byte[]> OriginalFiles,
    IReadOnlyDictionary<string, byte[]> CorrectedFiles,
    IReadOnlyDictionary<string, string> DatabaseNames,
    string MetaPath,
    NewUniformEquipmentPlan Equipment)
{
    public IReadOnlyDictionary<string, byte[]> Changes => CorrectedFiles
        .Where(kv => !kv.Value.AsSpan().SequenceEqual(OriginalFiles[kv.Key]))
        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
}

/// <summary>Plans and applies NHL 27 helmet/glove colors without cloning kits or touching any other game asset.</summary>
public static class InstalledEquipmentCorrection
{
    static readonly string[] DatabaseNames = ["nhl_game", "initial_experience"];

    public static InstalledEquipmentCorrectionPlan Build(string game, SuiteSettings settings,
        string source27Directory, IEnumerable<NewUniformAddition> additions, Action<string>? say = null)
    {
        var wanted = additions.ToList();
        if (wanted.Count == 0) throw new InvalidDataException("No installed uniforms were supplied for equipment correction.");
        var legacy = new LegacyFiles(game, settings);
        string metaPath = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "meta-cache")), "nhl_game-meta.xml");
        File.WriteAllBytes(metaPath, legacy.Read("gamemodes/database/t3db/nhl_game-meta.xml"));
        var sources = DatabaseNames.ToDictionary(name => name,
            name => legacy.Read(RosterSyncRun.LegacyName(name)), StringComparer.Ordinal);
        say?.Invoke("Planning all 15 NHL 27 color zones for helmets and gloves; stock and goalie equipment stay unchanged.");
        var equipment = NewUniformEquipmentPlanner.Build(sources, metaPath, source27Directory, wanted);
        ValidateComplete(equipment, sources.Keys, wanted);

        var names = sources.Keys.ToDictionary(RosterSyncRun.LegacyName, name => name, StringComparer.Ordinal);
        var original = sources.ToDictionary(kv => RosterSyncRun.LegacyName(kv.Key), kv => kv.Value, StringComparer.Ordinal);
        var corrected = equipment.Databases.ToDictionary(kv => RosterSyncRun.LegacyName(kv.Key), kv => kv.Value, StringComparer.Ordinal);
        if (original.Count != sources.Count || corrected.Count != sources.Count
            || original.Keys.Any(name => !corrected.ContainsKey(name)))
            throw new InvalidDataException("The logical-to-legacy database mapping is incomplete or ambiguous.");
        return new InstalledEquipmentCorrectionPlan(original, corrected, names, metaPath, equipment);
    }

    static void ValidateComplete(NewUniformEquipmentPlan plan, IEnumerable<string> databases,
        IReadOnlyList<NewUniformAddition> additions)
    {
        foreach (string database in databases)
        foreach (var addition in additions)
        foreach (long type in new long[] { 4, 7 })
        {
            var generic = plan.Rows.Where(r => r.Database == database && r.Team == addition.TeamId
                && r.TargetVariant == addition.TargetVariant && r.Equipment == type && r.Brand == -1).ToList();
            if (generic.Count != 1) throw new InvalidDataException(
                $"{database}: {addition.Team} {addition.SourceKit} has {generic.Count} generic equipment rows for type {type}; exactly one is required.");
        }
        var duplicate = plan.Rows.GroupBy(r => (r.Database, r.Team, r.Brand, r.JerseyArtId, r.Equipment, r.Era, r.TargetVariant))
            .FirstOrDefault(g => g.Count() != 1);
        if (duplicate is not null) throw new InvalidDataException("Ambiguous planned equipment key: " + duplicate.First().Key);
        if (plan.Rows.Any(r => r.Zones.Count != 15 || NewUniformEquipmentPlan.Zones.Any(z => !r.Zones.ContainsKey(z))))
            throw new InvalidDataException("An equipment row does not contain all 15 color zones.");
    }

    /// <summary>Writes only changed databases, verifies every row, and restores the current state on any failure.</summary>
    public static string? Apply(string game, SuiteSettings settings, InstalledEquipmentCorrectionPlan plan,
        string? backupRoot, bool restoreAfterVerify, Action<string> say)
    {
        if (ApplyTarget.RunningGame() is { } running) throw new InvalidOperationException(running + " is running. Close the game first.");
        if (ApplyTarget.Problems(game) is { Count: > 0 } problems)
            throw new InvalidOperationException("Unsafe apply target: " + string.Join("; ", problems));
        var changes = plan.Changes;
        if (changes.Count == 0)
        {
            VerifyDatabases(game, settings, plan, before: false);
            say("All planned helmet/glove rows are already current. No game files changed.");
            return null;
        }

        // Refuse a stale plan rather than overwriting a database changed after planning.
        VerifyFiles(game, settings, plan.OriginalFiles, "Current");
        string backup = ApplyTarget.Backup(game, backupRoot);
        say("Backup of the current corrected 64-kit state: " + backup);
        NewUniformTransaction.Run(backup,
            writeAssets: () => { },
            writeDatabases: () =>
            {
                foreach (string line in new LegacyFiles(game, settings).Replace(changes)) say("DB: " + line);
            },
            readBack: () => VerifyDatabases(game, settings, plan, before: false),
            restoreAndVerify: () => RestoreAndVerify(game, settings, backup, plan.OriginalFiles));
        say($"EQUIPMENT INSTALLED AND READ BACK: {plan.Equipment.Rows.Count} planned rows, "
            + $"{plan.Equipment.InsertedRows} inserted across both databases; {changes.Count} legacy files written.");
        if (restoreAfterVerify)
        {
            RestoreAndVerify(game, settings, backup, plan.OriginalFiles);
            say("ISOLATED EQUIPMENT WRITE, READ-BACK AND RESTORE PASSED: the current 64-kit state was restored exactly.");
        }
        return backup;
    }

    static void RestoreAndVerify(string game, SuiteSettings settings, string backup,
        IReadOnlyDictionary<string, byte[]> originals)
    {
        ApplyTarget.RestoreFromBackup(game, backup);
        var left = ApplyTarget.Compare(game, backup);
        if (left.Count > 0) throw new InvalidOperationException("Rollback differences: " + string.Join("; ", left.Take(10)));
        // A fresh LegacyFiles instance must independently recover the exact original logical payloads too.
        VerifyFiles(game, settings, originals, "Restored");
    }

    static void VerifyDatabases(string game, SuiteSettings settings, InstalledEquipmentCorrectionPlan plan, bool before)
    {
        var expected = before ? plan.OriginalFiles : plan.CorrectedFiles;
        VerifyFiles(game, settings, expected, before ? "Restored" : "Installed");
        if (before) return;
        var legacy = new LegacyFiles(game, settings);
        foreach (var (file, database) in plan.DatabaseNames)
        {
            var db = T3Db.Load(legacy.Read(file), plan.MetaPath);
            plan.Equipment.Verify(database, db);
        }
    }

    static void VerifyFiles(string game, SuiteSettings settings, IReadOnlyDictionary<string, byte[]> expected, string stage)
    {
        var legacy = new LegacyFiles(game, settings);
        foreach (var (name, bytes) in expected)
            if (!legacy.Read(name).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException(stage + " logical database bytes differ: " + name);
    }
}
