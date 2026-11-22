using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);

int tex = 0, arr = 0, mesh = 0, other = 0, metaShort = 0;
foreach (var kv in index.ResByName)
{
    var r = kv.Value[0];
    if (kv.Key.EndsWith("_mesh", StringComparison.Ordinal)) { mesh++; continue; }
    if (r.Meta.Length < 132) { metaShort++; continue; }
    try
    {
        var tr = TextureRes.Parse(r.Meta);
        if (Ps4Codec.Slices(r.Meta) > 1) arr++; else tex++;
    }
    catch { other++; }
}
Console.WriteLine($"textures {tex}, arrays {arr}, meshes {mesh}, meta-too-short {metaShort}, non-texture {other}");

// spot check one of each
foreach (var name in new[] { "content/texture/global/default_texture_color", "content/worlds/rink/ice/ice_centerlogo_1_color" }.Where(n => index.ResByName.ContainsKey(n)))
{
    var r = index.ResByName[name][0];
    try
    {
        var tr = TextureRes.Parse(r.Meta);
        Console.WriteLine($"{name}: {tr.Width}x{tr.Height} fmt=0x{r.Meta[12]:X2} slices={Ps4Codec.Slices(r.Meta)}");
    }
    catch (Exception ex) { Console.WriteLine($"{name}: {ex.Message}"); }
}
