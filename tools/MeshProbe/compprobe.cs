using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var settings = SuiteSettings.Load();
var view = Nhl24ReadView.Ensure(settings);
var index = BundleIndex.Build(view);

var comp = index.EbxByName.Keys.Where(k => k.StartsWith("content/worlds/components/", StringComparison.Ordinal)).ToList();
foreach (var g in comp.GroupBy(k => k.Split('/')[2]).OrderBy(g => g.Key))
    Console.WriteLine($"{g.Key}  ({g.Count()})");
Console.WriteLine("---- sample leaves");
foreach (var n in comp.OrderBy(x => x).Take(10)) Console.WriteLine("  " + n);
Console.WriteLine("---- team/arena-sounding leaves");
foreach (var n in comp.Where(k => k.Contains("arena") || k.Contains("stadium") || k.Contains("ice")).OrderBy(x => x).Take(40)) Console.WriteLine("  " + n);
