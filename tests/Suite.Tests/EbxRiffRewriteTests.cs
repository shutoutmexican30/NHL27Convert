using System.Text;
using Suite.Core.Frostbite;
using Xunit;

namespace Suite.Tests;

public class EbxRiffRewriteTests
{
    const string Name = "folder/home_asset";
    const int DataStart = 32;

    /// <summary>A minimal RIFF EBX: one self-relative string pointer at data offset 0, then the root name and a second string.</summary>
    static (byte[] Bytes, int PointerField) Sample(string extra = "weaveRegions")
    {
        var data = new MemoryStream();
        data.Write(new byte[16]);
        int stringsOffset = (int)data.Length;
        data.Write(Encoding.ASCII.GetBytes(Name + "\0"));
        int extraOffset = (int)data.Length;
        data.Write(Encoding.ASCII.GetBytes(extra + "\0"));
        var payload = data.ToArray();
        BitConverter.TryWriteBytes(payload.AsSpan(0), (long)extraOffset);

        var efix = new MemoryStream();
        void U(uint v) => efix.Write(BitConverter.GetBytes(v));
        efix.Write(Guid.NewGuid().ToByteArray());
        U(0); U(0); U(1);         // class GUIDs, signatures, exported instances
        U(0);                     // data offsets
        U(1); U(0);               // one pointer, at data offset 0
        U(0); U(0); U(0); U(0);   // resource references, imports, import offsets, type-info offsets
        U((uint)stringsOffset); U((uint)stringsOffset); U((uint)stringsOffset);   // arrays, boxed, strings

        var o = new MemoryStream();
        void A(string t) => o.Write(Encoding.ASCII.GetBytes(t));
        void I(int v) => o.Write(BitConverter.GetBytes(v));
        A("RIFF"); I(0); A("EBX\0"); A("EBXD");
        I(DataStart - 20 + payload.Length);
        o.Write(new byte[DataStart - 20]);
        o.Write(payload);
        if (o.Length % 2 != 0) o.WriteByte(0);
        A("EFIX"); I((int)efix.Length);
        o.Write(efix.ToArray());
        var bytes = o.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(4), bytes.Length - 8);
        return (bytes, DataStart);
    }

    static string Target(byte[] bytes, int field)
    {
        int at = field + (int)BitConverter.ToInt64(bytes, field);
        return Encoding.ASCII.GetString(bytes, at, Array.IndexOf(bytes, (byte)0, at) - at);
    }

    [Theory]
    [InlineData("folder/twentyfive_asset")]
    [InlineData("f/a")]
    public void PointersPastTheNameFollowTheirStrings(string newName)
    {
        var (bytes, field) = Sample();
        Assert.Equal("weaveRegions", Target(bytes, field));
        var rewritten = EbxRiff.Parse(bytes).Rewrite(newName, Guid.NewGuid());
        Assert.Equal(newName, EbxRiff.Parse(rewritten).Name);
        Assert.Equal("weaveRegions", Target(rewritten, field));
    }

    [Fact]
    public void SameLengthNamesLeaveThePointerAlone()
    {
        var (bytes, field) = Sample();
        var rewritten = EbxRiff.Parse(bytes).Rewrite("folder/away_asset", Guid.NewGuid());
        Assert.Equal(BitConverter.ToInt64(bytes, field), BitConverter.ToInt64(rewritten, field));
    }

    [Fact]
    public void GrowingEmbeddedContentKeepsTrailingPointersAndIdentity()
    {
        var (bytes, field) = Sample();
        int extra = field + (int)BitConverter.ToInt64(bytes, field);
        var identity = EbxRiff.Parse(bytes);
        byte[] replacement = Encoding.ASCII.GetBytes("a considerably longer replacement string\0");
        byte[] result = identity.ReplaceEmbedded(extra, "weaveRegions".Length + 1, replacement);
        Assert.True(result.Length > bytes.Length);
        Assert.Equal("a considerably longer replacement string", Target(result, field));
        Assert.Equal(identity.Name, EbxRiff.Parse(result).Name);
        Assert.Equal(identity.FileGuid, EbxRiff.Parse(result).FileGuid);
        Assert.Equal(identity.ImportedFileGuids, EbxRiff.Parse(result).ImportedFileGuids);
    }
}
