using System.Buffers.Binary;
using System.Security.Cryptography;
using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>One existing texture resource whose chunk gains safe blank slices without changing its identity.</summary>
public sealed record InstalledArrayExpansionJob(
    string Team, string Kit, string Name, string Role, Guid Chunk,
    int OldLayers, int NewLayers, byte[] ResourceBefore, byte[] ResourceAfter,
    byte[] Before, byte[] Data);

public sealed record InstalledArrayExpansionPlan(IReadOnlyList<InstalledArrayExpansionJob> Jobs);

/// <summary>
/// Expands only insufficient installed arrays, using a deeper copy of the same target resource as their structural
/// donor. Existing compressed team slices survive byte for byte; new slices are safe blanks until the branding pass.
/// RES payloads, manifests, bundle records and global chunk references are committed and read back as one transaction.
/// </summary>
public static class InstalledArrayExpansion
{
    readonly record struct PayloadKey(string Cas, uint Offset, uint Size);

    public static InstalledArrayExpansionPlan BuildAddition(string game, BundleIndex index,
        NewUniformAddition addition, int workers = 0)
    {
        var jobs = new Dictionary<Guid, InstalledArrayExpansionJob>();
        foreach (var color in addition.Textures.Where(t => t.SourceSlot.EndsWith("_color_array", StringComparison.Ordinal)
                     && t.Artwork.Layers is { Count: > 0 }))
        {
            string stem = color.SourceSlot[..^"_color_array".Length];
            string targetStem = color.TargetSlot[..^"_color_array".Length];
            var normal = addition.Textures.SingleOrDefault(t => t.SourceSlot == stem + "_normal_array")
                ?? throw new InvalidDataException($"{color.TargetSlot}: its converted normal array is missing.");
            var sourceColors = TextureSlots.ReadAll(game, index, color.SourceSlot);
            var targetColors = TextureSlots.ReadAll(game, index, color.TargetSlot);
            var sourceNormals = TextureSlots.ReadAll(game, index, normal.SourceSlot);
            var targetNormals = TextureSlots.ReadAll(game, index, normal.TargetSlot);
            var sourceCoeffs = TextureSlots.ReadAll(game, index, stem + "_coeff_array");
            var targetCoeffs = TextureSlots.ReadAll(game, index, targetStem + "_coeff_array");
            int sourceRequired = Math.Max(color.Artwork.Layers!.Keys.Max() + 1, color.RequiredRoles?.Count ?? 0);
            string sourceStamp = UniformPhysicalLayouts.StampForArray(color.SourceSlot);
            var hotspotLayouts = addition.HotspotLayouts.Where(l => l.SourceSlot == sourceStamp).ToList();
            if (hotspotLayouts.Count > 1) throw new InvalidDataException($"{sourceStamp}: duplicate hotspot overrides.");
            int overrideSlots = hotspotLayouts.SingleOrDefault() is { } layout ? UniformHotspots.Roles(layout).Count : 0;
            // Artwork count alone is insufficient: Boston has six images but seven physical positions, because its
            // obsolete back-tag slot stays blank. Expansion must account for the reviewed hotspots and donor role map.
            int required = Math.Max(sourceRequired, Math.Max(color.DonorRoles?.Count ?? 0, overrideSlots));

            foreach (var targetColor in targetColors)
            {
                var sourceColor = InstalledBrandingAudit.Matching(sourceColors, targetColor.Res, "source color", color.SourceSlot);
                var sourceNormal = InstalledBrandingAudit.Companion(sourceNormals, sourceColor.Res,
                    "source normal", normal.SourceSlot, normal.Artwork.Layers?.Count ?? sourceRequired);
                var sourceCoeff = InstalledBrandingAudit.Companion(sourceCoeffs, sourceColor.Res,
                    "source coefficient", stem + "_coeff_array", sourceRequired);
                var targetNormal = InstalledBrandingCopyMatcher.Target(index, targetNormals, targetColor, sourceNormal.Res, "target normal", normal.TargetSlot);
                var targetCoeff = InstalledBrandingCopyMatcher.Target(index, targetCoeffs, targetColor, sourceCoeff.Res, "target coefficient", targetStem + "_coeff_array");
                Add("color", targetColor, targetColors);
                Add("normal", targetNormal, targetNormals);
                Add("coeff", targetCoeff, targetCoeffs);
            }

            void Add(string role, TextureSlots.Slot team, IReadOnlyList<TextureSlots.Slot> sameResource)
            {
                int oldCount = Ps4Codec.Slices(team.Res.Header);
                if (oldCount >= required) return;
                var donor = StructuralDonor(game, index, team, sameResource, role, required) ?? throw new InvalidDataException(
                    $"{addition.Team} {addition.SourceKit}: {team.Name} chunk {team.Chunk} has {oldCount} {role} slots, "
                    + $"but its complete layout needs {required}, and no compatible structural donor was found.");
                int newCount = Ps4Codec.Slices(donor.Res.Header);
                var expanded = Expand(team, donor, role, workers);
                var job = new InstalledArrayExpansionJob(addition.Team, addition.SourceKit, team.Name, role, team.Chunk,
                    oldCount, newCount, team.Res.Header, expanded.Res.Header, team.Stock, expanded.Stock);
                if (jobs.TryGetValue(team.Chunk, out var existing))
                {
                    if (!existing.Data.AsSpan().SequenceEqual(job.Data) || !existing.ResourceAfter.AsSpan().SequenceEqual(job.ResourceAfter))
                        throw new InvalidDataException($"Conflicting expansion plans for {team.Name} chunk {team.Chunk}.");
                }
                else jobs.Add(team.Chunk, job);
            }
        }
        return new InstalledArrayExpansionPlan(jobs.Values.OrderBy(j => j.Team, StringComparer.Ordinal)
            .ThenBy(j => j.Kit, StringComparer.Ordinal).ThenBy(j => j.Name, StringComparer.Ordinal)
            .ThenBy(j => j.Chunk).ToArray());
    }

