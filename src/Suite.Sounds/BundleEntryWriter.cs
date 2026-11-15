using System.Buffers.Binary;
using System.Security.Cryptography;
using Suite.Core.Frostbite;

namespace Suite.Sounds;

/// <summary>
/// Replaces bundle EBX/RES payloads of the same decoded size (NHL 24 layout, <see cref="BundleIndex"/>). Each new payload
/// is encoded like the copy it replaces (stored-only stays stored-only), appended to that copy's CAS, its SHA1 written
/// into the bundle manifest, the manifest appended once per bundle, and the TOC bundle-record pairs repointed.
/// Append only, so <see cref="Suite.Core.ApplyTarget.RestoreFromBackup"/> undoes it. Take a backup first.
/// </summary>
public static class BundleEntryWriter
{
    public sealed record Job(BundleIndex.Location Data, BundleIndex.Bundle Bundle, int Sha1At, byte[] Raw);

    public static List<string> Write(string game, IReadOnlyList<Job> jobs)
    {
        var log = new List<string>();
        if (jobs.Count == 0) return log;
        var tocBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        byte[] TocBytes(string toc) => tocBytes.TryGetValue(toc, out var b) ? b : tocBytes[toc] = File.ReadAllBytes(toc);
        var pending = new List<(string Cas, byte[] Bytes, Action<uint> Done)>();

        foreach (var byBundle in jobs.GroupBy(j => (j.Bundle.Toc, j.Bundle.RecordAt)))
        {
            var bundle = byBundle.First().Bundle;
            var manifest = Read(bundle.Manifest);
            foreach (var j in byBundle)
            {
                var oldStored = Read(j.Data);
                var old = ChunkBlocks.Parse(oldStored);
                if (old.Blocks.Sum(b => (long)b.RawSize) != j.Raw.Length)
                    throw new InvalidOperationException($"Bundle '{bundle.FirstName}': new payload {j.Raw.Length} bytes, stored copy holds {old.Blocks.Sum(b => (long)b.RawSize)}.");
                bool storedOnly = old.Blocks.All(b => b.Type == 0x00);
                var stored = storedOnly ? ChunkBlocks.StoreBlocks(j.Raw, Math.Max(4096, old.Blocks.Max(b => b.RawSize))) : ChunkBlocks.EncodeBlocks(j.Raw);
                if (!ChunkBlocks.Parse(stored).Decode().AsSpan().SequenceEqual(j.Raw)) throw new InvalidOperationException("Encoded payload does not decode back.");
                SHA1.HashData(stored).CopyTo(manifest, j.Sha1At);
                var loc = j.Data;
                pending.Add((loc.Cas, stored, off =>
                {
                    var b = TocBytes(loc.Toc);
                    if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(loc.PairAt)) != loc.Offset) throw new InvalidOperationException("Bundle record changed while writing.");
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(loc.PairAt), off);
                    BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(loc.PairAt + 4), (uint)stored.Length);
                    log.Add($"bundle '{bundle.FirstName}': entry → {Path.GetRelativePath(game, loc.Cas)} @{off} ({stored.Length} stored)");
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
        foreach (var (toc, bytes) in tocBytes)
        {
            var tmp = toc + ".cims-tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, toc, true);
        }
        return log;
    }

    static byte[] Read(BundleIndex.Location l) => BundleIndex.ReadBytes(l);
}
