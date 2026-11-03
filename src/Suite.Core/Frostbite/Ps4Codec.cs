using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace Suite.Core.Frostbite;

/// <summary>
/// Picks the block codec for an NHL 24 texture resource and decodes/encodes one PS4-tiled mip. Format byte @12 of the
/// 132-byte header: 0x3C/0x3D BC3 (portraits, branding colour/coeff layers), 0x3F BC5 (normals), 0x43 BC7 (center ice);
/// 8-byte blocks are BC1 (jersey colour sheets). Arrays keep their slice count as a u16 @26; the chunk is mip-major:
/// mip 0 of every slice, then mip 1 of every slice, and so on (verified on a Boston branding array).
/// </summary>
public static class Ps4Codec
{
    public enum Kind { Bc1, Bc3, Bc5, Bc7 }

    public static Kind Of(byte[] header, int blockBytes) => blockBytes == 8 ? Kind.Bc1 : header[12] switch
    {
        0x3F or 0x40 => Kind.Bc5,
        0x43 or 0x44 => Kind.Bc7,
        _ => Kind.Bc3,
    };

    public static int Slices(byte[] header) => Math.Max(1, (int)BitConverter.ToUInt16(header, 26));

    public static Rgba Decode(Kind kind, byte[] tiled, int width, int height) => kind switch
    {
        Kind.Bc1 => Bc1.DecodeTiled(tiled, width, height),
        Kind.Bc5 => Bc5.DecodeTiled(tiled, width, height),
        Kind.Bc7 => Bc7.DecodeTiled(tiled, width, height),
        _ => DecodeBc3(tiled, width, height),
    };

    public static byte[] Encode(Kind kind, Rgba img) => kind switch
    {
        Kind.Bc1 => Bc1.EncodeTiled(img),
        Kind.Bc5 => Bc5.EncodeTiled(img),
        Kind.Bc7 => Bc7.EncodeTiled(img),
        _ => Bc3.EncodeTiled(img),
    };

    static Rgba DecodeBc3(byte[] tiled, int width, int height)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        var colors = new BcDecoder().DecodeRaw(Bc3.Untile(tiled, width, height), bw * 4, bh * 4, CompressionFormat.Bc3);
        var px = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var c = colors[y * bw * 4 + x]; int p = (y * width + x) * 4;
                px[p] = c.r; px[p + 1] = c.g; px[p + 2] = c.b; px[p + 3] = c.a;
            }
        return new Rgba(width, height, px);
    }

    /// <summary>Byte offset of <paramref name="slice"/>'s <paramref name="mip"/> in a mip-major chunk.</summary>
    public static int Offset(TextureRes res, int slices, int slice, int mip) =>
        res.MipSizes.Take(mip).Sum() * slices + slice * res.MipSizes[mip];

    /// <summary>Mip 0 of every slice, for previews and read-back.</summary>
    public static List<Rgba> DecodeSlices(TextureRes res, byte[] chunk)
    {
        var kind = Of(res.Header, res.BlockBytes);
        int slices = Slices(res.Header);
        if (chunk.Length != res.PayloadSize * slices) throw new InvalidDataException($"chunk is {chunk.Length} bytes, {res.PayloadSize} x {slices} slices expected");
        return Enumerable.Range(0, slices).Select(s => Decode(kind, chunk.AsSpan(Offset(res, slices, s, 0), res.MipSizes[0]).ToArray(), res.Width, res.Height)).ToList();
    }

    /// <summary>
    /// A new chunk: the given slices replaced by full mip chains built from their images (resized to the slot), every
    /// other slice copied byte for byte from <paramref name="stock"/>. Same length as stock.
    /// </summary>
    public static byte[] Build(TextureRes res, byte[] stock, IReadOnlyDictionary<int, Rgba> slices)
    {
        var kind = Of(res.Header, res.BlockBytes);
        int count = Slices(res.Header);
        if (stock.Length != res.PayloadSize * count) throw new InvalidDataException($"stock chunk is {stock.Length} bytes, {res.PayloadSize} x {count} expected");
        var output = (byte[])stock.Clone();
        foreach (var (slice, image) in slices)
        {
            if (slice < 0 || slice >= count) throw new ArgumentOutOfRangeException(nameof(slices), $"layer {slice} does not exist (the slot has {count}).");
            var img = image.Width == res.Width && image.Height == res.Height ? image : ImageIO.ResizeSmooth(image, res.Width, res.Height);
            for (int i = 0; i < res.MipCount; i++)
            {
                var mip = Encode(kind, img);
                if (mip.Length != res.MipSizes[i]) throw new InvalidDataException($"mip {i}: {mip.Length} bytes, the slot has {res.MipSizes[i]}");
                mip.CopyTo(output, Offset(res, count, slice, i));
                if (i + 1 < res.MipCount) img = TextureChunk.Halve(img);
            }
        }
        return output;
    }
}
