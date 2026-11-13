using System.Text.Json;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// Number/letter colours from NHL 27 on the team's NHL 24 font sheet. Kits keep the NHL 24 sheet byte for byte (its
/// glyph layout matches its stitch sheet; re-cutting NHL 27 glyphs into it misaligned Florida's digits), but some
/// teams changed colours: Ottawa's Fanatics home numbers are white with a red outline (NHL 24: black outline), its away
/// numbers black with a red outline and white/red sleeve digits (NHL 24: red with black). Per row of the sheet (letters,
/// big digits, captain letters, each small-digit row) the NHL 24 fill and outline colours are replaced by NHL 27's,
/// keeping each pixel's shading. The owner can force colours per kit in font_colours.json (user data folder):
/// { "ottawa home": { "fill": "#FFFFFF", "outline": "#C8102E" } }.
/// </summary>
public static class FontRecolor
{
    public readonly record struct Rgb(int R, int G, int B)
    {
        public int Distance(Rgb o) => Math.Abs(R - o.R) + Math.Abs(G - o.G) + Math.Abs(B - o.B);
        public static Rgb Parse(string hex)
        {
            hex = hex.TrimStart('#');
            return new Rgb(Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16));
        }
    }

    public sealed record Palette(Rgb Fill, Rgb Outline);

    /// <summary>Fill (most common opaque colour) and outline (most common colour clearly different from it) of a band.</summary>
    public static Palette? PaletteOf(Rgba img, int band)
    {
        var (x0, y0, x1, y1) = FontRemap.BandRect(img, band);
        var counts = new Dictionary<int, (int N, long R, long G, long B)>();
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            int at = (y * img.Width + x) * 4;
            if (img.Pixels[at + 3] < 200) continue;
            int key = (img.Pixels[at] >> 4) << 8 | (img.Pixels[at + 1] >> 4) << 4 | img.Pixels[at + 2] >> 4;
            var c = counts.GetValueOrDefault(key);
            counts[key] = (c.N + 1, c.R + img.Pixels[at], c.G + img.Pixels[at + 1], c.B + img.Pixels[at + 2]);
        }
        if (counts.Count == 0) return null;
        static Rgb Mean((int N, long R, long G, long B) c) => new((int)(c.R / c.N), (int)(c.G / c.N), (int)(c.B / c.N));
        var ordered = counts.Values.OrderByDescending(c => c.N).ToList();
        var fill = Mean(ordered[0]);
        var outline = ordered.Skip(1).Select(Mean).FirstOrDefault(c => c.Distance(fill) > 120, fill);
        return new Palette(fill, outline);
    }

    /// <summary>True when the two sheets' colours differ in any band (by more than shading).</summary>
    public static bool Differs(Rgba nhl24, Rgba nhl27)
    {
        for (int band = 0; band < FontRemap.BandCount; band++)
            if (PaletteOf(nhl24, band) is { } a && PaletteOf(nhl27, band) is { } b
                && (a.Fill.Distance(b.Fill) > 60 || a.Outline.Distance(b.Outline) > 60)) return true;
        return false;
    }

    /// <summary>The NHL 24 sheet with each band's fill/outline turned into the target band's colours.</summary>
    public static Rgba Recolor(Rgba nhl24, Func<int, Palette?> target)
    {
        var px = (byte[])nhl24.Pixels.Clone();
        for (int band = 0; band < FontRemap.BandCount; band++)
        {
            if (PaletteOf(nhl24, band) is not { } from || target(band) is not { } to) continue;
            var (x0, y0, x1, y1) = FontRemap.BandRect(nhl24, band);
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int at = (y * nhl24.Width + x) * 4;
                if (px[at + 3] == 0) continue;
                var c = new Rgb(nhl24.Pixels[at], nhl24.Pixels[at + 1], nhl24.Pixels[at + 2]);
                bool isFill = c.Distance(from.Fill) <= c.Distance(from.Outline);
                var (src, dst) = isFill ? (from.Fill, to.Fill) : (from.Outline, to.Outline);
                px[at] = (byte)Math.Clamp(c.R - src.R + dst.R, 0, 255);
                px[at + 1] = (byte)Math.Clamp(c.G - src.G + dst.G, 0, 255);
                px[at + 2] = (byte)Math.Clamp(c.B - src.B + dst.B, 0, 255);
            }
        }
        return new Rgba(nhl24.Width, nhl24.Height, px);
    }

    /// <summary>Bands of the sheet that hold numbers and the captain's letters (not the name letters).</summary>
    public static readonly int[] NumberBands = [2, 3, 4, 5, 6];

    /// <summary>The small sleeve-number rows.</summary>
    public static readonly int[] SleeveBands = [5, 6];

    /// <summary>
    /// The owner's number colours for a kit ("ottawa home"), per band, or null: "fill"/"outline" for the back numbers and
    /// captain letters, optional "sleeveFill"/"sleeveOutline" for the sleeve numbers (Ottawa's white away: black back
    /// numbers, white sleeve numbers, both outlined red). The file is font_colours.json in the user data folder.
    /// </summary>
    public static Func<int, Palette?>? Override(string kit)
    {
        string path = Path.Combine(SuitePaths.Roaming, "data", "font_colours.json");
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "data", "font_colours.json"); // seeded copy
        if (!File.Exists(path)) return null;
        var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path));
        return all is not null && all.TryGetValue(kit, out var c) ? Bands(c) : null;
    }

    public static Func<int, Palette?>? Bands(IReadOnlyDictionary<string, string> c)
    {
        if (!c.TryGetValue("fill", out var f) || !c.TryGetValue("outline", out var o)) return null;
        var main = new Palette(Rgb.Parse(f), Rgb.Parse(o));
        var sleeve = new Palette(Rgb.Parse(c.GetValueOrDefault("sleeveFill", f)), Rgb.Parse(c.GetValueOrDefault("sleeveOutline", o)));
        return band => !NumberBands.Contains(band) ? null : SleeveBands.Contains(band) ? sleeve : main;
    }
}
