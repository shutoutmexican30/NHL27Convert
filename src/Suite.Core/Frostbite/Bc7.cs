using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace Suite.Core.Frostbite;

/// <summary>
/// BC7 in the PS4 1D-thin tiled layout (same 16-byte blocks and tiling as BC3; see <see cref="Bc3.TiledIndex"/>).
/// NHL 24's center-ice logos are BC7 (resource format 0x43). Encoding and decoding of the blocks is BCnEncoder's.
/// </summary>
public static class Bc7
{
    public static Rgba DecodeTiled(byte[] tiled, int width, int height)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        var linear = Bc3.Untile(tiled, width, height);
        var colors = new BcDecoder().DecodeRaw(linear, bw * 4, bh * 4, CompressionFormat.Bc7);
        var px = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var c = colors[y * bw * 4 + x]; int p = (y * width + x) * 4;
                px[p] = c.r; px[p + 1] = c.g; px[p + 2] = c.b; px[p + 3] = c.a;
            }
        return new Rgba(width, height, px);
    }

    public static byte[] EncodeTiled(Rgba img)
    {
        int bw = (img.Width + 3) / 4, bh = (img.Height + 3) / 4, pw = (bw + 7) / 8 * 8, ph = (bh + 7) / 8 * 8;
        // Pad to whole blocks by repeating the edge pixels.
        int w = bw * 4, h = bh * 4;
        var padded = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.Pixels.AsSpan((Math.Min(y, img.Height - 1) * img.Width + Math.Min(x, img.Width - 1)) * 4, 4).CopyTo(padded.AsSpan((y * w + x) * 4));
        var enc = new BcEncoder(CompressionFormat.Bc7);
        enc.OutputOptions.GenerateMipMaps = false;
        enc.OutputOptions.Quality = CompressionQuality.Balanced;
        var linear = enc.EncodeToRawBytes(padded, w, h, PixelFormat.Rgba32)[0];
        var tiled = new byte[pw * ph * 16];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
                linear.AsSpan((y * bw + x) * 16, 16).CopyTo(tiled.AsSpan(Bc3.TiledIndex(x, y, pw) * 16));
        return tiled;
    }
}
