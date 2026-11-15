using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// Puts NHL 27's numbers and letters into NHL 24's font-sheet layout. Both games use one sheet per kit with the same
/// rows (A–O, P–Z, big 0–4 and 5–9, the captain's C and A, two rows of small digits), but NHL 27 places them slightly
/// differently and adds ' . - glyphs, so its sheet copied as-is comes out clipped (Detroit's C). Each glyph is cut from
/// NHL 27's sheet and fitted into the matching NHL 24 glyph box. When the glyph counts of a row do not match, the
/// team's own NHL 24 sheet is kept instead (<see cref="Remap"/> returns null).
/// </summary>
public static class FontRemap
{
    // Row bands as fractions of the sheet: (top, bottom, left, right). The captain's letters sit in their own column.
    static readonly (double Top, double Bottom, double Left, double Right)[] Bands =
    [
        (0.00, 0.095, 0.0, 1.0), (0.095, 0.19, 0.0, 0.84),
        (0.20, 0.46, 0.0, 0.84), (0.46, 0.72, 0.0, 0.84),
        (0.095, 0.62, 0.84, 1.0),
        (0.72, 0.86, 0.0, 1.0), (0.86, 1.0, 0.0, 1.0),
    ];

    public static int BandCount => Bands.Length;

    /// <summary>A band's pixel rectangle: (x0, y0, x1, y1), end-exclusive.</summary>
    public static (int X0, int Y0, int X1, int Y1) BandRect(Rgba img, int band)
    {
        var (t, b, l, r) = Bands[band];
        return ((int)(img.Width * l), (int)(img.Height * t), Math.Min(img.Width, (int)(img.Width * r)), Math.Min(img.Height, (int)(img.Height * b)));
    }

    public sealed record Box(int X1, int Y1, int X2, int Y2)
    {
        public int Width => X2 - X1 + 1;
        public int Height => Y2 - Y1 + 1;
    }

    static bool Ink(Rgba img, int x, int y) => img.Pixels[(y * img.Width + x) * 4 + 3] > 24;

    /// <summary>The glyph boxes in one band, left to right (top to bottom for the captain column), small marks dropped.</summary>
    public static List<Box> Glyphs(Rgba img, int band)
    {
        var (t, b, l, r) = Bands[band];
        int w = img.Width, h = img.Height, y0 = (int)(h * t), y1 = Math.Min(h, (int)(h * b)), x0 = (int)(w * l), x1 = Math.Min(w, (int)(w * r));
        bool vertical = band == 4;
        if (vertical)
        {
            // The captain column's left edge can cut through the right edge of the big digits beside it (Edmonton's
            // NHL 27 sheet), which then counts as part of the C or A; move it to the nearest empty column.
            x0 = EmptyColumn(img, x0, y0, y1);
        }
        if (!vertical)
        {
            // A fixed band edge can cut through the bottom of the row above (Florida's big digits end at 46.1%): that
            // row's last pixels then count as the top of this row's glyphs, the NHL 27 glyphs are stretched up to touch
            // the row above, and the game shows a gold sliver of the next glyph under the "1". Edges move to the
            // nearest empty row.
            y0 = EmptyRow(img, y0, x0, x1);
            y1 = EmptyRow(img, y1, x0, x1);
        }
        var boxes = new List<Box>();
        int outer0 = vertical ? y0 : x0, outer1 = vertical ? y1 : x1;
        int start = -1;
        for (int i = outer0; i <= outer1; i++)
        {
            bool any = false;
            if (i < outer1)
                for (int j = vertical ? x0 : y0; j < (vertical ? x1 : y1) && !any; j++)
                    any = vertical ? Ink(img, j, i) : Ink(img, i, j);
            if (any && start < 0) start = i;
            if (!any && start >= 0)
            {
                // Tighten the other axis to the longest unbroken run of ink: a sliver of the row above or below that
                // reaches into this band (Florida's 0-4 end inside the 5-9 band) is not part of the glyph.
                int a0 = int.MaxValue, a1 = -1, runStart = -1;
                for (int j = vertical ? x0 : y0; j <= (vertical ? x1 : y1); j++)
                {
                    bool any2 = false;
                    if (j < (vertical ? x1 : y1))
                        for (int k = start; k < i && !any2; k++) any2 = vertical ? Ink(img, j, k) : Ink(img, k, j);
                    if (any2 && runStart < 0) runStart = j;
                    if (!any2 && runStart >= 0)
                    {
                        if (j - 1 - runStart > a1 - a0) { a0 = runStart; a1 = j - 1; }
                        runStart = -1;
                    }
                }
                var box = vertical ? new Box(a0, start, a1, i - 1) : new Box(start, a0, i - 1, a1);
                boxes.Add(box);
                start = -1;
            }
        }
        if (boxes.Count == 0) return boxes;
        // Punctuation and stray marks are much shorter than the band's glyphs.
        int tallest = boxes.Max(x => vertical ? x.Width : x.Height);
        var kept = boxes.Where(x => (vertical ? x.Width : x.Height) >= tallest * 0.6).ToList();
        // Thin marks (an apostrophe, the ¨ beside Detroit's Z row) are also much narrower than a letter.
        var across = kept.Select(x => vertical ? x.Height : x.Width).Order().ToList();
        int median = across.Count == 0 ? 0 : across[across.Count / 2];
        return kept.Where(x => (vertical ? x.Height : x.Width) >= median * 0.25).ToList();
    }

