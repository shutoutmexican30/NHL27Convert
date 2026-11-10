using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Suite.Core.Frostbite;

/// <summary>
/// Applies completed <see cref="BrandingClonePlan"/> objects append-only, with exact preflight, a local backup,
/// coordinated rollback and complete read-back. Plans sharing a TOC are committed in one TOC rebuild; callers must not
/// loop over <see cref="Apply"/> for a multi-uniform operation. The caller chooses a workspace/staging copy first and
/// performs the normal game-target safety checks.
/// </summary>
public static class BrandingCloneWriter
{
    public sealed record Result(string BackupDirectory, string BundleName, int ClonedEntries, int SharedEntries, long AppendedBytes);
    public sealed record BatchResult(string BackupDirectory, IReadOnlyList<Result> Bundles)
    {
        public int ClonedEntries => Bundles.Sum(b => b.ClonedEntries);
        public int SharedEntries => Bundles.Sum(b => b.SharedEntries);
        public long AppendedBytes => Bundles.Sum(b => b.AppendedBytes);
    }

    sealed record TocState(string Path, string Relative, byte[] Original, TocArchive Archive, List<BrandingClonePlan> Plans);
    sealed record Payload(BrandingClonePlan Plan, BrandingClonePlan.Entry Entry, string Cas, byte[] Bytes);

    public static Result Apply(BrandingClonePlan plan, string targetGame, string backupDirectory) =>
        ApplyBatch([plan], targetGame, backupDirectory).Bundles.Single();

    /// <summary>Checks the complete set before a caller creates a backup or changes any game file.</summary>
    public static void ValidatePlanSet(IEnumerable<BrandingClonePlan> plans)
    {
        var list = plans.ToList();
        if (list.Count == 0) throw new ArgumentException("At least one clone plan is required.", nameof(plans));
        foreach (var plan in list)
        {
            plan.ThrowIfIncomplete();
            plan.ThrowIfConventionalRootMissing();
        }
        var duplicateBundle = list.GroupBy(p => p.NewBundleName, StringComparer.Ordinal).FirstOrDefault(g => g.Count() != 1);
        if (duplicateBundle is not null) throw new InvalidOperationException($"Clone plans reuse bundle name: {duplicateBundle.Key}");
        var duplicateChunk = list.SelectMany(p => p.Entries.Where(e => e.NewChunkId is not null).Select(e => e.NewChunkId!.Value))
            .GroupBy(id => id).FirstOrDefault(g => g.Count() != 1);
        if (duplicateChunk is not null) throw new InvalidOperationException($"Clone plans reuse chunk GUID: {duplicateChunk.Key}");
        var duplicateResource = list.SelectMany(p => p.Entries.Where(e => e.IsClone && e.NewChunkId is null)
                .Select(e => e.Name).Distinct(StringComparer.Ordinal))
            .GroupBy(name => name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() != 1);
        if (duplicateResource is not null) throw new InvalidOperationException(
            $"Clone plans would register the same EBX/RES resource twice: {duplicateResource.Key}");
    }

