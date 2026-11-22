namespace RinkSharp;

/// <summary>
/// The full-ice frame: one square texture that a vertex-stretched centre-ice patch maps over the whole rink
/// (u along the ice, v across, the 2:1 rink squeezed into a square). Geometry constants are NHL rule-book and
/// EA rink measurements; the detection/fitting approach is adapted from Blueline Studio's Rinks module
/// (MIT, github.com/SonkuModz/Blueline-Studio), rewritten here to work in linear light and at configurable
/// output resolution.
/// </summary>
public enum FitMode
{
    /// <summary>The picture is a top-down rink layout (TheFaceoff.net style, ~5000x2236): the rink is found
    /// in it and mapped onto the frame's ice area.</summary>
    Layout,
    /// <summary>The picture is already drawn to the whole frame (2:1): it is resampled to the square as-is.</summary>
    Template,
}

public sealed record RinkMeasure(double CentreX, double CentreY, double PixelsPerMetre, string How, bool Found = true);

public static class RinkFrame
{
    // Frame the stretched patch samples, in metres: u covers FrameLength along the ice, v covers FrameWidth across.
    public const double FrameLength = 62.56;
    public const double FrameWidth = 31.28;
    // The playing surface inside it.
    public const double IceLength = 60.96;   // 200 ft
    public const double IceWidth = 25.98;    // 85 ft
    public const double CornerRadius = 8.53; // 28 ft
    // Where the game's nets stand, measured through the stock ice mesh (89.1 ft from centre).
    public const double GoalLine = 27.165;
    // Creases are shrunk round the goal-line middle so their sides meet the posts of the game's 1.9 m net mouth.
    public const double CreaseScale = 0.8;

    public static double ZOfTexel(int i, int size) => ((i + 0.5) / (double)size - 0.5) * FrameLength;
    public static double XOfTexel(int j, int size) => (0.5 - (j + 0.5) / (double)size) * FrameWidth;
    public static int TexelI(double z, int size) => Math.Clamp((int)((0.5 + z / FrameLength) * size), 0, size - 1);
    public static int TexelJ(double x, int size) => Math.Clamp((int)((0.5 - x / FrameWidth) * size), 0, size - 1);

    /// <summary>
    /// Finds the rink in a layout picture: the inner edge of the dark board outline at both ends (median over
    /// rows where the end boards are straight), a side-board width check, then a refinement that locates the
    /// red goal lines near where the boards put them and rescales so they land on the game's. Falls back to
    /// "picture width == rink length" when no outline is found.
    /// </summary>
    public static RinkMeasure Measure(RgbaImage image)
    {
        var w = image.Width;
        var h = image.Height;
        var px = image.Pixels;

        int Sum(int x, int y)
        {
            var o = ((long)y * w + x) * 4;
            return px[o + 3] < 128 ? 765 : px[o] + px[o + 1] + px[o + 2];
        }

        // From the picture's edge inward: the first dark run (the board outline), then the first light pixel (ice).
        int? Inner(int count, int start, int step, Func<int, int> at)
        {
            var seenDark = false;
            for (int i = start, n = 0; n < count / 3; i += step, n++)
            {
                var s = at(i);
                if (s < 3 * 110) seenDark = true;
                else if (seenDark && s > 3 * 170) return i;
            }
            return null;
        }

        static double? Median(IEnumerable<int?> values)
        {
            var list = values.Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
            return list.Count >= 3 ? list[list.Count / 2] : null;
        }

        // The straight part of the end boards, away from nets and creases.
        var rows = new[] { 0.40, 0.43, 0.45, 0.55, 0.57, 0.60 }.Select(f => (int)(f * h)).ToArray();
        var left = Median(rows.Select(y => Inner(w, 0, 1, x => Sum(x, y))));
        var right = Median(rows.Select(y => Inner(w, w - 1, -1, x => Sum(x, y))));
        if (left is not { } l || right is not { } r || r - l <= 0.85 * w)
            return new RinkMeasure(w / 2.0, h / 2.0, w / IceLength,
                "no board outline found; the picture's width is taken as the rink", Found: false);

        var length = r - l + 1;
        var scale = length / IceLength;
        var columns = new[] { 0.42, 0.45, 0.55, 0.58 }.Select(f => (int)(l + f * length)).ToArray();
        var top = Median(columns.Select(x => Inner(h, 0, 1, y => Sum(x, y))));
        var bottom = Median(columns.Select(x => Inner(h, h - 1, -1, y => Sum(x, y))));
        var centreY = top is { } t && bottom is { } b ? (t + b + 1) / 2.0 : h / 2.0;
        var widthOk = top is { } t1 && bottom is { } b1 && Math.Abs((b1 - t1 + 1) / scale - IceWidth) < 2.5;
        var check = top is { } t2 && bottom is { } b2 ? $", side boards {(b2 - t2 + 1) / scale:F2} m apart (game {IceWidth})" : "";
        var centreX = (l + r + 1) / 2.0;
        var how = $"end boards at x {l} and {r}{check}";

        // The goal lines decide: drawings differ by centimetres in line placement, but the game's nets are fixed.
        bool Red(int x, int y)
        {
            var o = ((long)y * w + x) * 4;
            return px[o + 3] >= 128 && px[o] - px[o + 1] > 60 && px[o] - px[o + 2] > 40;
        }

        double? GoalLineAt(int side, int y)
        {
            var expected = centreX + side * GoalLine * scale;
            var reach = (int)(0.8 * scale);
            double? best = null;
            for (var x = (int)(expected - reach); x <= expected + reach; x++)
            {
                if (x < 0 || x >= w || !Red(x, y) || (x > 0 && Red(x - 1, y)))
                    continue;
                var end = x;
                while (end + 1 < w && Red(end + 1, y)) end++;
                var mid = (x + end) / 2.0;
                if (end - x < 0.25 * scale && (best == null || Math.Abs(mid - expected) < Math.Abs(best.Value - expected)))
                    best = mid;
            }
            return best;
        }

        static double? MedianOf(List<double> values)
        {
            values.Sort();
            return values.Count >= 3 ? values[values.Count / 2] : null;
        }

        // Rows clear of the creases, trapezoids and faceoff circles, offset across the rink in metres.
        var goalRows = new[] { -10.0, -9.0, -2.1, 2.1, 9.0, 10.0 }
            .Select(m => (int)Math.Round(centreY + m * scale)).Where(y => y >= 0 && y < h).ToList();
        var leftGoal = MedianOf(goalRows.Select(y => GoalLineAt(-1, y)).Where(v => v.HasValue).Select(v => v!.Value).ToList());
        var rightGoal = MedianOf(goalRows.Select(y => GoalLineAt(1, y)).Where(v => v.HasValue).Select(v => v!.Value).ToList());
        if (leftGoal is { } lg && rightGoal is { } rg)
        {
            var goalScale = (rg - lg) / (2 * GoalLine);
            if (Math.Abs(goalScale / scale - 1) < 0.03)
            {
                var boards = (r - l + 1) / goalScale / 2;
                return new RinkMeasure((lg + rg) / 2.0, centreY, goalScale,
                    $"goal lines at x {lg:F0} and {rg:F0} (boards then at +-{boards:F2} m){check}", widthOk);
            }
        }
        return new RinkMeasure(centreX, centreY, scale, how + ", goal lines not found", widthOk);
    }

