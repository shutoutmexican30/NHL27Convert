// ResList <game> <name-filter> — every EBX/RES whose name contains the filter, and how many copies each has.
using Suite.Core;
using Suite.Core.Frostbite;

if (args.Length < 2) { Console.Error.WriteLine("usage: ResList <game> <name-filter>"); return 2; }
Oodle.Ensure(SuiteSettings.Load());
var index = BundleIndex.Build(args[0]);
if (args.Length >= 4 && args[2] == "--dump")
{
    // ResList <game> <exact ebx name> --dump <out.bin>: the EBX bytes, plus every chunk GUID found inside them.
    var e = index.EbxByName[args[1]].First();
    var stored = BundleIndex.ReadBytes(e.Data);
    byte[] raw; try { raw = ChunkBlocks.Parse(stored).Decode(); } catch { raw = stored; }
    File.WriteAllBytes(args[3], raw);
    Console.WriteLine($"{args[1]}: {raw.Length} bytes");
    for (int p = 0; p + 16 <= raw.Length; p++)
    {
        var g = new Guid(raw.AsSpan(p, 16));
        if (index.ChunksById.TryGetValue(g, out var cs)) Console.WriteLine($"  @{p} chunk {g} (bundle copies {cs.Count}, u32 after {BitConverter.ToUInt32(raw, Math.Min(p + 16, raw.Length - 4))})");
        else foreach (var t in BundleIndex.Tocs(args[0]).Select(TocFile.Load)) if (t.Find(g) is { } r) { Console.WriteLine($"  @{p} toc chunk {g} size {r.Size} in {Path.GetFileName(t.Path)}"); break; }
    }
    return 0;
}
var names = index.EbxByName.Keys.Concat(index.ResByName.Keys)
    .Where(n => n.Contains(args[1], StringComparison.OrdinalIgnoreCase))
    .Order().ToList();
foreach (var n in names)
{
    var e = index.EbxByName.GetValueOrDefault(n)?.Count ?? 0;
    var r = index.ResByName.GetValueOrDefault(n)?.Count ?? 0;
    Console.WriteLine($"{(e > 0 ? $"EBX x{e}" : ""),8}{(r > 0 ? $"RES x{r}" : ""),8}  {n}");
}
Console.WriteLine($"{names.Count} name(s)");
return 0;
