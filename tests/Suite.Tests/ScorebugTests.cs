using System.IO.Compression;
using System.Text;
using Suite.Scorebug;

namespace Suite.Tests;

/// <summary>
/// Scorebug settings, the in-movie settings block and .nhl24bug packs. The synthetic movies stand in for the wire
/// format; the real style movies (copied to the output as data\scorebug\*.gfx) prove the schema keys and the rewrite.
/// </summary>
public class ScorebugTests
{
    // ---- Synthetic movies ----

    /// <summary>A movie whose body has a short marker constant first (like the real movies' own check), then the block.</summary>
    static byte[] SyntheticBody(string settings = "")
    {
        var ms = new MemoryStream();
        var junk = Encoding.ASCII.GetBytes("some fake abc bytecode, with the short check constant: ");
        ms.Write(junk);
        ms.WriteByte((byte)ScorebugMovie.Marker.Length);
        ms.Write(Encoding.ASCII.GetBytes(ScorebugMovie.Marker));
        ms.Write(Encoding.ASCII.GetBytes("more bytes before the real string constant"));
        ms.WriteByte(0x80); ms.WriteByte(0x20);                       // u30 length 4096
        var block = (ScorebugMovie.Marker + settings).PadRight(ScorebugMovie.BlockSize);
        ms.Write(Encoding.ASCII.GetBytes(block));
        ms.Write(Encoding.ASCII.GetBytes("tail bytes after the block"));
        return ms.ToArray();
    }

    static byte[] Fws(byte[] body)
    {
        var ms = new MemoryStream();
        ms.Write("FWS"u8);
        ms.WriteByte(6);
        ms.Write(BitConverter.GetBytes(body.Length));
        ms.Write(body);
        return ms.ToArray();
    }

    static byte[] Cfx(byte[] body)
    {
        var ms = new MemoryStream();
        ms.Write("CFX"u8);
        ms.WriteByte(6);
        ms.Write(BitConverter.GetBytes(body.Length));
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(body);
        return ms.ToArray();
    }

    [Fact]
    public void Settings_block_reads_back_and_nothing_outside_it_changes()
    {
        foreach (var wrap in new Func<byte[], byte[]>[] { Fws, Cfx })
        {
            var movie = wrap(SyntheticBody());
            const string text = "x=56;scale=1.25;teamColors=UTA:6CACE4:010101";
            var rewritten = ScorebugMovie.WriteSettings(movie, text);
            Assert.Equal(text, ScorebugMovie.ReadSettings(rewritten));
            var a = ScorebugMovie.Unpack(movie).Body;
            var b = ScorebugMovie.Unpack(rewritten).Body;
            Assert.Equal(a.Length, b.Length);
            int first = -1, last = -1;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { if (first < 0) first = i; last = i; }
            Assert.True(first >= 0 && last - first < ScorebugMovie.BlockSize, "every changed byte must sit inside the settings block");
        }
    }

    [Fact]
    public void The_short_marker_constant_is_not_mistaken_for_the_block()
    {
        var movie = Fws(SyntheticBody("orig=1"));
        // The short constant sits earlier in the body; the block is the one with the 0x80 0x20 prefix.
        Assert.Equal("orig=1", ScorebugMovie.ReadSettings(movie));
        var rewritten = ScorebugMovie.WriteSettings(movie, "orig=2");
        var body = ScorebugMovie.Unpack(rewritten).Body;
        int shortAt = Encoding.ASCII.GetBytes("some fake abc bytecode, with the short check constant: ").Length + 1;
        Assert.Equal(ScorebugMovie.Marker, Encoding.ASCII.GetString(body, shortAt, ScorebugMovie.Marker.Length)); // untouched
    }

