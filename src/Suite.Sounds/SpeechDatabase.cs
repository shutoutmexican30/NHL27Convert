using System.Buffers.Binary;
using System.Text;

namespace Suite.Sounds;

/// <summary>
/// The two NHL 24 speech-database EBX assets that tie a clip's VariationId to what it says and when it can play.
/// Field layouts come from FMT's NHL 23 SDK (same class GUIDs in NHL 24) or were reversed where the SDK has none.
/// EBX pointers here are self-relative i32 values in 8-byte fields; a list's count is the u32 just before its data;
/// the single exported root instance sits at 0x30.
/// <list type="bullet">
/// <item><c>…/database/…sentencetranscriptioncollection</c>: root +0x20 list of 16-byte {i64 rel → UTF-8 text, u32 VariationId, pad}
/// (not in the SDK).</item>
/// <item><c>…/database/…keywords</c> (SentenceKeywords, 3E01B300): root +0x20 List&lt;KeywordData&gt;, 16 bytes each:
/// {List&lt;Int32&gt; Keywords, u32 VariationId}. For surname clips the keyword is the per-surname id the commentary code asks
/// for (<c>PG_*_surname_id</c> param getters); both Toews brothers share keyword 1082, Stamkos is 1091.</item>
/// </list>
/// </summary>
public static class SpeechDatabase
{
    const int Root = 0x30;

    public static Dictionary<uint, string> ReadTranscriptions(byte[] ebx)
    {
        var (at, count) = List(ebx, Root + 0x20);
        var result = new Dictionary<uint, string>(count);
        for (int i = 0; i < count; i++)
        {
            int e = at + 16 * i;
            result[U32(ebx, e + 8)] = CString(ebx, e);
        }
        return result;
    }

    public static Dictionary<uint, int[]> ReadKeywords(byte[] ebx)
    {
        var (at, count) = List(ebx, Root + 0x20);
        var result = new Dictionary<uint, int[]>(count);
        for (int i = 0; i < count; i++)
        {
            int e = at + 16 * i;
            var (ka, kn) = List(ebx, e);
            var kws = new int[kn];
            for (int k = 0; k < kn; k++) kws[k] = BinaryPrimitives.ReadInt32LittleEndian(ebx.AsSpan(ka + 4 * k));
            result[U32(ebx, e + 8)] = kws;
        }
        return result;
    }

    /// <summary>Transcription text without the trailing " //" / " \\" take markers.</summary>
    public static string CleanText(string s) => s.TrimEnd(' ', '/', '\\').Trim();

    static (int At, int Count) List(byte[] b, int field)
    {
        int rel = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(field));
        if (rel == 0) return (0, 0);
        int at = field + rel;
        if (at < 4 || at > b.Length) throw new InvalidDataException($"List pointer at 0x{field:X} leaves the asset.");
        return (at, checked((int)U32(b, at - 4)));
    }

    static string CString(byte[] b, int field)
    {
        int rel = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(field));
        if (rel == 0) return "";
        int at = field + rel, end = Array.IndexOf(b, (byte)0, at);
        return Encoding.UTF8.GetString(b, at, end - at);
    }

    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
}
