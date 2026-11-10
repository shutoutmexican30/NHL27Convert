using System.Buffers.Binary;
using System.Security.Cryptography;
using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>One existing target stamp copy. Its name, GUID, imports and resource references never change.</summary>
public sealed record BrandingStampJob(string Team, string Kit, string SourceSlot, string TargetSlot,
    string BundleToc, int BundleRecordAt, byte[] Before, byte[] Data);

/// <summary>
/// Appends corrected EBX payloads and their manifests, repoints every matching bundle record, and verifies restoration.
/// The caller owns the surrounding NewUniformTransaction, so stamp and texture writes commit or roll back together.
/// </summary>
public static class InstalledHotspotCorrection
{
    readonly record struct PayloadKey(string Cas, uint Offset, uint Size);

    public static IReadOnlyList<BrandingStampJob> BuildAddition(BundleIndex index, NewUniformAddition addition)
    {
        var jobs = new List<BrandingStampJob>();
        foreach (var layout in addition.HotspotLayouts)
        {
            _ = UniformHotspots.Roles(layout);
            string target = UniformPhysicalLayouts.TargetStamp(addition, layout.SourceSlot);
            var copies = index.EbxByName.GetValueOrDefault(target) ?? [];
            if (copies.Count == 0) throw new InvalidDataException($"{target}: the installed stamp is missing.");
            foreach (var copy in copies)
            {
                byte[] before = UniformPhysicalLayouts.Decode(BundleIndex.ReadBytes(copy.Data));
                var identity = EbxRiff.Parse(before);
                if (identity.Name != target) throw new InvalidDataException($"{target}: embedded EBX name differs.");
                byte[] after = UniformHotspots.Replace(before, layout);
                var updated = EbxRiff.Parse(after);
                if (updated.Name != identity.Name || updated.FileGuid != identity.FileGuid
                    || !updated.ImportedFileGuids.SequenceEqual(identity.ImportedFileGuids))
                    throw new InvalidDataException($"{target}: hotspot repair changed its identity or imports.");
                if (!UniformPhysicalLayouts.SameLayout(UniformHotspots.Capture(after, target), layout))
                    throw new InvalidDataException($"{target}: repaired physical layout did not read back.");
                jobs.Add(new BrandingStampJob(addition.Team, addition.SourceKit, layout.SourceSlot, target,
                    copy.Bundle.Toc, copy.Bundle.RecordAt, before, after));
            }
        }
        return jobs;
    }

    public static void Write(string game, BundleIndex index, IReadOnlyList<BrandingStampJob> jobs,
        Action<string> say, int workers = 0)
    {
        if (jobs.Count == 0) return;
        var unique = jobs.GroupBy(j => (j.TargetSlot, j.BundleToc, j.BundleRecordAt)).Select(g =>
        {
            if (g.Any(j => !j.Before.AsSpan().SequenceEqual(g.First().Before)
                || !j.Data.AsSpan().SequenceEqual(g.First().Data)))
                throw new InvalidDataException($"Conflicting hotspot plans for {g.Key.TargetSlot}.");
            return g.First();
        }).ToList();
        var tocs = BundleIndex.Tocs(game).Select(TocFile.Load).ToList();
        var tocByPath = tocs.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
        var rawPayloads = new Dictionary<PayloadKey, byte[]>();
        var storedPayloads = new Dictionary<PayloadKey, byte[]>();
        var references = new List<(BrandingStampJob Job, BundleIndex.Ebx Ref, PayloadKey Key)>();

        PayloadKey Prepare(BrandingStampJob job, BundleIndex.Ebx reference)
        {
            var key = Key(reference.Data);
            byte[] oldStored = BundleIndex.ReadBytes(reference.Data);
            byte[] oldRaw = UniformPhysicalLayouts.Decode(oldStored);
            if (!oldRaw.AsSpan().SequenceEqual(job.Before)) throw new InvalidDataException(
                $"{job.TargetSlot}: its current decoded bytes differ from the reviewed correction plan.");
            if (rawPayloads.TryGetValue(key, out var existing))
            {
                if (!existing.AsSpan().SequenceEqual(job.Data))
                    throw new InvalidDataException($"Conflicting writes for shared stamp payload {job.TargetSlot}.");
                return key;
            }
            byte[] stored = EncodeLike(oldStored, job.Data, workers);
            if (!UniformPhysicalLayouts.Decode(stored).AsSpan().SequenceEqual(job.Data))
                throw new InvalidDataException($"{job.TargetSlot}: stored EBX does not decode back.");
            rawPayloads.Add(key, job.Data);
            storedPayloads.Add(key, stored);
            return key;
        }

        foreach (var job in unique)
        {
            var reference = (index.EbxByName.GetValueOrDefault(job.TargetSlot) ?? []).SingleOrDefault(e =>
                e.Bundle.Toc == job.BundleToc && e.Bundle.RecordAt == job.BundleRecordAt)
                ?? throw new InvalidDataException($"{job.TargetSlot}: its planned bundle copy is missing.");
            references.Add((job, reference, Prepare(job, reference)));
        }

        var affectedBundles = references.Select(r => r.Ref.Bundle).DistinctBy(b => (b.Toc, b.RecordAt)).ToList();
        var manifestBytes = new Dictionary<PayloadKey, byte[]>();
        foreach (var group in affectedBundles.GroupBy(b => Key(b.Manifest)))
        {
            var manifest = BundleManifest.Parse(BundleIndex.ReadBytes(group.First().Manifest));
            foreach (var (job, _, key) in references.Where(r => Key(r.Ref.Bundle.Manifest) == group.Key))
            {
                int i = manifest.Ebx.FindIndex(entry => entry.Name == job.TargetSlot);
                if (i < 0) throw new InvalidDataException($"{job.TargetSlot}: EBX entry is absent from its manifest.");
                manifest.Ebx[i] = manifest.Ebx[i] with
                {
                    OriginalSize = checked((uint)job.Data.Length),
                    Sha1 = SHA1.HashData(storedPayloads[key]),
                };
            }
            manifestBytes.Add(group.Key, manifest.Build());
        }

        var newPayloads = Append(storedPayloads);
        var newManifests = Append(manifestBytes);
        Dictionary<PayloadKey, (uint Offset, uint Size)> Append(Dictionary<PayloadKey, byte[]> payloads)
        {
            var result = new Dictionary<PayloadKey, (uint, uint)>();
            foreach (var group in payloads.GroupBy(kv => kv.Key.Cas, StringComparer.OrdinalIgnoreCase))
            {
                using var stream = new FileStream(group.Key, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 20);
                foreach (var (key, bytes) in group)
                {
                    if (stream.Position + bytes.Length > uint.MaxValue)
                        throw new InvalidDataException($"{group.Key} would grow past 4 GB.");
                    uint offset = checked((uint)stream.Position);
                    stream.Write(bytes);
                    result.Add(key, (offset, checked((uint)bytes.Length)));
                }
                stream.Flush(true);
            }
            return result;
        }

        void Repoint(BundleIndex.Location old, (uint Offset, uint Size) location)
        {
            var toc = tocByPath[old.Toc];
            if (BinaryPrimitives.ReadUInt32BigEndian(toc.Bytes.AsSpan(old.PairAt)) != old.Offset
                || BinaryPrimitives.ReadUInt32BigEndian(toc.Bytes.AsSpan(old.PairAt + 4)) != old.Size)
                throw new InvalidDataException("A bundle record changed while the hotspot repair was being written.");
            BinaryPrimitives.WriteUInt32BigEndian(toc.Bytes.AsSpan(old.PairAt), location.Offset);
            BinaryPrimitives.WriteUInt32BigEndian(toc.Bytes.AsSpan(old.PairAt + 4), location.Size);
        }
        foreach (var (_, reference, key) in references) Repoint(reference.Data, newPayloads[key]);
        foreach (var bundle in affectedBundles) Repoint(bundle.Manifest, newManifests[Key(bundle.Manifest)]);
        foreach (var toc in tocs.Where(t => affectedBundles.Any(b => b.Toc == t.Path))) toc.Save();
        say($"Hotspot repair appended {storedPayloads.Count} EBX payloads and {manifestBytes.Count} manifests; "
            + $"{references.Count} stamp references were repointed without changing identity.");
    }