    /// <summary>Resamples the source into the square frame. Filtering runs in linear light; alpha is set opaque.</summary>
    public static RgbaImage Fit(RgbaImage source, FitMode mode, int size, RinkMeasure? measure = null)
    {
        var output = new RgbaImage(size, size);
        double cx, cy, stepX, stepY;
        if (mode == FitMode.Template)
        {
            cx = source.Width / 2.0;
            cy = source.Height / 2.0;
            stepX = (double)source.Width / size;
            stepY = (double)source.Height / size;
        }
        else
        {
            measure ??= Measure(source);
            cx = measure.CentreX;
            cy = measure.CentreY;
            stepX = FrameLength / size * measure.PixelsPerMetre;
            stepY = FrameWidth / size * measure.PixelsPerMetre;
        }

        var samplesX = Math.Clamp((int)Math.Ceiling(stepX), 1, 8);
        var samplesY = Math.Clamp((int)Math.Ceiling(stepY), 1, 8);
        var src = source;
        Parallel.For(0, size, j =>
        {
            Span<double> sum = stackalloc double[3];
            for (var i = 0; i < size; i++)
            {
                sum.Clear();
                for (var b = 0; b < samplesY; b++)
                {
                    var sy = cy + (j - size / 2.0 + (b + 0.5) / samplesY) * stepY;
                    for (var a = 0; a < samplesX; a++)
                    {
                        var sx = cx + (i - size / 2.0 + (a + 0.5) / samplesX) * stepX;
                        src.SampleLinearBilinear(sx - 0.5, sy - 0.5, sum);
                    }
                }
                var o = ((long)j * size + i) * 4;
                var n = samplesX * samplesY;
                for (var c = 0; c < 3; c++)
                    output.Pixels[o + c] = Srgb.FromLinearToByte((float)(sum[c] / n));
                output.Pixels[o + 3] = 255;
            }
        });
        return output;
    }

