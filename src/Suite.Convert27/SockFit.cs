using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// Sock stripes, fitted like the jersey hem. NHL 24's socks show their stripes above about 70% of the sheet; below that
/// the sock runs into the skate. Some NHL 27 socks paint their stripe block lower (Boston thirteen/fourteen end at 79%,
/// San Jose 78%, Carolina away 84%, against 70.6%, 69.1% and 74.5% on the same teams' NHL 24 socks), so in NHL 24 the
/// lower stripes disappear into the skate. When the NHL 27 block ends lower than the team's own NHL 24 block, the sheet
/// above it is squeezed so the block ends where NHL 24's does; the part below follows. A team whose NHL 24 stripes stop
/// higher than usual (Arizona's Coyotes socks, 65.8%) is a design, not a limit, so the target is never above
/// <see cref="UsualEnd"/>. Socks that already fit are untouched.
/// </summary>
public static class SockFit
{
    public const double UsualEnd = 0.70;

    /// <summary>The first and last stripe edges down the middle column (0..1), or null for a plain sock.</summary>
    public static (double First, double Last)? Band(Rgba image)
    {
        int x = image.Width / 2;
        int? first = null, last = null;
        (int R, int G, int B) P(int y) { int at = (y * image.Width + x) * 4; return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]); }
        var prev = P(0);
        for (int y = 1; y < image.Height; y++)
        {
            var c = P(y);
            if (Math.Abs(c.R - prev.R) + Math.Abs(c.G - prev.G) + Math.Abs(c.B - prev.B) <= 120) continue;
            first ??= y; last = y; prev = c;
        }
        return first is null ? null : ((double)first.Value / image.Height, (double)last!.Value / image.Height);
    }

    /// <summary>Where the NHL 27 stripe block should end (null = leave the sock as it is).</summary>
    public static (double From, double To)? Plan(Rgba nhl27, Rgba? reference)
    {
        if (Band(nhl27) is not { } mine) return null;
        double target = Math.Max(UsualEnd, reference is not null && Band(reference) is { } theirs ? theirs.Last : UsualEnd);
        return mine.Last > target + 0.01 ? (mine.Last, target) : null;
    }

    /// <summary>The sheet with row <paramref name="from"/> moved to <paramref name="to"/>: above it squeezed, below it stretched.</summary>
    public static Rgba Remap(Rgba image, double from, double to)
    {
        int w = image.Width, h = image.Height;
        var output = new byte[image.Pixels.Length];
        for (int y = 0; y < h; y++)
        {
            double v = (double)y / h;
            double source = v <= to ? v * from / to : from + (v - to) * (1 - from) / (1 - to);
            int sy = Math.Clamp((int)Math.Round(source * h), 0, h - 1);
            Array.Copy(image.Pixels, sy * w * 4, output, y * w * 4, w * 4);
        }
        return new Rgba(w, h, output);
    }

    /// <summary>Fits every sock texture of an addition (colour, normal, coefficient; base and finished) to the plan.</summary>
    public static (double From, double To)? Apply(NewUniformAddition addition, Rgba? reference, Action<string>? say = null)
    {
        static string Leaf(string s) => s[(s.LastIndexOf('/') + 1)..];
        var socks = addition.Textures.Where(t => Leaf(t.SourceSlot).StartsWith("sock_", StringComparison.Ordinal) && File.Exists(t.Artwork.Source)).ToList();
        var color = socks.FirstOrDefault(t => Leaf(t.SourceSlot).EndsWith("_color", StringComparison.Ordinal) && !Leaf(t.SourceSlot).Contains("_base_"))
            ?? socks.FirstOrDefault(t => Leaf(t.SourceSlot).EndsWith("_base_color", StringComparison.Ordinal));
        if (color is null) return null;
        var art = ImageIO.Load(color.Artwork.Source);
        if (reference is not null && (reference.Height != art.Height || reference.Width != art.Width)) reference = null;
        if (Plan(art, reference) is not { } plan) return null;
        string dir = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "sock-fit"));
        foreach (var t in socks)
        {
            var image = ImageIO.Load(t.Artwork.Source);
            if (image.Height != art.Height) continue;
            string png = Path.Combine(dir, $"{addition.Team}_{addition.SourceKit}_" + Path.GetFileName(t.Artwork.Source));
            ImageIO.SavePng(Remap(image, plan.From, plan.To), png);
            t.Artwork = t.Artwork with { Source = png };
        }
        say?.Invoke($"{addition.Team} {addition.SourceKit}: sock stripes ended at {plan.From:P1}, lower than NHL 24's sock shows; moved up to {plan.To:P1}");
        return plan;
    }
}
