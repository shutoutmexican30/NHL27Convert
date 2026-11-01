// LegacyExtract <folder with texlib_*.rpsgl> <out folder>
// Decodes every texlib's rasters (jersey colour/spec/normal, font colour/normal/spec) to PNG in parallel and writes
// a contact sheet of all jersey colour maps labelled team_uniform. Writes only to <out folder>.
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Suite.Core;
using Suite.Core.Legacy;

if (args.Length < 2) { Console.WriteLine("usage: LegacyExtract <texlib folder> <out folder>"); return 2; }
var src = args[0];
var outDir = SuitePaths.Ensure(args[1]);
var files = Directory.GetFiles(src, "texlib_*.rpsgl").OrderBy(f => f).ToList();
var thumbs = new ConcurrentDictionary<string, Rgba>();
var log = new ConcurrentBag<string>();
int decoded = 0;
Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
{
    var b = File.ReadAllBytes(f);
    var key = Regex.Match(Path.GetFileName(f), @"texlib_1_(\d+)_(\d+)").Groups is var g && g[1].Success ? $"{int.Parse(g[1].Value):00}_{g[2].Value}" : Path.GetFileNameWithoutExtension(f);
    foreach (var r in Rw4Reader.Rasters(b))
    {
        try
        {
            var img = Rw4Reader.Decode(b, r);
            ImageIO.SavePng(img, Path.Combine(outDir, r.Name + ".png"));
            Interlocked.Increment(ref decoded);
            if (r.Name.StartsWith("jersey_") && r.Name.EndsWith("_cm")) thumbs[key] = ImageIO.Shrink(img, 256);
        }
        catch (Exception ex) { log.Add($"{Path.GetFileName(f)} {r.Name}: {ex.Message}"); }
    }
});
Console.WriteLine($"{files.Count} texlib files, {decoded} rasters decoded to {outDir}");
foreach (var l in log.Order()) Console.WriteLine("  " + l);

// Contact sheet: 8 columns of 256 px thumbnails, label strip under each.
var keys = thumbs.Keys.Order().ToList();
const int T = 256, Cols = 8, Lab = 18;
int rows = (keys.Count + Cols - 1) / Cols;
var sheet = new Rgba(Cols * T, rows * (T + Lab), new byte[Cols * T * rows * (T + Lab) * 4]);
for (int i = 0; i < sheet.Pixels.Length; i += 4) { sheet.Pixels[i] = 16; sheet.Pixels[i + 1] = 20; sheet.Pixels[i + 2] = 27; sheet.Pixels[i + 3] = 255; }
for (int k = 0; k < keys.Count; k++)
{
    var t = thumbs[keys[k]];
    int ox = k % Cols * T, oy = k / Cols * (T + Lab);
    for (int y = 0; y < t.Height; y++)
    for (int x = 0; x < t.Width; x++)
    {
        int s = (y * t.Width + x) * 4, d = ((oy + y) * sheet.Width + ox + x) * 4;
        sheet.Pixels[d] = t.Pixels[s]; sheet.Pixels[d + 1] = t.Pixels[s + 1]; sheet.Pixels[d + 2] = t.Pixels[s + 2];
    }
    DrawText(sheet, ox + 4, oy + T + 4, keys[k]);
}
var sheetPath = Path.Combine(outDir, "_contact_jersey_cm.png");
ImageIO.SavePng(sheet, sheetPath);
Console.WriteLine("contact sheet: " + sheetPath);
return 0;

// Tiny 3x5 digit/underscore font for labels.
static void DrawText(Rgba img, int x, int y, string text)
{
    var glyphs = new Dictionary<char, string>
    {
        ['0'] = "111101101101111", ['1'] = "010110010010111", ['2'] = "111001111100111", ['3'] = "111001111001111", ['4'] = "101101111001001",
        ['5'] = "111100111001111", ['6'] = "111100111101111", ['7'] = "111001001001001", ['8'] = "111101111101111", ['9'] = "111101111001111", ['_'] = "000000000000111",
    };
    foreach (var ch in text)
    {
        if (glyphs.TryGetValue(ch, out var g))
            for (int i = 0; i < 15; i++)
                if (g[i] == '1')
                    for (int sy = 0; sy < 2; sy++) for (int sx = 0; sx < 2; sx++)
                    {
                        int px = x + i % 3 * 2 + sx, py = y + i / 3 * 2 + sy;
                        if (px < img.Width && py < img.Height) { int d = (py * img.Width + px) * 4; img.Pixels[d] = 230; img.Pixels[d + 1] = 235; img.Pixels[d + 2] = 242; }
                    }
        x += 8;
    }
}
