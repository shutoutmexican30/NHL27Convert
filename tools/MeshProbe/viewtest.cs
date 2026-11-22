using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var view = @"C:\Users\devin\AppData\Local\CenterIceModdingSuite\nhl24view";
Console.WriteLine("building index on the read view...");
var index = BundleIndex.Build(view);
Console.WriteLine($"bundles {index.Bundles.Count}, ebx {index.EbxByName.Count}, res {index.ResByName.Count}, chunks {index.ChunksById.Count}");

int tex = 0, arr = 0, fail = 0;
var names = index.ResByName.Where(kv => !kv.Key.EndsWith("_mesh", StringComparison.Ordinal)).Select(kv => kv.Value[0]).ToList();
Console.WriteLine($"non-mesh RES: {names.Count}, probing 300...");
foreach (var r in names.Take(300))
{
    try
    {
        var stored = BundleIndex.ReadBytes(r.Data);
        byte[] header; try { header = ChunkBlocks.Parse(stored).Decode(); } catch { header = stored; }
        if (header.Length < 132) { fail++; continue; }
        var tr = TextureRes.Parse(header);
        if (Ps4Codec.Slices(header) > 1) arr++; else tex++;
    }
    catch { fail++; }
}
Console.WriteLine($"of 300: textures {tex}, arrays {arr}, not-texture {fail}");

// decode one known BC7 texture through the view
var name = index.ResByName.Keys.FirstOrDefault(k => k.Contains("ice_centerlogo_1_color"));
if (name is not null)
{
    var slot = TextureSlots.Read(view, index, name);
    var slices = Ps4Codec.DecodeSlices(slot.Res, slot.Stock);
    Console.WriteLine($"{name}: {slot.Res.Width}x{slot.Res.Height}, {slices.Count} slice(s), fmt 0x{slot.Res.Header[12]:X2}");
}
