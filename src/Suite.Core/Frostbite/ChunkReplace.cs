using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Suite.Core.Frostbite;

/// <summary>
/// Replaces a chunk everywhere the game can read it: every TOC chunk-table entry (the full chunk) and every bundle that
/// carries it (often only a slice: a jersey normal's bundle copy holds mips 1 and up, logical offset 4,194,304), plus the
/// SHA1 of each such bundle entry in its manifest. Writing only one of the two leaves the game reading old data through the
/// other. Each copy keeps its original block split; all data and manifest copies are appended (one append per CAS file),
/// then every TOC entry and bundle-record pair that pointed at an old copy is repointed. Append-only: the suite's restore
/// (truncate CAS + copy TOCs back) undoes it exactly. Take <see cref="ApplyTarget.Backup"/> first.
/// </summary>
public static class ChunkReplace
{
    /// <summary>Replaces chunk <paramref name="id"/> (whose full logical data is <paramref name="full"/>). Returns log lines.</summary>
    public static List<string> Replace(string game, BundleIndex index, Guid id, byte[] full)
    {
        var log = new List<string>();
        var tocs = BundleIndex.Tocs(game).Select(TocFile.Load).ToList();
        var tocRefs = tocs.Select(t => (Toc: t, Ref: t.Find(id))).Where(x => x.Ref is not null).ToList();
        var bundleRefs = index.ChunksById.GetValueOrDefault(id) ?? [];
        if (tocRefs.Count == 0 && bundleRefs.Count == 0) throw new InvalidOperationException($"Chunk {id} is not in this game.");

        static byte[] Read(string cas, uint off, uint size)
        {
            using var fs = new FileStream(cas, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Position = off; var b = new byte[size]; fs.ReadExactly(b); return b;
        }
        static byte[] Reencode(byte[] oldStored, byte[] raw)
        {
            var old = ChunkBlocks.Parse(oldStored);
            if (old.Blocks.Sum(b => (long)b.RawSize) != raw.Length) throw new InvalidOperationException($"New data is {raw.Length} bytes, the stored copy holds {old.Blocks.Sum(b => (long)b.RawSize)}.");
            var ms = new MemoryStream(); int at = 0;
            foreach (var b in old.Blocks) { ms.Write(ChunkBlocks.EncodeBlock(raw.AsSpan(at, b.RawSize))); at += b.RawSize; }
            var stored = ms.ToArray();
            if (!ChunkBlocks.Parse(stored).Decode().AsSpan().SequenceEqual(raw)) throw new InvalidOperationException("Re-encoded chunk does not decode back.");
            return stored;
        }

        // Appends, grouped per CAS: (cas, bytes) → assigned offset.
        var pending = new List<(string Cas, byte[] Bytes, Action<uint> Done)>();

        // TOC chunk-table copies: the full data.
        foreach (var group in tocRefs.GroupBy(x => (x.Ref!.Patch, x.Ref.Catalog, x.Ref.Cas, x.Ref.Offset, x.Ref.Size)))
        {
            var r = group.First().Ref!;
            var cas = BundleIndex.CasPath(game, r.Patch, r.Catalog, r.Cas);
            var stored = Reencode(Read(cas, r.Offset, r.Size), full);
            pending.Add((cas, stored, off =>
            {
                foreach (var (toc, rr) in group) toc.Repoint(rr!.EntryPos, off, (uint)stored.Length);
                log.Add($"TOC chunk table: {group.Count()} entr{(group.Count() == 1 ? "y" : "ies")} → {Path.GetRelativePath(game, cas)} @{off} ({stored.Length} stored)");
            }));
        }

        // Bundle copies: the logical slice, plus a manifest copy with the new SHA1 per bundle.
        var tocBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        byte[] TocBytes(string toc) => tocBytes.TryGetValue(toc, out var b) ? b : tocBytes[toc] = File.ReadAllBytes(toc);
        foreach (var byBundle in bundleRefs.GroupBy(c => c.Bundle))
        {
            var bundle = byBundle.Key;
            var manifest = Read(bundle.Manifest.Cas, bundle.Manifest.Offset, bundle.Manifest.Size);
            foreach (var c in byBundle)
            {
                if (c.LogicalOffset + (long)c.LogicalSize > full.Length) throw new InvalidOperationException($"Bundle slice {c.LogicalOffset}+{c.LogicalSize} is outside the data.");
                var slice = full.AsSpan((int)c.LogicalOffset, (int)c.LogicalSize).ToArray();
                var stored = Reencode(Read(c.Data.Cas, c.Data.Offset, c.Data.Size), slice);
                SHA1.HashData(stored).CopyTo(manifest, c.Sha1At);
                var loc = c.Data;
                pending.Add((loc.Cas, stored, off =>
                {
                    var b = TocBytes(loc.Toc);
                    if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(loc.PairAt)) != loc.Offset) throw new InvalidOperationException("Bundle record changed while writing.");
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(loc.PairAt), off);
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(loc.PairAt + 4), (uint)stored.Length);
                    log.Add($"bundle '{bundle.FirstName}': slice {c.LogicalOffset}+{c.LogicalSize} → {Path.GetRelativePath(game, loc.Cas)} @{off}");
                }));
            }
            var m = bundle.Manifest;
            pending.Add((m.Cas, manifest, off =>
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
                foreach (var (_, r) in tocRefs.Where(x => x.Toc == toc))
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
