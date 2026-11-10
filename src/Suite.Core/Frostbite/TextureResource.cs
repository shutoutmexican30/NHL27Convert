using System.Buffers.Binary;
using System.Text;

namespace Suite.Core.Frostbite;

/// <summary>
/// Reads a texture's 132-byte `.res` header straight out of the game's bundle manifests, with no FMT and no elevation.
///
/// A bundle record in a TOC names its CAS and the offset of a manifest, and the manifest lists how many EBX, RES and chunk
/// entries the bundle has plus the string table holding their names. The TOC record's pair table then gives each entry its
/// (offset, size) in that CAS, in the same order: manifest, EBX..., RES..., chunks. So the RES named after the texture is
/// one pair lookup away - which is what the texture writer needs to know the slot's size and mip layout.
/// </summary>
public static class TextureResource
{
    public sealed record Found(string Toc, string Cas, uint Offset, uint Size, byte[] Bytes);

    /// <summary>The `.res` bytes of one texture, or null when no bundle declares it.</summary>
    public static Found? Read(string gameDir, string textureName)
    {
        var hit = Find(gameDir, [textureName]).GetValueOrDefault(textureName);
        if (hit is null) return null;
        using var fs = new FileStream(Path.Combine(gameDir, hit.Cas), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (hit.Offset + (long)hit.Size > fs.Length) return null;
        fs.Position = hit.Offset;
        var buf = new byte[hit.Size];
        fs.ReadExactly(buf);
        return hit with { Bytes = buf };
    }

    /// <summary>Every texture among <paramref name="names"/> the bundles declare, keyed by name.</summary>
    public static Dictionary<string, Found> Find(string gameDir, IReadOnlyCollection<string> names)
    {
        var want = names.ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, Found>(StringComparer.Ordinal);
        if (want.Count == 0) return found;

        foreach (var toc in Tocs(gameDir))
        {
            byte[] b;
            try { b = File.ReadAllBytes(toc); } catch { continue; }
            const int H = TocFile.HeaderSize;
            if (b.Length < H + 64 || b[1] != 0xD1 || b[2] != 0xCE) continue;
            uint F(int i) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(H + 4 * i));
            long table = H + F(1), count = F(2), recBase = H + F(3);
            if (count == 0 || table + count * 16 > b.Length) continue;

            for (int i = 0; i < count; i++)
            {
                long rec = recBase + (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan((int)table + 16 * i + 8));
                if (rec + 44 > b.Length) continue;
                int n = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)rec + 12));
                if (n < 2 || n > 512 || rec + 36 + 8 * n > b.Length) continue;
                uint loc = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)rec + 32));
                var casPath = CasPath(gameDir, ((loc >> 16) & 0xFF) != 0, (int)((loc >> 8) & 0xFF), (int)(loc & 0xFF));
                uint mo = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)rec + 36));
                uint ms = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)rec + 40));
                if (ms < 40 || ms > 1 << 22) continue;
                if (!File.Exists(casPath)) continue;

                byte[] m;
                try
                {
                    using var fs = new FileStream(casPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (mo + (long)ms > fs.Length) continue;
                    fs.Position = mo;
                    m = new byte[ms];
                    fs.ReadExactly(m);
                }
                catch { continue; }
                if (m[4] != 0xD6 || m[5] != 0x8E || m[6] != 0x79 || m[7] != 0x9D) continue;

                int total = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(8));
                int ebx = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(12));
                int res = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(16));
                int chunks = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(20));
                int strings = BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(24));
                if (res <= 0 || ebx < 1 || chunks < 0 || total != ebx + res + chunks || n != 1 + total) continue;
                int resAt = 36 + 20 * total + 8 * ebx;
                if (resAt + 8 * res > m.Length) continue;

                for (int r = 0; r < res; r++)
                {
                    int nameAt = 4 + strings + BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(resAt + 8 * r));
                    if (nameAt < 0 || nameAt >= m.Length) continue;
                    int end = Array.IndexOf(m, (byte)0, nameAt);
                    if (end < 0) continue;
                    var name = Encoding.ASCII.GetString(m, nameAt, end - nameAt);
                    var key = Match(want, name);
                    if (key is null || found.ContainsKey(key)) continue;
                    int pair = 1 + ebx + r;
                    int at = (int)rec + 36 + 8 * pair;
                    if (at + 8 > b.Length) continue;
                    uint off = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
                    uint size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at + 4));
                    if (size == 0 || size > 1 << 20) continue;
                    found[key] = new Found(toc, Path.GetRelativePath(gameDir, casPath), off, size, []);
                }
            }
        }
        return found;
    }

    /// <summary>A manifest string may be the full asset path or just its leaf; accept either.</summary>
    static string? Match(HashSet<string> want, string name)
    {
        if (want.Contains(name)) return name;
        var leaf = name.Split('/')[^1];
        foreach (var w in want) if (w.Split('/')[^1] == leaf) return w;
        return null;
    }

    static IEnumerable<string> Tocs(string gameDir) => BundleIndex.Tocs(gameDir);

    static string CasPath(string gameDir, bool patch, int catalog, int cas) => BundleIndex.CasPath(gameDir, patch, catalog, cas);
}