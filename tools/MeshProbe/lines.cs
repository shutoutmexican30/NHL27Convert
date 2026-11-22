using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);
string LayerOf(string toc) => toc.Replace('\\', '/').Contains("game-UPDATE") || toc.Replace('\\', '/').Contains("/Patch/") ? "PATCH" : "BASE ";

foreach (var kv in index.ResByName.Where(k => k.Key.Contains("line", StringComparison.OrdinalIgnoreCase)
    && k.Key.Contains("ice", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k.Key))
{
    int b = kv.Value.Count(r => LayerOf(r.Data.Toc) == "BASE ");
    int p = kv.Value.Count(r => LayerOf(r.Data.Toc) == "PATCH");
    Console.WriteLine($"{kv.Key,-80} BASE={b} PATCH={p}");
}
Console.WriteLine("---- any res containing 'lines_'");
foreach (var kv in index.ResByName.Where(k => k.Key.Contains("lines_", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k.Key))
    Console.WriteLine($"{kv.Key,-80} n={kv.Value.Count}");
