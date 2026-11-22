using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var settings = SuiteSettings.Load();
var view = Nhl24ReadView.Ensure(settings);
var index = BundleIndex.Build(view);

byte[] EbxBytes(string name)
{
    var e = index.EbxByName[name][0];
    var raw = BundleIndex.ReadBytes(e.Data);
    try { return ChunkBlocks.Parse(raw).Decode(); } catch { return raw; }
}

foreach (var n in new[] {
    "content/worlds/rink/ice/components/rink_ice_nhl_prefab",
    "content/worlds/rink/ice/components/rink_ice_nhl",
    "content/worlds/rink/ice/components/rink_ice_nhl_mesh",
    "content/worlds/rink/textures/hotspot/ice_hotspot_1_20_color" })
{
    var b = EbxBytes(n);
    var head = string.Join(" ", b.Take(32).Select(x => x.ToString("X2")));
    Console.WriteLine($"{n.Split('/')[^1]}  {b.Length} bytes");
    Console.WriteLine("  " + head);
    try { var r = EbxRiff.Parse(b); Console.WriteLine("  RIFF ok, guid " + r.FileGuid); }
    catch (Exception ex) { Console.WriteLine("  RIFF FAIL: " + ex.Message); }
}
