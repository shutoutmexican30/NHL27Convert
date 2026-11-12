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

    /// <summary>
    /// Off (owner's in-game check, 2026-11-12): shifting the hem by measured percentages left Boston's hem solid gold
    /// with the black-white-black stripes gone. Where NHL 24's model shows the hem has to come from the two models'
    /// UVs, not from guesses on the texture, so the texture is installed unshifted until that transfer exists.
    /// </summary>
    public static bool Enabled = true;

    /// <summary>Where the last colour change of the hem is, down the middle of the texture (0..1), or null.</summary>
    public static double? LastChange(Rgba image)
    {
        int x = image.Width / 2, from = (int)(image.Height * HemTop);
        int? last = null;
        var prev = Quantize(image, x, from);
        for (int y = from + 1; y < image.Height; y++)
        {
            var c = Quantize(image, x, y);
            if (Differs(c, prev)) { last = y; prev = c; }
        }
        return last is null ? null : (double)last.Value / image.Height;
    }

    static (int R, int G, int B) Quantize(Rgba image, int x, int y)
    {
        int at = (y * image.Width + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
    }

    /// <summary>
    /// A stripe edge, not shading inside one band: Boston fourteen's bottom gold has a lighter lower part
    /// ((255,174,0) to (255,179,66)), which the old 48-step buckets read as the hem's last stripe and moved the whole
    /// hem away. A real edge changes colour by far more than that.
    /// </summary>
    static bool Differs((int R, int G, int B) a, (int R, int G, int B) b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 120;

    /// <summary>The image with its hem moved up so <paramref name="edge"/> lands at <see cref="LastEdge"/>.</summary>
    public static Rgba Remap(Rgba image, double edge) => Remap(image, edge, LastEdge);

    /// <summary>The image with its bottom hem moved so <paramref name="edge"/> lands at <paramref name="target"/>.</summary>
    public static Rgba Remap(Rgba image, double edge, double target)
    {
        int w = image.Width, h = image.Height;
        var output = new byte[image.Pixels.Length];
        Array.Copy(image.Pixels, output, output.Length);
        int top = (int)(h * HemTop);
        for (int y = top; y < h; y++)
        {
            double v = (double)y / h, source;
            if (v <= target) source = HemTop + (v - HemTop) * (edge - HemTop) / (target - HemTop);
            else source = edge + (v - target) * (1 - edge) / (1 - target);
            int sy = Math.Clamp((int)Math.Round(source * h), 0, h - 1);
            Array.Copy(image.Pixels, sy * w * 4, output, y * w * 4, w * 4);
        }
        return new Rgba(w, h, output);
    }

    /// <summary>
    /// Moves the hem of every jersey texture (colour, normal, coefficient; base and finished) of an addition when its
    /// base colour's hem runs lower than NHL 24 shows. Returns the edge that was moved, or null when nothing changed.
    /// </summary>
    /// <summary>The image upside down: the back panel's hem is at the top of the jersey sheet, mirrored.</summary>
    public static Rgba Flip(Rgba image)
    {
        int w = image.Width, h = image.Height;
        var output = new byte[image.Pixels.Length];
        for (int y = 0; y < h; y++) Array.Copy(image.Pixels, y * w * 4, output, (h - 1 - y) * w * 4, w * 4);
        return new Rgba(w, h, output);
    }

    /// <summary>
    /// Front (bottom of the sheet) and back (top, mirrored) hem edges to move: each only when NHL 27's last stripe runs
    /// further toward the sheet edge than the team's own NHL 24 kit (<paramref name="reference"/>) puts it. Florida's NHL 27
    /// hem matches its NHL 24 hem exactly and must stay; moving only the front (the first version) put a step in the
    /// stripe at the side seams. Without a reference, NHL 24's usual edges are used (0.948 / 0.04).
    /// </summary>
    public static (double From, double To)?[] Plan(Rgba nhl27, Rgba? reference)
    {
        var result = new (double, double)?[2];
        // The team's NHL 24 hem only proves how far down its jersey shows; a hem drawn higher (Arizona 92.5%, St. Louis
        // 92.4%) is that kit's design, not a limit, so the target is never above NHL 24's usual edge.
        double frontTarget = Math.Max(LastEdge, reference is null ? 0 : LastChange(reference) ?? 0);
        double? front = LastChange(nhl27);
        if (front is { } f && f > frontTarget + 0.002) result[0] = (f, frontTarget);
        var flipped = Flip(nhl27);
        double backTarget = Math.Max(LastEdge + 0.012, reference is null ? 0 : LastChange(Flip(reference)) ?? 0);
        double? back = LastChange(flipped);
        if (back is { } b && b > backTarget + 0.002) result[1] = (b, backTarget);
        return result;
    }

    public static Rgba Fit(Rgba image, (double From, double To)?[] plan)
    {
        if (plan[0] is { } front) image = Remap(image, front.From, front.To);
        if (plan[1] is { } back) image = Flip(Remap(Flip(image), back.From, back.To));
        return image;
    }

    public static double? Apply(NewUniformAddition addition, Action<string>? say = null) => Apply(addition, null, say);

    public static double? Apply(NewUniformAddition addition, Rgba? reference, Action<string>? say = null)
    {
        static string Leaf(string s) => s[(s.LastIndexOf('/') + 1)..];
        var jersey = addition.Textures.Where(t => Leaf(t.SourceSlot).StartsWith("jersey_", StringComparison.Ordinal)
            && !t.SourceSlot.EndsWith("_array", StringComparison.Ordinal) && File.Exists(t.Artwork.Source)).ToList();
        var baseColor = jersey.FirstOrDefault(t => Leaf(t.SourceSlot).EndsWith("_base_color", StringComparison.Ordinal))
            ?? jersey.FirstOrDefault(t => Leaf(t.SourceSlot).EndsWith("_color", StringComparison.Ordinal));
        if (baseColor is null) return null;
        var art = ImageIO.Load(baseColor.Artwork.Source);
        if (reference is not null && (reference.Width != art.Width || reference.Height != art.Height)) reference = null;
        var plan = Plan(art, reference);
        if (plan[0] is null && plan[1] is null) return null;
        string dir = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "hem-fit"));
        string tag = $"{plan[0]?.From:0.000}-{plan[0]?.To:0.000}_{plan[1]?.From:0.000}-{plan[1]?.To:0.000}_";
        foreach (var t in jersey)
        {
            string png = Path.Combine(dir, tag + Path.GetFileName(t.Artwork.Source));
            ImageIO.SavePng(Fit(ImageIO.Load(t.Artwork.Source), plan), png);
            t.Artwork = t.Artwork with { Source = png };
        }
        say?.Invoke($"{addition.Team} {addition.SourceKit}: hem stripes moved to the team's NHL 24 hem"
            + (plan[0] is { } f ? $"; front {f.From:P1} to {f.To:P1}" : "") + (plan[1] is { } b ? $"; back {1 - b.From:P1} to {1 - b.To:P1}" : "")
            + $" ({jersey.Count} textures)");
        return plan[0]?.From ?? plan[1]?.From;
    }
}
