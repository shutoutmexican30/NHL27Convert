namespace Suite.Core.Frostbite;

/// <summary>
/// The 132-byte texture resource header plus the mip payload of one top-level TOC texture chunk. The header is carried
/// through unchanged; the chunk file itself holds only the mip chain, back to back, no padding.
/// Layout read from Boston NHL 24 home: width/height are two little-endian u16 at 22/24, the mip count is byte 31, the
/// chunk GUID is at 32..47 and fifteen mip sizes start at 48.
/// </summary>
public sealed class TextureRes
{
    public const int HeaderSize = 132;
    public byte[] Header { get; }
    public int Width { get; }
    public int Height { get; }
    public int MipCount { get; }
    public IReadOnlyList<int> MipSizes { get; }
    public Guid ChunkGuid { get; }

    TextureRes(byte[] header, int width, int height, int mipCount, IReadOnlyList<int> mipSizes, Guid guid)
    {
        Header = header; Width = width; Height = height; MipCount = mipCount; MipSizes = mipSizes; ChunkGuid = guid;
    }

    public static TextureRes Parse(byte[] header)
    {
        if (header.Length < HeaderSize) throw new InvalidDataException($"resource header is {header.Length} bytes, {HeaderSize} expected");
        int width = BitConverter.ToUInt16(header, 22), height = BitConverter.ToUInt16(header, 24);
        int mips = header[30];
        if (width == 0 || height == 0) throw new InvalidDataException($"resource header has no size ({width}x{height})");
        if (mips is < 1 or > 15) throw new InvalidDataException($"resource header has an implausible mip count ({mips})");
        var sizes = new int[mips];
        for (int i = 0; i < mips; i++) sizes[i] = (int)BitConverter.ToUInt32(header, 48 + i * 4);
        var guid = new Guid(header.AsSpan(32, 16));
        var res = new TextureRes(header, width, height, mips, sizes, guid);
        res.ValidateSizes();
        return res;
    }

    /// <summary>
    /// Bytes per 4x4 block, inferred from mip 0: 16 for BC3/BC5 (1 byte per pixel), 8 for BC1 (half a byte per pixel).
    /// Every lower mip must scale the same way, which is what makes the size table trustworthy.
    /// </summary>
    public int BlockBytes { get; private set; }

    void ValidateSizes()
    {
        int w = Width, h = Height;
        long perPixel = (long)MipSizes[0] * 16 / ((long)w * h);
        BlockBytes = perPixel switch
        {
            16 => 16,
            8 => 8,
            _ => throw new InvalidDataException($"mip 0 is {MipSizes[0]} bytes for {w}x{h}: not a BC1/BC3/BC5 layout"),
        };
        string format = BlockBytes == 16 ? "BC3/BC5" : "BC1";
        for (int i = 0; i < MipCount; i++)
        {
            int expected = MipBytes(w, h, BlockBytes);
            if (MipSizes[i] != expected)
                throw new InvalidDataException($"mip {i} is {MipSizes[i]} bytes, {expected} expected for {w}x{h} {format}");
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
    }

    public long PayloadSize => MipSizes.Sum(s => (long)s);

    /// <summary>
    /// Bytes a mip occupies: blocks are padded to a multiple of 8 in both directions (the 8x8 micro-tile grid), so
    /// anything below 32x32 still occupies one whole tile - which is why the small mips are all 1024 bytes.
    /// </summary>
    public static int MipBytes(int width, int height, int blockBytes)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        int pw = (bw + 7) / 8 * 8, ph = (bh + 7) / 8 * 8;
        return pw * ph * blockBytes;
    }
}

/// <summary>One mip chain: parsed, rebuilt and encoded. Never writes to a game.</summary>
public sealed class TextureChunk
{
    public TextureRes Res { get; }
    public IReadOnlyList<byte[]> Mips { get; }
    public byte[] Data { get; }

    TextureChunk(TextureRes res, IReadOnlyList<byte[]> mips)
    {
        Res = res; Mips = mips;
        Data = new byte[res.PayloadSize];
        int o = 0;
        foreach (var m in mips) { Buffer.BlockCopy(m, 0, Data, o, m.Length); o += m.Length; }
    }

    public static TextureChunk Parse(byte[] chunk, TextureRes res)
    {
        if (chunk.Length != res.PayloadSize)
            throw new InvalidDataException($"chunk is {chunk.Length} bytes, {res.PayloadSize} expected from the header");
        var mips = new List<byte[]>(res.MipCount);
        int o = 0;
        for (int i = 0; i < res.MipCount; i++)
        {
            var m = new byte[res.MipSizes[i]];
            Buffer.BlockCopy(chunk, o, m, 0, m.Length);
            mips.Add(m);
            o += m.Length;
        }
        return new TextureChunk(res, mips);
    }

    /// <summary>Builds a chunk from replacement mips; every size and the total must match the header exactly.</summary>
    public static TextureChunk Build(TextureRes res, IReadOnlyList<byte[]> mips)
    {
        if (mips.Count != res.MipCount) throw new InvalidDataException($"{mips.Count} mips supplied, {res.MipCount} in the header");
        for (int i = 0; i < mips.Count; i++)
            if (mips[i].Length != res.MipSizes[i])
                throw new InvalidDataException($"mip {i} is {mips[i].Length} bytes, {res.MipSizes[i]} required");
        return new TextureChunk(res, mips);
    }

    /// <summary>Encodes a full mip chain from one image (BC1 or BC5 by block size): level 0 as given, each level below box-downsampled by two.</summary>
    public static List<byte[]> EncodeChain(TextureRes res, Rgba image, int workers = 0)
    {
        if (image.Width != res.Width || image.Height != res.Height)
            throw new InvalidDataException($"image is {image.Width}x{image.Height}, the slot needs {res.Width}x{res.Height}");
        var mips = new List<byte[]>(res.MipCount);
        var current = image;
        for (int i = 0; i < res.MipCount; i++)
        {
            mips.Add(res.BlockBytes == 8 ? Bc1.EncodeTiled(current, workers) : Bc5.EncodeTiled(current, workers));
            if (i + 1 < res.MipCount) current = Halve(current, workers);
        }
        return mips;
    }

    /// <summary>Box filter to half size, always at least 1x1.</summary>
    public static Rgba Halve(Rgba src, int workers = 0)
    {
        int w = Math.Max(1, src.Width / 2), h = Math.Max(1, src.Height / 2);
        var px = new byte[w * h * 4];
        CpuParallel.For(0, h, workers, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0, n = 0;
                    for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = Math.Min(src.Width - 1, x * 2 + dx), sy = Math.Min(src.Height - 1, y * 2 + dy);
                        sum += src.Pixels[(sy * src.Width + sx) * 4 + c];
                        n++;
                    }
                    px[p + c] = (byte)(sum / Math.Max(1, n));
                }
            }
        });
        return new Rgba(w, h, px);
    }
}