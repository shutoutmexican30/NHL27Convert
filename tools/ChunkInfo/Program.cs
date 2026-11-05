// ChunkInfo <game> <guid> [out.bin]: where a chunk id lives — TOC chunk-table entries and bundle records (with raw
// sizes) — plus the chunk's decoded raw size; with out.bin, writes the decoded bytes.
using Suite.Core;
using Suite.Core.Frostbite;

var game = args[0];
var want = Guid.Parse(args[1]);
var tocs = TocChunkWriter.LoadTocs(game);
ChunkRef? first = null;
foreach (var toc in tocs)
    foreach (var r in TocChunkWriter.FindAll([toc], want))
    {
        Console.WriteLine($"TOC {Path.GetFileName(r.Toc.Path)}: patch={r.Where.Patch} cat={r.Where.Catalog} cas={r.Where.Cas} @{r.Where.Offset} stored={r.Where.Size}");
        first ??= r.Where;
    }

if (first is not null)
{
    var raw = TocChunkWriter.ReadRaw(game, first);
    Console.WriteLine($"decoded raw size: {raw.Length:N0}");
    if (args.Length > 2) { File.WriteAllBytes(args[2], raw); Console.WriteLine($"-> {args[2]}"); }
}

var index = BundleIndex.Build(game, _ => { });
if (index.ChunksById.TryGetValue(want, out var list))
    foreach (var c in list)
        Console.WriteLine($"BUNDLE '{c.Bundle.FirstName}' ({Path.GetFileName(c.Bundle.Toc)}): logical {c.LogicalOffset}+{c.LogicalSize}, stored {c.Data.Offset}+{c.Data.Size}");
else
    Console.WriteLine("no bundle records");
