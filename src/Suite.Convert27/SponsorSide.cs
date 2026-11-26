using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// Chest sponsors belong on the player's right chest (the left half of the jersey texture): Boston's Rapid7, Utah home's
/// SME Steel and every other NHL 27 kit with one. Utah's NHL 27 away (thirteen) paints its 1Password patch on the other
/// side. When a finished jersey's chest sponsor sits only on the right half, it is moved (not flipped) to the mirrored
/// spot on the left half in the colour, normal and coefficient maps, and the plain jersey is put back where it was.
/// </summary>
public static class SponsorSide
{
    // The chest-sponsor band on the finished jersey texture, away from the centre (collar shield / crest).
    const double Top = 0.57, Bottom = 0.635, Inner = 0.53, Outer = 0.70;

    /// <summary>The sponsor's box on the right chest (pixels), or null when there is none or one already on the left.</summary>
    public static (int X1, int Y1, int X2, int Y2)? RightChestSponsor(Rgba finished, Rgba plain)
    {
        int w = finished.Width, h = finished.Height;
        bool Logo(int x, int y)
        {
            int at = (y * w + x) * 4;
            return Math.Abs(finished.Pixels[at] - plain.Pixels[at]) + Math.Abs(finished.Pixels[at + 1] - plain.Pixels[at + 1])
                + Math.Abs(finished.Pixels[at + 2] - plain.Pixels[at + 2]) > 90;
        }
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = -1, y2 = -1, left = 0, right = 0;
        for (int y = (int)(h * Top); y < (int)(h * Bottom); y++)
        for (int x = (int)(w * (1 - Outer)); x < (int)(w * Outer); x++)
        {
            if (!Logo(x, y)) continue;
            if (x < w * (1 - Inner)) { left++; continue; }
            if (x <= w * Inner) continue;
            x1 = Math.Min(x1, x); y1 = Math.Min(y1, y); x2 = Math.Max(x2, x); y2 = Math.Max(y2, y);
            right++;
        }
        // A few stray pixels (Detroit's wing tips) are not a sponsor patch.
        if (x2 < 0 || left > 0 || right < w * h / 20000) return null;
        int pad = Math.Max(2, w / 256);
        return (Math.Max(0, x1 - pad), Math.Max(0, y1 - pad), Math.Min(w - 1, x2 + pad), Math.Min(h - 1, y2 + pad));
    }

    /// <summary>The finished map with the box moved to the mirrored x position and the plain map restored under it.</summary>
    public static Rgba Move(Rgba finished, Rgba plain, (int X1, int Y1, int X2, int Y2) box)
    {
        int w = finished.Width;
        var output = (byte[])finished.Pixels.Clone();
        int width = box.X2 - box.X1 + 1, newX1 = w - 1 - box.X2;
        for (int y = box.Y1; y <= box.Y2; y++)
        {
            Array.Copy(plain.Pixels, (y * w + box.X1) * 4, output, (y * w + box.X1) * 4, width * 4);
            Array.Copy(finished.Pixels, (y * w + box.X1) * 4, output, (y * w + newX1) * 4, width * 4);
        }
        return new Rgba(w, finished.Height, output);
    }

    /// <summary>Moves a right-chest sponsor on the jersey's finished maps (already set as the base slots).</summary>
    public static bool Apply(NewUniformAddition addition, IReadOnlyDictionary<string, string> plainByKind, Action<string>? say = null)
    {
        static string Leaf(string s) => s[(s.LastIndexOf('/') + 1)..];
        NewUniformTexture? Base(string kind) => addition.Textures.FirstOrDefault(t => Leaf(t.SourceSlot).StartsWith("jersey_", StringComparison.Ordinal)
            && Leaf(t.SourceSlot).EndsWith("_base_" + kind, StringComparison.Ordinal));
        var color = Base("color");
        if (color is null || !plainByKind.TryGetValue("color", out var plainColor) || !File.Exists(plainColor)) return false;
        var box = RightChestSponsor(ImageIO.Load(color.Artwork.Source), ImageIO.Load(plainColor));
        if (box is null) return false;
        string dir = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "sponsor-side"));
        foreach (string kind in new[] { "color", "normal", "coeff" })
        {
            var t = Base(kind);
            if (t is null || !plainByKind.TryGetValue(kind, out var plain) || !File.Exists(plain)) continue;
            string png = Path.Combine(dir, Path.GetFileName(t.Artwork.Source));
            ImageIO.SavePng(Move(ImageIO.Load(t.Artwork.Source), ImageIO.Load(plain), box.Value), png);
            t.Artwork = t.Artwork with { Source = png };
        }
        say?.Invoke($"{addition.Team} {addition.Kit}: chest sponsor was on the player's left; moved to the right chest like every other kit");
        return true;
    }
}
