using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// NHL 24's modern jersey shows the texture only down to about 96% of its height: its own kits end the hem with a gold
/// band at 94.9–96% (Boston six/seven). Most NHL 27 Fanatics jerseys end their hem stripes by ~94.6%, but some paint
/// them lower (Boston thirteen/fourteen: black-white-black at 93–96%, the last band from 95.8% to the bottom), so in
/// NHL 24 the last band is cut off and the stripes above it look squeezed. When the hem's last colour change lies below
/// <see cref="LastEdge"/>, the hem from <see cref="HemTop"/> down is moved up so that change lands at LastEdge and the
/// final band keeps the same visible height NHL 24's own kits give it. Jerseys whose hem already fits are untouched.
/// </summary>
public static class HemFit
{
    public const double HemTop = 0.90, LastEdge = 0.948;

    /// <summary>Where the last colour change of the hem is, down the middle of the texture (0..1), or null.</summary>
    public static double? LastChange(Rgba image)
    {
        int x = image.Width / 2, from = (int)(image.Height * HemTop);
        int? last = null;
        var prev = Quantize(image, x, from);
        for (int y = from + 1; y < image.Height; y++)
        {
            var c = Quantize(image, x, y);
            if (c != prev) { last = y; prev = c; }
        }
        return last is null ? null : (double)last.Value / image.Height;
    }

    static (int, int, int) Quantize(Rgba image, int x, int y)
    {
        int at = (y * image.Width + x) * 4;
        return (image.Pixels[at] / 48, image.Pixels[at + 1] / 48, image.Pixels[at + 2] / 48);
    }

    /// <summary>The image with its hem moved up so <paramref name="edge"/> lands at <see cref="LastEdge"/>.</summary>
    public static Rgba Remap(Rgba image, double edge)
    {
        int w = image.Width, h = image.Height;
        var output = new byte[image.Pixels.Length];
        Array.Copy(image.Pixels, output, output.Length);
        int top = (int)(h * HemTop);
        for (int y = top; y < h; y++)
        {
            double v = (double)y / h, source;
            if (v <= LastEdge) source = HemTop + (v - HemTop) * (edge - HemTop) / (LastEdge - HemTop);
            else source = edge + (v - LastEdge) * (1 - edge) / (1 - LastEdge);
            int sy = Math.Clamp((int)Math.Round(source * h), 0, h - 1);
            Array.Copy(image.Pixels, sy * w * 4, output, y * w * 4, w * 4);
        }
        return new Rgba(w, h, output);
    }

    /// <summary>
    /// Moves the hem of every jersey texture (colour, normal, coefficient; base and finished) of an addition when its
    /// base colour's hem runs lower than NHL 24 shows. Returns the edge that was moved, or null when nothing changed.
    /// </summary>
    public static double? Apply(NewUniformAddition addition, Action<string>? say = null)
    {
        static string Leaf(string s) => s[(s.LastIndexOf('/') + 1)..];
        var jersey = addition.Textures.Where(t => Leaf(t.SourceSlot).StartsWith("jersey_", StringComparison.Ordinal)
            && !t.SourceSlot.EndsWith("_array", StringComparison.Ordinal) && File.Exists(t.Artwork.Source)).ToList();
        var baseColor = jersey.FirstOrDefault(t => Leaf(t.SourceSlot).EndsWith("_base_color", StringComparison.Ordinal))
            ?? jersey.FirstOrDefault(t => Leaf(t.SourceSlot).EndsWith("_color", StringComparison.Ordinal));
        if (baseColor is null || LastChange(ImageIO.Load(baseColor.Artwork.Source)) is not { } edge || edge <= LastEdge + 0.002) return null;
        string dir = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "hem-fit"));
        foreach (var t in jersey)
        {
            string png = Path.Combine(dir, $"{edge:0.000}_" + Path.GetFileName(t.Artwork.Source));
            ImageIO.SavePng(Remap(ImageIO.Load(t.Artwork.Source), edge), png);
            t.Artwork = t.Artwork with { Source = png };
        }
        say?.Invoke($"{addition.Team} {addition.SourceKit}: hem stripes ran to {edge:P1} of the texture; moved up to fit NHL 24's jersey ({jersey.Count} textures)");
        return edge;
    }
}