    /// <summary>The empty column (no ink between y0 and y1) nearest to x, within 3% of the sheet; x itself when none.</summary>
    static int EmptyColumn(Rgba img, int x, int y0, int y1)
    {
        int reach = Math.Max(1, img.Width * 3 / 100);
        for (int d = 0; d <= reach; d++)
            foreach (int c in new[] { x + d, x - d })
            {
                if (c <= 0 || c >= img.Width) continue;
                bool ink = false;
                for (int y = y0; y < y1 && !ink; y++) ink = Ink(img, c, y);
                if (!ink) return c;
            }
        return x;
    }

    /// <summary>The empty row (no ink between x0 and x1) nearest to y, within 3% of the sheet; y itself when none.</summary>
    static int EmptyRow(Rgba img, int y, int x0, int x1)
    {
        if (y <= 0 || y >= img.Height) return y;
        int reach = Math.Max(1, img.Height * 3 / 100);
        for (int d = 0; d <= reach; d++)
            foreach (int c in new[] { y + d, y - d })
            {
                if (c <= 0 || c >= img.Height) continue;
                bool ink = false;
                for (int x = x0; x < x1 && !ink; x++) ink = Ink(img, x, c);
                if (!ink) return c;
            }
        return y;
    }

    /// <summary>Each NHL 27 glyph fitted into the matching NHL 24 box; null when any band's glyph count differs.</summary>
    public static List<(Box From, Box To)>? Map(Rgba nhl27, Rgba nhl24)
    {
        var pairs = new List<(Box, Box)>();
        for (int band = 0; band < Bands.Length; band++)
        {
            var from = Glyphs(nhl27, band);
            var to = Glyphs(nhl24, band);
            if (from.Count != to.Count || from.Count == 0) return null;
            pairs.AddRange(from.Zip(to));
        }
        return pairs;
    }

