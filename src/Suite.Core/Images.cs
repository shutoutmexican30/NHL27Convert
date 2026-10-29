using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using StbImageSharp;
using StbImageWriteSharp;

namespace Suite.Core;

/// <summary>An 8-bit RGBA image, row-major, top row first.</summary>
public sealed class Rgba(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;

    /// <summary>Distinct alpha values with their pixel counts (fabric-region check on _base_color textures).</summary>
    public SortedDictionary<int, int> AlphaHistogram()
    {
        var counts = new int[256];
        for (int i = 3; i < Pixels.Length; i += 4) counts[Pixels[i]]++;
        var d = new SortedDictionary<int, int>();
        for (int a = 0; a < 256; a++) if (counts[a] > 0) d[a] = counts[a];
        return d;
    }
}

public static class ImageIO
{
    static readonly HashSet<string> Plain = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".psd", ".gif" };

    public static bool CanLoad(string path) =>
        Plain.Contains(Path.GetExtension(path)) || Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase);

    public static Rgba Load(string path)
    {
        if (Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase))
            return Dds.Load(path);
        var bytes = File.ReadAllBytes(path);
        var img = ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        return new Rgba(img.Width, img.Height, img.Data);
    }

    public static void SavePng(Rgba img, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
            new ImageWriter().WritePng(img.Pixels, img.Width, img.Height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, fs);
        File.Move(tmp, path, true);
    }

    /// <summary>Box-filter downscale, used for viewer previews (the viewer never needs more than 2048).</summary>
    public static Rgba Shrink(Rgba src, int maxSide)
    {
        int f = 1;
        while (Math.Max(src.Width, src.Height) / f > maxSide) f *= 2;
        if (f == 1) return src;
        int w = Math.Max(1, src.Width / f), h = Math.Max(1, src.Height / f);
        var dst = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int r = 0, g = 0, b = 0, a = 0;
                for (int dy = 0; dy < f; dy++)
                for (int dx = 0; dx < f; dx++)
                {
                    int i = ((y * f + dy) * src.Width + x * f + dx) * 4;
                    r += src.Pixels[i]; g += src.Pixels[i + 1]; b += src.Pixels[i + 2]; a += src.Pixels[i + 3];
                }
                int n = f * f, o = (y * w + x) * 4;
                dst[o] = (byte)(r / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(b / n);
                // Alpha on _base_color is a region index, so it is sampled, never averaged.
                dst[o + 3] = src.Pixels[((y * f) * src.Width + x * f) * 4 + 3];
            }
        });
        return new Rgba(w, h, dst);
    }
}

/// <summary>Minimal DDS reader for single-mip BC1/BC3/BC5/BC7 dumps (legacy and DX10 headers).</summary>
public static class Dds
{
    public static Rgba Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 128 || BitConverter.ToUInt32(bytes, 0) != 0x20534444)
            throw new InvalidDataException($"{path} is not a DDS file.");
        int height = BitConverter.ToInt32(bytes, 12);
        int width = BitConverter.ToInt32(bytes, 16);
        uint fourCc = BitConverter.ToUInt32(bytes, 84);
        int offset = 128;
        CompressionFormat format;
        if (fourCc == FourCc("DX10"))
        {
            uint dxgi = BitConverter.ToUInt32(bytes, 128);
            offset = 148;
            format = dxgi switch
            {
                70 or 71 or 72 => CompressionFormat.Bc1WithAlpha,
                76 or 77 or 78 => CompressionFormat.Bc3,
                82 or 83 or 84 => CompressionFormat.Bc5,
                79 or 80 or 81 => CompressionFormat.Bc4,
                97 or 98 or 99 => CompressionFormat.Bc7,
                87 or 88 or 91 or 93 => CompressionFormat.Bgra,
                28 or 29 => CompressionFormat.Rgba,
                _ => throw new NotSupportedException($"DXGI format {dxgi} in {path}"),
            };
        }
        else if (fourCc == FourCc("DXT1")) format = CompressionFormat.Bc1WithAlpha;
        else if (fourCc == FourCc("DXT5")) format = CompressionFormat.Bc3;
        else if (fourCc == FourCc("ATI2") || fourCc == FourCc("BC5U")) format = CompressionFormat.Bc5;
        else if (fourCc == FourCc("ATI1") || fourCc == FourCc("BC4U")) format = CompressionFormat.Bc4;
        else throw new NotSupportedException($"DDS fourCC 0x{fourCc:X8} in {path}");

        var data = bytes.AsSpan(offset).ToArray();
        var colors = new BcDecoder().DecodeRaw(data, width, height, format);
        var px = new byte[width * height * 4];
        for (int i = 0; i < colors.Length; i++)
        {
            var c = colors[i];
            px[i * 4] = c.r; px[i * 4 + 1] = c.g; px[i * 4 + 2] = c.b; px[i * 4 + 3] = c.a;
        }
        if (format == CompressionFormat.Bc5)
            for (int i = 0; i < colors.Length; i++) { px[i * 4 + 2] = 255; px[i * 4 + 3] = 255; }
        return new Rgba(width, height, px);
    }

    /// <summary>"BC3 · 2048×2048" from a DDS header, or null if the file is missing or not a DDS.</summary>
    public static string? Describe(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var h = new byte[148];
            if (fs.Read(h, 0, h.Length) < 128 || BitConverter.ToUInt32(h, 0) != 0x20534444) return null;
            int height = BitConverter.ToInt32(h, 12), width = BitConverter.ToInt32(h, 16);
            uint four = BitConverter.ToUInt32(h, 84);
            string fmt = four == FourCc("DX10")
                ? BitConverter.ToUInt32(h, 128) switch
                {
                    70 or 71 => "BC1", 72 => "BC1 sRGB", 76 or 77 => "BC3", 78 => "BC3 sRGB", 79 or 80 or 81 => "BC4",
                    82 or 83 or 84 => "BC5", 97 or 98 => "BC7", 99 => "BC7 sRGB", 28 => "RGBA8", 29 => "RGBA8 sRGB", 87 => "BGRA8", var d => $"DXGI {d}",
                }
                : four == FourCc("DXT1") ? "BC1" : four == FourCc("DXT5") ? "BC3" : four == FourCc("ATI2") ? "BC5" : "DDS";
            return $"{fmt} · {width}×{height}";
        }
        catch
        {
            return null;
        }
    }

    static uint FourCc(string s) => (uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24);
}
