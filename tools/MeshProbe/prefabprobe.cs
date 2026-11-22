using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Suite.Core;
using Suite.Core.Frostbite;

var settings = SuiteSettings.Load();
var view = Nhl24ReadView.Ensure(settings);
var index = BundleIndex.Build(view);

// GUID of every hotspot texture, from its RES meta (16 bytes).
var rx = new Regex(@"ice_hotspot_(\d+)_(\d+)_color$", RegexOptions.Compiled);
var guidOf = new Dictionary<string, Guid>(); // name -> guid
foreach (var k in index.ResByName.Keys)
{
    var m = rx.Match(k);
    if (!m.Success) continue;
    var res = index.ResByName[k][0];
    var g = new Guid(res.Meta); // meta holds the asset guid? verify length 16
    guidOf[k] = g;
}
Console.WriteLine("hotspot guids: " + guidOf.Count + ", meta len " + index.ResByName[guidOf.Keys.First()][0].Meta.Length);
Console.WriteLine("sample meta: " + Convert.ToHexString(index.ResByName[guidOf.Keys.First()][0].Meta));

// candidate ice EBX files
var candidates = index.EbxByName.Keys
    .Where(k => k.Contains("rink_ice", StringComparison.OrdinalIgnoreCase) || k.Contains("ice_dec", StringComparison.OrdinalIgnoreCase))
    .OrderBy(x => x).ToList();
Console.WriteLine("candidate ebx: " + candidates.Count);

foreach (var name in candidates.Where(n => !n.EndsWith("_mesh")).Take(40))
{
    var e = index.EbxByName[name][0];
    byte[] raw;
    try { raw = BundleIndex.ReadBytes(e.Data); } catch (Exception ex) { Console.WriteLine($"  {name}: read fail {ex.Message}"); continue; }
    byte[] b = raw; try { b = ChunkBlocks.Parse(raw).Decode(); } catch { }
    var hits = guidOf.Where(kv => Contains(b, kv.Value.ToByteArray())).Select(kv => kv.Key.Split('/')[^1]).ToList();
    Console.WriteLine($"  {name}  ({b.Length} bytes): {hits.Count} hotspot refs");
    if (hits.Count > 0 && hits.Count <= 80) Console.WriteLine("      " + string.Join(" ", hits.Select(h => h.Replace("ice_hotspot_", "").Replace("_color", ""))));
}

static bool Contains(byte[] hay, byte[] needle)
{
    if (needle.Length == 0 || hay.Length < needle.Length) return false;
    var first = needle[0];
    for (int i = 0; i <= hay.Length - needle.Length; i++)
    {
        if (hay[i] != first) continue;
        bool ok = true;
        for (int j = 1; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
        if (ok) return true;
    }
    return false;
}
