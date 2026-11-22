using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var settings = SuiteSettings.Load();
var view = Nhl24ReadView.Ensure(settings);
var index = BundleIndex.Build(view);

// everything hotspot-related
var hot = index.ResByName.Keys.Where(k => k.Contains("hotspot", StringComparison.OrdinalIgnoreCase)).ToList();
Console.WriteLine("== names containing 'hotspot': " + hot.Count);
foreach (var g in hot.GroupBy(k => string.Join('/', k.Split('/')[..^1])).OrderBy(g => g.Key))
    Console.WriteLine($"  {g.Key}  ({g.Count()})");

// the rink textures tree, top levels
Console.WriteLine("== rink textures folders");
var rink = index.ResByName.Keys.Where(k => k.StartsWith("content/worlds/rink/", StringComparison.Ordinal)).ToList();
foreach (var g in rink.GroupBy(k => string.Join('/', k.Split('/')[..4])).OrderBy(g => g.Key))
    Console.WriteLine($"  {g.Key}  ({g.Count()})");

// ice_* textures not in hotspot/centerlogo folders (per-arena ice sheets?)
Console.WriteLine("== ice_* names outside hotspot/centerlogo");
var ice = rink.Where(k => k.Split('/')[^1].StartsWith("ice_") && !k.Contains("/hotspot/") && !k.Contains("/centerlogo/")).ToList();
foreach (var g in ice.GroupBy(k => string.Join('/', k.Split('/')[..^1])).OrderBy(g => g.Key))
    Console.WriteLine($"  {g.Key}  ({g.Count()})");
foreach (var n in ice.Take(25)) Console.WriteLine("    " + n);
