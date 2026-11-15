namespace Suite.Sounds;

/// <summary>
/// EA SPS streams as NHL 24 and NHL 27 store them: an 'H' block (<c>48 00 00 0C</c>, then a big-endian header word
/// [version:4 | codec:4 | channel config:6 | sample rate:18] and [flags:4 | samples:28]), 'D' blocks
/// ([type u8][size u24 BE, header included][samples u32 BE][payload]) and a 4-byte 'E' block. Codec 0xF is EA Opus
/// (all NHL 24 / NHL 27 speech), 0xD is EA ATRAC9 (NHL 27 PS5 SFX). A wave chunk holding several clips concatenates
/// streams, each starting on a 4-byte boundary.
/// </summary>
public sealed record SpsStream(int Offset, int Length, int Version, int Codec, int ChannelConfig, int SampleRate, int Samples)
{
    public const int CodecAtrac9 = 0xD, CodecOpus = 0xF;

    public double Seconds => SampleRate == 0 ? 0 : (double)Samples / SampleRate;

    /// <summary>Parses the stream starting at <paramref name="at"/>, or returns null if no valid H…E stream starts there.</summary>
    public static SpsStream? TryRead(ReadOnlySpan<byte> b, int at)
    {
        if (at < 0 || at + 12 > b.Length || b[at] != 0x48 || b[at + 1] != 0 || b[at + 2] != 0 || b[at + 3] != 0x0C) return null;
        uint h1 = BeU32(b, at + 4), h2 = BeU32(b, at + 8);
        int p = at + 12;
        while (p + 4 <= b.Length)
        {
            byte type = b[p];
            if (type == 0x45) return new(at, p + 4 - at, (int)(h1 >> 28), (int)(h1 >> 24) & 0xF, (int)(h1 >> 18) & 0x3F,
                (int)(h1 & 0x3FFFF), (int)(h2 & 0x0FFFFFFF));
            int size = (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
            if (type != 0x44 || size < 8) return null;
            p += size;
        }
        return null;
    }

    /// <summary>Every stream in a wave chunk, in order. Zero padding between streams is skipped.</summary>
    public static List<SpsStream> ReadAll(ReadOnlySpan<byte> chunk)
    {
        var list = new List<SpsStream>();
        int p = 0;
        while (p < chunk.Length)
        {
            if (chunk[p] == 0) { p++; continue; }
            var s = TryRead(chunk, p) ?? throw new InvalidDataException($"No SPS stream at 0x{p:X} (byte 0x{chunk[p]:X2}).");
            list.Add(s);
            p += s.Length;
        }
        return list;
    }

    static uint BeU32(ReadOnlySpan<byte> b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
}
