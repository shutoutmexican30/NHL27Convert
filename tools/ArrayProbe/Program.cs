// ArrayProbe <game> <texture-name> — decode slice 0 of a texture's own chunk.
using Suite.Core;
using Suite.Core.Frostbite;

if (args.Length < 2) { Console.Error.WriteLine("usage: ArrayProbe <game> <texture-name>"); return 2; }
Oodle.Ensure(SuiteSettings.Load());
var index = BundleIndex.Build(args[0]);
var slot = TextureSlots.Read(args[0], index, args[1]);
var slices = Ps4Codec.DecodeSlices(slot.Res, slot.Stock);
Console.WriteLine($"{args[1]}: {slot.Res.Width}x{slot.Res.Height} guid {slot.Chunk} slices {slices.Count}");
var outDir = Path.Combine(Path.GetTempPath(), "arrayprobe");
Directory.CreateDirectory(outDir);
foreach (var i in Enumerable.Range(0, Math.Min(slices.Count, 6)))
    ImageIO.SavePng(slices[i], Path.Combine(outDir, $"arr{i}.png"));
Console.WriteLine("wrote " + outDir);
return 0;
