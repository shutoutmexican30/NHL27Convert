using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Suite.Core;
using Suite.Core.Frostbite;

var settings = SuiteSettings.Load();
var view = Nhl24ReadView.Ensure(settings);
var index = BundleIndex.Build(view);

var rx = new Regex(@"ice_hotspot_(\d+)_(\d+)_color$", RegexOptions.Compiled);
var slots = index.ResByName.Keys.Select(k => (Name: k, M: rx.Match(k)))
    .Where(t => t.M.Success)
    .Select(t => (Set: int.Parse(t.M.Groups[1].Value), Index: int.Parse(t.M.Groups[2].Value), Name: t.Name))
    .OrderBy(t => t.Set).ThenBy(t => t.Index).ToList();

Console.WriteLine("hotspot slots: " + slots.Count);
foreach (var g in slots.GroupBy(s => s.Set).OrderBy(g => g.Key))
    Console.WriteLine($"  set {g.Key}: indexes {g.Min(x => x.Index)}..{g.Max(x => x.Index)} ({g.Count()} slots)");

// any non-slot hotspot names
var others = index.ResByName.Keys.Where(k => k.Contains("/hotspot/") && !rx.IsMatch(k)).ToList();
Console.WriteLine("other hotspot-folder names: " + others.Count);
foreach (var o in others.Take(40)) Console.WriteLine("  " + o);

// decode every slot's top mip to png, contact sheet via filenames
var outDir = Path.Combine(Path.GetTempPath(), "hotspots");
Directory.CreateDirectory(outDir);
foreach (var s in slots)
{
    var png = Path.Combine(outDir, $"s{s.Set}_{s.Index}.png");
    if (File.Exists(png)) continue;
    try
    {
        var slot = IceHotspots.Read(view, index, s.Set, s.Index);
        ImageIO.SavePng(slot.Image, png);
    }
    catch (Exception ex) { Console.WriteLine($"  decode fail {s.Set}/{s.Index}: {ex.Message}"); }
}
Console.WriteLine("decoded to " + outDir);
