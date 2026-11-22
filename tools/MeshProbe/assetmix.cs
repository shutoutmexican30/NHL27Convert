using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);

static string Prefix(string k, int n) { var parts = k.Split('/'); return string.Join('/', parts.Take(Math.Min(n, parts.Length))); }

Console.WriteLine("== EBX by top folder ==");
foreach (var g in index.EbxByName.Keys.GroupBy(k => Prefix(k, 2)).OrderByDescending(g => g.Count()).Take(25))
    Console.WriteLine($"{g.Count(),8}  {g.Key}");

Console.WriteLine("\n== EBX sample names per top folder ==");
foreach (var g in index.EbxByName.Keys.GroupBy(k => Prefix(k, 2)).OrderByDescending(g => g.Count()).Take(8))
    Console.WriteLine($"  {g.Key}: {string.Join(" | ", g.Take(3))}");

Console.WriteLine("\n== RES by top folder ==");
foreach (var g in index.ResByName.Keys.GroupBy(k => Prefix(k, 3)).OrderByDescending(g => g.Count()).Take(30))
    Console.WriteLine($"{g.Count(),8}  {g.Key}");
