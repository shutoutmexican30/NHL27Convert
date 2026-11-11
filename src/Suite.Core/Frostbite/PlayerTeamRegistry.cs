using System.Buffers.Binary;
using System.Text;

namespace Suite.Core.Frostbite;

/// <summary>One new uniform to register: every row of <paramref name="SourceFolder"/> is mirrored for <paramref name="TargetFolder"/>.</summary>
public sealed record KitRegistration(string SourceFolder, string TargetFolder)
{
    /// <summary>The "team_variant" token inside kit names, e.g. detroit_home from .../detroit/adidas/home.</summary>
    public static string Identity(string folder)
    {
        var parts = folder.Trim('/').Split('/');
        if (parts.Length < 3) throw new InvalidDataException("Not a kit folder: " + folder);
        return parts[^3] + "_" + parts[^1];
    }
}

/// <summary>
/// The player_team bundle-reference table (<see cref="ResourceName"/>): the name-to-bundle list uniform.lua resolves kit
/// names through. Layout: 64-byte header (suffix string, pointers to C, A, B and tail, then counts), string pool,
/// A rows (24 bytes: bundle name, bundle folder or empty, group pointer), B rows (16 bytes: leaf, folder),
/// C rows (16 bytes: FNV-1 64 key, A index, B index; sorted by key, one row for the leaf and one for folder/leaf),
/// a 48-byte tail, then the relocation table. A bundle row's name is either a full path, or a bare leaf with the
/// folder held in the second column; both forms must be rewritten or the row keeps pointing at the source kit.
/// </summary>
public static class PlayerTeamRegistry
{
    public const string ResourceName = "content/common/configs/bundlereftable/player_team_brt";

    public sealed record Result(byte[] Raw, byte[] Meta, int Assets, int Bundles);
    public sealed record BundleRow(string Name, string Folder);
    public sealed record AssetRow(string Leaf, string Folder, BundleRow Bundle);

    public static ulong Key(string text)
    {
        ulong hash = 0xcbf29ce484222325;
        foreach (byte c in Encoding.ASCII.GetBytes(text)) hash = unchecked(hash * 0x100000001b3) ^ c;
        return hash;
    }

    /// <summary>The bundle a row loads: folder (when the name is bare) + name + the table's suffix, under gen4b/.</summary>
    public static string BundleName(BundleRow row, string suffix = "_player_team_brt") =>
        "gen4b/" + (row.Name.Contains('/') || row.Folder.Length == 0 ? row.Name : row.Folder + "/" + row.Name) + suffix;

    /// <summary>Every asset row in one folder, resolved through the sorted key rows.</summary>
    public static List<AssetRow> Rows(byte[] b, string folder)
    {
        var t = new Table(b);
        var wanted = Enumerable.Range(0, t.NB).Where(i => t.Str(t.U(t.OB + i * 16 + 8)) == folder).ToHashSet();
        var seen = new HashSet<int>();
        var rows = new List<AssetRow>();
        for (int i = 0; i < t.NC; i++)
        {
            int bi = (int)t.I(t.OC + i * 16 + 12);
            if (!wanted.Contains(bi) || !seen.Add(bi)) continue;
            int a = (int)t.I(t.OC + i * 16 + 8);
            rows.Add(new AssetRow(t.Str(t.U(t.OB + bi * 16)), folder,
                new BundleRow(t.Str(t.U(t.OA + a * 24)), t.Str(t.U(t.OA + a * 24 + 8)))));
        }
        return rows;
    }

