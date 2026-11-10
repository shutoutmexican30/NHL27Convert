using System.Text;
using Suite.Core.Frostbite;

namespace Suite.Tests;

public sealed class EbxRiffTests
{
    static readonly Guid OldFile = new("00112233-4455-6677-8899-aabbccddeeff");
    static readonly Guid OldImport = new("10213243-5465-7687-98a9-bacbdcedfe0f");
    static readonly Guid NewFile = new("ffeeddcc-bbaa-9988-7766-554433221100");
    static readonly Guid NewImport = new("0f0e0d0c-0b0a-0908-0706-050403020100");

    [Fact]
    public void Longer_team_path_and_new_identities_read_back_without_touching_object_data()
    {
        byte[] source = Fixture("content/nhl/arizona/adidas/four/slot_color_array");
        byte[] rewritten = EbxRiff.Parse(source).Rewrite(
            "content/nhl/anaheim/adidas/twentythree/slot_color_array_with_a_longer_name",
            NewFile, new Dictionary<Guid, Guid> { [OldImport] = NewImport });

        var parsed = EbxRiff.Parse(rewritten);
        Assert.Equal("content/nhl/anaheim/adidas/twentythree/slot_color_array_with_a_longer_name", parsed.Name);
        Assert.Equal(NewFile, parsed.FileGuid);
        Assert.Equal((uint)(rewritten.Length - 8), BitConverter.ToUInt32(rewritten, 4));
        Assert.Contains(ChunksOf16(rewritten), b => b.SequenceEqual(NewImport.ToByteArray()));
        Assert.DoesNotContain(ChunksOf16(rewritten), b => b.SequenceEqual(OldImport.ToByteArray()));
    }

    [Fact]
    public void Shorter_path_preserves_the_trailing_ebxx_chunk()
    {
        byte[] source = Fixture("content/nhl/arizona/adidas/four/a_very_long_slot_name");
        byte[] rewritten = EbxRiff.Parse(source).Rewrite("content/nhl/nyi/adidas/two/x", NewFile);
        Assert.Equal("EBXX", Encoding.ASCII.GetString(rewritten, rewritten.Length - 16, 4));
        Assert.Equal("content/nhl/nyi/adidas/two/x", EbxRiff.Parse(rewritten).Name);
    }

    [Fact]
    public void Non_ascii_or_empty_paths_are_refused()
    {
        var riff = EbxRiff.Parse(Fixture("content/team/slot"));
        Assert.Throws<ArgumentException>(() => riff.Rewrite("content/team/crest-é", NewFile));
        Assert.Throws<ArgumentException>(() => riff.Rewrite("", NewFile));
    }

    [Fact]
    public void Real_donor_branding_assets_parse_and_round_trip()
    {
        string folder = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../Debugging/donor-clone/payloads"));
        string[] files = Directory.GetFiles(folder, "*.ebx");
        Assert.Equal(3, files.Length);

        foreach (string file in files)
        {
            byte[] source = File.ReadAllBytes(file);
            var parsed = EbxRiff.Parse(source);
            string newName = parsed.Name.Replace("/arizona/", "/anaheim/", StringComparison.Ordinal);
            Assert.NotEqual(parsed.Name, newName);

            byte[] rewritten = parsed.Rewrite(newName, NewFile);
            var check = EbxRiff.Parse(rewritten);
            Assert.Equal(newName, check.Name);
            Assert.Equal(NewFile, check.FileGuid);
            Assert.Equal(source.Length, rewritten.Length); // arizona and anaheim have the same length
            int oldNameAt = source.AsSpan().LastIndexOf(Encoding.ASCII.GetBytes(parsed.Name));
            Assert.True(oldNameAt > 0);
            Assert.True(source.AsSpan(0, oldNameAt).SequenceEqual(rewritten.AsSpan(0, oldNameAt)));
            Assert.Equal("EBXX", Encoding.ASCII.GetString(rewritten, rewritten.Length - 16, 4));
        }
    }

    static IEnumerable<byte[]> ChunksOf16(byte[] bytes)
    {
        for (int i = 0; i + 16 <= bytes.Length; i++) yield return bytes.AsSpan(i, 16).ToArray();
    }

    static byte[] Fixture(string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        const int dataStart = 32;
        int ebxdSize = dataStart - 20 + nameBytes.Length + 1;
        int efix = (20 + ebxdSize + 1) & ~1;
        var efixBody = new List<byte>();
        efixBody.AddRange(OldFile.ToByteArray());
        void Count(uint value) => efixBody.AddRange(BitConverter.GetBytes(value));
        Count(0); // class GUIDs
        Count(0); // signatures
        Count(0); // exported
        Count(0); // data offsets
        Count(0); // pointer offsets
        Count(0); // resource refs
        Count(1); // imports
        efixBody.AddRange(OldImport.ToByteArray());
        efixBody.AddRange(Guid.NewGuid().ToByteArray());
        Count(0); // import offsets
        Count(0); // type-info offsets
        Count(0); // arrays
        Count(0); // boxed values
        Count(0); // root string, relative to dataStart
        int ebxx = efix + 8 + efixBody.Count;
        var output = new byte[ebxx + 16];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(output, 0);
        BitConverter.TryWriteBytes(output.AsSpan(4), (uint)(output.Length - 8));
        Encoding.ASCII.GetBytes("EBX").CopyTo(output, 8);
        Encoding.ASCII.GetBytes("EBXD").CopyTo(output, 12);
        BitConverter.TryWriteBytes(output.AsSpan(16), (uint)ebxdSize);
        for (int i = 20; i < dataStart; i++) output[i] = 0xA5;
        nameBytes.CopyTo(output, dataStart);
        Encoding.ASCII.GetBytes("EFIX").CopyTo(output, efix);
        BitConverter.TryWriteBytes(output.AsSpan(efix + 4), (uint)efixBody.Count);
        efixBody.CopyTo(output, efix + 8);
        Encoding.ASCII.GetBytes("EBXX").CopyTo(output, ebxx);
        BitConverter.TryWriteBytes(output.AsSpan(ebxx + 4), 0u);
        return output;
    }
}
