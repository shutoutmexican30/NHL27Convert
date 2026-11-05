using System.IO.Compression;
using System.Text;

namespace Suite.Scorebug;

/// <summary>
/// The settings block inside a custom scorebug movie: one 4096-byte ASCII string constant in the ABC string pool that
/// starts with <see cref="Marker"/> and has the u30 length prefix 0x80 0x20 (4096). The movie reads it at start
/// (loadConfig), so rewriting it in place changes the scorebug without a recompile. The movie is CFX (Scaleform's CWS:
/// "CFX", version, u32 uncompressed length, then zlib) or uncompressed GFX/FWS.
/// The marker also appears a second time as a short constant (the movie's own check), which has another prefix.
/// </summary>
public static class ScorebugMovie
{
    public const string Marker = "#NHL24KIT_CFG#";
    public const int BlockSize = 4096;
    static readonly byte[] MarkerBytes = Encoding.ASCII.GetBytes(Marker);

    public static (byte[] Header, byte[] Body) Unpack(byte[] movie)
    {
        if (movie.Length < 8) throw new InvalidDataException("Not a Scaleform/Flash movie.");
        var sig = Encoding.ASCII.GetString(movie, 0, 3);
        if (sig is "CFX" or "CWS")
        {
            using var z = new ZLibStream(new MemoryStream(movie, 8, movie.Length - 8), CompressionMode.Decompress);
            var ms = new MemoryStream();
            z.CopyTo(ms);
            // The length field is kept as it is: the game's own stock movies do not match it (42,524 vs 45,064).
            return (movie[..8], ms.ToArray());
        }
        if (sig is "GFX" or "FWS") return (movie[..8], movie[8..]);
        throw new InvalidDataException("Not a Scaleform/Flash movie.");
    }

    static int FindBlock(byte[] body)
    {
        int at = body.AsSpan().IndexOf(MarkerBytes);
        while (at >= 0)
        {
            if (at >= 2 && body[at - 2] == 0x80 && body[at - 1] == 0x20 && at + BlockSize <= body.Length) return at;
            int next = body.AsSpan(at + 1).IndexOf(MarkerBytes);
            at = next < 0 ? -1 : at + 1 + next;
        }
        return -1;
    }

    public static bool HasBlock(byte[] movie) => FindBlock(Unpack(movie).Body) >= 0;

    /// <summary>
    /// A content fingerprint that ignores the settings block: SHA-256 of the unpacked body with the whole 4096-byte
    /// block zeroed. Two builds of the same style match no matter what settings they carry, and any other movie
    /// (stock scoreclock, another style) does not.
    /// </summary>
    public static string Fingerprint(byte[] movie)
    {
        var body = Unpack(movie).Body;
        int at = FindBlock(body);
        if (at >= 0) Array.Clear(body, at, BlockSize);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));
    }

    /// <summary>The settings text in the movie (without the marker, trailing spaces trimmed).</summary>
    public static string ReadSettings(byte[] movie)
    {
        var body = Unpack(movie).Body;
        int at = FindBlock(body);
        if (at < 0) throw new InvalidDataException("This movie has no scorebug settings block (not one of the custom scorebugs).");
        return Encoding.ASCII.GetString(body, at + MarkerBytes.Length, BlockSize - MarkerBytes.Length).TrimEnd();
    }

    /// <summary>The movie with its settings block set to <paramref name="settings"/> (space-padded), recompressed.</summary>
    public static byte[] WriteSettings(byte[] movie, string settings)
    {
        if (settings.Any(c => c < ' ' || c > '~')) throw new ArgumentException("Scorebug settings must be plain ASCII text.");
        var text = Marker + settings;
        if (text.Length > BlockSize) throw new ArgumentException($"Scorebug settings are too long ({text.Length} of {BlockSize} characters).");
        var (header, body) = Unpack(movie);
        int at = FindBlock(body);
        if (at < 0) throw new InvalidDataException("This movie has no scorebug settings block (not one of the custom scorebugs).");
        Encoding.ASCII.GetBytes(text.PadRight(BlockSize), body.AsSpan(at, BlockSize));
        var ms = new MemoryStream();
        ms.Write(header);
        if (header[0] is (byte)'C')
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(body);
        else ms.Write(body);
        return ms.ToArray();
    }

    /// <summary>
    /// The settings keys the movie's ABC string pool contains (every u30-prefixed printable string). Used to check that the
    /// schema's keys are ones the movie's loadConfig asks for.
    /// </summary>
    public static HashSet<string> PoolStrings(byte[] movie)
    {
        var body = Unpack(movie).Body;
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int p = 0; p + 2 < body.Length; p++)
        {
            int n = body[p];
            if (n == 0 || n > 40 || p + 1 + n > body.Length) continue;
            var s = body.AsSpan(p + 1, n);
            bool ok = true;
            foreach (var c in s) if (c < 0x21 || c > 0x7E) { ok = false; break; }
            if (ok) set.Add(Encoding.ASCII.GetString(s));
        }
        return set;
    }
}