    /// <summary>Finds a compatible deeper layout without writing or retaining any donor pixels.</summary>
    public static TextureSlots.Slot? StructuralDonor(string game, BundleIndex index, TextureSlots.Slot team,
        IReadOnlyList<TextureSlots.Slot> sameResource, string role, int required)
    {
        int oldCount = Ps4Codec.Slices(team.Res.Header);
        if (oldCount >= required) return null;
        var donors = sameResource.Where(candidate =>
        {
            int count = Ps4Codec.Slices(candidate.Res.Header);
            return count >= required && count > oldCount && SameStructure(team.Res, candidate.Res);
        }).OrderBy(candidate => Ps4Codec.Slices(candidate.Res.Header)).ThenBy(candidate => candidate.Chunk).ToList();
        if (donors.Count == 0)
        {
            // A shallow source can have no deeper sibling (Utah's pant arrays). Borrow only an identical
            // structure from the same garment and role. Expand overwrites every donor slice with either the team's
            // original compressed slice or a newly encoded safe blank, so no donor art survives.
            string suffix = "_" + role + "_array";
            string leafPrefix = TextureName.Parse(team.Name).Garment + "_";
            var names = index.ResByName.Keys.Where(name => name != team.Name
                && name.StartsWith(TextureName.SkaterPrefix, StringComparison.Ordinal)
                && name.EndsWith(suffix, StringComparison.Ordinal)
                && name.Split('/')[^1].StartsWith(leafPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal);
            foreach (string name in names)
            {
                List<TextureSlots.Slot> candidates;
                try { candidates = TextureSlots.ReadAll(game, index, name); }
                catch (InvalidDataException) { continue; }
                catch (InvalidOperationException) { continue; }
                donors.AddRange(candidates.Where(candidate => Ps4Codec.Slices(candidate.Res.Header) == required
                    && SameStructure(team.Res, candidate.Res)));
                if (donors.Count > 0) break;
            }
            donors = donors.OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Chunk).ToList();
        }
        return donors.FirstOrDefault();
    }

