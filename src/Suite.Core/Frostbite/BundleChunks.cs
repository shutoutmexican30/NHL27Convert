using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Suite.Core.Frostbite;

/// <summary>
/// Chunks that live in bundles (UI textures such as player portraits), replaced in one batch.
/// TOC (after the 0x22C header, BE u32 fields): field 1 = bundle table, field 2 = bundle count, field 3 = base of the
/// bundle records. A bundle table entry is 16 bytes (u32 name offset, u32 size with flag bits, u64 record offset). A
/// record: u32 x3, u32 entry count, u32 x3, u32 0, u32 [0][patch][catalog][cas], then per entry a BE (offset, size) pair
/// into that CAS. Entry 0 is the bundle manifest; the others are the bundle's data (EBX, RES, chunks, in that order).
/// Manifest (in the CAS): BE u32 size, magic D6 8E 79 9D, LE u32 total / ebx / res / chunk counts, strings offset, meta
/// offset, meta size; then a SHA1 per data entry (SHA1 of its stored bytes), EBX (name offset, size), RES (name offset,
/// size), RES types, RES meta (16 bytes), RES ids (8 bytes), chunks (id, logical offset, logical size), strings.
/// Replacing a chunk: re-encode it with the same block split, append it and a manifest copy carrying its new SHA1 to the
/// same CAS, repoint both TOC pairs. Append-only, so <see cref="ApplyTarget.RestoreFromBackup"/> undoes it exactly.
/// </summary>
public sealed class BundleChunks
{
    readonly string _game;
    public BundleChunks(string gameDir, SuiteSettings? settings = null)
    {
        _game = Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar);
        Oodle.Ensure(settings);
    }

    sealed record Hit(string Toc, int PairsAt, int ChunkPair, int Sha1At, string Cas, uint ManifestOff, uint ManifestSize, uint ChunkOff, uint ChunkSize);

    string CasPath(bool patch, int cat, int cas) => Path.Combine(_game, patch ? "Patch" : "Data", "Gen4b", "superbundlelayout",
        $"nhl_installpackage_{cat:D2}", $"cas_{cas:D2}.cas");

    IEnumerable<string> Tocs() => new[] { "Patch", "Data" }.Select(s => Path.Combine(_game, s)).Where(Directory.Exists)
        .SelectMany(d => Directory.EnumerateFiles(d, "*.toc", SearchOption.AllDirectories)).Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every bundle record whose first EBX is one of <paramref name="names"/> and that holds exactly one chunk.</summary>
    Dictionary<string, List<Hit>> Find(IReadOnlySet<string> names, Dictionary<string, byte[]> tocBytes, Action<string>? progress)
    {
        var hits = new ConcurrentDictionary<string, List<Hit>>(StringComparer.Ordinal);
        foreach (var toc in Tocs())
        {
            var b = File.ReadAllBytes(toc);
            const int H = TocFile.HeaderSize;
            if (b.Length < H + 64 || b[1] != 0xD1 || b[2] != 0xCE) continue;
            uint F(int i) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(H + 4 * i));
            long table = H + F(1), count = F(2), recBase = H + F(3);
            if (count == 0 || table + count * 16 > b.Length) continue;
            // Records grouped by CAS so each CAS is read front to back once.
            var byCas = new Dictionary<string, List<(int PairsAt, int Count, uint Off, uint Size)>>();
            for (int i = 0; i < count; i++)
            {
                long rec = recBase + (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan((int)table + 16 * i + 8));
                if (rec + 36 > b.Length) continue;
                int n = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)rec + 12));
                if (n < 2 || n > 64 || rec + 36 + 8 * n > b.Length) continue;
                uint loc = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)rec + 32));
                var cas = CasPath(((loc >> 16) & 0xFF) != 0, (int)((loc >> 8) & 0xFF), (int)(loc & 0xFF));
                uint mo = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)rec + 36)), ms = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)rec + 40));
                if (ms < 40 || ms > 1 << 20) continue;
                if (!byCas.TryGetValue(cas, out var l)) byCas[cas] = l = [];
                l.Add(((int)rec + 36, n, mo, ms));
            }
            progress?.Invoke($"Scanning {Path.GetFileName(toc)}: {count:N0} bundles");
            bool any = false;
            Parallel.ForEach(byCas.Where(kv => File.Exists(kv.Key)), kv =>
            {
                using var fs = new FileStream(kv.Key, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                foreach (var (pairsAt, n, mo, ms) in kv.Value.OrderBy(x => x.Off))
                {
                    if (mo + (long)ms > fs.Length) continue;
                    var m = new byte[ms];
                    fs.Position = mo;
                    fs.ReadExactly(m);
                    if (m[4] != 0xD6 || m[5] != 0x8E || m[6] != 0x79 || m[7] != 0x9D) continue;
                    int total = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(8)), ebx = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(12)),
                        res = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(16)), chunks = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(20)),
                        strings = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(24));
                    if (chunks != 1 || ebx < 1 || total != ebx + res + chunks || n != 1 + total) continue;
                    int ebxAt = 36 + 20 * total;
                    if (ebxAt + 8 * ebx > m.Length) continue;
                    // Any EBX of the bundle may carry the texture's name (their order varies between bundles).
                    string? name = null;
                    for (int e = 0; e < ebx && name is null; e++)
                    {
                        int nameAt = 4 + strings + BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(ebxAt + 8 * e));
                        if (nameAt < 0 || nameAt >= m.Length) continue;
                        int end = Array.IndexOf(m, (byte)0, nameAt);
                        if (end < 0) continue;
                        var candidate = Encoding.ASCII.GetString(m, nameAt, end - nameAt);
                        if (names.Contains(candidate)) name = candidate;
                    }
                    if (name is null) continue;
                    int chunkPair = 1 + ebx + res;
                    uint co = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pairsAt + 8 * chunkPair)), cs = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pairsAt + 8 * chunkPair + 4));
                    hits.AddOrUpdate(name, _ => [new Hit(toc, pairsAt, chunkPair, 36 + 20 * (ebx + res), kv.Key, mo, ms, co, cs)],
                        (_, list) => { lock (list) list.Add(new Hit(toc, pairsAt, chunkPair, 36 + 20 * (ebx + res), kv.Key, mo, ms, co, cs)); return list; });
                    any = true;
                }
            });
            if (any) tocBytes[toc] = b;
        }
        return new Dictionary<string, List<Hit>>(hits);
    }

    static byte[] Read(string cas, uint off, uint size)
    {
        using var fs = new FileStream(cas, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Position = off;
        var buf = new byte[size];
        fs.ReadExactly(buf);
        return buf;
    }

    /// <summary>The raw (decoded) data of each named texture's chunk, as the game would read it now.</summary>
    public Dictionary<string, byte[]> Read(IReadOnlySet<string> names)
    {
        var tocs = new Dictionary<string, byte[]>();
        return Find(names, tocs, null).ToDictionary(kv => kv.Key, kv =>
        {
            var h = kv.Value[0];
            return ChunkBlocks.Parse(Read(h.Cas, h.ChunkOff, h.ChunkSize)).Decode();
        });
    }

    /// <summary>
    /// Replaces the chunks of the named bundle textures (name → new raw data, same size as the old). Take
    /// <see cref="ApplyTarget.Backup"/> first. Returns log lines; names not found are reported, not fatal.
    /// </summary>
    public List<string> Replace(IReadOnlyDictionary<string, byte[]> raw, Action<string>? progress = null)
    {
        var log = new List<string>();
        var tocBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var hits = Find(raw.Keys.ToHashSet(StringComparer.Ordinal), tocBytes, progress);
        foreach (var missing in raw.Keys.Where(k => !hits.ContainsKey(k))) log.Add("not found: " + missing);

        // Encode every chunk and its manifest copy in parallel (one per distinct bundle copy).
        var jobs = hits.SelectMany(kv => kv.Value.Select(h => (Name: kv.Key, Hit: h)))
            .GroupBy(j => (j.Hit.Cas, j.Hit.ManifestOff)).Select(g => g.ToList()).ToList();
        var built = new ConcurrentDictionary<(string Cas, uint ManifestOff), (byte[] Chunk, byte[] Manifest)>();
        int done = 0;
        Parallel.ForEach(jobs, group =>
        {
            var (name, h) = group[0];
            var old = ChunkBlocks.Parse(Read(h.Cas, h.ChunkOff, h.ChunkSize));
            var data = raw[name];
            if (data.Length != old.Blocks.Sum(x => x.RawSize)) throw new InvalidOperationException($"{name}: new data is {data.Length} bytes, the game's is {old.Blocks.Sum(x => x.RawSize)}.");
            var ms = new MemoryStream();
            int at = 0;
            foreach (var blk in old.Blocks) { ms.Write(ChunkBlocks.EncodeBlock(data.AsSpan(at, blk.RawSize))); at += blk.RawSize; }
            var chunk = ms.ToArray();
            if (!ChunkBlocks.Parse(chunk).Decode().AsSpan().SequenceEqual(data)) throw new InvalidOperationException($"{name}: encoded chunk does not decode back.");
            var manifest = Read(h.Cas, h.ManifestOff, h.ManifestSize);
            SHA1.HashData(chunk).CopyTo(manifest, h.Sha1At);
            built[(h.Cas, h.ManifestOff)] = (chunk, manifest);
            var n = Interlocked.Increment(ref done);
            if (n % 50 == 0) progress?.Invoke($"Encoded {n}/{jobs.Count}");
        });

        // Append per CAS file in one go, then repoint every TOC pair.
        var newLoc = new Dictionary<(string Cas, uint ManifestOff), (uint ChunkOff, uint ChunkSize, uint ManOff, uint ManSize)>();
        foreach (var byCas in built.GroupBy(kv => kv.Key.Cas))
        {
            using var fs = new FileStream(byCas.Key, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 20);
            foreach (var (key, (chunk, manifest)) in byCas.OrderBy(kv => kv.Key.ManifestOff))
            {
                long co = fs.Position;
                fs.Write(chunk);
                long mo = fs.Position;
                fs.Write(manifest);
                if (fs.Position > uint.MaxValue) throw new InvalidOperationException($"{byCas.Key} would grow past 4 GB.");
                newLoc[key] = ((uint)co, (uint)chunk.Length, (uint)mo, (uint)manifest.Length);
            }
            fs.Flush(true);
            log.Add($"appended {byCas.Count()} chunk(s) + manifests to {Path.GetRelativePath(_game, byCas.Key)}");
        }
        foreach (var (name, list) in hits)
            foreach (var h in list)
            {
                var b = tocBytes[h.Toc];
                // The TOC must still say what was read.
                if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(h.PairsAt)) != h.ManifestOff ||
                    BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(h.PairsAt + 8 * h.ChunkPair)) != h.ChunkOff)
                    throw new InvalidOperationException($"{name}: TOC entry changed while writing.");
                var l = newLoc[(h.Cas, h.ManifestOff)];
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(h.PairsAt), l.ManOff);
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(h.PairsAt + 4), l.ManSize);
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(h.PairsAt + 8 * h.ChunkPair), l.ChunkOff);
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(h.PairsAt + 8 * h.ChunkPair + 4), l.ChunkSize);
            }
        foreach (var (toc, b) in tocBytes)
        {
            var tmp = toc + ".cims-tmp";
            File.WriteAllBytes(tmp, b);
            File.Move(tmp, toc, true);
        }
        log.Add($"{hits.Count} texture(s) replaced in {hits.Values.Sum(l => l.Count)} bundle record(s); {tocBytes.Count} TOC file(s) updated");
        return log;
    }
}
