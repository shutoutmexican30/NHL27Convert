namespace RinkSharp;

/// <summary>
/// Color grade for the fitted rink frame, applied in linear light: exposure (darken/brighten), contrast about
/// mid grey, and saturation. Defaults are off (1.0); the in-game look is light and washed, so a mild recipe is
/// around --darken 0.92 --contrast 1.12 --saturation 1.25.
/// </summary>
public sealed record Grade(double Exposure = 1.0, double Contrast = 1.0, double Saturation = 1.0)
{
    public bool IsIdentity => Exposure == 1.0 && Contrast == 1.0 && Saturation == 1.0;
}

public static class Grading
{
    const double Pivot = 0.21404114; // sRGB 0.5 in linear

    public static RgbaImage Apply(RgbaImage image, Grade g)
    {
        if (g.IsIdentity) return image;
        var p = image.Pixels;
        Parallel.For(0, image.Height, y =>
        {
            for (var x = 0; x < image.Width; x++)
            {
                var o = ((long)y * image.Width + x) * 4;
                var r = Srgb.ByteToLinearF(p[o]) * g.Exposure;
                var gr = Srgb.ByteToLinearF(p[o + 1]) * g.Exposure;
                var b = Srgb.ByteToLinearF(p[o + 2]) * g.Exposure;
                r = Pivot + (r - Pivot) * g.Contrast;
                b = Pivot + (b - Pivot) * g.Contrast;
                gr = Pivot + (gr - Pivot) * g.Contrast;
                var luma = 0.2126 * r + 0.7152 * gr + 0.0722 * b;
                r = luma + (r - luma) * g.Saturation;
                gr = luma + (gr - luma) * g.Saturation;
                b = luma + (b - luma) * g.Saturation;
                p[o] = Srgb.FromLinearToByte((float)Math.Clamp(r, 0, 1));
                p[o + 1] = Srgb.FromLinearToByte((float)Math.Clamp(gr, 0, 1));
                p[o + 2] = Srgb.FromLinearToByte((float)Math.Clamp(b, 0, 1));
            }
        });
        return image;
    }
}
