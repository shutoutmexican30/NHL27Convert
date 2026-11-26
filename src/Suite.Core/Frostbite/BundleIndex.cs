using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace Suite.Core.Frostbite;

/// <summary>
/// Every bundle of one NHL 24 game folder, read from the TOCs and the bundle manifests, without FMT: which EBX, RES and
/// chunks each bundle holds, and where each entry's bytes are. This is the name → data map the writers need (a texture's
/// RES is listed by the texture's name; its 132-byte header names the texture's chunk).
///
/// TOC (after the 0x22C header, BE u32 fields): field 1 = bundle table offset, field 2 = bundle count. A table entry is
/// 16 bytes: u32 name offset, u32 size (flags in the top byte), u64 record offset from 0x22C (verified on every bundle
/// of all eight TOCs). A record: u32, u32, u32 flags offset, u32 entry count, u32 0x20 x3, u32 0, then a stream from +0x20:
/// for each entry, a BE location word [0][patch][catalog][cas] first when that entry's flag byte is 1 (a new CAS), then
/// BE u32 offset and size. The flag bytes (one per entry) start at the flags offset. Entry 0 is the bundle's manifest;
/// the others are its EBX, RES and chunks, in that order.
/// Manifest (stored in the CAS): BE u32 size, magic D6 8E 79 9D, LE u32 total / ebx / res / chunk counts, strings offset,
/// meta offset, meta size; then SHA1 per data entry, EBX (name offset, size), RES (name offset, size), RES types (u32),
/// RES meta (16 bytes), RES ids (u64), chunks (id, logical offset, logical size).
/// </summary>
public sealed class BundleIndex
{
    public sealed record Location(string Toc, int PairAt, string Cas, uint Offset, uint Size);
    public sealed record Res(string Name, uint Type, byte[] Meta, ulong Rid, Location Data, Bundle Bundle, int Sha1At);
    public sealed record Chunk(Guid Id, uint LogicalOffset, uint LogicalSize, Location Data, Bundle Bundle, int Sha1At);
    public sealed record Ebx(string Name, Location Data, Bundle Bundle, int Sha1At);
    public sealed record Bundle(string Toc, int RecordAt, Location Manifest, string FirstName);

    public List<Bundle> Bundles { get; } = [];
    public Dictionary<string, List<Res>> ResByName { get; } = new(StringComparer.Ordinal);
    public Dictionary<Guid, List<Chunk>> ChunksById { get; } = [];
    public Dictionary<string, List<Ebx>> EbxByName { get; } = new(StringComparer.Ordinal);
    public string Game { get; }

    BundleIndex(string game) { Game = game; }

    /// <summary>The CAS file a chunk location names. In a merged install (the 1.60 update folded into Data and the
    /// Patch folder deleted — the CIMS game copy's layout) a patch-flagged location resolves to the Data copy.</summary>
    public static string CasPath(string game, bool patch, int catalog, int cas)
    {
        var root = patch ? GameLayout.PatchDir(game) : GameLayout.DataDir(game);
        var path = Path.Combine(root, "Gen4b", "superbundlelayout", $"nhl_installpackage_{catalog:D2}", $"cas_{cas:D2}.cas");
        if (patch && !File.Exists(path)) return CasPath(game, false, catalog, cas);
        return path;
    }

    public static IEnumerable<string> Tocs(string game) => GameLayout.Layers(game)
        .SelectMany(d => Directory.EnumerateFiles(d, "*.toc", SearchOption.AllDirectories)).Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>The install folder above a TOC. TOCs can sit below Gen4b (levels/game/game.toc), so walk up to it.</summary>
    static string GameFolderOf(string toc)
    {
        for (var dir = Path.GetDirectoryName(toc); dir is not null; dir = Path.GetDirectoryName(dir))
            if (Path.GetFileName(dir).Equals("Gen4b", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(Path.GetDirectoryName(dir))!;
        return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(toc)))!;
    }

