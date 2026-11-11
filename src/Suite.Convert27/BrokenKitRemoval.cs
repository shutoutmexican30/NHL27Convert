using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

namespace Suite.Convert27;

/// <summary>
/// Removes an incomplete earlier install from its variant slot (Detroit's Nov 8 kit: bundles and jersey/equipment rows,
/// never registered), so Repair can add the kit fresh in the same slot. Only bundles named for that slot's folder, the
/// chunks only they use, and the team's rows for that variant are removed. Payload bytes stay in the CAS files,
/// unreferenced. Runs with a backup and a read-back, and restores on any failure.
/// </summary>
public static class BrokenKitRemoval
{
    public sealed record TocRemoval(string Toc, IReadOnlySet<string> Bundles, IReadOnlySet<Guid> Chunks);
    public sealed record Plan(string Folder, int TeamId, int Variant, IReadOnlyList<TocRemoval> Tocs)
    {
        public int BundleCount => Tocs.Sum(t => t.Bundles.Count);
        public int ChunkCount => Tocs.Sum(t => t.Chunks.Count);
    }

    public static Plan Build(BundleIndex index, string folder, int teamId, int variant)
    {
        string p = folder.Trim().TrimEnd('/') + "/";
        var removals = new List<TocRemoval>();
        foreach (string toc in index.Bundles.Select(b => b.Toc).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var archive = TocArchive.Parse(File.ReadAllBytes(toc));
            var names = archive.Bundles.Where(b => FullKitInstall.OwnsBundle(b.Name, p)).Select(b => b.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Count == 0) continue;
            var records = archive.Bundles.Where(b => names.Contains(b.Name) && b.RecordOffset != ulong.MaxValue)
                .Select(b => checked((int)b.RecordOffset + TocFile.HeaderSize)).ToHashSet();
            var removed = index.Bundles.Where(b => string.Equals(b.Toc, toc, StringComparison.OrdinalIgnoreCase) && records.Contains(b.RecordAt)).ToHashSet();
            bool Kept(BundleIndex.Bundle b) => !removed.Contains(b);
            var ids = new HashSet<Guid>();
            foreach (var r in index.ResByName.Values.SelectMany(x => x).Where(r => removed.Contains(r.Bundle)))
            {
                try { ids.Add(TextureRes.Parse(UniformPhysicalLayouts.Decode(BundleIndex.ReadBytes(r.Data))).ChunkGuid); }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
            }
            foreach (var c in index.ChunksById.Values.SelectMany(x => x).Where(c => removed.Contains(c.Bundle))) ids.Add(c.Id);
            // A chunk any kept bundle still carries stays.
            var chunks = ids.Where(id => archive.FindChunk(id) is not null
                && !(index.ChunksById.GetValueOrDefault(id)?.Any(c => Kept(c.Bundle)) ?? false)).ToHashSet();
            removals.Add(new TocRemoval(toc, names, chunks));
        }
        return new Plan(p, teamId, variant, removals);
    }

    /// <summary>Deletes the team's jersey and equipment rows for the variant; returns how many of each were removed.</summary>
    public static (int Jerseys, int Equipment) RemoveRows(T3Db db, int teamId, int variant)
    {
        int jerseys = 0, equipment = 0;
        if (db.Tables.TryGetValue(JerseyRowPlanner.TableName, out var jt))
            for (int r = jt.InUse - 1; r >= 0; r--)
                if (db.GetInt(jt, r, "teamid") == teamId && db.GetInt(jt, r, "variant") == variant)
                { jt = db.DeleteRecord(jt, r); jerseys++; }
        if (db.Tables.TryGetValue(NewUniformEquipmentPlan.TableName, out var et))
            for (int r = et.InUse - 1; r >= 0; r--)
                if (db.GetInt(et, r, "team") == teamId && db.GetInt(et, r, "variant") == variant)
                { et = db.DeleteRecord(et, r); equipment++; }
        return (jerseys, equipment);
    }

    /// <summary>Removes the planned pieces from the game folder. Returns the backup taken first.</summary>
    public static string Apply(string game, IReadOnlyList<Plan> plans, SuiteSettings settings, string? backupRoot, Action<string>? say = null)
    {
        string backup = ApplyTarget.Backup(game, backupRoot);
        say?.Invoke("backup: " + backup);
        try
        {
            foreach (var group in plans.SelectMany(p => p.Tocs).GroupBy(t => t.Toc, StringComparer.OrdinalIgnoreCase))
            {
                var bundles = group.SelectMany(t => t.Bundles).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var chunks = group.SelectMany(t => t.Chunks).ToHashSet();
                var archive = TocArchive.Parse(File.ReadAllBytes(group.Key));
                byte[] rebuilt = archive.Rebuild(removeBundles: bundles, removeChunks: chunks);
                var check = TocArchive.Parse(rebuilt);
                if (bundles.Any(b => check.FindBundle(b) is not null) || chunks.Any(c => check.FindChunk(c) is not null)
                    || check.Bundles.Count != archive.Bundles.Count - bundles.Count || check.Chunks.Count != archive.Chunks.Count - chunks.Count)
                    throw new InvalidDataException("TOC removal did not read back: " + group.Key);
                string tmp = group.Key + ".cims-repair-tmp";
                File.WriteAllBytes(tmp, rebuilt);
                File.Move(tmp, group.Key, true);
                say?.Invoke($"TOC {Path.GetRelativePath(game, group.Key)}: -{bundles.Count} bundles, -{chunks.Count} chunks");
            }

            var legacy = new LegacyFiles(game, settings);
            string meta = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "meta-cache")), "nhl_game-meta.xml");
            File.WriteAllBytes(meta, legacy.Read("gamemodes/database/t3db/nhl_game-meta.xml"));
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (string name in new[] { "nhl_game", "initial_experience" })
            {
                var db = T3Db.Load(legacy.Read(RosterSyncRun.LegacyName(name)), meta);
                int jerseys = 0, equipment = 0;
                foreach (var p in plans) { var (j, e) = RemoveRows(db, p.TeamId, p.Variant); jerseys += j; equipment += e; }
                if (jerseys + equipment == 0) continue;
                files[RosterSyncRun.LegacyName(name)] = db.Save();
                say?.Invoke($"DB {name}: -{jerseys} jersey rows, -{equipment} equipment rows");
            }
            if (files.Count > 0) foreach (string line in legacy.Replace(files)) say?.Invoke("DB: " + line);

            // Read back: nothing of the removed slots is left in the game.
            var fresh = BundleIndex.Build(game);
            foreach (var p in plans)
            {
                if (InstalledKits.FolderHasAssets(fresh, p.Folder)) throw new InvalidDataException($"{p.Folder}: assets are still present after removal.");
                foreach (string name in new[] { "nhl_game", "initial_experience" })
                {
                    var db = T3Db.Load(new LegacyFiles(game, settings).Read(RosterSyncRun.LegacyName(name)), meta);
                    if (RemoveRows(db, p.TeamId, p.Variant) != (0, 0)) throw new InvalidDataException($"{name}: rows for team {p.TeamId} variant {p.Variant} are still present.");
                }
            }
            say?.Invoke("removal read back: the slots are empty");
            return backup;
        }
        catch
        {
            ApplyTarget.RestoreFromBackup(game, backup);
            var left = ApplyTarget.Compare(game, backup);
            if (left.Count > 0) throw new InvalidOperationException("Removal failed and the restore left differences: " + string.Join("; ", left.Take(5)));
            throw;
        }
    }
}
