using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

// Smoke test for the new Convert27 NHL24 folder-decode path.
var settings = SuiteSettings.Load();
var view = Nhl24ReadView.Ensure(settings);
Console.WriteLine("view: " + view);
var index = BundleIndex.Build(view);
Console.WriteLine("bundles: " + index.ResByName.Count + " names");

// top-level folder prefixes
var tops = index.ResByName.Keys.Where(k => !k.EndsWith("_mesh", StringComparison.Ordinal))
    .Select(k => k.Split('/')[0] + "/" + (k.Split('/').Length > 1 ? k.Split('/')[1] : ""))
    .Distinct().OrderBy(x => x).Take(30).ToList();
foreach (var t in tops) Console.WriteLine("  " + t);

// find a folder containing ice_centerlogo
var name = index.ResByName.Keys.FirstOrDefault(k => k.Contains("ice_centerlogo", StringComparison.OrdinalIgnoreCase));
Console.WriteLine("sample: " + name);
if (name is not null)
{
    var folder = name[..name.LastIndexOf('/')];
    var names = index.ResByName.Keys
        .Where(k => k.StartsWith(folder + "/", StringComparison.Ordinal) && !k.EndsWith("_mesh", StringComparison.Ordinal))
        .ToList();
    Console.WriteLine($"folder {folder}: {names.Count} textures");
    var slot = TextureSlots.Read(view, index, name);
    var slices = Ps4Codec.DecodeSlices(slot.Res, slot.Stock);
    Console.WriteLine($"decoded {slices.Count} slice(s) {slices[0].Width}x{slices[0].Height}");
    var png = Path.Combine(Path.GetTempPath(), "folderprobe.png");
    ImageIO.SavePng(ImageIO.Shrink(slices[0], 2048), png);
    Console.WriteLine("png: " + png + " " + new FileInfo(png).Length + " bytes");
}
