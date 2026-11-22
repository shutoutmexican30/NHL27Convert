using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);
string LayerOf(string toc) => toc.Replace('\\', '/').Contains("game-UPDATE") || toc.Replace('\\', '/').Contains("/Patch/") ? "PATCH" : "BASE ";

var entries = index.ResByName["content/worlds/rink/ice/components/rink_ice_nhl_mesh"];
Console.WriteLine("layer | bundle (FirstName) | toc");
foreach (var r in entries.OrderBy(r => r.Bundle.FirstName).ThenBy(r => LayerOf(r.Data.Toc)))
    Console.WriteLine($"{LayerOf(r.Data.Toc)} | '{r.Bundle.FirstName}' | ...{r.Data.Toc[^40..]}");
