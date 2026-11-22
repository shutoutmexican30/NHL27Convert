using System;
using System.Collections.Generic;
using System.IO;
using Suite.Core;
using Suite.Core.FullRink;
using Suite.Core.Frostbite;

// Fit the cached faceoff layout for the full-rink mesh and write it into center-ice slot 0 (both layers).
var game = args[0];
var srcPath = @"C:\dev\NHL27Convert\tools\RinkSharp\test\faceoff_layout.png";

var img = ImageIO.Load(srcPath);
var m = RinkFrame.Measure(img);
Console.WriteLine($"measure: {m.How}  centre=({m.CentreX:F0},{m.CentreY:F0})  px/m={m.PixelsPerMetre:F2}  found={m.Found}");
var fit = RinkFrame.Fit(img, FitMode.Layout, 1024, m);
RinkFrame.HideDrawnNets(fit);
RinkFrame.ShrinkCreases(fit);
Directory.CreateDirectory(@"C:\dev\NHL27Convert\tools\MeshProbe\out");
ImageIO.SavePng(fit, @"C:\dev\NHL27Convert\tools\MeshProbe\out\slot0_fit.png");
Console.WriteLine("saved preview: tools\\MeshProbe\\out\\slot0_fit.png");

var dict = new Dictionary<int, Rgba> { [0] = fit };
var (ok, message, backup) = CenterIceSlots.Apply(game, dict, p => Console.WriteLine("  " + p));
Console.WriteLine((ok ? "SLOT APPLY OK" : "SLOT APPLY FAILED") + ": " + message);
if (backup is not null) Console.WriteLine("backup: " + backup);
return ok ? 0 : 1;