    /// <summary>Preflights every plan before any write, then commits all affected TOCs and CAS files as one transaction.</summary>
    public static BatchResult ApplyBatch(IEnumerable<BrandingClonePlan> plans, string targetGame, string backupDirectory,
        Action<string>? progress = null)
    {
        var list = plans.ToList();
        ValidatePlanSet(list);
        targetGame = Path.GetFullPath(targetGame).TrimEnd(Path.DirectorySeparatorChar);
        backupDirectory = Path.GetFullPath(backupDirectory);

        string TargetPath(BrandingClonePlan plan, string source)
        {
            string relative = Path.GetRelativePath(plan.SourceGame, source);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("A planned source lies outside its game folder.");
            return Path.GetFullPath(Path.Combine(targetGame, relative));
        }

        // Read each source and target TOC once, while still comparing every plan before opening any output stream.
        var tocStates = new Dictionary<string, TocState>(StringComparer.OrdinalIgnoreCase);
        var sourceTocs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in list)
        {
            string targetToc = TargetPath(plan, plan.SourceBundle.Toc);
            if (!File.Exists(targetToc)) throw new FileNotFoundException("Target TOC is missing.", targetToc);
            byte[] original = tocStates.TryGetValue(targetToc, out var existing) ? existing.Original : File.ReadAllBytes(targetToc);
            if (!sourceTocs.TryGetValue(plan.SourceBundle.Toc, out var sourceOriginal))
                sourceTocs.Add(plan.SourceBundle.Toc, sourceOriginal = File.ReadAllBytes(plan.SourceBundle.Toc));
            if (!original.AsSpan().SequenceEqual(sourceOriginal))
                throw new InvalidOperationException("Target TOC changed after the clone plan was built; rebuild every plan in the batch.");
            if (!tocStates.TryGetValue(targetToc, out var state))
            {
                state = new TocState(targetToc, Path.GetRelativePath(targetGame, targetToc), original,
                    TocArchive.Parse(original), []);
                tocStates.Add(targetToc, state);
            }
            state.Plans.Add(plan);
            if (state.Archive.FindBundle(plan.NewBundleName) is not null)
                throw new InvalidOperationException($"Bundle already exists: {plan.NewBundleName}");
            foreach (var entry in plan.Entries.Where(e => e.NewChunkId is not null))
                if (state.Archive.FindChunk(entry.NewChunkId!.Value) is not null)
                    throw new InvalidOperationException($"Chunk already exists: {entry.NewChunkId}");
        }

        var payloads = list.SelectMany(plan => plan.Entries.Where(e => e.IsClone)
            .Select(e => new Payload(plan, e, TargetPath(plan, e.Source.Cas), e.Replacement!))).ToList();
        var manifests = list.ToDictionary(plan => plan, plan => TargetPath(plan, plan.SourceBundle.Manifest.Cas));
        var casSources = new Dictionary<string, List<(BrandingClonePlan Plan, string Source)>>(StringComparer.OrdinalIgnoreCase);
        void AddCas(BrandingClonePlan plan, string source, string target)
        {
            if (!casSources.TryGetValue(target, out var sources)) casSources.Add(target, sources = []);
            sources.Add((plan, source));
        }
        foreach (var payload in payloads) AddCas(payload.Plan, payload.Entry.Source.Cas, payload.Cas);
        foreach (var (plan, cas) in manifests) AddCas(plan, plan.SourceBundle.Manifest.Cas, cas);

