namespace Suite.Core.Frostbite;

/// <summary>
/// BC3 (DXT5) encoder plus the PS4 1D-thin tiling NHL 24 stores its UI textures in: 4x4 blocks, padded to a multiple
/// of 8 blocks in both directions, laid out in 8x8-block micro tiles row by row, Morton (Z) order inside a tile.
/// Verified against a stock portrait (all 10,880 blocks of p5309).
/// </summary>
public static class Bc3
{
    /// <summary>The tiled BC3 data (single mip) for an RGBA image, as the game stores it.</summary>
    public static byte[] EncodeTiled(Rgba img, int workers = 0)
    {
        int bw = (img.Width + 3) / 4, bh = (img.Height + 3) / 4;
        int pw = (bw + 7) / 8 * 8, ph = (bh + 7) / 8 * 8;
        var linear = new byte[bw * bh * 16];
        CpuParallel.For(0, bh, workers, by =>
        {
            Span<byte> px = stackalloc byte[64];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, img.Width - 1), sy = Math.Min(by * 4 + y, img.Height - 1);
                        img.Pixels.AsSpan((sy * img.Width + sx) * 4, 4).CopyTo(px.Slice((y * 4 + x) * 4));
                    }
                EncodeBlock(px, linear.AsSpan((by * bw + bx) * 16, 16));
            }
        });
        var tiled = new byte[pw * ph * 16];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
                linear.AsSpan((y * bw + x) * 16, 16).CopyTo(tiled.AsSpan(TiledIndex(x, y, pw) * 16));
        return tiled;
    }

    /// <summary>Block (x, y) → its index in the 1D-thin tiled layout (pw = padded width in blocks).</summary>
    public static int TiledIndex(int x, int y, int pw)
    {
        int tile = y / 8 * (pw / 8) + x / 8;
        int mx = x & 7, my = y & 7, m = 0;
        for (int i = 0; i < 3; i++) m |= ((mx >> i) & 1) << (2 * i) | ((my >> i) & 1) << (2 * i + 1);
        return tile * 64 + m;
    }

    /// <summary>Untiles BC3 data back to linear block order (for read-back checks).</summary>
    public static byte[] Untile(byte[] tiled, int width, int height)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4, pw = (bw + 7) / 8 * 8;
        var linear = new byte[bw * bh * 16];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
                tiled.AsSpan(TiledIndex(x, y, pw) * 16, 16).CopyTo(linear.AsSpan((y * bw + x) * 16));
        return linear;
    }

    /// <summary>One 4x4 block (16 RGBA pixels) → 16 bytes of BC3.</summary>
    public static void EncodeBlock(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        // Alpha: 8-value ramp between the block's max and min.
        byte a0 = 0, a1 = 255;
        for (int i = 0; i < 16; i++) { var a = px[i * 4 + 3]; if (a > a0) a0 = a; if (a < a1) a1 = a; }
        dst[0] = a0; dst[1] = a1;
        ulong abits = 0;
        if (a0 > a1)
        {
            Span<int> ramp = stackalloc int[8];
            ramp[0] = a0; ramp[1] = a1;
            for (int i = 1; i <= 6; i++) ramp[i + 1] = ((7 - i) * a0 + i * a1) / 7;
            for (int i = 0; i < 16; i++)
            {
                int a = px[i * 4 + 3], best = 0, bestD = int.MaxValue;
                for (int k = 0; k < 8; k++) { int d = Math.Abs(ramp[k] - a); if (d < bestD) { bestD = d; best = k; } }
                abits |= (ulong)best << (3 * i);
            }
        }
        for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(abits >> (8 * i));

        // Colour: endpoints along the principal axis of the visible pixels, then the nearest of the 4 colours.
        Span<double> c = stackalloc double[48];
        int n = 0;
        for (int i = 0; i < 16; i++)
            if (px[i * 4 + 3] > 8 || a0 <= 8) { c[n * 3] = px[i * 4]; c[n * 3 + 1] = px[i * 4 + 1]; c[n * 3 + 2] = px[i * 4 + 2]; n++; }
        double mr = 0, mg = 0, mb = 0;
        for (int i = 0; i < n; i++) { mr += c[i * 3]; mg += c[i * 3 + 1]; mb += c[i * 3 + 2]; }
        mr /= n; mg /= n; mb /= n;
        double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        for (int i = 0; i < n; i++)
        {
            double r = c[i * 3] - mr, g = c[i * 3 + 1] - mg, b = c[i * 3 + 2] - mb;
            xx += r * r; xy += r * g; xz += r * b; yy += g * g; yz += g * b; zz += b * b;
        }
        double vx = 1, vy = 1, vz = 1;
        for (int it = 0; it < 8; it++)
        {
            double nx = xx * vx + xy * vy + xz * vz, ny = xy * vx + yy * vy + yz * vz, nz = xz * vx + yz * vy + zz * vz;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9) break;
            vx = nx / len; vy = ny / len; vz = nz / len;
        }
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double t = (c[i * 3] - mr) * vx + (c[i * 3 + 1] - mg) * vy + (c[i * 3 + 2] - mb) * vz;
            if (t < lo) lo = t; if (t > hi) hi = t;
        }
        ushort e0 = To565(mr + vx * hi, mg + vy * hi, mb + vz * hi), e1 = To565(mr + vx * lo, mg + vy * lo, mb + vz * lo);
        if (e0 < e1) (e0, e1) = (e1, e0);
        dst[8] = (byte)e0; dst[9] = (byte)(e0 >> 8); dst[10] = (byte)e1; dst[11] = (byte)(e1 >> 8);
        uint cbits = 0;
        if (e0 != e1)
        {
            Span<int> pal = stackalloc int[12];
            From565(e0, pal[0..3]); From565(e1, pal[3..6]);
            for (int k = 0; k < 3; k++) { pal[6 + k] = (2 * pal[k] + pal[3 + k]) / 3; pal[9 + k] = (pal[k] + 2 * pal[3 + k]) / 3; }
            for (int i = 0; i < 16; i++)
            {
                int best = 0, bestD = int.MaxValue;
                for (int k = 0; k < 4; k++)
                {
                    int dr = pal[k * 3] - px[i * 4], dg = pal[k * 3 + 1] - px[i * 4 + 1], db = pal[k * 3 + 2] - px[i * 4 + 2];
                    int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                    if (d < bestD) { bestD = d; best = k; }
                }
                cbits |= (uint)best << (2 * i);
            }
        }
        dst[12] = (byte)cbits; dst[13] = (byte)(cbits >> 8); dst[14] = (byte)(cbits >> 16); dst[15] = (byte)(cbits >> 24);
    }

    /// <summary>Decode tiled BC3 into RGBA (alpha from the BC4 half of each block).</summary>
    public static Rgba DecodeTiled(byte[] tiled, int width, int height)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4, pw = (bw + 7) / 8 * 8, ph = (bh + 7) / 8 * 8;
        if (tiled.Length < pw * ph * 16)
            throw new InvalidDataException($"BC3 data is {tiled.Length} bytes, {pw * ph * 16} expected for {width}x{height}.");
        var px = new byte[width * height * 4];
        Parallel.For(0, bh, by =>
        {
            Span<int> pal = stackalloc int[12];
            for (int bx = 0; bx < bw; bx++)
            {
                var block = tiled.AsSpan(TiledIndex(bx, by, pw) * 16, 16);
                // alpha: BC4 ramp over bytes 0..7
                int a0 = block[0], a1 = block[1];
                ulong abits = 0;
                for (int i = 0; i < 6; i++) abits |= (ulong)block[2 + i] << (8 * i);
                Span<int> aramp = stackalloc int[8];
                aramp[0] = a0; aramp[1] = a1;
                if (a0 > a1) for (int i = 1; i <= 6; i++) aramp[i + 1] = ((7 - i) * a0 + i * a1) / 7;
                else { aramp[2] = (4 * a0 + a1) / 5; aramp[3] = (3 * a0 + 2 * a1) / 5; aramp[4] = (2 * a0 + 3 * a1) / 5; aramp[5] = (a0 + 4 * a1) / 5; aramp[6] = 0; aramp[7] = 255; }
                // colour: BC1 565 endpoints over bytes 8..15
                ushort e0 = (ushort)(block[8] | block[9] << 8), e1 = (ushort)(block[10] | block[11] << 8);
                From565(e0, pal[0..3]); From565(e1, pal[3..6]);
                if (e0 > e1)
                    for (int k = 0; k < 3; k++) { pal[6 + k] = (2 * pal[k] + pal[3 + k]) / 3; pal[9 + k] = (pal[k] + 2 * pal[3 + k]) / 3; }
                else
                    for (int k = 0; k < 3; k++) { pal[6 + k] = (pal[k] + pal[3 + k]) / 2; pal[9 + k] = 0; }
                uint cbits = (uint)(block[12] | block[13] << 8 | block[14] << 16 | block[15] << 24);
                for (int i = 0; i < 16; i++)
                {
                    int sx = bx * 4 + i % 4, sy = by * 4 + i / 4;
                    if (sx >= width || sy >= height) continue;
                    int d = (sy * width + sx) * 4;
                    int ci = (int)(cbits >> (2 * i)) & 3;
                    px[d] = (byte)pal[ci * 3]; px[d + 1] = (byte)pal[ci * 3 + 1]; px[d + 2] = (byte)pal[ci * 3 + 2];
                    px[d + 3] = (byte)aramp[(int)(abits >> (3 * i)) & 7];
                }
            }
        });
        return new Rgba(width, height, px);
    }

    static ushort To565(double r, double g, double b)
    {
        int R = (int)Math.Round(Math.Clamp(r, 0, 255) * 31 / 255), G = (int)Math.Round(Math.Clamp(g, 0, 255) * 63 / 255), B = (int)Math.Round(Math.Clamp(b, 0, 255) * 31 / 255);
        return (ushort)(R << 11 | G << 5 | B);
    }

    static void From565(ushort v, Span<int> rgb)
    {
        rgb[0] = (v >> 11 & 31) * 255 / 31; rgb[1] = (v >> 5 & 63) * 255 / 63; rgb[2] = (v & 31) * 255 / 31;
    }
}
