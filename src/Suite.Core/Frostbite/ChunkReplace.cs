using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Suite.Core.Frostbite;

/// <summary>
/// Replaces chunks everywhere the game can read them: every TOC chunk-table entry (the full chunk) and every bundle that
/// carries them (often only a slice: a jersey normal's bundle copy holds mips 1 and up, logical offset 4,194,304), plus
/// the SHA1 of each such bundle entry in its manifest. Writing only one of the two leaves the game reading old data
/// through the other. Each copy keeps its original block split; all data and manifest copies are appended (one append
/// per CAS file), then every TOC entry and bundle-record pair that pointed at an old copy is repointed. Append-only:
/// the suite's restore (truncate CAS + copy TOCs back) undoes it exactly. Take <see cref="ApplyTarget.Backup"/> first.
/// </summary>
public static class ChunkReplace
{
    /// <summary>Replaces chunk <paramref name="id"/> (whose full logical data is <paramref name="full"/>). Returns log lines.</summary>
    public static List<string> Replace(string game, BundleIndex index, Guid id, byte[] full) =>
        ReplaceMany(game, index, [(id, full)]);

    /// <summary>Replaces many chunks in one pass, updating each affected bundle manifest once. Returns log lines.</summary>
    public static List<string> ReplaceMany(string game, BundleIndex index, IReadOnlyList<(Guid Id, byte[] Data)> replacements, int workers = 0)
    {
        var jobs = replacements.GroupBy(j => j.Id).Select(g => g.Last()).ToList();
        if (jobs.Count == 0) return [];
        var data = jobs.ToDictionary(j => j.Id, j => j.Data);
        var log = new List<string>();
        var tocs = BundleIndex.Tocs(game).Select(TocFile.Load).ToList();

        var tocRefs = jobs.SelectMany(j => tocs.Select(t => (Toc: t, Ref: t.Find(j.Id), Id: j.Id)))
                          .Where(x => x.Ref is not null).ToList();
        var bundleRefs = jobs.SelectMany(j => (index.ChunksById.GetValueOrDefault(j.Id) ?? [])
                                              .Select(c => (Chunk: j.Id, Ref: c))).ToList();
        var missing = jobs.Where(j => !tocRefs.Any(x => x.Id == j.Id) && !bundleRefs.Any(x => x.Chunk == j.Id)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException(
            string.Join("; ", missing.Select(j => $"Chunk {j.Id} is not in this game.")));

        static byte[] Read(string cas, uint off, uint size)
        {
            using var fs = new FileStream(cas, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Position = off; var b = new byte[size]; fs.ReadExactly(b); return b;
        }
        byte[] Reencode(byte[] oldStored, byte[] raw)
        {
            var old = ChunkBlocks.Parse(oldStored);
            if (old.Blocks.Sum(b => (long)b.RawSize) != raw.Length) throw new InvalidOperationException($"New data is {raw.Length} bytes, the stored copy holds {old.Blocks.Sum(b => (long)b.RawSize)}.");
            var offsets = new int[old.Blocks.Count];
            for (int i = 1; i < offsets.Length; i++) offsets[i] = offsets[i - 1] + old.Blocks[i - 1].RawSize;
            var encoded = new byte[old.Blocks.Count][];
            CpuParallel.For(0, old.Blocks.Count, workers, i => encoded[i] = ChunkBlocks.EncodeBlock(raw.AsSpan(offsets[i], old.Blocks[i].RawSize)));
            var ms = new MemoryStream();
            foreach (var block in encoded) ms.Write(block, 0, block.Length);
            var stored = ms.ToArray();
            if (!ChunkBlocks.Parse(stored).Decode().AsSpan().SequenceEqual(raw)) throw new InvalidOperationException("Re-encoded chunk does not decode back.");
            return stored;
        }

        // Appends, grouped per CAS: (cas, bytes) → assigned offset.
        var pending = new List<(string Cas, byte[] Bytes, Action<uint> Done)>();

        // TOC chunk-table copies: the full data.
        foreach (var group in tocRefs.GroupBy(x => (x.Id, x.Ref!.Patch, x.Ref.Catalog, x.Ref.Cas, x.Ref.Offset, x.Ref.Size)))
        {
            var r = group.First().Ref!;
            var full = data[group.First().Id];
            var cas = BundleIndex.CasPath(game, r.Patch, r.Catalog, r.Cas);
            var stored = Reencode(Read(cas, r.Offset, r.Size), full);
            pending.Add((cas, stored, new Action<uint>(off =>
            {
                foreach (var (toc, rr, _) in group) toc.Repoint(rr!.EntryPos, off, (uint)stored.Length);
                log.Add($"TOC chunk table: {group.Count()} entr{(group.Count() == 1 ? "y" : "ies")} → {Path.GetRelativePath(game, cas)} @{off} ({stored.Length} stored)");
            })));
        }

        // Bundle copies: logical slices, plus one updated manifest per bundle with the new SHA1 per touched entry.
        var tocBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        byte[] TocBytes(string toc) => tocBytes.TryGetValue(toc, out var b) ? b : tocBytes[toc] = File.ReadAllBytes(toc);
        foreach (var byBundle in bundleRefs.GroupBy(x => x.Ref.Bundle))
        {
            var bundle = byBundle.Key;
            var manifest = Read(bundle.Manifest.Cas, bundle.Manifest.Offset, bundle.Manifest.Size);
            foreach (var (chunk, c) in byBundle)
            {
                var full = data[chunk];
                if (c.LogicalOffset + (long)c.LogicalSize > full.Length) throw new InvalidOperationException($"Bundle slice {c.LogicalOffset}+{c.LogicalSize} is outside the data.");
                var slice = full.AsSpan((int)c.LogicalOffset, (int)c.LogicalSize).ToArray();
                byte[] oldStored = Read(c.Data.Cas, c.Data.Offset, c.Data.Size);
                long oldRawSize = ChunkBlocks.Parse(oldStored).Blocks.Sum(b => (long)b.RawSize);
                byte[] stored;
                if (oldRawSize == slice.LongLength) stored = Reencode(oldStored, slice);
                else if (c.LogicalOffset == 0 && c.LogicalSize == slice.LongLength && oldRawSize < slice.LongLength)
                {
                    // Older expansion writes could update the manifest's full logical size while retaining the short
                    // source payload. The manifest is authoritative for a complete bundle chunk; rebuild that payload.
                    // Partial slices and unexpected size changes keep the strict mismatch refusal above.
                    stored = ChunkBlocks.EncodeBlocks(slice, workers: workers);
                    if (!ChunkBlocks.Parse(stored).Decode().AsSpan().SequenceEqual(slice))
                        throw new InvalidOperationException("Repaired full bundle chunk does not decode back.");
                    log.Add($"bundle '{bundle.FirstName}': repaired short full payload {oldRawSize} -> {slice.Length} bytes");
                }
                else stored = Reencode(oldStored, slice);
                SHA1.HashData(stored).CopyTo(manifest, c.Sha1At);
                var loc = c.Data;
                pending.Add((loc.Cas, stored, (uint off) =>
                {
                    var b = TocBytes(loc.Toc);
                    if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(loc.PairAt)) != loc.Offset) throw new InvalidOperationException("Bundle record changed while writing.");
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(loc.PairAt), off);
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(loc.PairAt + 4), (uint)stored.Length);
                    log.Add($"bundle '{bundle.FirstName}': slice {c.LogicalOffset}+{c.LogicalSize} → {Path.GetRelativePath(game, loc.Cas)} @{off}");
                }));
            }
            var m = bundle.Manifest;
            pending.Add((m.Cas, manifest, (uint off) =>
            {
                var b = TocBytes(m.Toc);
                if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(m.PairAt)) != m.Offset) throw new InvalidOperationException("Bundle record changed while writing.");
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(m.PairAt), off);
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(m.PairAt + 4), (uint)manifest.Length);
            }));
        }

        foreach (var perCas in pending.GroupBy(p => p.Cas))
        {
            using var fs = new FileStream(perCas.Key, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 20);
            foreach (var p in perCas)
            {
                long off = fs.Position;
                if (off + p.Bytes.Length > uint.MaxValue) throw new InvalidOperationException($"{perCas.Key} would grow past 4 GB.");
                fs.Write(p.Bytes);
                p.Done((uint)off);
            }
            fs.Flush(true);
        }
        // TOC files: chunk-table repoints live in TocFile objects; bundle repoints in tocBytes. Merge per file.
        foreach (var toc in tocs.Where(t => tocRefs.Any(x => x.Toc == t)))
        {
            if (tocBytes.TryGetValue(toc.Path, out var b))
            {
                // Apply the chunk-table changes onto the bundle-edited copy.
                foreach (var (_, r, _) in tocRefs.Where(x => x.Toc == toc))
                    toc.Bytes.AsSpan(r!.EntryPos, 12).CopyTo(b.AsSpan(r.EntryPos));
            }
            else toc.Save();
        }
        foreach (var (path, b) in tocBytes)
        {
            var tmp = path + ".cims-tmp";
            File.WriteAllBytes(tmp, b);
            File.Move(tmp, path, true);
        }
        return log;
    }

    /// <summary>What the game would read for chunk <paramref name="id"/> through each of its references (for read-back checks).</summary>
    public static List<(string Where, byte[] Raw, uint LogicalOffset)> ReadAll(string game, BundleIndex index, Guid id)
    {
        var list = new List<(string, byte[], uint)>();
        foreach (var t in BundleIndex.Tocs(game).Select(TocFile.Load))
            if (t.Find(id) is { } r)
            {
                var cas = BundleIndex.CasPath(game, r.Patch, r.Catalog, r.Cas);
                list.Add(("toc " + Path.GetFileName(t.Path), ChunkBlocks.Parse(BundleIndex.ReadBytes(new BundleIndex.Location(t.Path, 0, cas, r.Offset, r.Size))).Decode(), 0));
            }
        foreach (var c in index.ChunksById.GetValueOrDefault(id) ?? [])
            list.Add(("bundle " + c.Bundle.FirstName, ChunkBlocks.Parse(BundleIndex.ReadBytes(c.Data)).Decode(), c.LogicalOffset));
        return list;
    }
}
