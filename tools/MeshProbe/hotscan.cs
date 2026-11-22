using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = args.Length > 0 ? args[0] : @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);

Console.WriteLine("== resource names containing 'hot' ==");
foreach (var kv in index.ResByName.Where(kv => kv.Key.Contains("hot", StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key))
    Console.WriteLine($"{kv.Value.Count,3} copy(ies)  {kv.Key}");

Console.WriteLine();
Console.WriteLine("== all rink/ice texture names ==");
foreach (var kv in index.ResByName.Where(kv => kv.Key.StartsWith("content/worlds/rink/", StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key))
    Console.WriteLine($"{kv.Value.Count,3} copy(ies)  {kv.Key}");