    [Fact]
    public void Fingerprint_ignores_the_settings_but_not_the_movie()
    {
        var plain = Fws(SyntheticBody());
        var changed = ScorebugMovie.WriteSettings(plain, "x=1;y=2");
        Assert.Equal(ScorebugMovie.Fingerprint(plain), ScorebugMovie.Fingerprint(changed));   // settings are invisible to it
        Assert.NotEqual(ScorebugMovie.Fingerprint(plain), ScorebugMovie.Fingerprint(Fws(SyntheticBody().Concat(new byte[] { 9, 9, 9 }).ToArray())));
        Assert.Equal(ScorebugMovie.Fingerprint(plain), ScorebugMovie.Fingerprint(Cfx(SyntheticBody()))); // compression does not matter
        Assert.Null(ScorebugApply.Identify(changed));   // a synthetic movie is not a shipped style
    }

    [Fact]
    public void Oversized_or_non_ascii_settings_are_refused()
    {
        var movie = Fws(SyntheticBody());
        Assert.Throws<ArgumentException>(() => ScorebugMovie.WriteSettings(movie, new string('x', ScorebugMovie.BlockSize)));
        Assert.Throws<ArgumentException>(() => ScorebugMovie.WriteSettings(movie, "label=GRÜN"));
        Assert.Throws<InvalidDataException>(() => ScorebugMovie.ReadSettings(Fws(Encoding.ASCII.GetBytes("no block here"))));
        Assert.Throws<InvalidDataException>(() => ScorebugMovie.Unpack(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
    }

    // ---- Settings text ----

    [Fact]
    public void Settings_round_trip_through_the_settings_text()
    {
        foreach (var style in ScorebugStyle.All)
        {
            var s = new ScorebugSettings(style);
            foreach (var setting in s.All.Where(x => x.Group == "Colours").Take(3)) s.Set(setting, "12ab34");
            var first = s.All.First(x => x.Kind == SettingKind.Number);
            s.Set(first, "57");
            s.SetTeamColour("UTA", "6cace4", "010101");
            s.SetTeamColour("SEA", "001628", "99d9d9");
            var text = s.ToSettingsString();
            Assert.Contains("0x12AB34", text);
            var back = new ScorebugSettings(style);
            back.FromSettingsString(text);
            Assert.Equal(text, back.ToSettingsString());
            Assert.Equal("57", back.Get(first));
        }
    }

    [Fact]
    public void Unknown_keys_bad_values_and_duplicate_teams_are_handled()
    {
        var s = new ScorebugSettings(ScorebugStyle.All[0]);
        s.FromSettingsString("notAKey=5;x=not-a-number;cClock=ZZZZZZ;x=57;teamColors=UTA:111111:222222|UTA:6CACE4:010101|bad");
        Assert.Equal("x=57;teamColors=UTA:6CACE4:010101", s.ToSettingsString()); // the later UTA wins
    }

    [Fact]
    public void Values_normalize_to_the_wire_format()
    {
        var s = new ScorebugSettings(ScorebugStyle.All[0]);
        var colour = s.All.First(x => x.Kind == SettingKind.Colour);
        var number = s.All.First(x => x.Kind == SettingKind.Number);
        var flag = s.All.First(x => x.Kind == SettingKind.Flag);
        var text = s.All.First(x => x.Kind == SettingKind.Text);
        s.Set(colour, "#12ab34");
        Assert.Equal("12AB34", s.Get(colour));
        s.Set(number, (number.Max + 1000).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(number.Max.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), s.Get(number)); // clamped
        s.Set(flag, "true");
        Assert.Equal("1", s.Get(flag));
        s.Set(text, "power;play=now|later");
        Assert.DoesNotContain(";", s.Get(text)); Assert.DoesNotContain("=", s.Get(text)); Assert.DoesNotContain("|", s.Get(text));
        // A value set back to the movie's default drops out of the settings text.
        s.Set(colour, colour.Default);
        Assert.DoesNotContain(colour.Key, s.ToSettingsString());
        Assert.True(s.IsDefault(colour));
    }

    // ---- Packs ----

    [Fact]
    public void Packs_round_trip_with_style_values_team_colours_and_preview()
    {
        var style = ScorebugStyle.All[1];
        var s = new ScorebugSettings(style);
        s.Set(s.All[0], "123");
        s.SetTeamColour("UTA", "6CACE4", "010101");
        var preview = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var path = Path.Combine(Path.GetTempPath(), "cims-pack-test" + ScorebugPack.Extension);
        try
        {
            ScorebugPack.Write(path, "Test pack", "tester", s, preview);
            var back = ScorebugPack.Read(path);
            Assert.Equal(style, back.Style);
            Assert.Equal("Test pack", back.Manifest.Name);
            Assert.Equal("tester", back.Manifest.Author);
            Assert.Equal(preview, back.Preview);
            var s2 = new ScorebugSettings(back.Style);
            s2.FromStored(back.Manifest.Scorebug);
            Assert.Equal(s.ToSettingsString(), s2.ToSettingsString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_pack_without_a_style_is_espn_and_a_newer_format_is_refused()
    {
        Assert.Equal("espn", ScorebugStyle.ById(null).Id);
        Assert.Equal("espn", ScorebugStyle.ById("nonsense").Id);
        var path = Path.Combine(Path.GetTempPath(), "cims-pack-future" + ScorebugPack.Extension);
        try
        {
            using (var zip = System.IO.Compression.ZipFile.Open(path, ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("pack.json").Open()))
                w.Write("{\"Format\": 99, \"Name\": \"future\"}");
            Assert.Throws<InvalidDataException>(() => ScorebugPack.Read(path));
        }
        finally { File.Delete(path); }
    }

    // ---- The real style movies ----

    public static IEnumerable<object[]> Styles => ScorebugStyle.All.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Styles))]
    public void Every_schema_key_is_a_string_the_movie_contains(ScorebugStyle style)
    {
        var movie = File.ReadAllBytes(style.MoviePath);
        Assert.Empty(ScorebugSettings.MissingKeys(style, movie));
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void A_full_settings_rewrite_of_the_real_movie_reads_back_inside_the_block(ScorebugStyle style)
    {
        var movie = File.ReadAllBytes(style.MoviePath);
        Assert.True(ScorebugMovie.HasBlock(movie));
        Assert.Equal("", ScorebugMovie.ReadSettings(movie)); // the shipped movies carry no settings
        var s = new ScorebugSettings(style);
        foreach (var setting in s.All)
            s.Set(setting, setting.Kind switch
            {
                SettingKind.Number => (setting.Min + (setting.Max - setting.Min) * 0.37).ToString(System.Globalization.CultureInfo.InvariantCulture),
                SettingKind.Colour => "12AB34",
                SettingKind.Flag => setting.Default == "1" ? "0" : "1",
                _ => "TEST " + setting.Key,
            });
        s.SetTeamColour("UTA", "6CACE4", "010101");
        var text = s.ToSettingsString();
        var rewritten = ScorebugMovie.WriteSettings(movie, text);
        Assert.Equal(text, ScorebugMovie.ReadSettings(rewritten));
        var round = new ScorebugSettings(style);
        round.FromSettingsString(ScorebugMovie.ReadSettings(rewritten));
        Assert.Equal(text, round.ToSettingsString());
        // Every changed byte is inside the block, and the style is still identified.
        var a = ScorebugMovie.Unpack(movie).Body;
        var b = ScorebugMovie.Unpack(rewritten).Body;
        Assert.Equal(a.Length, b.Length);
        int first = -1, last = -1;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { if (first < 0) first = i; last = i; }
        Assert.True(first >= 0 && last - first < ScorebugMovie.BlockSize, "every changed byte must sit inside the settings block");
        Assert.Equal(style, ScorebugApply.Identify(rewritten));
        Assert.Equal(style, ScorebugApply.Identify(movie));
    }

    [Fact]
    public void The_two_styles_have_different_fingerprints_and_a_stock_movie_has_none()
    {
        var espn = File.ReadAllBytes(ScorebugStyle.All[0].MoviePath);
        var prime = File.ReadAllBytes(ScorebugStyle.All[1].MoviePath);
        Assert.NotEqual(ScorebugMovie.Fingerprint(espn), ScorebugMovie.Fingerprint(prime));
        Assert.Null(ScorebugApply.Identify(Fws(SyntheticBody())));       // not one of the shipped styles
        Assert.Equal(ScorebugStyle.All[0], ScorebugApply.Identify(espn)); // even with its stock (empty) settings
    }
}
