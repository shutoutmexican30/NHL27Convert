using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace Suite.Core.Legacy;

/// <summary>One texture inside an RW4 PS3 (.rpsgl) file.</summary>
public sealed record Rw4Raster(string Name, uint Format, int Width, int Height, int Mips, long Offset, long Size)
{
    public bool IsDxt1 => Format == 0x83f1;
    public bool IsDxt5 => Format == 0x83f3;
    public string FormatName => Format switch { 0x83f1 => "DXT1", 0x83f3 => "DXT5", _ => $"0x{Format:x}" };
}

/// <summary>
/// Reader for the PS3-era NHL "RW4ps3" texture files (legacy jerseys: rendering\jersey\texlib_1_&lt;team&gt;_&lt;uniform&gt;.rpsgl).
/// Layout: magic 89 'RW4ps3' 00; raster descriptors of 0x38 bytes starting 00 00 0d e1 (format u32 BE at +4, width/height
/// u16 BE at +0x14, mip count at +0x18) in the order of the "*.Raster" names; pixel data from 0x600, linear (not swizzled)
/// DXT, full mip chains back to back.
/// </summary>
public static class Rw4Reader
{
    static readonly byte[] Magic = [0x89, (byte)'R', (byte)'W', (byte)'4', (byte)'p', (byte)'s', (byte)'3', 0];
    const int DataStart = 0x600;

    public static bool IsRw4(ReadOnlySpan<byte> b) => b.Length > 16 && b[..8].SequenceEqual(Magic);

    public static List<Rw4Raster> Rasters(byte[] b)
    {
        if (!IsRw4(b)) throw new InvalidDataException("not an RW4 PS3 file");
        var head = Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 0x2000));
        var names = Regex.Matches(head, @"[\w]+\.Raster").Select(m => m.Value[..^".Raster".Length]).ToList();
        var list = new List<Rw4Raster>();
        long offset = DataStart;
        int n = 0;
        for (int o = 0; o + 0x38 <= Math.Min(b.Length, DataStart); o += 4)
        {
            if (b[o] != 0 || b[o + 1] != 0 || b[o + 2] != 0x0d || b[o + 3] != 0xe1) continue;
            uint fmt = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o + 4));
            int w = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o + 0x14));
            int h = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o + 0x16));
            int mips = Math.Max(1, (int)b[o + 0x18]);
            long size = ChainSize(fmt, w, h, mips);
            list.Add(new Rw4Raster(n < names.Count ? names[n] : $"raster{n}", fmt, w, h, mips, offset, size));
            offset += size;
            n++;
            o += 0x34;
        }
        return list;
    }

    static int BlockBytes(uint fmt) => fmt == 0x83f1 ? 8 : 16;

    static long ChainSize(uint fmt, int w, int h, int mips)
    {
        if (fmt is not (0x83f1 or 0x83f3)) return 0; // only DXT chains are sized; others are read on their own
        long total = 0;
        for (int i = 0; i < mips; i++)
            total += (long)Math.Max(1, (Math.Max(1, w >> i) + 3) / 4) * Math.Max(1, (Math.Max(1, h >> i) + 3) / 4) * BlockBytes(fmt);
        return total;
    }

    /// <summary>Decodes the top mip of a DXT raster to RGBA.</summary>
    public static Rgba Decode(byte[] file, Rw4Raster r)
    {
        if (!r.IsDxt1 && !r.IsDxt5) throw new NotSupportedException($"{r.Name}: format {r.FormatName}");
        int top = (r.Width + 3) / 4 * ((r.Height + 3) / 4) * BlockBytes(r.Format);
        if (r.Offset + top > file.Length) throw new InvalidDataException($"{r.Name}: data past end of file");
        var colors = new BcDecoder().DecodeRaw(file.AsSpan((int)r.Offset, top).ToArray(), r.Width, r.Height,
            r.IsDxt1 ? CompressionFormat.Bc1 : CompressionFormat.Bc3);
        var px = new byte[r.Width * r.Height * 4];
        for (int i = 0; i < colors.Length; i++)
        {
            var c = colors[i];
            px[i * 4] = c.r; px[i * 4 + 1] = c.g; px[i * 4 + 2] = c.b; px[i * 4 + 3] = c.a;
        }
        return new Rgba(r.Width, r.Height, px);
    }
}