    /// <summary>All registered folders beneath a team prefix, including numbered alternate kits.</summary>
    public static string[] Folders(byte[] b, string prefix)
    {
        var t = new Table(b);
        string parent = prefix.TrimEnd('/') + "/";
        return Enumerable.Range(0, t.NB).Select(i => t.Str(t.U(t.OB + i * 16 + 8)))
            .Where(folder => folder.StartsWith(parent, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>True when the sorted key rows contain <paramref name="key"/>'s FNV-1 hash.</summary>
    public static bool Contains(byte[] b, string key)
    {
        var t = new Table(b);
        ulong h = Key(key);
        int lo = 0, hi = t.NC - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            ulong v = t.U(t.OC + mid * 16);
            if (v == h) return true;
            if (v < h) lo = mid + 1; else hi = mid - 1;
        }
        return false;
    }

    /// <summary>Resolves one folder/leaf lookup to its stored row, including aliases to another kit folder.</summary>
    public static AssetRow? Resolve(byte[] b, string key)
    {
        var t = new Table(b);
        ulong h = Key(key);
        int lo = 0, hi = t.NC - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            ulong value = t.U(t.OC + mid * 16);
            if (value == h)
            {
                int a = (int)t.I(t.OC + mid * 16 + 8), bi = (int)t.I(t.OC + mid * 16 + 12);
                return new AssetRow(t.Str(t.U(t.OB + bi * 16)), t.Str(t.U(t.OB + bi * 16 + 8)),
                    new BundleRow(t.Str(t.U(t.OA + a * 24)), t.Str(t.U(t.OA + a * 24 + 8))));
            }
            if (value < h) lo = mid + 1; else hi = mid - 1;
        }
        return null;
    }

    public static bool IsSorted(byte[] b)
    {
        var t = new Table(b);
        for (int i = 0; i + 1 < t.NC; i++) if (t.U(t.OC + i * 16) > t.U(t.OC + (i + 1) * 16)) return false;
        return true;
    }

    /// <summary>A new table with rows added for every kit. A version of the table without a source kit gets no rows for it.</summary>
    public static Result Register(byte[] b, byte[] meta, IReadOnlyList<KitRegistration> kits)
    {
        int ds = (int)BinaryPrimitives.ReadUInt32LittleEndian(meta), rs = (int)BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(4));
        if (ds + rs != b.Length) throw new InvalidDataException("player_team_brt: RES meta does not match the data size.");
        var t = new Table(b);
        if (t.OC + t.NC * 16 != t.OTail || t.OA + t.NA * 24 > t.OB || t.OB + t.NB * 16 > t.OC)
            throw new InvalidDataException("player_team_brt: unexpected layout.");
        // The suffix is no longer the last string after an earlier registration. Preserve the entire existing
        // string pool, including previously added kits and its alignment, so later passes keep every old pointer valid.
        int poolEnd = t.OA;

        var bRows = Enumerable.Range(0, t.NB).Select(i => (Leaf: t.Str(t.U(t.OB + i * 16)), Folder: t.Str(t.U(t.OB + i * 16 + 8)))).ToArray();
        var byFolder = bRows.Select((r, i) => (r.Folder, i)).ToLookup(x => x.Folder, x => x.i);
        var cRows = Enumerable.Range(0, t.NC).Select(i => (Lo: t.I(t.OC + i * 16), Hi: t.I(t.OC + i * 16 + 4), A: t.I(t.OC + i * 16 + 8), B: t.I(t.OC + i * 16 + 12))).ToList();
        var bToA = new Dictionary<int, int>();
        foreach (var r in cRows) bToA[(int)r.B] = (int)r.A;

        using var pool = new MemoryStream();
        pool.Write(b, 0, poolEnd);
        var strings = new Dictionary<string, ulong>(StringComparer.Ordinal);
        ulong Add(string s)
        {
            if (strings.TryGetValue(s, out var at)) return at;
            at = (ulong)pool.Position;
            pool.Write(Encoding.ASCII.GetBytes(s));
            pool.WriteByte(0);
            return strings[s] = at;
        }

        var newA = new List<(ulong Name, ulong Folder, ulong Group)>();
        var newB = new List<(ulong Leaf, ulong Folder)>();
        var newC = new List<(uint Lo, uint Hi, uint A, uint B)>();
        foreach (var kit in kits)
        {
            string from = kit.SourceFolder.TrimEnd('/'), to = kit.TargetFolder.TrimEnd('/');
            string idFrom = KitRegistration.Identity(from), idTo = KitRegistration.Identity(to);
            var source = byFolder[from].ToArray();
            string Convert(string s) => s.Replace(from, to, StringComparison.Ordinal).Replace(idFrom, idTo, StringComparison.Ordinal);
            if (byFolder.Contains(to))
            {
                // Repair re-adds a kit into the slot it already had: its rows are exactly the ones this would add, so they
                // stay. Rows that differ (another source kit) are never merged.
                var have = byFolder[to].Select(i => bRows[i].Leaf).ToHashSet(StringComparer.Ordinal);
                if (source.Length > 0 && have.SetEquals(source.Select(i => Convert(bRows[i].Leaf)))) continue;
                throw new InvalidDataException($"player_team_brt already has {to}.");
            }
            if (source.Length == 0) continue;

            int probe = source[0];
            ulong key = Key(bRows[probe].Leaf);
            if (!cRows.Any(r => r.Lo == (uint)key && r.Hi == (uint)(key >> 32) && r.B == (uint)probe))
                throw new InvalidDataException("player_team_brt: key rows do not match the expected hash.");

            var aMap = new Dictionary<int, int>();
            foreach (int a in source.Select(s => bToA.TryGetValue(s, out int v) ? v : throw new InvalidDataException($"{bRows[s].Leaf}: asset row without a bundle")).Distinct().Order())
            {
                string name = t.Str(t.U(t.OA + a * 24)), folder = t.Str(t.U(t.OA + a * 24 + 8));
                string converted = Convert(name);
                if (converted == name) throw new InvalidDataException("Bundle name has no kit identity: " + name);
                if (folder.Length != 0 && folder != from)
                    throw new InvalidDataException($"{name}: unexpected bundle folder {folder}.");
                aMap[a] = t.NA + newA.Count;
                newA.Add((Add(converted), folder.Length == 0 ? t.U(t.OA + a * 24 + 8) : Add(to), t.U(t.OA + a * 24 + 16)));
            }
            foreach (int s in source)
            {
                string leaf = Convert(bRows[s].Leaf);
                if (leaf == bRows[s].Leaf) throw new InvalidDataException("Asset name has no kit identity: " + leaf);
                uint bi = (uint)(t.NB + newB.Count), ai = (uint)aMap[bToA[s]];
                newB.Add((Add(leaf), Add(to)));
                foreach (string k in new[] { leaf, to + "/" + leaf })
                {
                    ulong h = Key(k);
                    newC.Add(((uint)h, (uint)(h >> 32), ai, bi));
                }
            }
        }

        static int Align(int v) => (v + 15) & ~15;
        var merged = cRows.Concat(newC).OrderBy(r => r.Hi).ThenBy(r => r.Lo).ToList();
        int nA2 = t.NA + newA.Count, nB2 = t.NB + newB.Count, nC2 = merged.Count;
        int aAt = Align((int)pool.Length), bAt = Align(aAt + nA2 * 24), cAt = Align(bAt + nB2 * 16), tailAt = Align(cAt + nC2 * 16);
        int tailLength = ds - t.OTail, ds2 = tailAt + tailLength;
        ulong Remap(ulong x)
        {
            if (x < (ulong)poolEnd) return x;
            if (x >= (ulong)t.OTail && x < (ulong)ds) return x - (ulong)t.OTail + (ulong)tailAt;
            throw new InvalidDataException($"player_team_brt: pointer outside the pool and tail: {x:X}");
        }

        var o = new byte[ds2];
        pool.ToArray().CopyTo(o, 0);
        void W(int at, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(o.AsSpan(at), v);
        void W4(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(at), v);
        W(0, t.Suffix); W(8, (ulong)cAt); W(16, (ulong)aAt); W(24, (ulong)bAt); W(32, (ulong)tailAt); W(40, t.U(40));
        W4(48, (uint)nC2); W4(52, (uint)nA2); W4(56, (uint)nB2);
        for (int i = 0; i < t.NA; i++) { W(aAt + i * 24, t.U(t.OA + i * 24)); W(aAt + i * 24 + 8, Remap(t.U(t.OA + i * 24 + 8))); W(aAt + i * 24 + 16, Remap(t.U(t.OA + i * 24 + 16))); }
        for (int i = 0; i < newA.Count; i++) { int at = aAt + (t.NA + i) * 24; W(at, newA[i].Name); W(at + 8, newA[i].Folder); W(at + 16, Remap(newA[i].Group)); }
        for (int i = 0; i < t.NB; i++) { W(bAt + i * 16, t.U(t.OB + i * 16)); W(bAt + i * 16 + 8, t.U(t.OB + i * 16 + 8)); }
        for (int i = 0; i < newB.Count; i++) { int at = bAt + (t.NB + i) * 16; W(at, newB[i].Leaf); W(at + 8, newB[i].Folder); }
        for (int i = 0; i < nC2; i++) { int at = cAt + i * 16; W4(at, merged[i].Lo); W4(at + 4, merged[i].Hi); W4(at + 8, merged[i].A); W4(at + 12, merged[i].B); }
        for (int i = 0; i < tailLength; i += 8) W(tailAt + i, Remap(t.U(t.OTail + i)));

        var relocations = new List<uint>(rs / 4 + newA.Count * 3 + newB.Count * 2);
        var head = new List<uint>();
        for (int i = 0; i < rs; i += 4)
        {
            int p = (int)t.I(ds + i);
            if (p < 0x30) head.Add((uint)p);
            else if (p >= t.OA && p < t.OA + t.NA * 24) relocations.Add((uint)(p - t.OA + aAt));
            else if (p >= t.OB && p < t.OB + t.NB * 16) relocations.Add((uint)(p - t.OB + bAt));
            else if (p >= t.OTail && p < ds) relocations.Add((uint)(p - t.OTail + tailAt));
            else throw new InvalidDataException($"player_team_brt: relocation in an unknown region: {p:X}");
        }
        for (int i = 0; i < newA.Count; i++) { int at = aAt + (t.NA + i) * 24; relocations.Add((uint)at); relocations.Add((uint)(at + 8)); relocations.Add((uint)(at + 16)); }
        for (int i = 0; i < newB.Count; i++) { int at = bAt + (t.NB + i) * 16; relocations.Add((uint)at); relocations.Add((uint)(at + 8)); }
        relocations.AddRange(head);

        var final = new byte[ds2 + relocations.Count * 4];
        o.CopyTo(final, 0);
        for (int i = 0; i < relocations.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(final.AsSpan(ds2 + i * 4), relocations[i]);
        foreach (uint p in relocations)
            if (BinaryPrimitives.ReadUInt64LittleEndian(final.AsSpan((int)p)) >= (ulong)ds2)
                throw new InvalidDataException($"player_team_brt: relocated value out of range at {p:X}");
        var meta2 = (byte[])meta.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(meta2, (uint)ds2);
        BinaryPrimitives.WriteUInt32LittleEndian(meta2.AsSpan(4), (uint)(relocations.Count * 4));
        return new Result(final, meta2, newB.Count, newA.Count);
    }

    sealed class Table(byte[] b)
    {
        public ulong U(int at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at));
        public uint I(int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
        public string Str(ulong at)
        {
            int a = checked((int)at);
            return Encoding.ASCII.GetString(b, a, Array.IndexOf(b, (byte)0, a) - a);
        }
        public ulong Suffix => U(0);
        public int OC => (int)U(8);
        public int OA => (int)U(16);
        public int OB => (int)U(24);
        public int OTail => (int)U(32);
        public int NC => (int)I(48);
        public int NA => (int)I(52);
        public int NB => (int)I(56);
    }
}
