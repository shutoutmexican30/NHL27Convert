using System.Buffers.Binary;
using System.Text;
using Suite.Core.Frostbite;

namespace Suite.Sounds;

/// <summary>
/// Read-only NHL 27 (PS5, PPSA34063 app0) asset reader. The superbundle TOCs use NHL 24's field order (after the 0x3C
/// magic: bundle table, bundle count, …, chunk id table, chunk count, chunk location table) with two differences:
/// <list type="bullet">
/// <item>Data-tree TOCs have no 0x22C signature block (the header is at 0); Patch-tree TOCs keep it.</item>
/// <item>A chunk location is 16 bytes {u32 magic 0x0000F895 / 0x0001F895, u32 loc, u32 offset, u32 size}; 0x0001F895 in
/// a Patch TOC means the bytes are in the Patch tree, anything else means Data. <c>loc</c>: package =
/// ((loc &gt;&gt; 16) &amp; 0xF) ^ 4, cas = loc &amp; 0xFFFF (from D:\Tools\NHL27TextureViewer\frostbite\toc.py).</item>
/// </list>
/// Bundle records and manifests are laid out as in NHL 24 (see <see cref="BundleIndex"/>). CAS blocks are stored
/// uncompressed, so no Oodle is needed.
/// </summary>
public sealed class Ps5SoundAssets : ISoundAssets
{
    public sealed record Loc(string Cas, uint Offset, uint Size);

    readonly string _root;
    readonly Dictionary<string, List<Loc>> _ebx = new(StringComparer.Ordinal), _res = new(StringComparer.Ordinal);
    readonly Dictionary<Guid, Loc> _chunks = [];
    public int Bundles { get; private set; }
    public List<string> Warnings { get; } = [];

    Ps5SoundAssets(string root) { _root = root; }

    public IEnumerable<string> EbxNames => _ebx.Keys;
    public IReadOnlyDictionary<string, List<Loc>> EbxLocations => _ebx;
    public IReadOnlyDictionary<Guid, Loc> ChunkLocations => _chunks;

