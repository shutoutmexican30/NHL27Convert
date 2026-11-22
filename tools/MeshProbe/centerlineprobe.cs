using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Suite.Core;
using Suite.Core.Frostbite;

var settings = SuiteSettings.Load();
var view = Nhl24ReadView.Ensure(settings);
var index = BundleIndex.Build(view);

// centerline sheets: how many, and are they per-team?
var cl = index.ResByName.Keys.Where(k => k.StartsWith("content/worlds/rink/textures/centerline/", StringComparison.Ordinal) && k.EndsWith("_color", StringComparison.Ordinal)).OrderBy(x => x).ToList();
Console.WriteLine("centerline sheets: " + cl.Count);
foreach (var n in cl.Take(40)) Console.WriteLine("  " + n.Split('/')[^1]);

var lines = index.ResByName.Keys.Where(k => k.StartsWith("content/worlds/rink/textures/lines/", StringComparison.Ordinal)).OrderBy(x => x).ToList();
Console.WriteLine("lines sheets: " + lines.Count);
foreach (var n in lines) Console.WriteLine("  " + n.Split('/')[^1]);

// what EBX lives next to centerline sheets (per-arena config?)
var clEbx = index.EbxByName.Keys.Where(k => k.StartsWith("content/worlds/rink/textures/centerline/", StringComparison.Ordinal)).ToList();
Console.WriteLine("centerline ebx: " + clEbx.Count + "  e.g. " + (clEbx.FirstOrDefault() ?? "-"));
var runtime = index.EbxByName.Keys.Where(k => k.StartsWith("content/worlds/rink/textures/runtime/", StringComparison.Ordinal)).ToList();
foreach (var n in runtime) Console.WriteLine("runtime ebx: " + n);
var runtimeRes = index.ResByName.Keys.Where(k => k.StartsWith("content/worlds/rink/textures/runtime/", StringComparison.Ordinal)).ToList();
foreach (var n in runtimeRes) Console.WriteLine("runtime res: " + n);