    /// <summary>
    /// Layout pictures draw the nets in grey behind the goal lines; under the game's own 3D nets they show as
    /// ghosts. Unsaturated texels darker than the local ice in a box behind each goal line become the ice colour.
    /// </summary>
    public static void HideDrawnNets(RgbaImage texture)
    {
        var size = texture.Width;
        var p = texture.Pixels;
        int U(double z) => (int)Math.Round((0.5 + z / FrameLength) * size);
        int V(double x) => (int)Math.Round((0.5 - x / FrameWidth) * size);

        foreach (var side in new[] { -1, 1 })
        {
            int u0 = U(side * (GoalLine + 0.06)), u1 = U(side * (GoalLine + 1.5));
            int iceU0 = U(side * (GoalLine + 1.7)), iceU1 = U(side * (GoalLine + 2.0));
            if (u0 > u1) (u0, u1) = (u1, u0);
            if (iceU0 > iceU1) (iceU0, iceU1) = (iceU1, iceU0);
            int v0 = V(1.3), v1 = V(-1.3);

            // Local ice colour, averaged in linear light from a strip just past the net box.
            double r = 0, g = 0, b = 0;
            var n = 0;
            for (var j = v0; j <= v1; j++)
                for (var i = iceU0; i <= iceU1; i++)
                {
                    var o = ((long)j * size + i) * 4;
                    if (p[o] + p[o + 1] + p[o + 2] < 600) continue;
                    r += Srgb.ByteToLinearF(p[o]);
                    g += Srgb.ByteToLinearF(p[o + 1]);
                    b += Srgb.ByteToLinearF(p[o + 2]);
                    n++;
                }
            if (n == 0) continue;
            var ir = Srgb.FromLinearToByte((float)(r / n));
            var ig = Srgb.FromLinearToByte((float)(g / n));
            var ib = Srgb.FromLinearToByte((float)(b / n));

            for (var j = v0; j <= v1; j++)
                for (var i = u0; i <= u1; i++)
                {
                    var o = ((long)j * size + i) * 4;
                    int cr = p[o], cg = p[o + 1], cb = p[o + 2];
                    var spread = Math.Max(cr, Math.Max(cg, cb)) - Math.Min(cr, Math.Min(cg, cb));
                    if (spread < 30 && cr + cg + cb < ir + ig + ib - 12)
                    {
                        p[o] = ir; p[o + 1] = ig; p[o + 2] = ib;
                    }
                }
        }
    }

    /// <summary>
    /// Shrinks the crease area in front of each goal line round the goal-line middle, filling the freed ice
    /// with the surrounding ice colour, so a rule-book crease does not reach past the game's goal posts.
    /// </summary>
    public static void ShrinkCreases(RgbaImage texture, double scale = CreaseScale)
    {
        if (scale >= 0.999) return;
        var size = texture.Width;
        const double depth = 2.1, half = 1.45, keep = 0.06; // the box in front of the goal line, and line half-width left alone
        var copy = (byte[])texture.Pixels.Clone();

        foreach (var side in new[] { -1, 1 })
        {
            // Ice colour beside the box, averaged in linear light.
            double r = 0, g = 0, b = 0;
            var n = 0;
            int js = TexelJ(half + 0.4, size), je = TexelJ(half + 0.1, size);
            int ia = Math.Min(TexelI(side * (GoalLine - depth), size), TexelI(side * (GoalLine - 0.3), size));
            int ib = Math.Max(TexelI(side * (GoalLine - depth), size), TexelI(side * (GoalLine - 0.3), size));
            for (var j = js; j <= je; j++)
                for (var i = ia; i <= ib; i++)
                {
                    var o = ((long)j * size + i) * 4;
                    if (copy[o] + copy[o + 1] + copy[o + 2] < 600) continue;
                    r += Srgb.ByteToLinearF(copy[o]);
                    g += Srgb.ByteToLinearF(copy[o + 1]);
                    b += Srgb.ByteToLinearF(copy[o + 2]);
                    n++;
                }
            if (n == 0) continue;
            var ir = Srgb.FromLinearToByte((float)(r / n));
            var ig = Srgb.FromLinearToByte((float)(g / n));
            var ibc = Srgb.FromLinearToByte((float)(b / n));

            int i0 = Math.Min(TexelI(side * (GoalLine - depth), size), TexelI(side * (GoalLine - keep), size));
            int i1 = Math.Max(TexelI(side * (GoalLine - depth), size), TexelI(side * (GoalLine - keep), size));
            for (var j = TexelJ(half, size); j <= TexelJ(-half, size); j++)
                for (var i = i0; i <= i1; i++)
                {
                    var front = side * GoalLine - ZOfTexel(i, size); // metres in front of the goal line
                    var d = side * front;
                    if (d < keep || d > depth) continue;
                    var sd = d / scale;
                    var sx = XOfTexel(j, size) / scale;
                    var o = ((long)j * size + i) * 4;
                    if (sd > depth || Math.Abs(sx) > half)
                    {
                        texture.Pixels[o] = ir; texture.Pixels[o + 1] = ig; texture.Pixels[o + 2] = ibc;
                        continue;
                    }
                    var so = ((long)TexelJ(sx, size) * size + TexelI(side * (GoalLine - sd), size)) * 4;
                    texture.Pixels[o] = copy[so];
                    texture.Pixels[o + 1] = copy[so + 1];
                    texture.Pixels[o + 2] = copy[so + 2];
                }
        }
    }
}
