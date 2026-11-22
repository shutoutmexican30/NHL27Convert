using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace RinkSharp;

/// <summary>
/// BC7 encoding with the color pipeline kept explicit: the RGBA bytes handed to the encoder are sRGB-encoded
/// texel values, exactly what a texture flagged sRGB must store (the GPU applies sRGB->linear on sample).
/// The encoder is left at ColorSpaceBehavior.None so it does not convert; all linear-space work happened in
/// Fit/MipChain already.
/// </summary>
public static class Bc7
{
    public static byte[] Encode(RgbaImage image, CompressionQuality quality)
    {
        var encoder = new BcEncoder
        {
            OutputOptions =
            {
                Format = CompressionFormat.Bc7,
                GenerateMipMaps = false,
                Quality = quality,
            },
            Options = { IsParallel = true },
        };
        var mips = encoder.EncodeToRawBytes(image.Pixels, image.Width, image.Height, PixelFormat.Rgba32);
        return mips[0];
    }
}

/// <summary>Minimal DDS writer/reader for a BC7 sRGB 2D texture with a full mip chain (DX10 header).</summary>
public static class Dds
{
    public const int Bc7UnormSrgb = 99; // DXGI_FORMAT_BC7_UNORM_SRGB

    public static void Write(string path, int width, int height, IReadOnlyList<byte[]> mips)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);

        var linearSize = Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16;
        w.Write(new byte[] { (byte)'D', (byte)'D', (byte)'S', (byte)' ' });
        w.Write(124);                                             // header size
        w.Write(0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);   // caps|height|width|pixelformat|mipcount|linearsize
        w.Write(height);
        w.Write(width);
        w.Write(linearSize);
        w.Write(0);                                               // depth
        w.Write(mips.Count);
        for (var i = 0; i < 11; i++) w.Write(0);                  // reserved
        w.Write(32);                                              // pixel format size
        w.Write(4);                                               // FOURCC
        w.Write(new byte[] { (byte)'D', (byte)'X', (byte)'1', (byte)'0', 0, 0, 0, 0 });
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);           // RGB bit masks
        w.Write(0x1000 | 0x8 | 0x400000);                         // caps: TEXTURE | MIPMAP | COMPLEX
        w.Write(0); w.Write(0); w.Write(0);                       // caps2/3/4
        w.Write(0);                                               // reserved2
        // DX10 extension
        w.Write(Bc7UnormSrgb);
        w.Write(3);                                               // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        w.Write(0);                                               // misc flag
        w.Write(1);                                               // array size
        w.Write(0);                                               // misc flags 2

        foreach (var mip in mips)
            w.Write(mip);
    }

    public static (int Width, int Height, List<byte[]> Mips) Read(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs);
        var magic = r.ReadBytes(4);
        if (magic[0] != 'D' || magic[1] != 'D' || magic[2] != 'S' || magic[3] != ' ')
            throw new InvalidDataException("Not a DDS file.");
        r.ReadInt32();                                            // header size
        r.ReadInt32();                                            // flags
        var height = r.ReadInt32();
        var width = r.ReadInt32();
        r.ReadInt32();                                            // linear size
        r.ReadInt32();                                            // depth
        var mipCount = r.ReadInt32();
        r.ReadBytes(11 * 4);                                      // reserved
        r.ReadInt32(); r.ReadInt32();                             // pf size, flags
        var fourcc = r.ReadBytes(4);
        if (fourcc[0] != 'D' || fourcc[1] != 'X' || fourcc[2] != '1' || fourcc[3] != '0')
            throw new InvalidDataException("Not a DX10 DDS.");
        r.ReadBytes(5 * 4);                                       // RGB bit count + 4 masks
        r.ReadBytes(5 * 4);                                       // caps, caps2, caps3, caps4, reserved2
        var format = r.ReadInt32();
        if (format != Bc7UnormSrgb)
            throw new InvalidDataException($"Unexpected DXGI format {format}, expected BC7 sRGB ({Bc7UnormSrgb}).");
        r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32();

        var mips = new List<byte[]>(mipCount);
        for (var level = 0; level < mipCount; level++)
        {
            var mw = Math.Max(1, width >> level);
            var mh = Math.Max(1, height >> level);
            var bytes = Math.Max(1, (mw + 3) / 4) * Math.Max(1, (mh + 3) / 4) * 16;
            mips.Add(r.ReadBytes(bytes));
        }
        return (width, height, mips);
    }
}
