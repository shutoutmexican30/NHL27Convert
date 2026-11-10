using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Suite.Core.Frostbite;

/// <summary>
/// Plans one independent branding-resource clone inside an existing CAS bundle. Only the requested EBX dependency
/// closure, same-name texture RES entries and their chunks are cloned. Every other manifest entry remains a reference
/// to its original CAS bytes. The plan never writes and cannot be applied while required team artwork is absent.
/// </summary>
public sealed class BrandingClonePlan
{
    public sealed record Entry(string Name, int PairAt, BundleIndex.Location Source, byte[]? Replacement, Guid? NewChunkId = null)
    {
        public bool IsClone => Replacement is not null;
    }

    sealed record RecordPart(BrandingClonePlan Plan, byte[] Record, IReadOnlyDictionary<int, int> FlagOffsets,
        IReadOnlyDictionary<int, uint> LocationWords);
    sealed record MergedEntry(Entry Entry, RecordPart Part, int FlagOffset, byte Flag, uint LocationWord);

    public string SourceGame { get; }
    public string SourceBundleName { get; }
    public string NewBundleName { get; }
    public uint SourceSizeAndFlags { get; }
    public BundleIndex.Bundle SourceBundle { get; }
    public byte[] Manifest { get; }
    public IReadOnlyList<Entry> Entries { get; }
    public IReadOnlyList<string> MissingArtwork { get; }
    public IReadOnlyDictionary<Guid, Guid> EbxGuidRemap { get; }
    public IReadOnlyDictionary<Guid, Guid> ChunkGuidRemap { get; }
    public bool IsComplete => MissingArtwork.Count == 0;

    readonly IReadOnlyList<RecordPart>? _recordParts;
    readonly IReadOnlyList<MergedEntry>? _mergedEntries;

    BrandingClonePlan(string sourceGame, string sourceBundleName, string newBundleName, uint sourceSizeAndFlags,
        BundleIndex.Bundle sourceBundle, byte[] manifest, List<Entry> entries, List<string> missingArtwork,
        Dictionary<Guid, Guid> ebxGuidRemap, Dictionary<Guid, Guid> chunkGuidRemap,
        IReadOnlyList<RecordPart>? recordParts = null, IReadOnlyList<MergedEntry>? mergedEntries = null)
    {
        SourceGame = sourceGame;
        SourceBundleName = sourceBundleName;
        NewBundleName = newBundleName;
        SourceSizeAndFlags = sourceSizeAndFlags;
        SourceBundle = sourceBundle;
        Manifest = manifest;
        Entries = entries;
        MissingArtwork = missingArtwork;
        EbxGuidRemap = ebxGuidRemap;
        ChunkGuidRemap = chunkGuidRemap;
        _recordParts = recordParts;
        _mergedEntries = mergedEntries;
    }

    public void ThrowIfIncomplete()
    {
        if (!IsComplete) throw new InvalidOperationException("Team texture artwork is required; donor pixels cannot be used as a fallback:\n"
            + string.Join("\n", MissingArtwork));
    }

    /// <summary>
    /// A conventional uniform bundle needs its same-name root EBX. Renaming only the TOC record and texture EBX
    /// leaves the engine resolving the old home/away blueprint, even though every new texture reads back correctly.
    /// </summary>
    public void ThrowIfConventionalRootMissing()
    {
        string root = NewBundleName.StartsWith("gen4b/", StringComparison.Ordinal) ? NewBundleName["gen4b/".Length..] : NewBundleName;
        if (!root.EndsWith("_player_team_brt", StringComparison.Ordinal)) return;
        if (!Entries.Any(e => e.IsClone && e.NewChunkId is null && e.Name == root))
            throw new InvalidDataException($"The new uniform bundle does not contain its conventional root EBX: {root}");
    }

    /// <summary>Copies the original record and changes only the manifest and cloned-entry CAS offset/size pairs.</summary>
    public byte[] BuildRecord(byte[] originalRecord, IReadOnlyDictionary<int, (uint Offset, uint Size)> cloneLocations,
        (uint Offset, uint Size) manifestLocation)
    {
        ThrowIfIncomplete();
        if (_mergedEntries is not null) return BuildMergedRecord(cloneLocations, manifestLocation);
        var record = (byte[])originalRecord.Clone();
        if (record.Length < 32) throw new InvalidDataException("Source bundle record is truncated.");
        foreach (var entry in Entries.Where(e => e.IsClone))
        {
            if (!cloneLocations.TryGetValue(entry.PairAt, out var location))
                throw new InvalidOperationException($"No CAS location was assigned for {entry.Name}.");
            WritePair(record, entry.PairAt - SourceBundle.RecordAt, location.Offset, location.Size);
        }
        WritePair(record, SourceBundle.Manifest.PairAt - SourceBundle.RecordAt, manifestLocation.Offset, manifestLocation.Size);
        return record;
    }