    /// <summary>Preserves every team slice and encodes only safe blanks into the added structural slots.</summary>
    public static TextureSlots.Slot Expand(TextureSlots.Slot team, TextureSlots.Slot donor, string role, int workers = 0)
    {
        int oldCount = Ps4Codec.Slices(team.Res.Header), newCount = Ps4Codec.Slices(donor.Res.Header);
        if (newCount <= oldCount) throw new InvalidDataException($"{team.Name}: expansion needs a deeper structural donor.");
        var added = Enumerable.Range(oldCount, newCount - oldCount).ToDictionary(layer => layer,
            layer => UniformBrandingLayers.Blank(team.Res.Width, team.Res.Height, role));
        byte[] expanded = Ps4Codec.BuildExpandedArray(donor.Res, donor.Stock, team.Res, team.Stock, added, workers);
        byte[] resource = (byte[])team.Res.Header.Clone();
        if (resource.Length < TextureRes.HeaderSize) throw new InvalidDataException($"{team.Name}: truncated texture resource.");
        BinaryPrimitives.WriteUInt16LittleEndian(resource.AsSpan(26, 2), checked((ushort)newCount));
        var parsed = TextureRes.Parse(resource);
        if (parsed.ChunkGuid != team.Chunk || Ps4Codec.Slices(parsed.Header) != newCount
            || resource.AsSpan(112, 4).SequenceEqual(team.Res.Header.AsSpan(112, 4)) == false)
            throw new InvalidDataException($"{team.Name}: expansion changed its resource identity.");
        return team with { Res = parsed, Stock = expanded };
    }

    static bool SameStructure(TextureRes team, TextureRes donor) => team.Width == donor.Width && team.Height == donor.Height
        && team.BlockBytes == donor.BlockBytes && team.Header[12] == donor.Header[12] && team.MipCount == donor.MipCount
        && team.PayloadSize == donor.PayloadSize && team.MipSizes.SequenceEqual(donor.MipSizes);

    public static string? Apply(string game, InstalledArrayExpansionPlan plan, string? backupRoot,
        bool restoreAfterVerify, Action<string> say, int workers = 0)
    {
        if (ApplyTarget.RunningGame() is { } running) throw new InvalidOperationException(running + " is running. Close the game first.");
        if (ApplyTarget.Problems(game) is { Count: > 0 } problems)
            throw new InvalidOperationException("Unsafe apply target: " + string.Join("; ", problems));
        var jobs = plan.Jobs.GroupBy(j => j.Chunk).Select(g =>
        {
            if (g.Any(j => !j.Data.AsSpan().SequenceEqual(g.First().Data)
                || !j.ResourceAfter.AsSpan().SequenceEqual(g.First().ResourceAfter)))
                throw new InvalidDataException($"Conflicting expansion payloads for shared chunk {g.Key}.");
            return g.First();
        }).ToList();
        if (jobs.Count == 0)
        {
            say("Every installed array already has enough slots. No game files changed.");
            return null;
        }
        string backup = ApplyTarget.Backup(game, backupRoot);
        say("Backup of the current 64-kit state: " + backup);
        var index = BundleIndex.Build(game);
        NewUniformTransaction.Run(backup,
            writeAssets: () => Write(game, index, jobs, say, workers),
            writeDatabases: () => { },
            readBack: () => Verify(game, jobs, before: false),
            restoreAndVerify: () => RestoreAndVerify(game, backup, jobs));
        say($"ARRAYS EXPANDED AND READ BACK: {jobs.Count} existing resources; names, RIDs and chunk GUIDs preserved.");
        if (restoreAfterVerify)
        {
            RestoreAndVerify(game, backup, jobs);
            say("ISOLATED ARRAY WRITE, READ-BACK AND RESTORE PASSED: the current 64-kit state was restored exactly.");
        }
        return backup;
    }

    static void RestoreAndVerify(string game, string backup, IReadOnlyList<InstalledArrayExpansionJob> jobs)
    {
        ApplyTarget.RestoreFromBackup(game, backup);
        var left = ApplyTarget.Compare(game, backup);
        if (left.Count > 0) throw new InvalidOperationException("Rollback differences: " + string.Join("; ", left.Take(10)));
        Verify(game, jobs, before: true);
    }

    static byte[] Decode(byte[] stored)
    {
        try { return ChunkBlocks.Parse(stored).Decode(); }
        catch (InvalidDataException) { return stored; }
    }

