// BundleProbe <game> [resource name...]
// Read-only. Builds the bundle index (BundleIndex) and, for each name, prints its RES entries, the decoded RES size,
// and for textures the 132-byte header's chunk id and where that chunk is stored (TOC chunk table or bundle).
using System.Diagnostics;
using Suite.Core;
using Suite.Core.Frostbite;

var sw = Stopwatch.StartNew();
Oodle.Ensure(SuiteSettings.Load());
var index = BundleIndex.Build(args[0], s => Console.WriteLine(s));
Console.WriteLine($"index built in {sw.Elapsed.TotalSeconds:F1}s");
var tocs = BundleIndex.Tocs(args[0]).Select(TocFile.Load).ToList();
foreach (var name in args.Skip(1).SelectMany(a => index.ResByName.ContainsKey(a) ? [a]
    : index.ResByName.Keys.Where(k => k.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(a)))
{
    if (!index.ResByName.TryGetValue(name, out var list)) { Console.WriteLine($"{name}: no RES"); continue; }
    foreach (var r in list)
    {
        var stored = BundleIndex.ReadBytes(r.Data);
        byte[] raw;
        try { raw = ChunkBlocks.Parse(stored).Decode(); } catch { raw = stored; }
        Console.WriteLine($"{name}: type 0x{r.Type:X8} in {Path.GetFileName(r.Bundle.Toc)} bundle '{r.Bundle.FirstName}', stored {stored.Length}, raw {raw.Length}");
        if (raw.Length >= 48)
        {
            var id = new Guid(raw.AsSpan(32, 16));
            int w = BitConverter.ToUInt16(raw, 22), h = BitConverter.ToUInt16(raw, 24), mips = raw[30];
            Console.WriteLine($"   header: {w}x{h}, {mips} mips, chunk {id}");
            foreach (var tocHit in tocs.Select(t => t.Find(id)).Where(x => x is not null))
                Console.WriteLine($"   chunk in TOC table {Path.GetFileName(tocHit.Toc)}: patch={tocHit.Patch} cat={tocHit.Catalog} cas={tocHit.Cas} @{tocHit.Offset} {tocHit.Size}");
            if (index.ChunksById.TryGetValue(id, out var bc))
                foreach (var c in bc) Console.WriteLine($"   chunk in bundle '{c.Bundle.FirstName}' ({Path.GetFileName(c.Bundle.Toc)}): {Path.GetRelativePath(args[0], c.Data.Cas)} @{c.Data.Offset} {c.Data.Size}, logical {c.LogicalOffset}+{c.LogicalSize}");
        }
    }
}