    public static void Verify(string game, IReadOnlyList<BrandingStampJob> jobs, bool before)
    {
        var fresh = BundleIndex.Build(game);
        foreach (var job in jobs)
        {
            var reference = (fresh.EbxByName.GetValueOrDefault(job.TargetSlot) ?? []).SingleOrDefault(e =>
                e.Bundle.Toc == job.BundleToc && e.Bundle.RecordAt == job.BundleRecordAt)
                ?? throw new InvalidDataException($"{job.TargetSlot}: its stamp reference disappeared.");
            byte[] stored = BundleIndex.ReadBytes(reference.Data);
            byte[] raw = UniformPhysicalLayouts.Decode(stored);
            byte[] expected = before ? job.Before : job.Data;
            if (!raw.AsSpan().SequenceEqual(expected)) throw new InvalidDataException(
                $"{job.TargetSlot}: {(before ? "restored" : "installed")} EBX bytes differ.");
            var identity = EbxRiff.Parse(raw);
            var original = EbxRiff.Parse(job.Before);
            if (identity.Name != original.Name || identity.FileGuid != original.FileGuid
                || !identity.ImportedFileGuids.SequenceEqual(original.ImportedFileGuids))
                throw new InvalidDataException($"{job.TargetSlot}: its identity or imports changed.");
            var manifest = BundleManifest.Parse(BundleIndex.ReadBytes(reference.Bundle.Manifest));
            int i = manifest.Ebx.FindIndex(entry => entry.Name == job.TargetSlot);
            if (i < 0 || manifest.Ebx[i].OriginalSize != expected.Length
                || !manifest.Ebx[i].Sha1.AsSpan().SequenceEqual(SHA1.HashData(stored)))
                throw new InvalidDataException($"{job.TargetSlot}: its EBX manifest size or SHA1 is wrong.");
            if (!before && !UniformPhysicalLayouts.SameLayout(
                UniformHotspots.Capture(raw, job.TargetSlot), UniformHotspots.Capture(job.Data, job.TargetSlot)))
                throw new InvalidDataException($"{job.TargetSlot}: its installed hotspot layout differs.");
        }
    }

    static PayloadKey Key(BundleIndex.Location location) => new(location.Cas, location.Offset, location.Size);

    static byte[] EncodeLike(byte[] old, byte[] raw, int workers)
    {
        ChunkBlocks parsed;
        try { parsed = ChunkBlocks.Parse(old); }
        catch (InvalidDataException) { return raw; }
        if (parsed.Blocks.Sum(b => b.RawSize) != raw.Length)
            return ChunkBlocks.EncodeBlocks(raw, workers: workers);
        using var stream = new MemoryStream();
        int at = 0;
        foreach (var block in parsed.Blocks)
        {
            stream.Write(ChunkBlocks.EncodeBlock(raw.AsSpan(at, block.RawSize)));
            at += block.RawSize;
        }
        return stream.ToArray();
    }
}
