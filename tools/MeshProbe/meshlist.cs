using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = args.Length > 0 ? args[0] : @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);
string LayerOf(string toc) => toc.Replace('\\', '/').Contains("game-UPDATE") || toc.Replace('\\', '/').Contains("/Patch/") ? "PATCH" : "BASE ";

// All RES names that mention ice + mesh-ish, grouped.
foreach (var kv in index.ResByName.Where(k =>
    (k.Key.Contains("ice", StringComparison.OrdinalIgnoreCase) && k.Key.Contains("mesh", StringComparison.OrdinalIgnoreCase))
    || k.Key.Contains("centerlogo", StringComparison.OrdinalIgnoreCase)
    || (k.Key.Contains("rink", StringComparison.OrdinalIgnoreCase) && k.Key.Contains("decal", StringComparison.OrdinalIgnoreCase))
    ).OrderBy(k => k.Key))
{
    int b = kv.Value.Count(r => LayerOf(r.Data.Toc) == "BASE ");
    int p = kv.Value.Count(r => LayerOf(r.Data.Toc) == "PATCH");
    Console.WriteLine($"{kv.Key,-88} BASE={b} PATCH={p}");
}
