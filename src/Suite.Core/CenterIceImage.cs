namespace Suite.Core;

/// <summary>
/// Turns a picture of a real rink (a TheFaceoff.net rink image or its center circle, or any photo-like flat render) into
/// an NHL 24 center-ice decal laid out like the stock slot. NHL 24's center logo is a transparent decal over the ice: it
/// draws its own blue center circle and the arena text, and leaves a transparent gap where the ice's red line shows
/// through. So the ice white is keyed to alpha, the red-line strip is cut out, the blue circle is kept, and the result is scaled and
/// centred so its blue circle lands exactly on the stock slot's circle.
/// Rules (user, 2026-11-03): keep the blue circle; key out only the ice white; the red-line strip is a clean
/// transparent cutout (<see cref="CutRedLine"/>), exactly like stock.
/// </summary>
public static class CenterIceImage
{
    public sealed record Circle(double X, double Y, double R);

    static bool IsBlue(byte r, byte g, byte b) => b > 110 && b > r + 50 && b > g + 25;

    /// <summary>The blue center circle: the ring of blue pixels nearest the middle (centroid + median radius).</summary>
    public static Circle FindCircle(Rgba img, bool useAlpha = false)
    {
        int w = img.Width, h = img.Height;
        // Search a central window (a full rink is much wider than tall; its center circle is in the middle).
        int side = Math.Min(w, h);
        int x0 = (w - side) / 2, y0 = (h - side) / 2;
        var pts = new List<(int X, int Y)>();
        for (int y = y0; y < y0 + side; y++)
            for (int x = x0; x < x0 + side; x++)
            {
                int p = (y * w + x) * 4;
                if (useAlpha && img.Pixels[p + 3] < 128) continue;
                if (IsBlue(img.Pixels[p], img.Pixels[p + 1], img.Pixels[p + 2])) pts.Add((x, y));
            }
        if (pts.Count < 50) throw new InvalidOperationException("No blue center circle found in the image.");
        // Robust circle: centre = bounding-box middle of the blue ring after dropping stray blue (logo colours) by
        // iterating on the median radius.
        double cx = pts.Average(q => q.X), cy = pts.Average(q => q.Y), r = 0;
        for (int it = 0; it < 6; it++)
        {
            var d = pts.Select(q => Math.Sqrt((q.X - cx) * (q.X - cx) + (q.Y - cy) * (q.Y - cy))).ToArray();
            var sorted = d.Order().ToArray();
            r = sorted[(int)(sorted.Length * 0.8)];   // the ring is the outermost blue
            var ring = pts.Where((q, i) => Math.Abs(d[i] - r) < r * 0.04).ToList();
            if (ring.Count < 30) break;
            cx = (ring.Min(q => q.X) + ring.Max(q => q.X)) / 2.0;
            cy = (ring.Min(q => q.Y) + ring.Max(q => q.Y)) / 2.0;
        }
        return new Circle(cx, cy, r);
    }

    /// <summary>
    /// Converts <paramref name="rink"/> to the layout of <paramref name="stock"/> (the slot's current decoded mip 0).
    /// <paramref name="iceWhite"/> is the ice colour to key out (sampled from the image when null).
    /// </summary>
    public static Rgba ToSlot(Rgba rink, Rgba stock)
    {
        var src = FindCircle(rink);
        var dst = FindCircle(stock, useAlpha: true);
        double scale = src.R / dst.R;
        int w = stock.Width, h = stock.Height;
        // Ice colour: median of pixels just inside the circle that are light and unsaturated.
        var ice = SampleIce(rink, src);
        // Content radius: keep what the stock keeps (its text ring), nothing beyond.
        double keep = StockExtent(stock, dst) / dst.R;
        var px = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double dx = x - dst.X, dy = y - dst.Y;
                if (Math.Sqrt(dx * dx + dy * dy) > keep * dst.R) continue;
                double sx = src.X + dx * scale, sy = src.Y + dy * scale;
                var c = Sample(rink, sx, sy);
                double a = 1;
                // Ice white → transparent, with the white un-mixed from edge pixels so there is no halo.
                double dIce = Dist(c, ice);
                a = Math.Clamp((dIce - 18) / 60.0, 0, 1);
                if (a <= 0.004) continue;
                int p = (y * w + x) * 4;
                for (int k = 0; k < 3; k++)
                    px[p + k] = (byte)Math.Clamp((c[k] - (1 - a) * ice[k]) / a, 0, 255);
                px[p + 3] = (byte)Math.Round(a * 255);
            }
        });
        return CutRedLine(new Rgba(w, h, px));
    }

    /// <summary>
    /// The red-line gap every stock NHL 24 center logo has: columns 494-529 of 1024 fully transparent (the ice's red line
    /// shows through). Applied to every center-ice texture the suite writes, whatever its source, so no logo or texture
    /// bridges the line. Scales with the texture width.
    /// </summary>
    public static Rgba CutRedLine(Rgba img)
    {
        int x0 = (int)Math.Round(494 * img.Width / 1024.0), x1 = (int)Math.Round(529 * img.Width / 1024.0);
        var px = (byte[])img.Pixels.Clone();
        for (int y = 0; y < img.Height; y++)
            for (int x = x0; x <= x1; x++)
            {
                int p = (y * img.Width + x) * 4;
                px[p] = px[p + 1] = px[p + 2] = px[p + 3] = 0;
            }
        return new Rgba(img.Width, img.Height, px);
    }

    static double Dist(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

    static double[] Sample(Rgba img, double x, double y)
    {
        x = Math.Clamp(x, 0, img.Width - 1.001); y = Math.Clamp(y, 0, img.Height - 1.001);
        int x0 = (int)x, y0 = (int)y; double fx = x - x0, fy = y - y0;
        var c = new double[3];
        for (int k = 0; k < 3; k++)
        {
            double P(int xx, int yy) => img.Pixels[(yy * img.Width + xx) * 4 + k];
            c[k] = P(x0, y0) * (1 - fx) * (1 - fy) + P(x0 + 1, y0) * fx * (1 - fy) + P(x0, y0 + 1) * (1 - fx) * fy + P(x0 + 1, y0 + 1) * fx * fy;
        }
        return c;
    }

    static double[] SampleIce(Rgba img, Circle c)
    {
        var list = new List<double[]>();
        for (int i = 0; i < 720; i++)
        {
            double t = i * Math.PI / 360, rr = c.R * (0.6 + 0.35 * ((i * 7) % 10) / 10.0);
            var s = Sample(img, c.X + Math.Cos(t) * rr, c.Y + Math.Sin(t) * rr);
            if (s.Min() > 170 && s.Max() - s.Min() < 40) list.Add(s);
        }
        if (list.Count < 20) return [235, 240, 245];
        return new[] { 0, 1, 2 }.Select(k => list.Select(s => s[k]).Order().ElementAt(list.Count / 2)).ToArray();
    }

    static double StockExtent(Rgba stock, Circle c)
    {
        double max = c.R;
        for (int y = 0; y < stock.Height; y += 2)
            for (int x = 0; x < stock.Width; x += 2)
                if (stock.Pixels[(y * stock.Width + x) * 4 + 3] > 16)
                    max = Math.Max(max, Math.Sqrt((x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y)));
        return max + 4;
    }
}
