namespace Suite.Core.Frostbite;

/// <summary>
/// BC1 (DXT1) with the same PS4 1D-thin tiling as <see cref="Bc3"/>, but 8-byte blocks: the block index from
/// <see cref="Bc3.TiledIndex"/> is multiplied by 8 instead of 16. The jersey colour map is stored this way (Boston home:
/// 2,798,592 bytes for 2048x2048 + 11 mips, i.e. half a byte per pixel). Opaque four-colour mode only; the three-colour
/// punch-through variant (e0 &lt;= e1) is decoded for completeness but never produced.
/// </summary>
public static class Bc1
{
    public const int BlockBytes = 8;

    public static Rgba DecodeTiled(byte[] tiled, int width, int height)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4, pw = (bw + 7) / 8 * 8, ph = (bh + 7) / 8 * 8;
        if (tiled.Length < pw * ph * BlockBytes)
            throw new InvalidDataException($"BC1 data is {tiled.Length} bytes, {pw * ph * BlockBytes} expected for {width}x{height}.");
        var px = new byte[width * height * 4];
        Parallel.For(0, bh, by =>
        {
            Span<int> pal = stackalloc int[12];
            Span<int> alpha = stackalloc int[4];
            for (int bx = 0; bx < bw; bx++)
            {
                int o = Bc3.TiledIndex(bx, by, pw) * BlockBytes;
                DecodePalette(tiled.AsSpan(o, 8), pal, alpha);
                uint bits = BitConverter.ToUInt32(tiled, o + 4);
                for (int i = 0; i < 16; i++)
                {
                    int sx = bx * 4 + i % 4, sy = by * 4 + i / 4;
                    if (sx >= width || sy >= height) continue;
                    int k = (int)((bits >> (2 * i)) & 3);
                    int d = (sy * width + sx) * 4;
                    px[d] = (byte)pal[k * 3];
                    px[d + 1] = (byte)pal[k * 3 + 1];
                    px[d + 2] = (byte)pal[k * 3 + 2];
                    px[d + 3] = (byte)alpha[k];
                }
            }
        });
        return new Rgba(width, height, px);
    }

    static void DecodePalette(ReadOnlySpan<byte> src, Span<int> pal, Span<int> alpha)
    {
        ushort e0 = BitConverter.ToUInt16(src), e1 = BitConverter.ToUInt16(src[2..]);
        From565(e0, pal[0..3]);
        From565(e1, pal[3..6]);
        if (e0 > e1)
        {
            for (int k = 0; k < 3; k++)
            {
                pal[6 + k] = (2 * pal[k] + pal[3 + k]) / 3;
                pal[9 + k] = (pal[k] + 2 * pal[3 + k]) / 3;
            }
            alpha[0] = alpha[1] = alpha[2] = alpha[3] = 255;
        }
        else
        {
            for (int k = 0; k < 3; k++) pal[6 + k] = (pal[k] + pal[3 + k]) / 2;
            pal[9] = pal[10] = pal[11] = 0;
            alpha[0] = alpha[1] = alpha[2] = 255;
            alpha[3] = 0;
        }
    }

    /// <summary>Encode RGBA into tiled BC1, four-colour opaque mode. Size is exactly width*height/2 bytes.</summary>
    public static byte[] EncodeTiled(Rgba img)
    {
        int bw = (img.Width + 3) / 4, bh = (img.Height + 3) / 4, pw = (bw + 7) / 8 * 8, ph = (bh + 7) / 8 * 8;
        var tiled = new byte[pw * ph * BlockBytes];
        Parallel.For(0, bh, by =>
        {
            Span<byte> block = stackalloc byte[64];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, img.Width - 1), sy = Math.Min(by * 4 + y, img.Height - 1);
                        img.Pixels.AsSpan((sy * img.Width + sx) * 4, 4).CopyTo(block.Slice((y * 4 + x) * 4));
                    }
                EncodeBlock(block, tiled.AsSpan(Bc3.TiledIndex(bx, by, pw) * BlockBytes, BlockBytes));
            }
        });
        return tiled;
    }

    /// <summary>One 4x4 block of RGBA pixels into 8 bytes of BC1.</summary>
    public static void EncodeBlock(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        // endpoints along the principal axis of the block, then the nearest of the four colours
        Span<double> c = stackalloc double[48];
        int n = 0;
        for (int i = 0; i < 16; i++)
        {
            c[n * 3] = px[i * 4]; c[n * 3 + 1] = px[i * 4 + 1]; c[n * 3 + 2] = px[i * 4 + 2]; n++;
        }
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
            if (t < lo) lo = t;
            if (t > hi) hi = t;
        }
        ushort e0 = To565(mr + vx * hi, mg + vy * hi, mb + vz * hi);
        ushort e1 = To565(mr + vx * lo, mg + vy * lo, mb + vz * lo);
        if (e0 < e1) (e0, e1) = (e1, e0);
        if (e0 == e1 && e1 > 0) e1--;
        BitConverter.TryWriteBytes(dst, e0);
        BitConverter.TryWriteBytes(dst[2..], e1);
        Span<int> pal = stackalloc int[12];
        From565(e0, pal[0..3]);
        From565(e1, pal[3..6]);
        for (int k = 0; k < 3; k++)
        {
            pal[6 + k] = (2 * pal[k] + pal[3 + k]) / 3;
            pal[9 + k] = (pal[k] + 2 * pal[3 + k]) / 3;
        }
        uint bits = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 0, bestD = int.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                int dr = pal[k * 3] - px[i * 4], dg = pal[k * 3 + 1] - px[i * 4 + 1], db = pal[k * 3 + 2] - px[i * 4 + 2];
                int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                if (d < bestD) { bestD = d; best = k; }
            }
            bits |= (uint)best << (2 * i);
        }
        BitConverter.TryWriteBytes(dst[4..], bits);
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