    byte[] BuildMergedRecord(IReadOnlyDictionary<int, (uint Offset, uint Size)> cloneLocations,
        (uint Offset, uint Size) manifestLocation)
    {
        var primary = _recordParts![0];
        var mergedEntries = _mergedEntries!;
        int flagsAt = mergedEntries.Max(e => e.Entry.PairAt + 8) - SourceBundle.RecordAt;
        int count = mergedEntries.Count + 1;
        var record = new byte[flagsAt + count];
        Array.Copy(primary.Record, record, 32);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(8, 4), (uint)flagsAt);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(12, 4), (uint)count);

        void WriteEntry(byte flag, uint locationWord, int targetPair, int flagIndex, uint offset, uint size)
        {
            if (flag > 1) throw new InvalidDataException("Unsupported bundle-entry flag in merged record.");
            record[flagsAt + flagIndex] = flag;
            int relative = targetPair - SourceBundle.RecordAt;
            if (flag == 1)
                BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(relative - 4, 4), locationWord);
            WritePair(record, relative, offset, size);
        }

        // A merged record starts a new CAS-inheritance sequence. Always make its manifest location explicit.
        WriteEntry(1, primary.LocationWords[SourceBundle.Manifest.PairAt], SourceBundle.Manifest.PairAt, 0,
            manifestLocation.Offset, manifestLocation.Size);
        foreach (var merged in mergedEntries)
        {
            var entry = merged.Entry;
            var location = entry.IsClone
                ? cloneLocations.TryGetValue(entry.PairAt, out var assigned)
                    ? assigned
                    : throw new InvalidOperationException($"No CAS location was assigned for {entry.Name}.")
                : (entry.Source.Offset, entry.Source.Size);
            WriteEntry(merged.Flag, merged.LocationWord, entry.PairAt, merged.FlagOffset,
                location.Offset, location.Size);
        }
        return record;
    }

    public TocArchive.NewBundle NewBundle(byte[] record) => new(NewBundleName, SourceSizeAndFlags, record);

    public static BrandingClonePlan Build(
        BundleIndex index,
        string donorPrefix,
        string targetPrefix,
        string donorName,
        string targetName,
        IReadOnlyDictionary<string, byte[]>? teamChunkArtwork = null,
        string? newBundleName = null) =>
        Build(index, donorPrefix, targetPrefix, [(donorName, targetName)], teamChunkArtwork, newBundleName);

    /// <summary>
    /// Multi-root build: several texture roots that live in the SAME donor bundle are cloned as one plan, so their
    /// target resources ship in one conventional target bundle. Building separate plans per root would either collide
    /// on the target bundle name or register sibling resources the engine never loads.
    /// </summary>
    public static BrandingClonePlan Build(
        BundleIndex index,
        string donorPrefix,
        string targetPrefix,
        IReadOnlyList<(string DonorName, string TargetName)> roots,
        IReadOnlyDictionary<string, byte[]>? teamChunkArtwork = null,
        string? newBundleName = null)
    {
        if (roots.Count == 0) throw new ArgumentException("At least one clone root is required.", nameof(roots));
        donorPrefix = Prefix(donorPrefix);
        targetPrefix = Prefix(targetPrefix);
        if (donorPrefix == targetPrefix) throw new InvalidDataException("Donor and target prefixes must differ.");
        var rootNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (donor, target) in roots)
        {
            string d = Name(donor), t = Name(target);
            if (!d.StartsWith(donorPrefix, StringComparison.Ordinal) || !t.StartsWith(targetPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("The root resource must be inside its donor and target prefixes.");
            rootNames.Add(d, t);
        }
        teamChunkArtwork ??= new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string firstRoot = rootNames.Keys.First();
        if (!index.EbxByName.TryGetValue(firstRoot, out var rootEntries) || rootEntries.Count == 0)
            throw new InvalidDataException($"Donor EBX was not found: {firstRoot}");
        var root = rootEntries[0];
        var bundle = root.Bundle;
        bool InSourceBundle(BundleIndex.Bundle other) => other.Toc == bundle.Toc && other.RecordAt == bundle.RecordAt;
        var toc = TocArchive.Parse(File.ReadAllBytes(bundle.Toc));
        var sourceBundles = toc.Bundles.Where(b => b.RecordOffset == (ulong)(bundle.RecordAt - TocFile.HeaderSize)).ToList();
        if (sourceBundles.Count != 1) throw new InvalidDataException("Could not identify the donor bundle's TOC entry.");
        string sourceName = sourceBundles[0].Name;
        newBundleName ??= sourceName + "__cims_" + FamilyIdentity(targetPrefix);
        if (toc.FindBundle(newBundleName) is not null) throw new InvalidDataException($"Target bundle already exists: {newBundleName}");

        // Only dependencies in this bundle and beneath the donor team/kit prefix are eligible for cloning. This keeps
        // chunk metadata and all shared record locations intact and prevents an accidental whole-team duplication.
        var nodes = new Dictionary<string, (BundleIndex.Ebx Asset, EbxRiff Parsed, byte[] Raw, byte[] Stored)>(StringComparer.Ordinal);
        foreach (var asset in index.EbxByName.Values.SelectMany(v => v).Where(e => InSourceBundle(e.Bundle)
                     && e.Name.StartsWith(donorPrefix, StringComparison.Ordinal)))
        {
            if (nodes.ContainsKey(asset.Name)) continue;
            byte[] stored = BundleIndex.ReadBytes(asset.Data);
            byte[] raw = Decode(stored);
            var parsed = EbxRiff.Parse(raw);
            nodes.Add(asset.Name, (asset, parsed, raw, stored));
        }
        foreach (string donorRoot in rootNames.Keys)
            if (!nodes.ContainsKey(donorRoot)) throw new InvalidDataException($"The donor root is not in its own source bundle: {donorRoot}");
        var byGuid = new Dictionary<Guid, string>();
        foreach (var node in nodes.Values)
            if (!byGuid.TryAdd(node.Parsed.FileGuid, node.Asset.Name))
                throw new InvalidDataException($"Two donor EBX assets share file GUID {node.Parsed.FileGuid}.");

        string donorIdentity = FamilyIdentity(donorPrefix), targetIdentity = FamilyIdentity(targetPrefix);
        string MapName(string oldName)
        {
            if (rootNames.TryGetValue(oldName, out string? explicitTarget)) return explicitTarget;
            string relative = oldName[donorPrefix.Length..];
            return targetPrefix + relative.Replace(donorIdentity, targetIdentity, StringComparison.Ordinal);
        }
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        void Select(string oldName)
        {
            if (selected.ContainsKey(oldName)) return;
            string mapped = MapName(oldName);
            if (!mapped.StartsWith(targetPrefix, StringComparison.Ordinal)) throw new InvalidDataException($"Invalid clone path: {mapped}");
            selected.Add(oldName, mapped);
            foreach (var import in nodes[oldName].Parsed.ImportedFileGuids)
                if (byGuid.TryGetValue(import, out string? dependency)) Select(dependency);
        }
        foreach (string donorRoot in rootNames.Keys) Select(donorRoot);
        foreach (string target in selected.Values)
        {
            if (index.EbxByName.ContainsKey(target)) throw new InvalidDataException($"Target EBX already exists: {target}");
            if (selected.Values.Count(v => v == target) != 1) throw new InvalidDataException($"Two donor assets map to {target}.");
        }
        var ebxRemap = selected.ToDictionary(kv => nodes[kv.Key].Parsed.FileGuid, kv => StableGuid("ebx:" + kv.Value));
        if (ebxRemap.Count != selected.Count) throw new InvalidDataException("Duplicate donor EBX file GUID.");

        var manifest = BundleManifest.Parse(BundleIndex.ReadBytes(bundle.Manifest));
        var replacements = new Dictionary<int, Entry>();
        var chunkRemap = new Dictionary<Guid, Guid>();
        var missingArtwork = new List<string>();
        var claimedChunks = new Dictionary<Guid, string>();

        // Associate a texture RES by its full resource name, preferring the copy in this same bundle.
        foreach (var (oldName, mappedName) in selected)
        {
            if (!index.ResByName.TryGetValue(oldName, out var resources)) continue;
            var resource = resources.FirstOrDefault(r => InSourceBundle(r.Bundle));
            if (resource is null) throw new InvalidDataException($"Texture RES is outside the donor bundle: {oldName}");
            int resIndex = manifest.Res.FindIndex(r => r.Name == oldName);
            if (resIndex < 0) throw new InvalidDataException($"Texture RES is absent from the donor manifest: {oldName}");
            if (index.ResByName.ContainsKey(mappedName)) throw new InvalidDataException($"Target RES already exists: {mappedName}");
            byte[] resStored = BundleIndex.ReadBytes(resource.Data);
            byte[] resRaw = Decode(resStored);
            var texture = TextureRes.Parse(resRaw);
            if (!claimedChunks.TryAdd(texture.ChunkGuid, oldName))
                throw new InvalidDataException($"Two cloned textures share chunk {texture.ChunkGuid}; explicit chunk planning is required.");
            var chunkCandidates = index.ChunksById.GetValueOrDefault(texture.ChunkGuid)?
                .Where(c => InSourceBundle(c.Bundle)).ToList() ?? [];
            if (chunkCandidates.Count != 1) throw new InvalidDataException($"Texture chunk is not uniquely present in the donor bundle: {texture.ChunkGuid}");
            var chunk = chunkCandidates[0];
            int chunkIndex = manifest.Chunks.FindIndex(c => c.Id == texture.ChunkGuid);
            if (chunkIndex < 0) throw new InvalidDataException($"Texture chunk is absent from the donor manifest: {texture.ChunkGuid}");
            Guid newChunk = StableGuid("chunk:" + mappedName);
            chunkRemap.Add(texture.ChunkGuid, newChunk);
            newChunk.TryWriteBytes(resRaw.AsSpan(32, 16));
            var readBack = TextureRes.Parse(resRaw);
            if (readBack.ChunkGuid != newChunk) throw new InvalidOperationException("Cloned texture chunk GUID did not read back.");
            replacements[1 + manifest.Ebx.Count + resIndex] = new Entry(mappedName, resource.Data.PairAt, resource.Data,
                EncodeLike(resStored, resRaw));

            long fullSize = texture.PayloadSize * Ps4Codec.Slices(texture.Header);
            var (chunkStored, donorArtwork) = ReadFullChunk(index, texture.ChunkGuid, fullSize, chunk);
            if (!teamChunkArtwork.TryGetValue(mappedName, out byte[]? artwork) || artwork.Length == 0)
            {
                missingArtwork.Add(mappedName);
                artwork = donorArtwork; // Inspection only; ThrowIfIncomplete blocks apply.
            }
            if (artwork.Length != donorArtwork.Length)
                throw new InvalidDataException($"{mappedName}: artwork decodes to {artwork.Length} bytes; the complete {donorArtwork.Length}-byte donor array layout is required.");
            byte[] newChunkStored = EncodeLike(chunkStored, artwork);
            if (!ChunkBlocks.Parse(newChunkStored).Decode().AsSpan().SequenceEqual(artwork))
                throw new InvalidDataException($"Encoded chunk does not read back: {mappedName}");
            replacements[1 + manifest.Ebx.Count + manifest.Res.Count + chunkIndex] =
                new Entry(mappedName, chunk.Data.PairAt, chunk.Data, newChunkStored, newChunk);
        }

        var originalEbxNames = manifest.Ebx.Select(e => e.Name).ToArray();
        var originalResNames = manifest.Res.Select(r => r.Name).ToArray();
        var originalChunkIds = manifest.Chunks.Select(c => c.Id).ToArray();
        foreach (var (oldName, mappedName) in selected)
        {
            var node = nodes[oldName];
            int ebxIndex = manifest.Ebx.FindIndex(e => e.Name == oldName);
            if (ebxIndex < 0) throw new InvalidDataException($"Donor EBX is absent from its manifest: {oldName}");
            byte[] raw = node.Parsed.Rewrite(mappedName, ebxRemap[node.Parsed.FileGuid], ebxRemap);
            byte[] stored = EncodeLike(node.Stored, raw);
            replacements[1 + ebxIndex] = new Entry(mappedName, node.Asset.Data.PairAt, node.Asset.Data, stored);
            manifest.Ebx[ebxIndex] = manifest.Ebx[ebxIndex] with
            {
                Name = mappedName, OriginalSize = checked((uint)raw.Length), Sha1 = SHA1.HashData(stored),
            };
        }
        for (int i = 0; i < manifest.Res.Count; i++)
        {
            int pair = 1 + manifest.Ebx.Count + i;
            if (!replacements.TryGetValue(pair, out var entry)) continue;
            byte[] raw = Decode(entry.Replacement!);
            manifest.Res[i] = manifest.Res[i] with
            {
                Name = entry.Name, OriginalSize = checked((uint)raw.Length), Sha1 = SHA1.HashData(entry.Replacement!),
            };
        }
        for (int i = 0; i < manifest.Chunks.Count; i++)
        {
            var old = manifest.Chunks[i];
            if (!chunkRemap.TryGetValue(old.Id, out Guid mapped)) continue;
            var entry = replacements[1 + manifest.Ebx.Count + manifest.Res.Count + i];
            manifest.Chunks[i] = old with
            {
                Id = mapped,
                LogicalOffset = 0,
                LogicalSize = checked((uint)ChunkBlocks.Parse(entry.Replacement!).Decode().Length),
                Sha1 = SHA1.HashData(entry.Replacement!),
            };
        }

        var entries = new List<Entry>();
        for (int i = 0; i < originalEbxNames.Length; i++)
        {
            var original = index.EbxByName[originalEbxNames[i]].First(e => InSourceBundle(e.Bundle));
            entries.Add(replacements.TryGetValue(1 + i, out var clone) ? clone
                : new Entry(original.Name, original.Data.PairAt, original.Data, null));
        }
        for (int i = 0; i < originalResNames.Length; i++)
        {
            int pair = 1 + manifest.Ebx.Count + i;
            if (replacements.TryGetValue(pair, out var clone)) { entries.Add(clone); continue; }
            string oldName = originalResNames[i];
            var original = index.ResByName[oldName].First(e => InSourceBundle(e.Bundle));
            entries.Add(new Entry(oldName, original.Data.PairAt, original.Data, null));
        }
        for (int i = 0; i < originalChunkIds.Length; i++)
        {
            int pair = 1 + manifest.Ebx.Count + manifest.Res.Count + i;
            if (replacements.TryGetValue(pair, out var clone)) { entries.Add(clone); continue; }
            Guid id = originalChunkIds[i];
            var original = index.ChunksById[id].First(e => InSourceBundle(e.Bundle));
            entries.Add(new Entry(original.Id.ToString(), original.Data.PairAt, original.Data, null));
        }
        return new BrandingClonePlan(index.Game, sourceName, newBundleName, sourceBundles[0].SizeAndFlags, bundle,
            manifest.Build(), entries, missingArtwork, ebxRemap, chunkRemap);
    }

    /// <summary>
    /// Combines donor plans whose conventional target bundle name is the same. A garment's base textures and branding
    /// arrays can require different structural donors, but the engine still expects them in one named target bundle.
    /// Manifest entries, CAS record pairs and per-chunk metadata are merged together; incompatible duplicates fail.
    /// </summary>
    public static BrandingClonePlan Merge(IEnumerable<BrandingClonePlan> plans)
    {
        var list = plans.ToList();
        if (list.Count < 2) return list.Single();
        var first = list[0];
        foreach (var plan in list)
        {
            // Data/Patch donor copies can live in different TOCs. Their CAS references remain valid inside the
            // primary target record; only the primary target TOC is rebuilt by the writer.
            if (!string.Equals(plan.SourceGame, first.SourceGame, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Only donor bundles from the same game can be merged.");
            if (!string.Equals(plan.NewBundleName, first.NewBundleName, StringComparison.Ordinal))
                throw new InvalidDataException("Only plans with the same target bundle name can be merged.");
        }

        var parts = list.Select(SourcePart).ToList();
        var manifests = list.Select(p => BundleManifest.Parse(p.Manifest)).ToList();
        var mergedManifest = BundleManifest.Parse(manifests[0].Build());
        mergedManifest.Ebx.Clear();
        mergedManifest.Res.Clear();
        mergedManifest.Chunks.Clear();
        mergedManifest.ChunkMeta = [];
        var merged = new List<MergedEntry>();
        var ebxSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        var resSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        var chunkSeen = new Dictionary<Guid, int>();
        var chunkMeta = new List<byte[]>();
        var metaItems = manifests.Select(m => ChunkMetaItems(m.ChunkMeta, m.Chunks.Count)).ToList();

        static void CheckDuplicate(Entry oldEntry, Entry newEntry, string identity)
        {
            if (oldEntry.IsClone != newEntry.IsClone)
                throw new InvalidDataException($"Merged bundle has conflicting shared/cloned entries for {identity}.");
            if (oldEntry.IsClone && !oldEntry.Replacement!.AsSpan().SequenceEqual(newEntry.Replacement!))
                throw new InvalidDataException($"Merged bundle has different cloned payloads for {identity}.");
        }
        static bool SameSha(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

        for (int p = 0; p < list.Count; p++)
        {
            var plan = list[p];
            var manifest = manifests[p];
            for (int i = 0; i < manifest.Ebx.Count; i++)
            {
                var item = manifest.Ebx[i];
                var entry = plan.Entries[i];
                if (ebxSeen.TryGetValue(item.Name, out int mergedAt))
                {
                    CheckDuplicate(merged[mergedAt].Entry, entry, item.Name);
                    int manifestAt = mergedManifest.Ebx.FindIndex(e => e.Name == item.Name);
                    var old = mergedManifest.Ebx[manifestAt];
                    if (old.OriginalSize != item.OriginalSize || !SameSha(old.Sha1, item.Sha1))
                        throw new InvalidDataException($"Merged EBX metadata differs: {item.Name}");
                    continue;
                }
                ebxSeen.Add(item.Name, merged.Count);
                mergedManifest.Ebx.Add(item);
                merged.Add(new MergedEntry(entry, parts[p], 0, 0, 0));
            }
            for (int i = 0; i < manifest.Res.Count; i++)
            {
                var item = manifest.Res[i];
                var entry = plan.Entries[manifest.Ebx.Count + i];
                if (resSeen.TryGetValue(item.Name, out int mergedAt))
                {
                    CheckDuplicate(merged[mergedAt].Entry, entry, item.Name);
                    int manifestAt = mergedManifest.Res.FindIndex(e => e.Name == item.Name);
                    var old = mergedManifest.Res[manifestAt];
                    if (old.OriginalSize != item.OriginalSize || old.Type != item.Type || old.Rid != item.Rid
                        || !SameSha(old.Sha1, item.Sha1) || !SameSha(old.Meta, item.Meta))
                        throw new InvalidDataException($"Merged RES metadata differs: {item.Name}");
                    continue;
                }
                resSeen.Add(item.Name, merged.Count);
                mergedManifest.Res.Add(item);
                merged.Add(new MergedEntry(entry, parts[p], 0, 0, 0));
            }
            for (int i = 0; i < manifest.Chunks.Count; i++)
            {
                var item = manifest.Chunks[i];
                var entry = plan.Entries[manifest.Ebx.Count + manifest.Res.Count + i];
                if (chunkSeen.TryGetValue(item.Id, out int mergedAt))
                {
                    CheckDuplicate(merged[mergedAt].Entry, entry, item.Id.ToString());
                    int manifestAt = mergedManifest.Chunks.FindIndex(c => c.Id == item.Id);
                    var old = mergedManifest.Chunks[manifestAt];
                    if (old.LogicalOffset != item.LogicalOffset || old.LogicalSize != item.LogicalSize
                        || !SameSha(old.Sha1, item.Sha1))
                        throw new InvalidDataException($"Merged chunk metadata differs: {item.Id}");
                    continue;
                }
                chunkSeen.Add(item.Id, merged.Count);
                mergedManifest.Chunks.Add(item);
                chunkMeta.Add(metaItems[p][i]);
                merged.Add(new MergedEntry(entry, parts[p], 0, 0, 0));
            }
        }
        mergedManifest.ChunkMeta = BuildChunkMeta(chunkMeta, manifests[0].ChunkMeta);
        // The source plans each store EBX/RES/chunks consecutively. A merged manifest has one global EBX section,
        // then one global RES section and one global chunk section, so the CAS record must use that same global order.
        merged = mergedManifest.Ebx.Select(item => merged[ebxSeen[item.Name]])
            .Concat(mergedManifest.Res.Select(item => merged[resSeen[item.Name]]))
            .Concat(mergedManifest.Chunks.Select(item => merged[chunkSeen[item.Id]])).ToList();

        // Renumber the combined CAS pair table and recalculate inheritance for its new order. A donor's flag-zero
        // entry inherited from its own preceding entry; copying that flag without its effective CAS can select a
        // completely different archive after plans are merged.
        int cursor = first.SourceBundle.Manifest.PairAt + 8;
        uint currentLocation = parts[0].LocationWords[first.SourceBundle.Manifest.PairAt];
        for (int i = 0; i < merged.Count; i++)
        {
            var item = merged[i];
            uint locationWord = item.Part.LocationWords[item.Entry.Source.PairAt];
            byte flag = locationWord == currentLocation ? (byte)0 : (byte)1;
            if (flag == 1)
            {
                cursor += 4;
                currentLocation = locationWord;
            }
            merged[i] = item with
            {
                Entry = item.Entry with { PairAt = cursor },
                FlagOffset = i + 1,
                Flag = flag,
                LocationWord = locationWord,
            };
            cursor += 8;
        }

        Dictionary<Guid, Guid> CombineMaps(IEnumerable<IReadOnlyDictionary<Guid, Guid>> maps, string kind)
        {
            var result = new Dictionary<Guid, Guid>();
            foreach (var map in maps)
            foreach (var (oldId, newId) in map)
            {
                if (result.TryGetValue(oldId, out var existing) && existing != newId)
                    throw new InvalidDataException($"Merged {kind} GUID remap differs for {oldId}.");
                result[oldId] = newId;
            }
            return result;
        }
        return new BrandingClonePlan(first.SourceGame, first.SourceBundleName, first.NewBundleName,
            first.SourceSizeAndFlags, first.SourceBundle, mergedManifest.Build(),
            merged.Select(m => m.Entry).ToList(), list.SelectMany(p => p.MissingArtwork).Distinct(StringComparer.Ordinal).ToList(),
            CombineMaps(list.Select(p => p.EbxGuidRemap), "EBX"),
            CombineMaps(list.Select(p => p.ChunkGuidRemap), "chunk"), parts, merged);
    }

    static RecordPart SourcePart(BrandingClonePlan plan)
    {
        byte[] toc = File.ReadAllBytes(plan.SourceBundle.Toc);
        var record = BundleIndex.Records(plan.SourceBundle.Toc, toc).Single(r => r.RecordAt == plan.SourceBundle.RecordAt);
        int flagsAt = plan.SourceBundle.RecordAt
            + (int)BinaryPrimitives.ReadUInt32BigEndian(toc.AsSpan(plan.SourceBundle.RecordAt + 8, 4));
        int length = Math.Max(record.Entries.Max(e => e.PairAt + 8), flagsAt + record.Entries.Count)
            - plan.SourceBundle.RecordAt;
        byte[] bytes = toc.AsSpan(plan.SourceBundle.RecordAt, length).ToArray();
        var flags = record.Entries.Select((entry, index) => (entry.PairAt, Offset: flagsAt - plan.SourceBundle.RecordAt + index))
            .ToDictionary(e => e.PairAt, e => e.Offset);
        var locationWords = new Dictionary<int, uint>();
        uint? currentLocation = null;
        foreach (var entry in record.Entries)
        {
            int flagOffset = flags[entry.PairAt];
            byte flag = bytes[flagOffset];
            if (flag > 1) throw new InvalidDataException("Unsupported bundle-entry flag in source record.");
            if (flag == 1)
            {
                int locationAt = entry.PairAt - plan.SourceBundle.RecordAt - 4;
                if (locationAt < 32 || locationAt + 4 > bytes.Length)
                    throw new InvalidDataException("Bundle-entry CAS location lies outside the source record.");
                currentLocation = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(locationAt, 4));
            }
            if (currentLocation is not uint effectiveLocation)
                throw new InvalidDataException("The first bundle entry does not declare its CAS location.");
            locationWords.Add(entry.PairAt, effectiveLocation);
        }
        return new RecordPart(plan, bytes, flags, locationWords);
    }

    static List<byte[]> ChunkMetaItems(byte[] raw, int expected)
    {
        if (expected == 0 && raw.Length == 0) return [];
        if (raw.Length < 3 || (raw[0] & 0x1F) != 1)
            throw new InvalidDataException("Chunk metadata is not a DB list.");
        int at = 1;
        if ((raw[0] & 0x80) == 0)
        {
            while (at < raw.Length && raw[at] != 0) at++;
            if (at >= raw.Length) throw new InvalidDataException("Unterminated chunk-metadata field name.");
            at++;
        }
        int size = Read7Bit(raw, ref at);
        int end = checked(at + size - 1);
        if (size < 1 || end >= raw.Length || raw[end] != 0 || end != raw.Length - 1)
            throw new InvalidDataException("Chunk metadata list has an invalid size or terminator.");
        var items = new List<byte[]>(expected);
        while (at < end)
        {
            int start = at;
            SkipDbValue(raw, ref at);
            if (at > end) throw new InvalidDataException("Chunk metadata item crosses the list boundary.");
            items.Add(raw[start..at]);
        }
        if (at != end || items.Count != expected)
            throw new InvalidDataException($"Chunk metadata has {items.Count} items for {expected} chunks.");
        return items;
    }

    static byte[] BuildChunkMeta(IReadOnlyList<byte[]> items, byte[] template)
    {
        if (items.Count == 0) return [];
        if (template.Length < 3 || (template[0] & 0x1F) != 1)
            throw new InvalidDataException("Chunk metadata template is not a DB list.");
        using var body = new MemoryStream();
        foreach (byte[] item in items) body.Write(item);
        using var output = new MemoryStream();
        output.WriteByte(template[0]);
        int at = 1;
        if ((template[0] & 0x80) == 0)
        {
            while (at < template.Length && template[at] != 0) output.WriteByte(template[at++]);
            if (at >= template.Length) throw new InvalidDataException("Unterminated chunk-metadata template name.");
            output.WriteByte(0);
        }
        Write7Bit(output, checked((int)body.Length + 1));
        body.Position = 0;
        body.CopyTo(output);
        output.WriteByte(0);
        return output.ToArray();
    }

    static int Read7Bit(byte[] raw, ref int at)
    {
        int value = 0, shift = 0;
        for (int i = 0; i < 5; i++)
        {
            if (at >= raw.Length) throw new InvalidDataException("Truncated DB length.");
            byte b = raw[at++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
        }
        throw new InvalidDataException("DB length is too large.");
    }

    static void Write7Bit(Stream output, int value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            output.WriteByte(b);
        } while (value != 0);
    }

    static void SkipDbValue(byte[] raw, ref int at)
    {
        if (at >= raw.Length) throw new InvalidDataException("Truncated DB value.");
        int flags = raw[at++];
        int type = flags & 0x1F;
        if ((flags & 0x80) == 0)
        {
            while (at < raw.Length && raw[at] != 0) at++;
            if (at >= raw.Length) throw new InvalidDataException("Unterminated DB value name.");
            at++;
        }
        switch (type)
        {
            case 1:
            case 2:
            {
                int size = Read7Bit(raw, ref at);
                if (size < 1 || at + size > raw.Length || raw[at + size - 1] != 0)
                    throw new InvalidDataException("Invalid nested DB value.");
                at += size;
                return;
            }
            case 6: at += 1; break;
            case 7:
            case 19:
            {
                int size = Read7Bit(raw, ref at);
                at += size;
                break;
            }
            case 8:
            case 11: at += 4; break;
            case 9:
            case 12: at += 8; break;
            case 15: at += 16; break;
            case 16: at += 20; break;
            default: throw new InvalidDataException($"Unsupported DB value type {type}.");
        }
        if (at > raw.Length) throw new InvalidDataException("Truncated DB scalar value.");
    }

    public static Guid StableGuid(string identity)
    {
        byte[] hash = SHA1.HashData(System.Text.Encoding.ASCII.GetBytes("NHL27Convert:" + identity));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50); // UUID v5 in Guid's mixed-endian byte representation.
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BundleIndex,
        IReadOnlyDictionary<Guid, List<BundleIndex.Location>>> GlobalChunkLocations = new();

    static IReadOnlyDictionary<Guid, List<BundleIndex.Location>> IndexGlobalChunks(BundleIndex index)
    {
        var locations = new Dictionary<Guid, List<BundleIndex.Location>>();
        foreach (string path in BundleIndex.Tocs(index.Game))
        {
            var toc = TocFile.Load(path);
            foreach (Guid id in toc.Chunks)
            {
                var reference = toc.Find(id)!;
                string cas = BundleIndex.CasPath(index.Game, reference.Patch, reference.Catalog, reference.Cas);
                if (!locations.TryGetValue(id, out var copies)) locations[id] = copies = [];
                copies.Add(new BundleIndex.Location(path, 0, cas, reference.Offset, reference.Size));
            }
        }
        return locations;
    }

    static (byte[] Stored, byte[] Raw) ReadFullChunk(BundleIndex index, Guid id, long fullSize, BundleIndex.Chunk preferred)
    {
        // A bundle often carries only the texture's top mip. Its global TOC copy normally holds the complete mip chain.
        // Prefer a complete donor-bundle copy, then other bundles, and finally the global chunk tables.
        var candidates = index.ChunksById.GetValueOrDefault(id)?
            .Where(c => c.LogicalOffset == 0 && c.LogicalSize == fullSize)
            .OrderByDescending(c => c.Data.PairAt == preferred.Data.PairAt && c.Data.Toc == preferred.Data.Toc)
            .ToList() ?? [];
        foreach (var candidate in candidates)
            if (TryRead(candidate.Data, out var result)) return result;

        var global = GlobalChunkLocations.GetValue(index, IndexGlobalChunks);
        foreach (var location in global.GetValueOrDefault(id) ?? [])
            if (TryRead(location, out var result)) return result;
        throw new InvalidDataException($"No complete {fullSize}-byte copy exists for texture chunk {id}.");

        bool TryRead(BundleIndex.Location location, out (byte[] Stored, byte[] Raw) result)
        {
            result = default;
            if (!File.Exists(location.Cas)) return false;
            byte[] stored = BundleIndex.ReadBytes(location);
            byte[] raw = Decode(stored);
            if (raw.Length != fullSize) return false;
            result = (stored, raw);
            return true;
        }
    }

    static byte[] Decode(byte[] stored)
    {
        try { return ChunkBlocks.Parse(stored).Decode(); }
        catch (InvalidDataException) { return stored; }
    }

    static byte[] EncodeLike(byte[] oldStored, byte[] raw)
    {
        ChunkBlocks old;
        try { old = ChunkBlocks.Parse(oldStored); }
        catch (InvalidDataException) { return raw; }
        if (old.Blocks.Sum(b => b.RawSize) != raw.Length)
            return ChunkBlocks.EncodeBlocks(raw); // EBX root-path length may change; texture sizes are checked separately.
        using var output = new MemoryStream();
        int at = 0;
        foreach (var block in old.Blocks)
        {
            output.Write(ChunkBlocks.EncodeBlock(raw.AsSpan(at, block.RawSize)));
            at += block.RawSize;
        }
        return output.ToArray();
    }

    static void WritePair(byte[] record, int at, uint offset, uint size)
    {
        if (at < 32 || at + 8 > record.Length) throw new InvalidDataException("Bundle-record pair lies outside the record.");
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(at, 4), offset);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(at + 4, 4), size);
    }

    static string Prefix(string value) => Name(value).Trim('/') + "/";
    static string Name(string value) => value.Trim().Replace('\\', '/').TrimStart('/');
    static string FamilyIdentity(string prefix)
    {
        var parts = prefix.Trim('/').Split('/');
        if (parts.Length < 3) throw new InvalidDataException($"Uniform family prefix is incomplete: {prefix}");
        return parts[^3] + "_" + parts[^1];
    }
}
