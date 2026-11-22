using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);
var slots = IceHotspots.Slots(index);
Console.WriteLine($"slots: {slots.Count}, sets: {string.Join(",", slots.Select(s => s.Set).Distinct())}");

var slot = IceHotspots.Read(game, index, 0, 1);
Console.WriteLine($"read 0/1: {slot.Res.Width}x{slot.Res.Height} mips {slot.Res.MipCount}");

// Replacement with no alpha -> white key path.
var img = ImageIO.Load(@"C:\dev\NHL27Convert\tools\RinkSharp\test\faceoff_layout.png");
var built = IceHotspots.Build(slot.Res, img);
var check = TextureChunk.Parse(built, slot.Res);
var decoded = Bc7.DecodeTiled(check.Mips[0], slot.Res.Width, slot.Res.Height);
ImageIO.SavePng(decoded, @"C:\dev\NHL27Convert\tools\MeshProbe\out\hot_roundtrip.png");
int trans = 0; for (int i = 3; i < decoded.Pixels.Length; i += 4) if (decoded.Pixels[i] < 10) trans++;
Console.WriteLine($"roundtrip: {built.Length} bytes (stock payload {slot.Res.PayloadSize}), transparent {trans}/{decoded.Width * decoded.Height} px");
Console.WriteLine(built.Length == slot.Res.PayloadSize ? "BUILD OK" : "BUILD SIZE MISMATCH");