    /// <summary>
    /// Problems of a remapped sheet against the NHL 24 layout it was fitted into: every band must have the same glyph
    /// count, and every glyph must stay inside its NHL 24 cell (one pixel of slack). A sliver of a neighbouring glyph
    /// (Florida's gold line under the "1") or a glyph drawn past its stitching shows up as an extra or larger box.
    /// </summary>
    public static List<string> Verify(Rgba remapped, Rgba layout24)
    {
        var problems = new List<string>();
        for (int band = 0; band < Bands.Length; band++)
        {
            var got = Glyphs(remapped, band);
            var want = Glyphs(layout24, band);
            if (got.Count != want.Count) { problems.Add($"band {band}: {got.Count} glyphs, NHL 24 has {want.Count}"); continue; }
            for (int i = 0; i < got.Count; i++)
            {
                var g = got[i]; var w = want[i];
                if (g.X1 < w.X1 - 1 || g.Y1 < w.Y1 - 1 || g.X2 > w.X2 + 1 || g.Y2 > w.Y2 + 1)
                    problems.Add($"band {band} glyph {i}: ({g.X1},{g.Y1})-({g.X2},{g.Y2}) outside its cell ({w.X1},{w.Y1})-({w.X2},{w.Y2})");
            }
        }
        return problems;
    }

    /// <summary>A sheet in NHL 24's layout drawn from <paramref name="source"/> (colour, normal or coefficient map).</summary>
    public static Rgba Apply(Rgba source, Rgba layoutBase, IReadOnlyList<(Box From, Box To)> pairs, Rgba? fill = null, int? mappedWidth = null)
    {
        int w = layoutBase.Width, h = layoutBase.Height;
        // The boxes come from the sheet the glyphs were found on; NHL 27's normal sheet is half the size of its colour
        // sheet (1024 vs 2048), so its source boxes are scaled to it.
        double scale = (double)source.Width / (mappedWidth ?? source.Width);
        var output = fill is null ? new byte[w * h * 4] : (byte[])fill.Pixels.Clone();
        foreach (var (from, to) in pairs)
        {
            // Clear the NHL 24 glyph, then draw the NHL 27 one scaled into its box.
            for (int y = to.Y1; y <= to.Y2; y++)
            for (int x = to.X1; x <= to.X2; x++)
            {
                int sx = (int)((from.X1 + (x - to.X1 + 0.5) * from.Width / to.Width) * scale);
                int sy = (int)((from.Y1 + (y - to.Y1 + 0.5) * from.Height / to.Height) * scale);
                sx = Math.Clamp(sx, 0, source.Width - 1); sy = Math.Clamp(sy, 0, source.Height - 1);
                Array.Copy(source.Pixels, (sy * source.Width + sx) * 4, output, (y * w + x) * 4, 4);
            }
        }
        // NHL 24 captain artwork outside the C/A cells (a shadow strip left of Edmonton's and Ottawa's A) is not part of
        // any glyph cell; clear it so nothing from the old sheet remains next to the new letters.
        var (cx0, cy0, cx1, cy1) = BandRect(layoutBase, 4);
        var captainCells = pairs.Where(p => p.To.X2 >= cx0 && p.To.X1 < cx1 && p.To.Y2 >= cy0 && p.To.Y1 < cy1).Select(p => p.To).ToList();
        if (captainCells.Count > 0)
        {
            // The sheet's empty value: its most common pixel (transparent on a colour sheet, flat blue on a normal sheet).
            var counts = new Dictionary<uint, int>();
            bool anyClear = false;
            for (int i = 3; i < layoutBase.Pixels.Length && !anyClear; i += 4 * 7) anyClear = layoutBase.Pixels[i] < 128;
            for (int i = 0; i < layoutBase.Pixels.Length; i += 4 * 7)
            {
                if (anyClear && layoutBase.Pixels[i + 3] >= 128) continue; // a colour sheet's empty value is transparent
                uint v = BitConverter.ToUInt32(layoutBase.Pixels, i);
                counts[v] = counts.GetValueOrDefault(v) + 1;
            }
            byte[] background = BitConverter.GetBytes(counts.MaxBy(kv => kv.Value).Key);
            for (int y = cy0; y < cy1; y++)
            for (int x = cx0; x < cx1; x++)
                if (!captainCells.Any(b => x >= b.X1 - 1 && x <= b.X2 + 1 && y >= b.Y1 - 1 && y <= b.Y2 + 1))
                    Array.Copy(background, 0, output, (y * w + x) * 4, 4);
        }
        return new Rgba(w, h, output);
    }
}
