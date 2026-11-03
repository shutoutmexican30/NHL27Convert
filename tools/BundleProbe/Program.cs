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
if (args.Length > 3 && args[1] == "--dump")
{
    // --dump <out-dir> <name>...: decoded EBX/RES bytes (first copy) for inspection.
    Directory.CreateDirectory(args[2]);
    foreach (var a in args.Skip(3))
    {
        // "res:<name>" picks the RES when an EBX has the same name (textures).
        bool wantRes = a.StartsWith("res:"); var n = wantRes ? a[4..] : a;
        var loc = (wantRes ? null : index.EbxByName.GetValueOrDefault(n)?[0].Data) ?? index.ResByName.GetValueOrDefault(n)?[0].Data;
        if (loc is null) { Console.WriteLine($"{n}: not found"); continue; }
        var st = BundleIndex.ReadBytes(loc);
        byte[] dec; try { dec = ChunkBlocks.Parse(st).Decode(); } catch { dec = st; }
        var file = Path.Combine(args[2], n.Replace('/', '~') + (!wantRes && index.EbxByName.ContainsKey(n) ? ".ebx" : ".res"));
        File.WriteAllBytes(file, dec);
        Console.WriteLine($"{n}: {dec.Length} bytes -> {file}");
        if ((wantRes || !index.EbxByName.ContainsKey(n)) && dec.Length >= 48)
        {
            // A texture: also its full chunk, from the TOC chunk table (else the bundle copy that holds all of it).
            var id = new Guid(dec.AsSpan(32, 16));
            var hit = tocs.Select(t => t.Find(id)).FirstOrDefault(x => x is not null);
            byte[]? raw = null;
            if (hit is not null) raw = ChunkBlocks.Parse(BundleIndex.ReadBytes(new BundleIndex.Location(hit.Toc, 0, BundleIndex.CasPath(args[0], hit.Patch, hit.Catalog, hit.Cas), hit.Offset, hit.Size))).Decode();
            else if (index.ChunksById.GetValueOrDefault(id)?.FirstOrDefault(c => c.LogicalOffset == 0) is { } bc) raw = ChunkBlocks.Parse(BundleIndex.ReadBytes(bc.Data)).Decode();
            if (raw is not null) { File.WriteAllBytes(Path.ChangeExtension(file, ".chunk"), raw); Console.WriteLine($"   chunk {id}: {raw.Length} bytes"); }
        }
    }
    return;
}
if (args.Length > 2 && args[1] == "--grep")
{
    foreach (var (n, l) in index.ResByName.Where(kv => kv.Key.Contains(args[2], StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key))
        Console.WriteLine($"{n}	0x{l[0].Type:X8}	{l.Count}");
    foreach (var (n, l) in index.EbxByName.Where(kv => kv.Key.Contains(args[2], StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key))
        Console.WriteLine($"EBX {n}	{l.Count}	{Path.GetRelativePath(args[0], l[0].Data.Cas)} @{l[0].Data.Offset} {l[0].Data.Size}");
    return;
}
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