    /// <summary>One TOC's bundle records with their entry locations (no CAS reads).</summary>
    public static List<(int RecordAt, List<Location> Entries)> Records(string toc, byte[] b)
    {
        var list = new List<(int, List<Location>)>();
        const int H = TocFile.HeaderSize;
        if (b.Length < H + 64 || b[1] != 0xD1 || b[2] != 0xCE) return list;
        uint F(int i) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(H + 4 * i));
        long table = H + F(1), count = F(2);
        if (count == 0 || table + count * 16 > b.Length) return list;
        var game = GameFolderOf(toc);
        for (int i = 0; i < count; i++)
        {
            long rec = H + (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan((int)table + 16 * i + 8));
            if (rec < 0 || rec + 32 > b.Length) continue;
            int flagsAt = (int)rec + (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)rec + 8));
            int n = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)rec + 12));
            if (n < 1 || n > 100_000 || flagsAt + n > b.Length || BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)rec + 16)) != 0x20) continue;
            int at = (int)rec + 0x20;
            string? cas = null;
            var entries = new List<Location>(n);
            bool ok = true;
            for (int k = 0; k < n; k++)
            {
                if (b[flagsAt + k] == 1)
                {
                    uint loc = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
                    cas = CasPath(game, ((loc >> 16) & 0xFF) != 0, (int)((loc >> 8) & 0xFF), (int)(loc & 0xFF));
                    at += 4;
                }
                if (cas is null || at + 8 > flagsAt) { ok = false; break; }
                entries.Add(new Location(toc, at, cas, BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at)), BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at + 4))));
                at += 8;
            }
            if (ok) list.Add(((int)rec, entries));
        }
        return list;
    }

    static readonly object SharedLock = new();
    static readonly List<(string Key, BundleIndex Index)> SharedIndexes = [];

    /// <summary>
    /// The index of <paramref name="game"/>, built once and shared by every page and tool in the process. It is rebuilt
    /// only when a TOC of the game changed (size or write time), so a write by the suite or anyone else is always seen.
    /// The last three games are kept. Callers must treat the index as read only.
    /// </summary>
    public static BundleIndex Shared(string game, Action<string>? progress = null)
    {
        game = Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar);
        var key = game.ToLowerInvariant() + "|" + string.Join(";", Tocs(game).OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(t => { var i = new FileInfo(t); return $"{t}:{i.Length}:{i.LastWriteTimeUtc.Ticks}"; }));
        lock (SharedLock)
            if (SharedIndexes.FirstOrDefault(s => s.Key == key) is { Index: { } hit }) return hit;
        var built = TryLoadCache(game, key);
        if (built is null)
        {
            built = Build(game, progress);
            var toSave = built;
            _ = Task.Run(() => SaveCache(toSave, key)); // ~3 s, off the caller's path
        }
        lock (SharedLock)
        {
            SharedIndexes.RemoveAll(s => s.Key.StartsWith(game.ToLowerInvariant() + "|", StringComparison.Ordinal) || s.Key == key);
            SharedIndexes.Add((key, built));
            while (SharedIndexes.Count > 3) SharedIndexes.RemoveAt(0);
        }
        return built;
    }

    /// <summary>Reads every TOC and every bundle manifest of <paramref name="game"/> (CAS reads in parallel, one pass per file).</summary>
    public static BundleIndex Build(string game, Action<string>? progress = null)
    {
        game = Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar);
        var index = new BundleIndex(game);
        var all = new List<(string Toc, int RecordAt, List<Location> Entries)>();
        foreach (var toc in Tocs(game))
        {
            var recs = Records(toc, File.ReadAllBytes(toc));
            all.AddRange(recs.Select(r => (toc, r.RecordAt, r.Entries)));
            progress?.Invoke($"{Path.GetRelativePath(game, toc)}: {recs.Count:N0} bundles");
        }
        var manifests = new ConcurrentDictionary<(string Toc, int RecordAt), byte[]>();
        Parallel.ForEach(all.GroupBy(r => r.Entries[0].Cas).Where(g => File.Exists(g.Key)), g =>
        {
            // Antivirus/search indexers briefly lock freshly written CAS files; retry instead of failing the build.
            FileStream? fs = null;
            for (var attempt = 0; ; attempt++)
            {
                try { fs = new FileStream(g.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16); break; }
                catch (IOException) when (attempt < 10) { Thread.Sleep(500); }
            }
            using (fs)
            foreach (var r in g.OrderBy(r => r.Entries[0].Offset))
            {
                var m = r.Entries[0];
                if (m.Size < 40 || m.Size > 64 << 20 || m.Offset + (long)m.Size > fs.Length) continue;
                var buf = new byte[m.Size];
                fs.Position = m.Offset;
                fs.ReadExactly(buf);
                if (buf[4] == 0xD6 && buf[5] == 0x8E && buf[6] == 0x79 && buf[7] == 0x9D) manifests[(r.Toc, r.RecordAt)] = buf;
            }
        });
        foreach (var r in all)
        {
            if (!manifests.TryGetValue((r.Toc, r.RecordAt), out var m)) continue;
            int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(at));
            int total = I(8), ebx = I(12), res = I(16), chunks = I(20), strings = I(24);
            if (total != ebx + res + chunks || r.Entries.Count != 1 + total) continue;
            string Name(int nameOff)
            {
                int at = 4 + strings + nameOff;
                if (at < 0 || at >= m.Length) return "";
                int end = Array.IndexOf(m, (byte)0, at);
                return end < 0 ? "" : Encoding.ASCII.GetString(m, at, end - at);
            }
            int ebxAt = 36 + 20 * total, resAt = ebxAt + 8 * ebx, typesAt = resAt + 8 * res, metaAt = typesAt + 4 * res,
                ridAt = metaAt + 16 * res, chunkAt = ridAt + 8 * res;
            if (chunkAt + 24 * chunks > m.Length) continue;
            var bundle = new Bundle(r.Toc, r.RecordAt, r.Entries[0], ebx > 0 ? Name(I(ebxAt)) : "");
            index.Bundles.Add(bundle);
            for (int k = 0; k < ebx; k++)
            {
                var name = Name(I(ebxAt + 8 * k));
                if (!index.EbxByName.TryGetValue(name, out var l)) index.EbxByName[name] = l = [];
                l.Add(new Ebx(name, r.Entries[1 + k], bundle, 36 + 20 * k));
            }
            for (int k = 0; k < res; k++)
            {
                var name = Name(I(resAt + 8 * k));
                var entry = new Res(name, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(typesAt + 4 * k)), m.AsSpan(metaAt + 16 * k, 16).ToArray(),
                    BinaryPrimitives.ReadUInt64LittleEndian(m.AsSpan(ridAt + 8 * k)), r.Entries[1 + ebx + k], bundle, 36 + 20 * (ebx + k));
                if (!index.ResByName.TryGetValue(name, out var l)) index.ResByName[name] = l = [];
                l.Add(entry);
            }
            for (int k = 0; k < chunks; k++)
            {
                int c = chunkAt + 24 * k;
                var id = new Guid(m.AsSpan(c, 16));
                var entry = new Chunk(id, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(c + 16)), BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(c + 20)),
                    r.Entries[1 + ebx + res + k], bundle, 36 + 20 * (ebx + res + k));
                if (!index.ChunksById.TryGetValue(id, out var l)) index.ChunksById[id] = l = [];
                l.Add(entry);
            }
        }
        progress?.Invoke($"{index.Bundles.Count:N0} bundles, {index.ResByName.Count:N0} resources, {index.ChunksById.Count:N0} bundle chunks");
        return index;
    }

    // ------------------------------------------------------------------ disk cache
    // Reading 150k bundle manifests from the CAS files takes ~5 s warm and 25+ s from a cold disk. The finished index is
    // saved once per TOC state (the same key as Shared: every TOC's path, size and write time). Writers only append to a
    // CAS and repoint TOCs, so an unchanged TOC set means unchanged manifests. Any read problem falls back to a rebuild.

    const int CacheVersion = 1;
    static string CacheDir => SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "index", "bundles"));
    static string CacheFile(string key) => Path.Combine(CacheDir, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..20] + ".bin");

    static void SaveCache(BundleIndex index, string key)
    {
        try
        {
            var path = CacheFile(key);
            var tmp = path + ".tmp";
            var strings = new Dictionary<string, int>(StringComparer.Ordinal);
            var stringList = new List<string>();
            int S(string s) { if (!strings.TryGetValue(s, out var i)) { strings[s] = i = stringList.Count; stringList.Add(s); } return i; }
            var bundleIds = new Dictionary<Bundle, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < index.Bundles.Count; i++) bundleIds[index.Bundles[i]] = i;
            using var body = new MemoryStream();
            using (var w = new BinaryWriter(body, Encoding.UTF8, true))
            {
                void L(Location l) { w.Write(S(l.Toc)); w.Write(l.PairAt); w.Write(S(l.Cas)); w.Write(l.Offset); w.Write(l.Size); }
                w.Write(index.Bundles.Count);
                foreach (var b in index.Bundles) { w.Write(S(b.Toc)); w.Write(b.RecordAt); L(b.Manifest); w.Write(S(b.FirstName)); }
                w.Write(index.EbxByName.Count);
                foreach (var (name, list) in index.EbxByName)
                {
                    w.Write(S(name)); w.Write(list.Count);
                    foreach (var e in list) { L(e.Data); w.Write(bundleIds[e.Bundle]); w.Write(e.Sha1At); }
                }
                w.Write(index.ResByName.Count);
                foreach (var (name, list) in index.ResByName)
                {
                    w.Write(S(name)); w.Write(list.Count);
                    foreach (var r in list) { w.Write(r.Type); w.Write(r.Meta.Length); w.Write(r.Meta); w.Write(r.Rid); L(r.Data); w.Write(bundleIds[r.Bundle]); w.Write(r.Sha1At); }
                }
                w.Write(index.ChunksById.Count);
                foreach (var (id, list) in index.ChunksById)
                {
                    w.Write(id.ToByteArray()); w.Write(list.Count);
                    foreach (var c in list) { w.Write(c.LogicalOffset); w.Write(c.LogicalSize); L(c.Data); w.Write(bundleIds[c.Bundle]); w.Write(c.Sha1At); }
                }
            }
            using (var fs = File.Create(tmp))
            using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
            using (var w = new BinaryWriter(gz, Encoding.UTF8))
            {
                w.Write(CacheVersion); w.Write(key); w.Write(index.Game);
                w.Write(stringList.Count); foreach (var s in stringList) w.Write(s);
                body.Position = 0; w.Flush(); body.CopyTo(gz);
            }
            File.Move(tmp, path, true);
            foreach (var old in new DirectoryInfo(CacheDir).GetFiles("*.bin").OrderByDescending(f => f.LastWriteTimeUtc).Skip(4)) old.Delete();
        }
        catch (Exception ex) { SuiteLog.Write("bundle index cache not saved: " + ex.Message); }
    }

    static BundleIndex? TryLoadCache(string game, string key)
    {
        var path = CacheFile(key);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(gz, 1 << 20), Encoding.UTF8);
            if (r.ReadInt32() != CacheVersion || r.ReadString() != key) return null;
            var index = new BundleIndex(r.ReadString());
            if (!string.Equals(index.Game, game, StringComparison.OrdinalIgnoreCase)) return null;
            var strings = new string[r.ReadInt32()];
            for (int i = 0; i < strings.Length; i++) strings[i] = r.ReadString();
            Location L() => new(strings[r.ReadInt32()], r.ReadInt32(), strings[r.ReadInt32()], r.ReadUInt32(), r.ReadUInt32());
            int nb = r.ReadInt32();
            for (int i = 0; i < nb; i++) index.Bundles.Add(new Bundle(strings[r.ReadInt32()], r.ReadInt32(), L(), strings[r.ReadInt32()]));
            int ne = r.ReadInt32();
            for (int i = 0; i < ne; i++)
            {
                var name = strings[r.ReadInt32()]; int n = r.ReadInt32(); var list = new List<Ebx>(n);
                for (int k = 0; k < n; k++) { var loc = L(); list.Add(new Ebx(name, loc, index.Bundles[r.ReadInt32()], r.ReadInt32())); }
                index.EbxByName[name] = list;
            }
            int nr = r.ReadInt32();
            for (int i = 0; i < nr; i++)
            {
                var name = strings[r.ReadInt32()]; int n = r.ReadInt32(); var list = new List<Res>(n);
                for (int k = 0; k < n; k++)
                {
                    uint type = r.ReadUInt32(); var meta = r.ReadBytes(r.ReadInt32()); ulong rid = r.ReadUInt64(); var loc = L();
                    list.Add(new Res(name, type, meta, rid, loc, index.Bundles[r.ReadInt32()], r.ReadInt32()));
                }
                index.ResByName[name] = list;
            }
            int nc = r.ReadInt32();
            for (int i = 0; i < nc; i++)
            {
                var id = new Guid(r.ReadBytes(16)); int n = r.ReadInt32(); var list = new List<Chunk>(n);
                for (int k = 0; k < n; k++) { uint lo = r.ReadUInt32(), ls = r.ReadUInt32(); var loc = L(); list.Add(new Chunk(id, lo, ls, loc, index.Bundles[r.ReadInt32()], r.ReadInt32())); }
                index.ChunksById[id] = list;
            }
            return index;
        }
        catch (Exception ex) { SuiteLog.Write("bundle index cache unreadable, rebuilding: " + ex.Message); return null; }
    }

    public static byte[] ReadBytes(Location l)
    {
        using var fs = new FileStream(l.Cas, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Position = l.Offset;
        var buf = new byte[l.Size];
        fs.ReadExactly(buf);
        return buf;
    }
}