    static byte[] EncodeLike(byte[] old, byte[] raw, int workers)
    {
        ChunkBlocks parsed;
        try { parsed = ChunkBlocks.Parse(old); }
        catch (InvalidDataException) { return raw; }
        if (parsed.Blocks.Sum(b => b.RawSize) != raw.Length) return ChunkBlocks.EncodeBlocks(raw, workers: workers);
        using var stream = new MemoryStream();
        int at = 0;
        foreach (var block in parsed.Blocks)
        {
            stream.Write(ChunkBlocks.EncodeBlock(raw.AsSpan(at, block.RawSize)));
            at += block.RawSize;
        }
        return stream.ToArray();
    }

    static PayloadKey Key(BundleIndex.Location location) => new(location.Cas, location.Offset, location.Size);

    static void Write(string game, BundleIndex index, IReadOnlyList<InstalledArrayExpansionJob> jobs,
        Action<string> say, int workers)
    {
        var tocs = BundleIndex.Tocs(game).Select(TocFile.Load).ToList();
        var tocByPath = tocs.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
        var rawPayloads = new Dictionary<PayloadKey, byte[]>();
        var storedPayloads = new Dictionary<PayloadKey, byte[]>();
        PayloadKey Prepare(BundleIndex.Location location, byte[] expectedBefore, byte[] after, string description)
        {
            var key = Key(location);
            byte[] oldStored = BundleIndex.ReadBytes(location);
            byte[] oldRaw = Decode(oldStored);
            if (!oldRaw.AsSpan().SequenceEqual(expectedBefore))
                throw new InvalidDataException($"{description}: its current logical bytes differ from the expansion plan.");
            if (rawPayloads.TryGetValue(key, out var existing))
            {
                if (!existing.AsSpan().SequenceEqual(after)) throw new InvalidDataException($"Conflicting writes for shared payload {description}.");
                return key;
            }
            byte[] stored = EncodeLike(oldStored, after, workers);
            if (!Decode(stored).AsSpan().SequenceEqual(after)) throw new InvalidDataException($"{description}: encoded payload does not decode back.");
            rawPayloads.Add(key, after);
            storedPayloads.Add(key, stored);
            return key;
        }

        var resourceRefs = new List<(InstalledArrayExpansionJob Job, BundleIndex.Res Ref, PayloadKey Key)>();
        var chunkRefs = new List<(InstalledArrayExpansionJob Job, BundleIndex.Chunk Ref, PayloadKey Key)>();
        var globalRefs = new List<(InstalledArrayExpansionJob Job, TocFile Toc, ChunkRef Ref, PayloadKey Key)>();
        foreach (var job in jobs)
        {
            int resources = 0;
            foreach (var res in index.ResByName.GetValueOrDefault(job.Name) ?? [])
            {
                byte[] raw = Decode(BundleIndex.ReadBytes(res.Data));
                if (TextureRes.Parse(raw).ChunkGuid != job.Chunk) continue;
                var key = Prepare(res.Data, job.ResourceBefore, job.ResourceAfter, job.Name + " RES");
                resourceRefs.Add((job, res, key));
                resources++;
            }
            if (resources == 0) throw new InvalidDataException($"{job.Name}: no installed RES copy names chunk {job.Chunk}.");
            foreach (var chunk in index.ChunksById.GetValueOrDefault(job.Chunk) ?? [])
            {
                if (chunk.LogicalOffset != 0 || chunk.LogicalSize != job.Before.Length)
                    throw new InvalidDataException($"{job.Name}: bundle chunk {chunk.LogicalOffset}+{chunk.LogicalSize} is not the complete original array.");
                var key = Prepare(chunk.Data, job.Before, job.Data, job.Name + " bundle chunk");
                chunkRefs.Add((job, chunk, key));
            }
            foreach (var toc in tocs)
            {
                if (toc.Find(job.Chunk) is not { } reference) continue;
                string cas = BundleIndex.CasPath(game, reference.Patch, reference.Catalog, reference.Cas);
                var location = new BundleIndex.Location(toc.Path, 0, cas, reference.Offset, reference.Size);
                var key = Prepare(location, job.Before, job.Data, job.Name + " global chunk");
                globalRefs.Add((job, toc, reference, key));
            }
        }

        var affectedBundles = resourceRefs.Select(r => r.Ref.Bundle).Concat(chunkRefs.Select(c => c.Ref.Bundle))
            .DistinctBy(b => (b.Toc, b.RecordAt)).ToList();
        var manifestBytes = new Dictionary<PayloadKey, byte[]>();
        foreach (var group in affectedBundles.GroupBy(b => Key(b.Manifest)))
        {
            var manifest = BundleManifest.Parse(BundleIndex.ReadBytes(group.First().Manifest));
            foreach (var (job, res, key) in resourceRefs.Where(r => Key(r.Ref.Bundle.Manifest) == group.Key))
            {
                int i = manifest.Res.FindIndex(entry => entry.Name == res.Name && entry.Rid == res.Rid);
                if (i < 0) throw new InvalidDataException($"{job.Name}: RES entry is absent from an affected manifest.");
                manifest.Res[i] = manifest.Res[i] with
                {
                    OriginalSize = checked((uint)job.ResourceAfter.Length),
                    Sha1 = SHA1.HashData(storedPayloads[key]),
                };
            }
            foreach (var (job, chunk, key) in chunkRefs.Where(c => Key(c.Ref.Bundle.Manifest) == group.Key))
            {
                int i = manifest.Chunks.FindIndex(entry => entry.Id == job.Chunk);
                if (i < 0) throw new InvalidDataException($"{job.Name}: chunk entry is absent from an affected manifest.");
                manifest.Chunks[i] = manifest.Chunks[i] with
                {
                    LogicalSize = checked((uint)job.Data.Length),
                    Sha1 = SHA1.HashData(storedPayloads[key]),
                };
            }
            manifestBytes.Add(group.Key, manifest.Build());
        }

        var newPayloads = Append(storedPayloads, game);
        var newManifests = Append(manifestBytes, game);
        static Dictionary<PayloadKey, (uint Offset, uint Size)> Append(Dictionary<PayloadKey, byte[]> payloads, string game)
        {
            var result = new Dictionary<PayloadKey, (uint, uint)>();
            foreach (var group in payloads.GroupBy(kv => kv.Key.Cas, StringComparer.OrdinalIgnoreCase))
            {
                using var stream = new FileStream(group.Key, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 20);
                foreach (var (key, bytes) in group)
                {
                    if (stream.Position + bytes.Length > uint.MaxValue) throw new InvalidDataException($"{group.Key} would grow past 4 GB.");
                    uint offset = checked((uint)stream.Position);
                    stream.Write(bytes);
                    result.Add(key, (offset, checked((uint)bytes.Length)));
                }
                stream.Flush(true);
            }
            return result;
        }

        void Repoint(BundleIndex.Location old, int pairAt, (uint Offset, uint Size) location)
        {
            var toc = tocByPath[old.Toc];
            if (BinaryPrimitives.ReadUInt32BigEndian(toc.Bytes.AsSpan(pairAt)) != old.Offset
                || BinaryPrimitives.ReadUInt32BigEndian(toc.Bytes.AsSpan(pairAt + 4)) != old.Size)
                throw new InvalidDataException("A bundle record changed while the array expansion was being written.");
            BinaryPrimitives.WriteUInt32BigEndian(toc.Bytes.AsSpan(pairAt), location.Offset);
            BinaryPrimitives.WriteUInt32BigEndian(toc.Bytes.AsSpan(pairAt + 4), location.Size);
        }
        foreach (var (_, res, key) in resourceRefs) Repoint(res.Data, res.Data.PairAt, newPayloads[key]);
        foreach (var (_, chunk, key) in chunkRefs) Repoint(chunk.Data, chunk.Data.PairAt, newPayloads[key]);
        foreach (var (_, toc, reference, key) in globalRefs)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(toc.Bytes.AsSpan(reference.EntryPos + 4)) != reference.Offset
                || BinaryPrimitives.ReadUInt32BigEndian(toc.Bytes.AsSpan(reference.EntryPos + 8)) != reference.Size)
                throw new InvalidDataException("A global chunk reference changed while the array expansion was being written.");
            var location = newPayloads[key];
            toc.Repoint(reference.EntryPos, location.Offset, location.Size);
        }
        foreach (var bundle in affectedBundles)
        {
            var key = Key(bundle.Manifest);
            Repoint(bundle.Manifest, bundle.Manifest.PairAt, newManifests[key]);
        }
        foreach (var toc in tocs.Where(t => affectedBundles.Any(b => b.Toc == t.Path)
                     || globalRefs.Any(r => r.Toc.Path == t.Path))) toc.Save();
        say($"Appended {storedPayloads.Count} expanded payloads and {manifestBytes.Count} manifests; "
            + $"updated {affectedBundles.Count} bundle records and {globalRefs.Count} global chunk references.");
    }

    static void Verify(string game, IReadOnlyList<InstalledArrayExpansionJob> jobs, bool before)
    {
        var fresh = BundleIndex.Build(game);
        foreach (var job in jobs)
        {
            byte[] expected = before ? job.Before : job.Data;
            byte[] expectedResource = before ? job.ResourceBefore : job.ResourceAfter;
            int expectedLayers = before ? job.OldLayers : job.NewLayers;
            var slot = TextureSlots.ReadAll(game, fresh, job.Name).SingleOrDefault(s => s.Chunk == job.Chunk)
                ?? throw new InvalidDataException($"{job.Name}: chunk {job.Chunk} is no longer readable.");
            if (Ps4Codec.Slices(slot.Res.Header) != expectedLayers || !slot.Stock.AsSpan().SequenceEqual(expected))
                throw new InvalidDataException($"{job.Name}: its {(before ? "restored" : "expanded")} resource or full chunk differs.");
            var references = ChunkReplace.ReadAll(game, fresh, job.Chunk);
            if (references.Count == 0) throw new InvalidDataException($"{job.Name}: no readable chunk references remain.");
            foreach (var reference in references)
            {
                int at = checked((int)reference.LogicalOffset);
                if (at + reference.Raw.Length > expected.Length
                    || !reference.Raw.AsSpan().SequenceEqual(expected.AsSpan(at, reference.Raw.Length)))
                    throw new InvalidDataException($"{job.Name}: {(before ? "restored" : "expanded")} bytes differ via {reference.Where}.");
            }
            int resources = 0;
            foreach (var res in fresh.ResByName.GetValueOrDefault(job.Name) ?? [])
            {
                byte[] raw = Decode(BundleIndex.ReadBytes(res.Data));
                if (TextureRes.Parse(raw).ChunkGuid != job.Chunk) continue;
                resources++;
                if (!raw.AsSpan().SequenceEqual(expectedResource)) throw new InvalidDataException($"{job.Name}: a RES copy differs after read-back.");
                var manifest = BundleManifest.Parse(BundleIndex.ReadBytes(res.Bundle.Manifest));
                int ri = manifest.Res.FindIndex(entry => entry.Name == res.Name && entry.Rid == res.Rid);
                if (ri < 0 || manifest.Res[ri].OriginalSize != expectedResource.Length
                    || !manifest.Res[ri].Sha1.AsSpan().SequenceEqual(SHA1.HashData(BundleIndex.ReadBytes(res.Data))))
                    throw new InvalidDataException($"{job.Name}: its manifest RES size or SHA1 is wrong.");
            }
            if (resources == 0) throw new InvalidDataException($"{job.Name}: its RES references disappeared.");
            foreach (var chunk in fresh.ChunksById.GetValueOrDefault(job.Chunk) ?? [])
            {
                if (chunk.LogicalOffset != 0 || chunk.LogicalSize != expected.Length)
                    throw new InvalidDataException($"{job.Name}: a manifest chunk range is incomplete.");
                var manifest = BundleManifest.Parse(BundleIndex.ReadBytes(chunk.Bundle.Manifest));
                int ci = manifest.Chunks.FindIndex(entry => entry.Id == job.Chunk);
                if (ci < 0 || manifest.Chunks[ci].LogicalOffset != 0 || manifest.Chunks[ci].LogicalSize != expected.Length
                    || !manifest.Chunks[ci].Sha1.AsSpan().SequenceEqual(SHA1.HashData(BundleIndex.ReadBytes(chunk.Data))))
                    throw new InvalidDataException($"{job.Name}: its manifest chunk size or SHA1 is wrong.");
            }
        }
    }
}
