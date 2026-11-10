using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Tests;

public sealed class ArrayExpansionTests
{
    [Fact]
    public void Existing_team_slices_are_copied_losslessly_into_the_donor_layout()
    {
        var team = Header(2);
        var donor = Header(3);
        byte[] teamStock = [.. Enumerable.Range(0, 2048).Select(i => (byte)(i / 1024 == 0 ? 0x31 : 0x72))];
        byte[] donorStock = new byte[3072];
        donorStock.AsSpan().Fill(0xEE);

        byte[] expanded = Ps4Codec.BuildExpandedArray(TextureRes.Parse(donor), donorStock,
            TextureRes.Parse(team), teamStock, new Dictionary<int, Rgba> { [2] = Solid() });

        Assert.Equal(3072, expanded.Length);
        Assert.All(expanded.Take(1024), b => Assert.Equal(0x31, b));
        Assert.All(expanded.Skip(1024).Take(1024), b => Assert.Equal(0x72, b));
        Assert.False(expanded.Skip(2048).All(b => b == 0xEE));
    }

    [Fact]
    public void Every_new_layer_needs_team_artwork()
    {
        var team = TextureRes.Parse(Header(2));
        var donor = TextureRes.Parse(Header(3));
        var ex = Assert.Throws<InvalidDataException>(() => Ps4Codec.BuildExpandedArray(donor, new byte[3072],
            team, new byte[2048], new Dictionary<int, Rgba>()));
        Assert.Contains("layer 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_layers_cannot_be_replaced_by_expansion()
    {
        var team = TextureRes.Parse(Header(2));
        var donor = TextureRes.Parse(Header(3));
        var images = new Dictionary<int, Rgba> { [0] = Solid(), [2] = Solid() };
        var ex = Assert.Throws<InvalidDataException>(() => Ps4Codec.BuildExpandedArray(donor, new byte[3072],
            team, new byte[2048], images));
        Assert.Contains("existing team layers must be preserved", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_team_and_donor_dimensions_are_refused()
    {
        var team = TextureRes.Parse(Header(2));
        var donorBytes = Header(3);
        donorBytes[22] = 64;
        var donor = TextureRes.Parse(donorBytes);
        Assert.Throws<InvalidDataException>(() => Ps4Codec.BuildExpandedArray(donor, new byte[3072],
            team, new byte[2048], new Dictionary<int, Rgba> { [2] = Solid() }));
    }

    static Rgba Solid() => new(32, 32, [.. Enumerable.Range(0, 32 * 32 * 4).Select(i => (byte)(i % 4 == 3 ? 255 : 90))]);

    static byte[] Header(int slices)
    {
        var h = new byte[TextureRes.HeaderSize];
        h[12] = 0x3C;
        BitConverter.TryWriteBytes(h.AsSpan(22), (ushort)32);
        BitConverter.TryWriteBytes(h.AsSpan(24), (ushort)32);
        BitConverter.TryWriteBytes(h.AsSpan(26), (ushort)slices);
        h[30] = 1;
        new Guid("11111111-2222-3333-4444-555555555555").TryWriteBytes(h.AsSpan(32, 16));
        BitConverter.TryWriteBytes(h.AsSpan(48), 1024);
        return h;
    }
}
