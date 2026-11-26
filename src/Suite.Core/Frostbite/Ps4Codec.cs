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
    public enum Kind { Bc1, Bc3, Bc5, Bc7, Bc4, Bc6 }

    /// <summary>
    /// 8-byte blocks: 0x36/0x37 BC1, 0x3E BC4 (one-channel maps: ambient, heightmap, sky visibility). 16-byte blocks: 0x3C/0x3D BC3, 0x3F BC5,
    /// 0x40 BC6H (HDR: irradiance, lrv, hdri), 0x42/0x43/0x44 BC7. Audited across every texture of the NHL 24 index.
    /// </summary>
    public static Kind Of(byte[] header, int blockBytes) => blockBytes == 8 ? header[12] == 0x3E ? Kind.Bc4 : Kind.Bc1 : header[12] switch
    {
        0x3F => Kind.Bc5,
        0x40 => Kind.Bc6,
        0x42 or 0x43 or 0x44 => Kind.Bc7,
        _ => Kind.Bc3,
    };

    public static int Slices(byte[] header) => Math.Max(1, (int)BitConverter.ToUInt16(header, 26));

    public static Rgba Decode(Kind kind, byte[] tiled, int width, int height) => kind switch
    {
        Kind.Bc1 => Bc1.DecodeTiled(tiled, width, height),
        Kind.Bc5 => Bc5.DecodeTiled(tiled, width, height),
        Kind.Bc7 => Bc7.DecodeTiled(tiled, width, height),
        Kind.Bc4 => DecodeBc4(tiled, width, height),
        Kind.Bc6 => DecodeBc6(tiled, width, height),
        _ => DecodeBc3(tiled, width, height),
    };

    public static byte[] Encode(Kind kind, Rgba img, int workers = 0) => kind switch
    {
        Kind.Bc1 => Bc1.EncodeTiled(img, workers),
        Kind.Bc5 => Bc5.EncodeTiled(img, workers),
        Kind.Bc7 => Bc7.EncodeTiled(img),
        Kind.Bc4 or Kind.Bc6 => throw new NotSupportedException($"{kind} textures can be read and previewed but not written; the suite has no encoder for them."),
        _ => Bc3.EncodeTiled(img, workers),
    };

    /// <summary>One-channel map: the value shown as grey.</summary>
    static Rgba DecodeBc4(byte[] tiled, int width, int height)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4, pw = (bw + 7) / 8 * 8, ph = (bh + 7) / 8 * 8;
        if (tiled.Length < pw * ph * 8) throw new InvalidDataException($"BC4 data is {tiled.Length} bytes, {pw * ph * 8} expected for {width}x{height}.");
        var linear = new byte[bw * bh * 8];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
                Buffer.BlockCopy(tiled, Bc3.TiledIndex(bx, by, pw) * 8, linear, (by * bw + bx) * 8, 8);
        var colors = new BcDecoder().DecodeRaw(linear, bw * 4, bh * 4, CompressionFormat.Bc4);
        var px = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte v = colors[y * bw * 4 + x].r; int p = (y * width + x) * 4;
                px[p] = px[p + 1] = px[p + 2] = v; px[p + 3] = 255;
            }
        return new Rgba(width, height, px);
    }

    /// <summary>HDR colour: clamped and gamma-encoded for a viewable preview.</summary>
    static Rgba DecodeBc6(byte[] tiled, int width, int height)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        var colors = new BcDecoder().DecodeRawHdr(Bc3.Untile(tiled, width, height), bw * 4, bh * 4, CompressionFormat.Bc6U);
        var px = new byte[width * height * 4];
        static byte Tone(float v) => (byte)Math.Round(Math.Pow(Math.Clamp(float.IsFinite(v) ? v : 0f, 0f, 1f), 1 / 2.2) * 255);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var c = colors[y * bw * 4 + x]; int p = (y * width + x) * 4;
                px[p] = Tone(c.r); px[p + 1] = Tone(c.g); px[p + 2] = Tone(c.b); px[p + 3] = 255;
            }
        return new Rgba(width, height, px);
    }

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
    public static byte[] Build(TextureRes res, byte[] stock, IReadOnlyDictionary<int, Rgba> slices, int workers = 0)
    {
        var kind = Of(res.Header, res.BlockBytes);
        int count = Slices(res.Header);
        if (stock.Length != res.PayloadSize * count) throw new InvalidDataException($"stock chunk is {stock.Length} bytes, {res.PayloadSize} x {count} expected");
        var output = (byte[])stock.Clone();
        foreach (var (slice, image) in slices)
        {
            if (slice < 0 || slice >= count) throw new ArgumentOutOfRangeException(nameof(slices), $"layer {slice} does not exist (the slot has {count}).");
            var img = image.Width == res.Width && image.Height == res.Height ? image : ImageIO.ResizeSmooth(image, res.Width, res.Height, workers);
            for (int i = 0; i < res.MipCount; i++)
            {
                var mip = Encode(kind, img, workers);
                if (mip.Length != res.MipSizes[i]) throw new InvalidDataException($"mip {i}: {mip.Length} bytes, the slot has {res.MipSizes[i]}");
                mip.CopyTo(output, Offset(res, count, slice, i));
                if (i + 1 < res.MipCount) img = TextureChunk.Halve(img, workers);
            }
        }
        return output;
    }

    /// <summary>
    /// Builds a deeper array from a structural donor while retaining the team's existing compressed slices byte for byte.
    /// Only the added slices are encoded from team artwork. The donor's pixels never supply a preserved team layer.
    /// </summary>
    public static byte[] BuildExpandedArray(TextureRes donor, byte[] donorStock, TextureRes team, byte[] teamStock,
        IReadOnlyDictionary<int, Rgba> addedSlices, int workers = 0)
    {
        if (donor.Width != team.Width || donor.Height != team.Height || donor.BlockBytes != team.BlockBytes
            || donor.MipCount != team.MipCount || !donor.MipSizes.SequenceEqual(team.MipSizes))
            throw new InvalidDataException("Array expansion requires identical dimensions, format and mip layout; choose another structural donor.");
        int oldCount = Slices(team.Header), newCount = Slices(donor.Header);
        if (newCount <= oldCount) throw new InvalidDataException($"Donor has {newCount} layers, team already has {oldCount}.");
        if (teamStock.Length != team.PayloadSize * oldCount || donorStock.Length != donor.PayloadSize * newCount)
            throw new InvalidDataException("An array chunk does not match its resource header.");
        foreach (int layer in Enumerable.Range(oldCount, newCount - oldCount))
            if (!addedSlices.ContainsKey(layer)) throw new InvalidDataException($"Added array layer {layer} has no team artwork.");
        if (addedSlices.Keys.Any(k => k < oldCount || k >= newCount))
            throw new InvalidDataException("Expansion can replace only newly added layers; existing team layers must be preserved.");

        var output = (byte[])donorStock.Clone();
        for (int slice = 0; slice < oldCount; slice++)
            for (int mip = 0; mip < team.MipCount; mip++)
                teamStock.AsSpan(Offset(team, oldCount, slice, mip), team.MipSizes[mip])
                    .CopyTo(output.AsSpan(Offset(donor, newCount, slice, mip), donor.MipSizes[mip]));
        return Build(donor, output, addedSlices, workers);
    }
}