        var casLengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cas, sources) in casSources)
        {
            if (!File.Exists(cas)) throw new FileNotFoundException("Target CAS is missing.", cas);
            long actual = new FileInfo(cas).Length;
            foreach (var (plan, source) in sources)
            {
                long planned = new FileInfo(source).Length;
                if (actual != planned) throw new InvalidOperationException($"Target CAS length changed after planning: {cas}");
            }
            casLengths.Add(cas, actual);
        }
        var appendedByCas = payloads.GroupBy(p => p.Cas, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(p => (long)p.Bytes.Length), StringComparer.OrdinalIgnoreCase);
        foreach (var (plan, cas) in manifests)
            appendedByCas[cas] = appendedByCas.GetValueOrDefault(cas) + plan.Manifest.Length;
        foreach (var (cas, appended) in appendedByCas)
            if (casLengths[cas] + appended > uint.MaxValue)
                throw new InvalidOperationException($"{cas} would grow past 4 GB; no files were changed.");

        Directory.CreateDirectory(backupDirectory);
        if (Directory.EnumerateFileSystemEntries(backupDirectory).Any())
            throw new InvalidOperationException("The clone backup directory must be empty.");
        var states = tocStates.Values.OrderBy(s => s.Relative, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < states.Count; i++)
            File.WriteAllBytes(Path.Combine(backupDirectory, $"original-{i}.toc"), states[i].Original);
        File.WriteAllText(Path.Combine(backupDirectory, "snapshot.json"), JsonSerializer.Serialize(new
        {
            Format = 2,
            Bundles = list.Select(p => p.NewBundleName).ToArray(),
            Tocs = states.Select((state, i) => new
            {
                Toc = state.Relative,
                File = $"original-{i}.toc",
                Sha256 = Convert.ToHexString(SHA256.HashData(state.Original)),
            }).ToArray(),
            CasLengths = casLengths.ToDictionary(kv => Path.GetRelativePath(targetGame, kv.Key), kv => kv.Value),
        }, new JsonSerializerOptions { WriteIndented = true }));

        var writtenTocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var locations = list.ToDictionary(plan => plan, _ => new Dictionary<int, (uint Offset, uint Size)>());
            var manifestLocations = new Dictionary<BrandingClonePlan, (uint Offset, uint Size)>();
            var globalChunks = states.ToDictionary(state => state.Path, _ => new List<TocArchive.Chunk>(), StringComparer.OrdinalIgnoreCase);
            string TocFor(BrandingClonePlan plan) => TargetPath(plan, plan.SourceBundle.Toc);

            progress?.Invoke($"Writing cloned payload data for {list.Count} asset bundle(s)...");
            foreach (var group in payloads.GroupBy(p => p.Cas, StringComparer.OrdinalIgnoreCase))
            {
                using var stream = new FileStream(group.Key, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 20);
                foreach (var payload in group)
                {
                    uint offset = checked((uint)stream.Position);
                    stream.Write(payload.Bytes);
                    locations[payload.Plan].Add(payload.Entry.PairAt, (offset, (uint)payload.Bytes.Length));
                    if (payload.Entry.NewChunkId is { } id)
                        globalChunks[TocFor(payload.Plan)].Add(new TocArchive.Chunk(id,
                            LocationWord(targetGame, group.Key), offset, (uint)payload.Bytes.Length));
                }
                stream.Flush(true);
            }
            foreach (var group in list.GroupBy(plan => manifests[plan], StringComparer.OrdinalIgnoreCase))
            {
                using var stream = new FileStream(group.Key, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 20);
                foreach (var plan in group)
                {
                    uint offset = checked((uint)stream.Position);
                    stream.Write(plan.Manifest);
                    manifestLocations.Add(plan, (offset, (uint)plan.Manifest.Length));
                }
                stream.Flush(true);
            }
            if (casLengths.Any(kv => new FileInfo(kv.Key).Length > uint.MaxValue))
                throw new InvalidOperationException("A CAS grew past 4 GB during apply.");

            progress?.Invoke($"Rebuilding {states.Count} asset index file(s)...");
            foreach (var state in states)
            {
                var additions = new List<TocArchive.NewBundle>();
                var originalRecords = BundleIndex.Records(state.Path, state.Original).ToDictionary(r => r.RecordAt);
                foreach (var plan in state.Plans)
                {
                    if (!originalRecords.TryGetValue(plan.SourceBundle.RecordAt, out var sourceRecord))
                        throw new InvalidOperationException("Could not parse the original bundle record: " + plan.SourceBundle.FirstName);
                    byte[] originalRecord = ReadOriginalRecord(state.Original, plan.SourceBundle, sourceRecord);
                    additions.Add(plan.NewBundle(plan.BuildRecord(originalRecord, locations[plan], manifestLocations[plan])));
                }
                byte[] rebuilt = state.Archive.Rebuild(additions, globalChunks[state.Path]);
                string temporary = state.Path + ".cims-clone-tmp";
                File.WriteAllBytes(temporary, rebuilt);
                var check = TocArchive.Parse(File.ReadAllBytes(temporary));
                foreach (var plan in state.Plans)
                    if (check.FindBundle(plan.NewBundleName) is null)
                        throw new InvalidOperationException($"New bundle is absent from the rebuilt TOC: {plan.NewBundleName}");
                File.Move(temporary, state.Path, true);
                writtenTocs.Add(state.Path);
            }

            foreach (var state in states)
            {
                progress?.Invoke($"Reading back {state.Plans.Count} cloned bundle(s) from {state.Relative}...");
                byte[] rebuilt = File.ReadAllBytes(state.Path);
                var archive = TocArchive.Parse(rebuilt);
                var records = BundleIndex.Records(state.Path, rebuilt).ToDictionary(r => r.RecordAt);
                foreach (var plan in state.Plans) VerifyReadBack(plan, targetGame, archive, records);
            }
            var results = list.Select(plan => new Result(backupDirectory, plan.NewBundleName,
                plan.Entries.Count(e => e.IsClone), plan.Entries.Count(e => !e.IsClone), plan.Manifest.Length
                + plan.Entries.Where(e => e.IsClone).Sum(e => (long)e.Replacement!.Length))).ToArray();
            return new BatchResult(backupDirectory, results);
        }
        catch (Exception applyError)
        {
            try
            {
                foreach (var state in states)
                    if (writtenTocs.Contains(state.Path) || !File.ReadAllBytes(state.Path).AsSpan().SequenceEqual(state.Original))
                        File.WriteAllBytes(state.Path, state.Original);
                foreach (var (cas, length) in casLengths)
                    using (var stream = new FileStream(cas, FileMode.Open, FileAccess.Write, FileShare.Read))
                        stream.SetLength(length);
                foreach (var state in states) VerifyRollback(state.Path, state.Original, casLengths);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException($"Clone batch failed and rollback also failed. Backup: {backupDirectory}", applyError, rollbackError);
            }
            throw new InvalidOperationException($"Clone batch failed; every original TOC and CAS length was restored. Backup: {backupDirectory}", applyError);
        }
    }

    /// <summary>Restores a target from this writer's single- or multi-TOC backup and verifies every byte and CAS length.</summary>
    public static void Restore(string targetGame, string backupDirectory)
    {
        targetGame = Path.GetFullPath(targetGame).TrimEnd(Path.DirectorySeparatorChar);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(backupDirectory, "snapshot.json")));
        var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in json.RootElement.GetProperty("CasLengths").EnumerateObject())
            lengths.Add(Path.Combine(targetGame, item.Name), item.Value.GetInt64());
        var tocs = new List<(string Path, byte[] Original)>();
        if (json.RootElement.TryGetProperty("Tocs", out var entries))
        {
            foreach (var entry in entries.EnumerateArray())
            {
                byte[] original = File.ReadAllBytes(Path.Combine(backupDirectory, entry.GetProperty("File").GetString()!));
                if (Convert.ToHexString(SHA256.HashData(original)) != entry.GetProperty("Sha256").GetString())
                    throw new InvalidDataException("Backup TOC checksum does not match its snapshot.");
                tocs.Add((Path.Combine(targetGame, entry.GetProperty("Toc").GetString()!), original));
            }
        }
        else
        {
            string toc = Path.Combine(targetGame, json.RootElement.GetProperty("Toc").GetString()!);
            byte[] original = File.ReadAllBytes(Path.Combine(backupDirectory, "original.toc"));
            if (Convert.ToHexString(SHA256.HashData(original)) != json.RootElement.GetProperty("TocSha256").GetString())
                throw new InvalidDataException("Backup TOC checksum does not match its snapshot.");
            tocs.Add((toc, original));
        }
        foreach (var (cas, length) in lengths)
            using (var stream = new FileStream(cas, FileMode.Open, FileAccess.Write, FileShare.Read))
                stream.SetLength(length);
        foreach (var (toc, original) in tocs) File.WriteAllBytes(toc, original);
        foreach (var (toc, original) in tocs) VerifyRollback(toc, original, lengths);
    }

    static void VerifyReadBack(BrandingClonePlan plan, string targetGame, TocArchive archive,
        IReadOnlyDictionary<int, (int RecordAt, List<BundleIndex.Location> Entries)> records)
    {
        string ExpectedCas(BundleIndex.Location source) => Path.GetFullPath(Path.Combine(targetGame,
            Path.GetRelativePath(plan.SourceGame, source.Cas)));
        var bundle = archive.FindBundle(plan.NewBundleName)
            ?? throw new InvalidOperationException("Read-back could not resolve the new bundle name.");
        int recordAt = checked(TocFile.HeaderSize + (int)bundle.RecordOffset);
        if (!records.TryGetValue(recordAt, out var record))
            throw new InvalidOperationException("Read-back could not parse the new bundle record.");
        if (record.Entries.Count != plan.Entries.Count + 1)
            throw new InvalidOperationException("Read-back bundle entry count differs from the plan.");
        if (!string.Equals(record.Entries[0].Cas, ExpectedCas(plan.SourceBundle.Manifest), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Read-back manifest resolves to the wrong CAS archive.");
        if (!BundleIndex.ReadBytes(record.Entries[0]).AsSpan().SequenceEqual(plan.Manifest))
            throw new InvalidOperationException("Read-back manifest differs from the plan.");
        for (int i = 0; i < plan.Entries.Count; i++)
        {
            var expected = plan.Entries[i];
            var actual = record.Entries[i + 1];
            if (!string.Equals(actual.Cas, ExpectedCas(expected.Source), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Read-back entry resolves to the wrong CAS archive: {expected.Name}: "
                    + $"expected {ExpectedCas(expected.Source)}, got {actual.Cas}.");
            if (expected.IsClone)
            {
                byte[] payload;
                try { payload = BundleIndex.ReadBytes(actual); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Read-back could not read {expected.Name}: {actual.Cas} "
                        + $"offset {actual.Offset}, size {actual.Size}; source offset {expected.Source.Offset}.", ex);
                }
                if (!payload.AsSpan().SequenceEqual(expected.Replacement))
                    throw new InvalidOperationException($"Read-back payload differs: {expected.Name}");
            }
            else if (actual.Offset != expected.Source.Offset || actual.Size != expected.Source.Size)
                throw new InvalidOperationException($"Shared entry location changed: {expected.Name}");
            if (expected.NewChunkId is { } id)
            {
                var chunk = archive.FindChunk(id) ?? throw new InvalidOperationException($"Read-back chunk is missing: {id}");
                if (chunk.Offset != actual.Offset || chunk.Size != actual.Size)
                    throw new InvalidOperationException($"Global chunk location differs: {id}");
            }
        }
    }

    static void VerifyRollback(string toc, byte[] originalToc, IReadOnlyDictionary<string, long> casLengths)
    {
        if (!File.ReadAllBytes(toc).AsSpan().SequenceEqual(originalToc))
            throw new InvalidOperationException("TOC rollback verification failed.");
        foreach (var (cas, length) in casLengths)
            if (new FileInfo(cas).Length != length) throw new InvalidOperationException($"CAS rollback verification failed: {cas}");
    }

    static byte[] ReadOriginalRecord(byte[] toc, BundleIndex.Bundle bundle,
        (int RecordAt, List<BundleIndex.Location> Entries) record)
    {
        int pairsEnd = record.Entries.Max(e => e.PairAt + 8);
        int flagsAt = bundle.RecordAt + (int)BinaryPrimitives.ReadUInt32BigEndian(toc.AsSpan(bundle.RecordAt + 8, 4));
        int length = Math.Max(pairsEnd, flagsAt + record.Entries.Count) - bundle.RecordAt;
        return toc.AsSpan(bundle.RecordAt, length).ToArray();
    }

    static uint LocationWord(string game, string cas)
    {
        bool patch = cas.StartsWith(GameLayout.PatchDir(game) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        string root = patch ? GameLayout.PatchDir(game) : GameLayout.DataDir(game);
        string relative = Path.GetRelativePath(root, cas).Replace('\\', '/');
        var parts = relative.Split('/');
        if (parts.Length != 4 || parts[0] != "Gen4b" || parts[1] != "superbundlelayout"
            || !parts[2].StartsWith("nhl_installpackage_", StringComparison.Ordinal)
            || !int.TryParse(parts[2][^2..], out int catalog)
            || !parts[3].StartsWith("cas_", StringComparison.Ordinal) || !parts[3].EndsWith(".cas", StringComparison.Ordinal)
            || !int.TryParse(parts[3][4..6], out int casIndex))
            throw new InvalidOperationException($"Cannot encode CAS location: {cas}");
        return (uint)((patch ? 1 << 16 : 0) | (catalog << 8) | casIndex);
    }
}
