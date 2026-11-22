using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var settings = SuiteSettings.Load();
var view = Nhl24ReadView.Ensure(settings);
var index = BundleIndex.Build(view);

var rink = index.EbxByName.Keys.Where(k => k.StartsWith("content/worlds/rink/", StringComparison.Ordinal)).ToList();
foreach (var g in rink.GroupBy(k => string.Join('/', k.Split('/')[..Math.Min(4, k.Split('/').Length - 1)])).OrderBy(g => g.Key))
    Console.WriteLine($"{g.Key}  ({g.Count()})");
Console.WriteLine("---- leaf names with 'hotspot' or 'config' or 'data'");
foreach (var n in rink.Where(k => k.Contains("hotspot") || k.Contains("config") || k.Contains("data")).Take(60))
    Console.WriteLine("  " + n);
