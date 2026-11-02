namespace Suite.Core.Frostbite;

/// <summary>
/// BC5 (two BC4 channels, red and green) in the PS4 1D-thin tiling NHL 24 stores jersey maps in. A BC5 block is 16 bytes
/// exactly like BC3, so the block layout is <see cref="Bc3.TiledIndex"/>: 8x8-block micro tiles, Morton order inside a
/// tile. Blue is reconstructed as sqrt(1 - x^2 - y^2) for read-back and previews, never stored.
/// </summary>
public static class Bc5
{
    /// <summary>Decode tiled BC5 into RGBA (blue reconstructed, alpha opaque).</summary>
    public static Rgba DecodeTiled(byte[] tiled, int width, int height)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4, pw = (bw + 7) / 8 * 8, ph = (bh + 7) / 8 * 8;
        if (tiled.Length < pw * ph * 16)
            throw new InvalidDataException($"BC5 data is {tiled.Length} bytes, {pw * ph * 16} expected for {width}x{height}.");
        var px = new byte[width * height * 4];
        Parallel.For(0, bh, by =>
        {
            Span<int> rampR = stackalloc int[8], rampG = stackalloc int[8];
            Span<int> idxR = stackalloc int[16], idxG = stackalloc int[16];
            for (int bx = 0; bx < bw; bx++)
            {
                int o = Bc3.TiledIndex(bx, by, pw) * 16;
                var block = tiled.AsSpan(o, 16);
                Ramp(block[..8], rampR);
                Ramp(block.Slice(8, 8), rampG);
                Indices(block[..8], idxR);
                Indices(block.Slice(8, 8), idxG);
                for (int i = 0; i < 16; i++)
                {
                    int x = Math.Clamp(rampR[idxR[i]], 0, 255);
                    int y = Math.Clamp(rampG[idxG[i]], 0, 255);
                    double xf = x / 255.0, yf = y / 255.0;
                    double zz = 1.0 - xf * xf - yf * yf;
                    int z = zz <= 0 ? 0 : (int)Math.Round(Math.Sqrt(zz) * 255.0);
                    int sx = bx * 4 + i % 4, sy = by * 4 + i / 4;
                    if (sx >= width || sy >= height) continue;
                    int d = (sy * width + sx) * 4;
                    px[d] = (byte)x; px[d + 1] = (byte)y; px[d + 2] = (byte)Math.Clamp(z, 0, 255); px[d + 3] = 255;
                }
            }        });
        return new Rgba(width, height, px);
    }

    /// <summary>Encode RGBA into tiled BC5; only red and green are stored. Size is exactly width*height bytes.</summary>
    public static byte[] EncodeTiled(Rgba img)
    {
        int bw = (img.Width + 3) / 4, bh = (img.Height + 3) / 4, pw = (bw + 7) / 8 * 8, ph = (bh + 7) / 8 * 8;
        var tiled = new byte[pw * ph * 16];
        Parallel.For(0, bh, by =>
        {
            Span<byte> block = stackalloc byte[64];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, img.Width - 1), sy = Math.Min(by * 4 + y, img.Height - 1);
                        int s = (sy * img.Width + sx) * 4;
                        block[(y * 4 + x) * 4] = img.Pixels[s];
                        block[(y * 4 + x) * 4 + 1] = img.Pixels[s + 1];
                    }
                int o = Bc3.TiledIndex(bx, by, pw) * 16;
                EncodeChannel(block, 0, tiled.AsSpan(o, 8));
                EncodeChannel(block, 1, tiled.AsSpan(o + 8, 8));
            }
        });
        return tiled;
    }

    /// <summary>One BC4 channel of a 4x4 block: 8-value ramp between the block's extreme values.</summary>
    static void EncodeChannel(ReadOnlySpan<byte> px, int channel, Span<byte> dst)
    {
        int a0 = 0, a1 = 255;
        for (int i = 0; i < 16; i++)
        {
            int v = px[i * 4 + channel];
            if (v > a0) a0 = v;
            if (v < a1) a1 = v;
        }
        dst[0] = (byte)a0;
        dst[1] = (byte)a1;
        Span<int> ramp = stackalloc int[8];
        RampCore(a0, a1, ramp);
        ulong bits = 0;
        for (int i = 0; i < 16; i++)
        {
            int v = px[i * 4 + channel], best = 0, bestD = int.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                int d = Math.Abs(ramp[k] - v);
                if (d < bestD) { bestD = d; best = k; }
            }
            bits |= (ulong)best << (3 * i);
        }
        for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(bits >> (8 * i));
    }

    static void Ramp(ReadOnlySpan<byte> src, Span<int> ramp) => RampCore(src[0], src[1], ramp);

    static void RampCore(int a0, int a1, Span<int> ramp)
    {
        ramp[0] = a0;
        ramp[1] = a1;
        if (a0 > a1)
        {
            for (int i = 1; i <= 6; i++) ramp[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        }
        else
        {
            for (int i = 1; i <= 4; i++) ramp[i + 1] = ((5 - i) * a0 + i * a1) / 5;
            ramp[6] = 0;
            ramp[7] = 255;
        }
    }

    static void Indices(ReadOnlySpan<byte> src, Span<int> idx)
    {
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)src[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) idx[i] = (int)((bits >> (3 * i)) & 7);
    }
}