using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var settings = SuiteSettings.Load();
var view = Nhl24ReadView.Ensure(settings);
var index = BundleIndex.Build(view);

Console.WriteLine("== content/worlds/rink/ice");
foreach (var n in index.EbxByName.Keys.Where(k => k.StartsWith("content/worlds/rink/ice/", StringComparison.Ordinal)).OrderBy(x => x))
    Console.WriteLine("  " + n);

Console.WriteLine("== levels/game folders");
foreach (var g in index.EbxByName.Keys.Where(k => k.StartsWith("levels/", StringComparison.Ordinal))
             .GroupBy(k => string.Join('/', k.Split('/')[..Math.Min(4, k.Split('/').Length - 1)])).OrderBy(g => g.Key).Take(40))
    Console.WriteLine($"  {g.Key}  ({g.Count()})");

Console.WriteLine("== nhl/data");
foreach (var n in index.EbxByName.Keys.Where(k => k.StartsWith("nhl/", StringComparison.Ordinal)).OrderBy(x => x).Take(40))
    Console.WriteLine("  " + n);
