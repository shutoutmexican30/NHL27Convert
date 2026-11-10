namespace Suite.Core;

/// <summary>
/// The fixed reference geometry of the user's Centerlogo_Template.psd (1024x1024), measured 2026-11-08:
/// blue centre circle at (511.5, 511.5) with radius 382.5, red centre-line stripe in columns 491-532.
/// Every converted center-ice decal is validated against this template so a wrong-sized or off-centre logo is
/// caught before it is written to the game, and the same guides can be drawn over any decal for a visual check.
/// All values scale with the texture size, exactly like <see cref="CenterIceImage.CutRedLine"/>.
/// </summary>
public static class CenterIceTemplate
{
    public const double ReferenceSize = 1024;

    public static CenterIceImage.Circle ReferenceCircle(int width, int height) =>
        new(511.5 * width / ReferenceSize, 511.5 * height / ReferenceSize, 382.5 * width / ReferenceSize);

    /// <summary>The template's red-line stripe (inclusive columns), used for the guide overlay. It is a drawing guide
    /// 3px wider per side than the stock cut, so it must NOT be the transparency check.</summary>
    public static (int X0, int X1) RedLineColumns(int width) =>
        ((int)Math.Round(491 * width / ReferenceSize), (int)Math.Round(532 * width / ReferenceSize));

    /// <summary>The stock slot's actual transparent red-line gap (inclusive columns), same as CutRedLine: this is the
    /// region every written decal must leave transparent so the ice's red line shows through.</summary>
    public static (int X0, int X1) StockCutColumns(int width) =>
        ((int)Math.Round(494 * width / ReferenceSize), (int)Math.Round(529 * width / ReferenceSize));

    public sealed record Report(
        CenterIceImage.Circle? Measured,
        double CenterErrorPx,
        double RadiusErrorPct,
        int RedLineOpaquePixels,
        IReadOnlyList<string> Issues,
        bool CircleChecked = true)
    {
        public bool Ok => Issues.Count == 0;
        public string Summary
        {
            get
            {
                if (!Ok) return string.Join(" ", Issues);
                if (!CircleChecked) return "no blue circle in this team's art; circle check skipped, red-line gap clear";
                return $"template check passed (circle centre off by {CenterErrorPx:0.0}px, radius {RadiusErrorPct:+0.0;-0.0;0.0}%, red-line gap clear)";
            }
        }
    }

