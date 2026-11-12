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
                // Tighten the other axis.
                int a0 = int.MaxValue, a1 = -1;
                for (int k = start; k < i; k++)
                for (int j = vertical ? x0 : y0; j < (vertical ? x1 : y1); j++)
                    if (vertical ? Ink(img, j, k) : Ink(img, k, j)) { a0 = Math.Min(a0, j); a1 = Math.Max(a1, j); }
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

    /// <summary>A sheet in NHL 24's layout drawn from <paramref name="source"/> (colour, normal or coefficient map).</summary>
    public static Rgba Apply(Rgba source, Rgba layoutBase, IReadOnlyList<(Box From, Box To)> pairs, Rgba? fill = null)
    {
        int w = layoutBase.Width, h = layoutBase.Height;
        var output = fill is null ? new byte[w * h * 4] : (byte[])fill.Pixels.Clone();
        foreach (var (from, to) in pairs)
        {
            // Clear the NHL 24 glyph, then draw the NHL 27 one scaled into its box.
            for (int y = to.Y1; y <= to.Y2; y++)
            for (int x = to.X1; x <= to.X2; x++)
            {
                int sx = from.X1 + (int)((x - to.X1 + 0.5) * from.Width / to.Width);
                int sy = from.Y1 + (int)((y - to.Y1 + 0.5) * from.Height / to.Height);
                sx = Math.Clamp(sx, 0, source.Width - 1); sy = Math.Clamp(sy, 0, source.Height - 1);
                Array.Copy(source.Pixels, (sy * source.Width + sx) * 4, output, (y * w + x) * 4, 4);
            }
        }
        return new Rgba(w, h, output);
    }
}
