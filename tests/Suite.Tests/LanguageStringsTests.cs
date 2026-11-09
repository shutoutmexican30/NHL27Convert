using Suite.Rosters;
using Xunit;

namespace Suite.Tests;

public class LanguageStringsTests
{
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789 -acfinst";

    [Theory]
    [InlineData("VINTAGE_2_0_4", 0x83AFA0DAu)]
    [InlineData("alternatejersey", 0x793F43FDu)]
    [InlineData("TXT_BOTH", 0x000143AEu)]
    public void HashMatchesTheGame(string key, uint expected) => Assert.Equal(expected, LanguageStrings.Hash(key));

    [Fact]
    public void AddedStringsReadBackSortedWithValidChecksums()
    {
        var table = LanguageStrings.Load(LanguageStrings.ForTest(Alphabet));
        table.Add("VINTAGE_2_10_25", "Fanatics 26-27");
        table.Add("VINTAGE_2_10_24", "Fanatics 26-27");
        byte[] built = table.Build();
        var back = LanguageStrings.Load(built);
        Assert.Equal("Fanatics 26-27", back.Find("VINTAGE_2_10_25"));
        Assert.Equal("Fanatics 26-27", back.Find("VINTAGE_2_10_24"));
        Assert.Null(back.Find("VINTAGE_2_10_26"));
        int count = (int)(BitConverter.ToUInt32(built, 36 + 16) >> 16);
        uint previous = 0;
        for (int i = 0; i < count; i++)
        {
            uint hash = BitConverter.ToUInt32(built, 120 + i * 16 + 4);
            Assert.True(hash >= previous);
            previous = hash;
        }
        Assert.Equal(T3Db.Crc(built.AsSpan(0, 20)), BitConverter.ToUInt32(built, 20));
    }

    [Fact]
    public void ADuplicateKeyIsRefused()
    {
        var table = LanguageStrings.Load(LanguageStrings.ForTest(Alphabet));
        table.Add("VINTAGE_2_10_25", "Fanatics 26-27");
        Assert.Throws<InvalidDataException>(() => table.Add("VINTAGE_2_10_25", "Other"));
    }
}
