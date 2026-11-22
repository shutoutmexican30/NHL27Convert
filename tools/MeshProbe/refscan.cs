using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

// FileGuid of every hotspot texture asset (its companion EBX, base copy only).
var rx = new Regex(@"ice_hotspot_(\d+)_(\d+)_color$", RegexOptions.Compiled);
var guidOf = new Dictionary<Guid, string>();
foreach (var k in index.ResByName.Keys)
{
    var m = rx.Match(k);
    if (!m.Success || !index.EbxByName.ContainsKey(k)) continue;
    try { guidOf[EbxRiff.Parse(EbxBytes(k)).FileGuid] = k.Split('/')[^1]; } catch { }
}
Console.WriteLine("hotspot guids: " + guidOf.Count);

// scan: which EBX files import hotspot guids?
var scope = index.EbxByName.Keys
    .Where(k => k.StartsWith("content/worlds/", StringComparison.Ordinal) || k.StartsWith("levels/", StringComparison.Ordinal))
    .ToList();
Console.WriteLine("scanning " + scope.Count + " ebx");
var refs = new ConcurrentDictionary<string, List<string>>();
Parallel.ForEach(scope, name =>
{
    List<string> hits;
    try
    {
        var riff = EbxRiff.Parse(EbxBytes(name));
        hits = riff.ImportedFileGuids.Where(guidOf.ContainsKey).Select(g => guidOf[g]).ToList();
    }
    catch { return; }
    if (hits.Count > 0) refs[name] = hits;
});
Console.WriteLine("files referencing hotspots: " + refs.Count);
foreach (var kv in refs.OrderBy(kv => kv.Key))
{
    var shortNames = kv.Value.Select(h => h.Replace("ice_hotspot_", "h").Replace("_color", "")).OrderBy(x => x, StringComparer.Ordinal);
    Console.WriteLine($"  {kv.Key}  ({kv.Value.Count})");
    Console.WriteLine("    " + string.Join(" ", shortNames));
}
