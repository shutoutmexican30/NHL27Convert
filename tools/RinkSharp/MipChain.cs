namespace RinkSharp;

/// <summary>
/// Mip chain built by successive 2x2 averaging in linear light. This is the single biggest anti-fade measure:
/// averaging sRGB bytes underweights bright values, so every mip level of an sRGB-averaged chain loses a little
/// more contrast, and the game's trilinear sampling then shows progressively washed ice with distance.
/// </summary>
public static class MipChain
{
    public static List<RgbaImage> Build(RgbaImage top)
    {
        var chain = new List<RgbaImage> { top };
        var current = top;
        while (current.Width > 1 || current.Height > 1)
        {
            current = Half(current);
            chain.Add(current);
        }
        return chain;
    }

    private static RgbaImage Half(RgbaImage source)
    {
        var w = Math.Max(1, source.Width / 2);
        var h = Math.Max(1, source.Height / 2);
        var output = new RgbaImage(w, h);
        var src = source.Pixels;
        var dst = output.Pixels;

        Parallel.For(0, h, y =>
        {
            var sy0 = Math.Min(2 * y, source.Height - 1);
            var sy1 = Math.Min(2 * y + 1, source.Height - 1);
            for (var x = 0; x < w; x++)
            {
                var sx0 = Math.Min(2 * x, source.Width - 1);
                var sx1 = Math.Min(2 * x + 1, source.Width - 1);
                double r = 0, g = 0, b = 0, a = 0;
                for (var j = sy0; j <= sy1; j++)
                    for (var i = sx0; i <= sx1; i++)
                    {
                        var o = ((long)j * source.Width + i) * 4;
                        r += Srgb.ByteToLinearF(src[o]);
                        g += Srgb.ByteToLinearF(src[o + 1]);
                        b += Srgb.ByteToLinearF(src[o + 2]);
                        a += src[o + 3];
                    }
                var count = (sy1 - sy0 + 1) * (sx1 - sx0 + 1);
                var d = ((long)y * w + x) * 4;
                dst[d] = Srgb.FromLinearToByte((float)(r / count));
                dst[d + 1] = Srgb.FromLinearToByte((float)(g / count));
                dst[d + 2] = Srgb.FromLinearToByte((float)(b / count));
                dst[d + 3] = (byte)(a / count + 0.5);
            }
        });
        return output;
    }
}
