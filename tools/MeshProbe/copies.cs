using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = args.Length > 0 ? args[0] : @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);
string LayerOf(string toc) => toc.Replace('\\', '/').Contains("game-UPDATE") || toc.Replace('\\', '/').Contains("/Patch/") ? "PATCH" : "BASE ";

foreach (var kv in index.ResByName.Where(k => k.Key.Contains("centerlogo_0_color") || k.Key.Contains("centerlogo_22_color")).OrderBy(k => k.Key))
    foreach (var r in kv.Value)
        Console.WriteLine($"{LayerOf(r.Data.Toc)} rid={r.Rid:X16} {kv.Key,-66} toc=...{r.Data.Toc[^45..]}");

Console.WriteLine("==== mesh RES copies (rink_ice_nhl_mesh)");
foreach (var kv in index.ResByName.Where(k => k.Key.EndsWith("rink_ice_nhl_mesh", StringComparison.Ordinal)).OrderBy(k => k.Key))
    foreach (var r in kv.Value)
        Console.WriteLine($"{LayerOf(r.Data.Toc)} rid={r.Rid:X16} toc=...{r.Data.Toc[^52..]}");
Console.WriteLine("==== mesh EBX copies");
foreach (var kv in index.EbxByName.Where(k => k.Key.EndsWith("rink_ice_nhl_mesh", StringComparison.Ordinal)).OrderBy(k => k.Key))
    foreach (var r in kv.Value)
        Console.WriteLine($"{LayerOf(r.Data.Toc)} toc=...{r.Data.Toc[^52..]}");
Console.WriteLine("==== rink ice prefab/world ebx copies");
foreach (var kv in index.EbxByName.Where(k => k.Key.Contains("rink_ice") && k.Key.Contains("world")).OrderBy(k => k.Key))
    foreach (var r in kv.Value)
        Console.WriteLine($"{LayerOf(r.Data.Toc)} {kv.Key} toc=...{r.Data.Toc[^40..]}");

// Dump the BASE-layer copy of slot 0 (stock logo) and the PATCH copy, side by side.
foreach (var want in new[] { ("BASE ", "base0"), ("PATCH", "patch0") })
{
    var res = index.ResByName["content/worlds/rink/textures/centerlogo/ice_centerlogo_0_color"]
        .Where(r => LayerOf(r.Data.Toc) == want.Item1).FirstOrDefault();
    if (res == null) { Console.WriteLine($"no {want.Item1} copy of slot 0"); continue; }
    var stored = BundleIndex.ReadBytes(res.Data);
    byte[] header; try { header = ChunkBlocks.Parse(stored).Decode(); } catch { header = stored; }
    var tr = TextureRes.Parse(header);
    var full = ChunkReplace.ReadAll(game, index, tr.ChunkGuid).First(r => r.LogicalOffset == 0 && r.Raw.Length == tr.PayloadSize).Raw;
    var img = Bc7.DecodeTiled(TextureChunk.Parse(full, tr).Mips[0], tr.Width, tr.Height);
    ImageIO.SavePng(ImageIO.ResizeSmooth(img, 512, 512), $@"C:\dev\NHL27Convert\tools\MeshProbe\out\slot0_{want.Item2}.png");
    Console.WriteLine($"saved slot0_{want.Item2}.png ({tr.Width}x{tr.Height})");
}
