using Suite.Core;
using Suite.Core.Frostbite;

// Decode every center-logo slot and save a thumbnail contact sheet.
var game = args.Length > 0 ? args[0] : @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var outDir = @"C:\dev\NHL27Convert\tools\MeshProbe\out\slots";
Directory.CreateDirectory(outDir);
var index = BundleIndex.Build(game);
for (int slot = 0; slot < 34; slot++)
{
    try
    {
        var s = CenterIceSlots.Read(game, index, slot);
        ImageIO.SavePng(ImageIO.ResizeSmooth(s.Image, 256, 256), Path.Combine(outDir, $"slot-{slot:D2}.png"));
        Console.WriteLine($"slot {slot:D2}: {s.Res.Width}x{s.Res.Height} ok");
    }
    catch (Exception ex) { Console.WriteLine($"slot {slot:D2}: {ex.Message}"); }
}
