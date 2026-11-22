namespace RinkSharp;

/// <summary>sRGB &lt;-&gt; linear conversions. All filtering and averaging in the pipeline runs in linear
/// light; sRGB bytes are only a storage/interchange form. This is what keeps mips from fading:
/// averaging sRGB values underweights the bright end and drags saturated markings toward the ice colour.</summary>
public static class Srgb
{
    // Byte -> linear LUT: exact, and the per-pixel cost becomes an array lookup.
    private static readonly float[] ByteToLinear = new float[256];

    static Srgb()
    {
        for (var i = 0; i < 256; i++)
            ByteToLinear[i] = ToLinear(i / 255.0f);
    }

    public static float ToLinear(float v) =>
        v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    public static float FromLinear(float v)
    {
        var s = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
        return Math.Clamp(s, 0f, 1f);
    }

    public static byte FromLinearToByte(float v) => (byte)(FromLinear(v) * 255f + 0.5f);

    public static float ByteToLinearF(byte b) => ByteToLinear[b];
}

/// <summary>An sRGB 8-bit RGBA image.</summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != (long)width * height * 4)
            throw new ArgumentException("Pixel buffer size does not match dimensions.");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public RgbaImage(int width, int height) : this(width, height, new byte[(long)width * height * 4]) { }

    /// <summary>Bilinear sample at floating-point position, in linear light, accumulated into sum[0..2].</summary>
    public void SampleLinearBilinear(double x, double y, Span<double> sum)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        var fx = x - x0;
        var fy = y - y0;
        var p = Pixels;
        for (var c = 0; c < 3; c++)
        {
            var top = Srgb.ByteToLinearF(p[((long)y0 * Width + x0) * 4 + c]) * (1 - fx)
                    + Srgb.ByteToLinearF(p[((long)y0 * Width + x1) * 4 + c]) * fx;
            var bottom = Srgb.ByteToLinearF(p[((long)y1 * Width + x0) * 4 + c]) * (1 - fx)
                       + Srgb.ByteToLinearF(p[((long)y1 * Width + x1) * 4 + c]) * fx;
            sum[c] += top * (1 - fy) + bottom * fy;
        }
    }
}
