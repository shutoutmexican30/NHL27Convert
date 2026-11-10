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
            using var fs = new FileStream(g.Key, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
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

    public static byte[] ReadBytes(Location l)
    {
        using var fs = new FileStream(l.Cas, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Position = l.Offset;
        var buf = new byte[l.Size];
        fs.ReadExactly(buf);
        return buf;
    }
}
