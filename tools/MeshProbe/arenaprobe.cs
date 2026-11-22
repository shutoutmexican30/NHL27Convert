using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var settings = SuiteSettings.Load();
var view = Nhl24ReadView.Ensure(settings);
var index = BundleIndex.Build(view);

var pats = new[] { "arena", "rink", "ice" };
foreach (var p in pats)
{
    var hits = index.EbxByName.Keys.Where(k => k.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"== EBX containing '{p}': {hits.Count}");
    foreach (var g in hits.GroupBy(k => string.Join('/', k.Split('/')[..Math.Min(3, k.Split('/').Length - 1)])).OrderBy(g => g.Key).Take(40))
        Console.WriteLine($"   {g.Key}  ({g.Count()})");
}