    /// <summary>Indexes every TOC under app0\Data\Ps5 and app0\Patch\Ps5. <paramref name="tocFilter"/> limits which TOCs are read.</summary>
    public static Ps5SoundAssets Build(string app0, Func<string, bool>? tocFilter = null)
    {
        var a = new Ps5SoundAssets(app0);
        // Patch first so its copies win (first add is kept for chunks; EBX/RES lists keep Patch copies in front).
        foreach (var tree in new[] { "Patch", "Data" })
        {
            var dir = Path.Combine(app0, tree, "Ps5");
            if (!Directory.Exists(dir)) continue;
            foreach (var toc in Directory.EnumerateFiles(dir, "*.toc", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                if (tocFilter?.Invoke(toc) ?? true) a.ReadToc(toc, tree);
        }
        return a;
    }

    void ReadToc(string toc, string tree)
    {
        var b = File.ReadAllBytes(toc);
        if (b.Length < 64) return;
        int h = b[0] == 0x00 && b[1] == 0xD1 && b[2] == 0xCE ? TocFile.HeaderSize : 0;
        if (h + 64 > b.Length || BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(h)) != 0x3C) return;
        uint F(int i) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(h + 4 * i));

        // Chunk table: 20-byte id records {16 bytes id (reversed), u32 index}; location at table6 + (index & 0x7FFFFFFF) * 4.
        long ids = h + F(4), count = F(5), locs = h + F(6);
        for (int k = 0; k < count && ids + k * 20 + 20 <= b.Length; k++)
        {
            int p = (int)ids + k * 20;
            var raw = b.AsSpan(p, 16).ToArray();
            Array.Reverse(raw);
            long e = locs + (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p + 16)) & 0x7FFFFFFF) * 4L;
            if (e + 16 > b.Length) continue;
            uint magic = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)e));
            var cas = CasPath(magic == 0x0001F895 ? tree : "Data", BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)e + 4)));
            _chunks.TryAdd(new Guid(raw), new Loc(cas, BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)e + 8)), BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)e + 12))));
        }

        // Bundles: table of 16-byte records whose u64 at +8 points (from h) at {.. +8 flags offset, +12 entry count, +16 0x20}.
        long table = h + F(1), bundles = F(2);
        for (int i = 0; i < bundles && table + 16 * i + 16 <= b.Length; i++)
        {
            long rec = h + (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan((int)table + 16 * i + 8));
            if (rec < 0 || rec + 32 > b.Length) continue;
            int flagsAt = (int)rec + (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)rec + 8));
            int n = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)rec + 12));
            int first = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)rec + 16));
            if (n < 1 || n > 100_000 || flagsAt + n > b.Length || first is < 0x20 or > 0x40) continue;
            int at = (int)rec + first;
            string? cas = null;
            var entries = new List<Loc>(n);
            for (int k = 0; k < n; k++)
            {
                // A "new file" flag is followed by {u32 magic 0x0000F895 / 0x0001F895, u32 loc} (NHL 24: just u32 loc).
                if ((b[flagsAt + k] & 0x80) != 0 || b[flagsAt + k] == 1)
                {
                    uint magic = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
                    cas = CasPath(magic == 0x0001F895 ? tree : "Data", BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at + 4)));
                    at += 8;
                }
                if (cas is null || at + 8 > flagsAt) { entries.Clear(); break; }
                entries.Add(new Loc(cas, BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at)), BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at + 4))));
                at += 8;
            }
            if (entries.Count > 0) ReadManifest(entries);
        }
    }

    /// <summary>The bundle's cas file: Patch-tree records point into the Patch tree when that file exists.</summary>
    string CasPath(string tree, uint loc)
    {
        int package = (int)((loc >> 16) & 0xF) ^ 4, cas = (int)(loc & 0xFFFF);
        string P(string t) => Path.Combine(_root, t, "Ps5", "superbundlelayout", $"nhl_installpackage_{package:D2}", $"cas_{cas:D2}.cas");
        var p = P(tree);
        return tree == "Patch" && !File.Exists(p) ? P("Data") : p;
    }

    void ReadManifest(List<Loc> entries)
    {
        var mLoc = entries[0];
        if (mLoc.Size < 40 || mLoc.Size > 64 << 20 || !File.Exists(mLoc.Cas)) return;
        byte[] m;
        try { m = Nhl24SoundAssets.Decode(Read(mLoc)); } catch (Exception ex) { Warnings.Add($"{mLoc.Cas}@{mLoc.Offset}: {ex.Message}"); return; }
        if (m.Length < 40 || m[4] != 0xD6 || m[5] != 0x8E || m[6] != 0x79 || m[7] != 0x9D) return;
        int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(at));
        int total = I(8), ebx = I(12), res = I(16), chunks = I(20), strings = I(24);
        if (total != ebx + res + chunks || entries.Count != 1 + total) return;
        string Name(int off)
        {
            int at = 4 + strings + off;
            if (at < 0 || at >= m.Length) return "";
            int end = Array.IndexOf(m, (byte)0, at);
            return end < 0 ? "" : Encoding.ASCII.GetString(m, at, end - at);
        }
        int ebxAt = 36 + 20 * total, resAt = ebxAt + 8 * ebx;
        Bundles++;
        for (int k = 0; k < ebx; k++) Add(_ebx, Name(I(ebxAt + 8 * k)), entries[1 + k]);
        for (int k = 0; k < res; k++) Add(_res, Name(I(resAt + 8 * k)), entries[1 + ebx + k]);
        int chunkAt = resAt + 8 * res + 4 * res + 16 * res + 8 * res;
        for (int k = 0; k < chunks && chunkAt + 24 * k + 16 <= m.Length; k++)
            _chunks.TryAdd(new Guid(m.AsSpan(chunkAt + 24 * k, 16)), entries[1 + ebx + res + k]);
    }

    static void Add(Dictionary<string, List<Loc>> d, string name, Loc l)
    {
        if (name.Length == 0) return;
        if (!d.TryGetValue(name, out var list)) d[name] = list = [];
        list.Add(l);
    }

    public static byte[] Read(Loc l)
    {
        using var fs = new FileStream(l.Cas, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Position = l.Offset;
        var buf = new byte[l.Size];
        fs.ReadExactly(buf);
        return buf;
    }

    public byte[]? Ebx(string name) => _ebx.TryGetValue(name, out var l) ? Nhl24SoundAssets.Decode(Read(l[0])) : null;
    public byte[]? Res(string name) => _res.TryGetValue(name, out var l) ? Nhl24SoundAssets.Decode(Read(l[0])) : null;
    public byte[]? Chunk(Guid id) => _chunks.TryGetValue(id, out var l) ? Nhl24SoundAssets.Decode(Read(l)) : null;
}
