using System.Buffers.Binary;
using System.Text;
using Suite.Core.Frostbite;
using Xunit;

namespace Suite.Tests;

public class PlayerTeamRegistryTests
{
    const string From = "content/character/skater/league/nhl/detroit/adidas/home";
    const string To = "content/character/skater/league/nhl/detroit/adidas/twentyfive";

    /// <summary>A minimal table: one bundle row with a full path, one with a bare name and a folder column.</summary>
    static (byte[] Raw, byte[] Meta) Sample()
    {
        using var pool = new MemoryStream();
        pool.Write(new byte[64]);
        ulong Add(string s) { ulong at = (ulong)pool.Position; pool.Write(Encoding.ASCII.GetBytes(s)); pool.WriteByte(0); return at; }
        ulong empty = Add(""), folder = Add(From);
        ulong fullBundle = Add(From + "/jersey_adidas_detroit_home"), bareBundle = Add("jersey_adidas_detroit_home_hq_asp");
        ulong leafA = Add("jersey_adidas_detroit_home_color"), leafB = Add("jersey_adidas_detroit_home_hq_asp");
        ulong suffix = Add("_player_team_brt");
        int oA = (int)((pool.Position + 15) & ~15L), oB = oA + 2 * 24, oC = oB + 2 * 16, oTail = oC + 4 * 16, ds = oTail + 16;
        var keys = new[]
        {
            (Key: "jersey_adidas_detroit_home_color", A: 0u, B: 0u), (Key: From + "/jersey_adidas_detroit_home_color", A: 0u, B: 0u),
            (Key: "jersey_adidas_detroit_home_hq_asp", A: 1u, B: 1u), (Key: From + "/jersey_adidas_detroit_home_hq_asp", A: 1u, B: 1u),
        }.Select(k => (Hash: PlayerTeamRegistry.Key(k.Key), k.A, k.B)).OrderBy(k => k.Hash).ToArray();

        uint[] relocs = [0, 8, 16, 24, 32, (uint)oA, (uint)(oA + 8), (uint)(oA + 16), (uint)(oA + 24), (uint)(oA + 32), (uint)(oA + 40),
            (uint)oB, (uint)(oB + 8), (uint)(oB + 16), (uint)(oB + 24)];
        var b = new byte[ds + relocs.Length * 4];
        pool.ToArray().CopyTo(b, 0);
        void W(int at, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), v);
        void W4(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
        W(0, suffix); W(8, (ulong)oC); W(16, (ulong)oA); W(24, (ulong)oB); W(32, (ulong)oTail); W(40, empty);
        W4(48, 4); W4(52, 2); W4(56, 2);
        W(oA, fullBundle); W(oA + 8, empty); W(oA + 16, (ulong)oTail);
        W(oA + 24, bareBundle); W(oA + 32, folder); W(oA + 40, (ulong)oTail);
        W(oB, leafA); W(oB + 8, folder); W(oB + 16, leafB); W(oB + 24, folder);
        for (int i = 0; i < keys.Length; i++) { W(oC + i * 16, keys[i].Hash); W4(oC + i * 16 + 8, keys[i].A); W4(oC + i * 16 + 12, keys[i].B); }
        W(oTail, empty); W(oTail + 8, (ulong)oTail);
        for (int i = 0; i < relocs.Length; i++) W4(ds + i * 4, relocs[i]);
        var meta = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(meta, (uint)ds);
        BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(4), (uint)(relocs.Length * 4));
        return (b, meta);
    }

    [Fact]
    public void BareBundleRowsTakeTheTargetFolder()
    {
        var (raw, meta) = Sample();
        var result = PlayerTeamRegistry.Register(raw, meta, [new KitRegistration(From, To)]);
        var bare = PlayerTeamRegistry.Rows(result.Raw, To).Single(r => r.Leaf.EndsWith("_hq_asp", StringComparison.Ordinal));
        Assert.Equal("jersey_adidas_detroit_twentyfive_hq_asp", bare.Bundle.Name);
        Assert.Equal(To, bare.Bundle.Folder);
        Assert.Equal($"gen4b/{To}/jersey_adidas_detroit_twentyfive_hq_asp_player_team_brt", PlayerTeamRegistry.BundleName(bare.Bundle));
    }

    [Fact]
    public void FullPathRowsAndLookupKeysAreRegistered()
    {
        var (raw, meta) = Sample();
        var result = PlayerTeamRegistry.Register(raw, meta, [new KitRegistration(From, To)]);
        Assert.Equal(2, result.Assets);
        Assert.Equal(2, result.Bundles);
        Assert.True(PlayerTeamRegistry.IsSorted(result.Raw));
        Assert.True(PlayerTeamRegistry.Contains(result.Raw, "jersey_adidas_detroit_twentyfive_color"));
        Assert.True(PlayerTeamRegistry.Contains(result.Raw, To + "/jersey_adidas_detroit_twentyfive_color"));
        var full = PlayerTeamRegistry.Rows(result.Raw, To).Single(r => r.Leaf.EndsWith("_color", StringComparison.Ordinal));
        Assert.Equal($"gen4b/{To}/jersey_adidas_detroit_twentyfive_player_team_brt", PlayerTeamRegistry.BundleName(full.Bundle));
    }

    [Fact]
    public void StockRowsStayIntactAndMetaMatches()
    {
        var (raw, meta) = Sample();
        var result = PlayerTeamRegistry.Register(raw, meta, [new KitRegistration(From, To)]);
        var stock = PlayerTeamRegistry.Rows(result.Raw, From);
        Assert.Equal(2, stock.Count);
        Assert.Contains(stock, r => r.Bundle.Folder == From);
        Assert.Equal(result.Raw.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(result.Meta) + (int)BinaryPrimitives.ReadUInt32LittleEndian(result.Meta.AsSpan(4)));
    }

    [Fact]
    public void ReAddingTheSameKitIntoItsSlotKeepsItsRowsAndAddsNothing()
    {
        // Repair removes a slot's bundles and adds the same kit again; its registration rows are already exactly right.
        var (raw, meta) = Sample();
        var once = PlayerTeamRegistry.Register(raw, meta, [new KitRegistration(From, To)]);
        var again = PlayerTeamRegistry.Register(once.Raw, once.Meta, [new KitRegistration(From, To)]);
        Assert.Equal(0, again.Assets);
        Assert.Equal(0, again.Bundles);
        Assert.Equal(PlayerTeamRegistry.Rows(once.Raw, To).Count, PlayerTeamRegistry.Rows(again.Raw, To).Count);
    }

    [Fact]
    public void LaterRegistrationsPreserveEarlierStringPoolRows()
    {
        const string secondTo = "content/character/skater/league/nhl/detroit/adidas/twentyfour";
        const string thirdTo = "content/character/skater/league/nhl/detroit/adidas/twentythree";
        var (raw, meta) = Sample();
        var once = PlayerTeamRegistry.Register(raw, meta, [new KitRegistration(From, To)]);
        var twice = PlayerTeamRegistry.Register(once.Raw, once.Meta, [new KitRegistration(From, secondTo)]);
        var thrice = PlayerTeamRegistry.Register(twice.Raw, twice.Meta, [new KitRegistration(From, thirdTo)]);

        foreach (string folder in new[] { From, To, secondTo, thirdTo })
        {
            var rows = PlayerTeamRegistry.Rows(thrice.Raw, folder);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.True(PlayerTeamRegistry.Contains(thrice.Raw, row.Leaf));
                Assert.True(PlayerTeamRegistry.Contains(thrice.Raw, row.Folder + "/" + row.Leaf));
                Assert.False(string.IsNullOrWhiteSpace(row.Bundle.Name));
            });
        }
        Assert.Equal(PlayerTeamRegistry.Rows(once.Raw, To), PlayerTeamRegistry.Rows(thrice.Raw, To));
        Assert.True(PlayerTeamRegistry.IsSorted(thrice.Raw));
        Assert.Equal(thrice.Raw.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(thrice.Meta)
            + (int)BinaryPrimitives.ReadUInt32LittleEndian(thrice.Meta.AsSpan(4)));
    }
}
