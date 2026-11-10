// LegacyWhere <game> <name filter>: every legacy index chunk (globals.toc, base game and update) listing a matching
// file, the record's chunk id and raw size, and every TOC entry holding that chunk - so duplicate base/update copies
// of a legacy file show side by side, each with a hash of its decoded bytes. Read only.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Suite.Core;
using Suite.Core.Frostbite;

var game = Path.GetFullPath(args[0]);
var filter = args[1];
var needle = Encoding.ASCII.GetBytes(filter);
Oodle.Ensure(SuiteSettings.Load());
var allTocs = TocChunkWriter.LoadTocs(game);
var globals = allTocs.Where(t => t.Path.EndsWith("globals.toc", StringComparison.OrdinalIgnoreCase)).ToList();
Console.WriteLine($"scanning {globals.Count} globals.toc file(s)");

string CasPath(ChunkRef r) => BundleIndex.CasPath(game, r.Patch, r.Catalog, r.Cas);
byte[] ReadStored(ChunkRef r)
{
    using var fs = new FileStream(CasPath(r), FileMode.Open, FileAccess.Read, FileShare.Read);
    fs.Position = r.Offset;
    var b = new byte[r.Size];
    fs.ReadExactly(b);
    return b;
}

foreach (var toc in globals)
{
    foreach (var id in toc.Chunks)
    {
        var r = toc.Find(id);
        if (r is null || r.Size > 4_000_000) continue;
        byte[] raw; try { raw = ChunkBlocks.Parse(ReadStored(r)).Decode(); } catch { continue; }
        if (raw.AsSpan().IndexOf(needle) < 0) continue;
        Console.WriteLine($"index chunk {id} in {Path.GetRelativePath(game, toc.Path)}");
        for (int p = 0; p + 56 <= raw.Length; p += 4)
        {
            long nameOff = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(p));
            if (nameOff <= 0 || nameOff >= raw.Length) continue;
            long s0 = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(p + 8)), s1 = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(p + 16)),
                 ro = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(p + 24)), rs = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(p + 32));
            if (s0 < 0 || s1 < s0 || ro < 0 || rs <= 0 || rs > 1L << 32 || s1 > 1L << 32) continue;
            int end = Array.IndexOf(raw, (byte)0, (int)nameOff);
            if (end < 0 || end - nameOff < 3 || end - nameOff > 260) continue;
            var name = Encoding.ASCII.GetString(raw, (int)nameOff, end - (int)nameOff);
            if (!name.Contains('/') || name.Any(c => c < 0x20 || c > 0x7E)) continue;
            int rec = p;
            p += 52;
            if (!name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var cid = new Guid(raw.AsSpan(rec + 40, 16));
            Console.WriteLine($"  {name}: raw {rs:N0} bytes, chunk {cid}");
            foreach (var hit in TocChunkWriter.FindAll(allTocs, cid))
            {
                byte[] content; try { content = ChunkBlocks.Parse(ReadStored(hit.Where)).Decode(); } catch { content = []; }
                var hash = content.Length == 0 ? "(undecodable)" : Convert.ToHexString(SHA1.HashData(content))[..12];
                Console.WriteLine($"    {Path.GetRelativePath(game, hit.Toc.Path)}: cas_{hit.Where.Cas:D2} @{hit.Where.Offset} stored={hit.Where.Size}, decoded {content.Length:N0} bytes, sha1 {hash}");
            }
        }
    }
}