    /// <summary>
    /// The OUTER blue ring, not the strongest blue: dense logo ink (Tampa Bay's lightning roundel, off-centre crests)
    /// fooled a centroid/percentile fit. For each angle the farthest blue pixel wins; the median of those distances is
    /// the outer circle, robust to arena text outside it and solid logos inside it. Returns null when no ring exists.
    /// </summary>
    internal static CenterIceImage.Circle? FindOuterCircle(Rgba img, out int bluePixels)
    {
        int w = img.Width, h = img.Height;
        var pts = new List<(int X, int Y)>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                if (img.Pixels[p + 3] < 128) continue;
                byte cr = img.Pixels[p], cg = img.Pixels[p + 1], cb = img.Pixels[p + 2];
                if (cb > 110 && cb > cr + 50 && cb > cg + 25) pts.Add((x, y));
            }
        bluePixels = pts.Count;
        if (pts.Count < 50) return null;
        var reference = ReferenceCircle(w, h);
        double cx = reference.X, cy = reference.Y, r = 0;
        const int Bins = 720;
        var farthest = new double[Bins];
        for (int it = 0; it < 4; it++)
        {
            Array.Fill(farthest, -1.0);
            foreach (var (x, y) in pts)
            {
                double dx = x - cx, dy = y - cy;
                double d = Math.Sqrt(dx * dx + dy * dy);
                int bin = (int)((Math.Atan2(dy, dx) + Math.PI) / (2 * Math.PI) * Bins) % Bins;
                if (d > farthest[bin]) farthest[bin] = d;
            }
            var ds = farthest.Where(d => d > 0).Order().ToArray();
            if (ds.Length < Bins / 3) return null;   // no ring around most of the circle
            r = ds[ds.Length / 2];
            var ring = pts.Where(q =>
            {
                double d = Math.Sqrt((q.X - cx) * (q.X - cx) + (q.Y - cy) * (q.Y - cy));
                return Math.Abs(d - r) < Math.Max(4, r * 0.02);
            }).ToList();
            if (ring.Count < 60) return null;
            double nx = (ring.Min(q => q.X) + ring.Max(q => q.X)) / 2.0;
            double ny = (ring.Min(q => q.Y) + ring.Max(q => q.Y)) / 2.0;
            if (Math.Abs(nx - cx) < 0.5 && Math.Abs(ny - cy) < 0.5) { cx = nx; cy = ny; break; }
            cx = nx; cy = ny;
        }
        return new CenterIceImage.Circle(cx, cy, r);
    }

    /// <summary>
    /// Checks a finished decal (the exact pixels that would be written) against the template: its blue circle must sit
    /// on the template circle within <paramref name="centerTolerancePx"/> and <paramref name="radiusTolerancePct"/>,
    /// and the whole template red-line stripe must be transparent so the ice's red line shows through.
    /// </summary>
    public static Report Validate(Rgba decal, double centerTolerancePx = 12, double radiusTolerancePct = 4)
    {
        var reference = ReferenceCircle(decal.Width, decal.Height);
        var issues = new List<string>();
        CenterIceImage.Circle? measured = null;
        double centerError = double.NaN, radiusError = double.NaN;
        bool circleChecked = false;
        measured = FindOuterCircle(decal, out int bluePixels);
        if (measured is not null)
        {
            circleChecked = true;
            centerError = Math.Sqrt((measured.X - reference.X) * (measured.X - reference.X)
                + (measured.Y - reference.Y) * (measured.Y - reference.Y));
            radiusError = 100 * (measured.R - reference.R) / reference.R;
            double centerTol = centerTolerancePx * decal.Width / ReferenceSize;
            if (centerError > centerTol)
                issues.Add($"Off-centre: the blue circle is {centerError:0.0}px from the template centre (allowed {centerTol:0.0}).");
            if (Math.Abs(radiusError) > radiusTolerancePct)
                issues.Add($"Wrong size: the blue circle radius is {measured.R:0.0}px, template {reference.R:0.0}px ({radiusError:+0.0;-0.0;0.0}%, allowed ±{radiusTolerancePct:0.0}%).");
        }
        else if (bluePixels >= 50)
        {
            issues.Add("Circle not measurable: blue ink is present but no outer blue ring was found; review this team's art on the sheet.");
        }
        var (x0, x1) = StockCutColumns(decal.Width);
        int opaque = 0;
        for (int y = 0; y < decal.Height; y++)
            for (int x = x0; x <= x1; x++)
                if (decal.Pixels[(y * decal.Width + x) * 4 + 3] > 16) opaque++;
        if (opaque > 0)
            issues.Add($"Red line covered: {opaque} pixel(s) inside the stock centre-line gap are not transparent.");
        return new Report(measured, centerError, radiusError, opaque, issues, circleChecked);
    }

    /// <summary>The decal composited over flat bright ice, for previews and the verification sheet.</summary>
    public static Rgba OnIce(Rgba decal)
    {
        var px = new byte[decal.Width * decal.Height * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            double a = decal.Pixels[i + 3] / 255.0;
            px[i] = (byte)Math.Round(decal.Pixels[i] * a + 232 * (1 - a));
            px[i + 1] = (byte)Math.Round(decal.Pixels[i + 1] * a + 238 * (1 - a));
            px[i + 2] = (byte)Math.Round(decal.Pixels[i + 2] * a + 244 * (1 - a));
            px[i + 3] = 255;
        }
        return new Rgba(decal.Width, decal.Height, px);
    }

    /// <summary>
    /// Draws the template's guides (red centre line and blue reference circle) semi-transparently on top of a decal,
    /// so any offset or size mismatch is visible at a glance. Input should usually be <see cref="OnIce"/> output.
    /// </summary>
    public static Rgba OverlayGuides(Rgba image, double guideAlpha = 0.45)
    {
        var px = (byte[])image.Pixels.Clone();
        int w = image.Width, h = image.Height;
        void Blend(int x, int y, double r, double g, double b, double a)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int p = (y * w + x) * 4;
            px[p] = (byte)Math.Round(px[p] * (1 - a) + r * a);
            px[p + 1] = (byte)Math.Round(px[p + 1] * (1 - a) + g * a);
            px[p + 2] = (byte)Math.Round(px[p + 2] * (1 - a) + b * a);
        }
        var (x0, x1) = RedLineColumns(w);
        for (int y = 0; y < h; y++)
            for (int x = x0; x <= x1; x++) Blend(x, y, 210, 30, 30, guideAlpha * 0.7);
        var c = ReferenceCircle(w, h);
        // Two concentric strokes make a ~2px ring at 1024.
        for (int i = 0; i < 3600; i++)
        {
            double t = i * Math.PI / 1800;
            foreach (double rr in new[] { c.R - 0.5, c.R + 0.5 })
                Blend((int)Math.Round(c.X + Math.Cos(t) * rr), (int)Math.Round(c.Y + Math.Sin(t) * rr), 20, 60, 220, guideAlpha);
        }
        return new Rgba(w, h, px);
    }

    /// <summary>One verification cell: the decal over ice with the template guides on top.</summary>
    public static Rgba VerificationCell(Rgba decal) => OverlayGuides(OnIce(decal));

    /// <summary>The 32 NHL team slots: 0-29 plus Vegas 32 and Seattle 33 (30/31 are All-Star logos).</summary>
    public static int[] TeamSlots() => [.. Enumerable.Range(0, 30).Concat([32, 33])];

    /// <summary>A contact sheet of every slot-N.png in <paramref name="dir"/>, captioned with its validation line.</summary>
    public static void WriteSheet(string dir, IEnumerable<int> slots, IReadOnlyList<string> reportLines)
    {
        using var w = new StreamWriter(Path.Combine(dir, "sheet.html"));
        w.WriteLine("<!doctype html><meta charset=\"utf-8\"><title>Center ice — template verification</title>");
        w.WriteLine("<style>body{background:#12161c;color:#b9c8d8;font:14px Segoe UI,sans-serif;margin:24px}" +
            "h1{color:#fff;font-size:20px}.grid{display:grid;grid-template-columns:repeat(4,1fr);gap:16px}" +
            ".cell{background:#1d242c;border:1px solid #2c3642;border-radius:6px;padding:8px}" +
            "img{width:100%;display:block}.cap{margin-top:6px;font-size:12px;line-height:1.4}</style>");
        w.WriteLine("<h1>Center ice vs Centerlogo_Template.psd — NHL 27 art, red-line gap cut, template guides overlaid</h1>");
        w.WriteLine("<div class=\"grid\">");
        foreach (var slot in slots)
        {
            string png = $"slot-{slot}.png";
            if (!File.Exists(Path.Combine(dir, png))) continue;
            string caption = reportLines.FirstOrDefault(l => l.StartsWith($"slot {slot} ")) ?? $"slot {slot}";
            w.WriteLine($"<div class=\"cell\"><img src=\"{png}\"><div class=\"cap\">{System.Net.WebUtility.HtmlEncode(caption)}</div></div>");
        }
        w.WriteLine("</div>");
    }
}
