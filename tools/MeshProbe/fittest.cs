using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.FullRink;

// Fit the cached faceoff layout through the suite's real pipeline and save the result for the sign test.
var srcPath = @"C:\dev\NHL27Convert\tools\RinkSharp\test\faceoff_layout.png";
var img = ImageIO.Load(srcPath);
var m = RinkFrame.Measure(img);
Console.WriteLine($"measure: {m.How}  centre=({m.CentreX:F0},{m.CentreY:F0})  px/m={m.PixelsPerMetre:F2}  found={m.Found}");
var fit = RinkFrame.Fit(img, FitMode.Layout, 1024, m);
RinkFrame.HideDrawnNets(fit);
RinkFrame.ShrinkCreases(fit);
ImageIO.SavePng(fit, @"C:\dev\NHL27Convert\tools\MeshProbe\out\fit_test.png");
Console.WriteLine("saved fit_test.png");
