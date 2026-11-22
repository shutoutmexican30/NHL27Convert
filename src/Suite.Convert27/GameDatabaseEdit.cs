using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

namespace Suite.Convert27;

/// <summary>
/// Reads one of the game's own T3DB databases (nhl_game, initial_experience, the draft/threes rosters) straight out of the
/// apply target's legacy files, and writes an in-place edit of it back (the raw Database editor's "Game database" source).
/// The write refuses a target that fails <see cref="ApplyTarget.Problems"/>, refuses when the game's copy changed since
/// it was read, takes a full TOC/CAS backup, writes every copy (base and update index), reads every copy back, and
/// restores from the backup on any failure. Undo is recorded like any other apply.
/// </summary>
public static class GameDatabaseEdit
{
    public static readonly string[] Databases = ["nhl_game", "initial_experience", "entry_draft", "expansion_draft", "threes_campaign"];

    public static byte[] Read(string game, SuiteSettings settings, string database) =>
        new LegacyFiles(game, settings).Read(RosterSyncRun.LegacyName(database));

    /// <summary>Writes <paramref name="edited"/> over <paramref name="database"/> in <paramref name="game"/>; returns the backup folder.</summary>
    public static string Write(string game, SuiteSettings settings, string database, byte[] original, byte[] edited, Action<string> say,
        string? backupRoot = null)
    {
        if (edited.Length != original.Length) throw new InvalidOperationException($"{database}: the edit changed the database size ({original.Length} -> {edited.Length}).");
        if (ApplyTarget.Problems(game) is { Count: > 0 } problems) throw new InvalidOperationException("Write refused: " + string.Join("; ", problems));
        string name = RosterSyncRun.LegacyName(database);
        if (!new LegacyFiles(game, settings).Read(name).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException($"{database} changed in the game since it was opened. Reopen it; nothing was written.");
        if (edited.AsSpan().SequenceEqual(original)) { say($"{database}: no changes, nothing written."); return ""; }

        string backup = ApplyTarget.Backup(game, backupRoot);
        void Restore()
        {
            ApplyTarget.RestoreFromBackup(game, backup);
            if (ApplyTarget.Compare(game, backup) is { Count: > 0 } diffs) throw new InvalidDataException("Rollback differs: " + string.Join("; ", diffs.Take(10)));
        }
        NewUniformTransaction.Run(backup,
            writeAssets: () => { },
            writeDatabases: () => { foreach (var line in new LegacyFiles(game, settings).Replace(new Dictionary<string, byte[]> { [name] = edited })) say(line); },
            readBack: () =>
            {
                var back = new LegacyFiles(game, settings).Read(name);
                if (!back.AsSpan().SequenceEqual(edited)) throw new InvalidDataException($"{database}: the game's copy did not read back as written.");
            },
            restoreAndVerify: Restore);
        if (backupRoot is null) ModApply.RecordUndo(game, backup);
        say($"{database} written into {game} and read back. Backup: {backup}");
        return backup;
    }
}
