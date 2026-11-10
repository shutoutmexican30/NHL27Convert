using Suite.Convert27;
using Suite.Core;
using Xunit;

namespace Suite.Tests;

public class SponsorSlotsTests
{
    static UniformHotspotPlacement Slot(int id, string name, string role) => new() { Id = id, Name = name, Role = role };

    static UniformHotspotLayout Stock() => new()
    {
        SourceSlot = "content/character/skater/league/nhl/nyr/adidas/home/stamp_jersey_adidas_nyr_home",
        Branding = [Slot(8, "mainlogo", "crest"), Slot(9, "collar_nhl", "shield"), Slot(10, "back_adidas", "brand"), Slot(11, "adidas primegreen", "epw_tag")],
    };

    static readonly UniformHotspotPlacement Position = new()
    {
        Rotation = "177.0000", ScaleX = "0.0556", ScaleY = "0.0556", TranslateX = "0.4290", TranslateY = "0.3880",
    };

    [Fact]
    public void TheSponsorBecomesTheNextSlotWithTheStockPosition()
    {
        var layout = SponsorSlots.WithSponsor(Stock(), Position);
        Assert.Equal(["crest", "shield", "brand", "epw_tag", "sponsor"], UniformHotspots.Roles(layout));
        var added = layout.Branding.Last();
        Assert.Equal(12, added.Id);
        Assert.Equal("sponsor", added.Name);
        Assert.Equal(("177.0000", "0.4290", "0.3880"), (added.Rotation, added.TranslateX, added.TranslateY));
    }

    [Fact]
    public void TheStockLayoutIsNotChanged()
    {
        var stock = Stock();
        _ = SponsorSlots.WithSponsor(stock, Position);
        Assert.Equal(4, stock.Branding.Count);
    }

    [Fact]
    public void AKitThatAlreadyHasASponsorIsRefused()
    {
        var layout = SponsorSlots.WithSponsor(Stock(), Position);
        Assert.Throws<InvalidDataException>(() => SponsorSlots.WithSponsor(layout, Position));
    }

    [Theory]
    [InlineData("main", "crest")]
    [InlineData("main_logo", "crest")]
    [InlineData("l_shoulder", "shoulder")]
    [InlineData("r_shoulder", "shoulder")]
    [InlineData("adidas", "brand")]
    [InlineData("adidas_back", "brand")]
    [InlineData("nhl", "shield")]
    [InlineData("nhl_collar", "shield")]
    [InlineData("primegreen", "epw_tag")]
    [InlineData("adidas_tag", "epw_tag")]
    public void StockPlacementNamesHaveRoles(string name, string role) => Assert.Equal(role, UniformHotspots.Role(name));
}

public class HotspotRoleTests
{
    [Theory]
    [InlineData("maintlogo", "crest")]
    [InlineData("frontcollarnhllogo", "shield")]
    [InlineData("collornhl", "shield")]
    [InlineData("backcollarnhl", "shield")]
    [InlineData("backaddiaslogo", "brand")]
    [InlineData("logoendplasticbackbottom", "epw_tag")]
    [InlineData("leftshoulderlogo", "shoulder")]
    [InlineData("rightshoulder", "shoulder")]
    [InlineData("bottom", "epw_tag")]
    public void SpellingVariantsHaveRoles(string name, string role) => Assert.Equal(role, Suite.Convert27.UniformHotspots.Role(name));

    [Theory]
    [InlineData("180.0000", "0.1905", "0.5000", "0.2870", "crest")]
    [InlineData("180.0000", "0.0286", "0.5000", "0.4230", "shield")]
    [InlineData("0.0000", "0.0357", "0.5000", "0.5625", "brand")]
    [InlineData("0.0000", "0.0192", "0.3930", "0.9800", "epw_tag")]
    [InlineData("90.0000", "0.0800", "0.3910", "0.5000", "shoulder")]
    [InlineData("270.0000", "0.0800", "0.6090", "0.5000", "shoulder")]
    [InlineData("177.0000", "0.0556", "0.4290", "0.3880", "sponsor")]
    public void NumberedSlotsGetRolesFromPosition(string r, string s, string tx, string ty, string role) =>
        Assert.Equal(role, Suite.Convert27.UniformHotspots.PositionRole(new Suite.Core.UniformHotspotPlacement
            { Name = "3", Rotation = r, ScaleX = s, ScaleY = s, TranslateX = tx, TranslateY = ty }));
}

public class AddedSlotTests
{
    [Fact]
    public void ASecondShoulderMirrorsTheFirst()
    {
        var right = new Suite.Core.UniformHotspotPlacement { Id = 13, Name = "right_shoulder", Role = "shoulder", Rotation = "90.0000", ScaleX = "0.0577", ScaleY = "0.0577", TranslateX = "0.4000", TranslateY = "0.4935" };
        var left = Suite.Convert27.SponsorSlots.Mirror(right);
        Assert.Equal(("0.6000", "270.0000", "0.4935"), (left.TranslateX, left.Rotation, left.TranslateY));
    }

    [Fact]
    public void BostonsBorrowedLayoutGainsTheMissingShoulder()
    {
        var stock = new Suite.Core.UniformHotspotLayout
        {
            SourceSlot = "boston/five/stamp",
            Branding =
            [
                new() { Id = 8, Name = "mainlogo", Role = "crest", Rotation = "180", ScaleX = "0.1095", ScaleY = "0.1095", TranslateX = "0.5", TranslateY = "0.244" },
                new() { Id = 9, Name = "collar_nhl", Role = "shield" }, new() { Id = 10, Name = "back_adidas", Role = "brand" },
                new() { Id = 11, Name = "bottomlogo", Role = "epw_tag" }, new() { Id = 12, Name = "front_logo", Role = "crest" },
                new() { Id = 13, Name = "right_shoulder", Role = "shoulder", Rotation = "90.0000", TranslateX = "0.4000", TranslateY = "0.4935" },
            ],
        };
        var right = stock.Branding.Last();
        var layout = Suite.Convert27.SponsorSlots.WithAdded(stock, [("shoulder", Suite.Convert27.SponsorSlots.Mirror(right))]);
        string[] art = ["crest", "shoulder", "shoulder", "sponsor", "brand", "shield"];
        var roles = Suite.Convert27.UniformHotspots.Roles(layout).ToList();
        roles.Add("sponsor");
        var placement = Suite.Convert27.UniformBrandingLayers.PlanRoles(art, roles);
        for (int layer = 0; layer < art.Length; layer++) Assert.Equal(art[layer], roles[placement.Destination(layer)]);
        Assert.Equal("left_shoulder", layout.Branding.Last().Name);
    }
}

public class SyntheticArrayTests
{
    static byte[] Header(int slices)
    {
        var h = new byte[Suite.Core.Frostbite.TextureRes.HeaderSize];
        h[12] = 0x3C;
        BitConverter.TryWriteBytes(h.AsSpan(22), (ushort)32);
        BitConverter.TryWriteBytes(h.AsSpan(24), (ushort)32);
        BitConverter.TryWriteBytes(h.AsSpan(26), (ushort)slices);
        h[30] = 1;
        new Guid("11111111-2222-3333-4444-555555555555").TryWriteBytes(h.AsSpan(32, 16));
        BitConverter.TryWriteBytes(h.AsSpan(48), 1024);
        return h;
    }

    [Fact]
    public void AnArrayGrowsPastEveryStockDepthAndKeepsItsSlices()
    {
        var res = Suite.Core.Frostbite.TextureRes.Parse(Header(6));
        var stock = new byte[6 * 1024];
        for (int i = 0; i < stock.Length; i++) stock[i] = (byte)(i / 1024 + 1);
        var team = new Suite.Core.Frostbite.TextureSlots.Slot("x/branding_jersey_adidas_boston_five_color_array", res, res.ChunkGuid, stock);
        var donor = Suite.Convert27.InstalledArrayExpansion.Synthetic(team, 8);
        Assert.Equal(8, Suite.Core.Frostbite.Ps4Codec.Slices(donor.Res.Header));
        Assert.Equal(8 * 1024, donor.Stock.Length);
        var grown = Suite.Convert27.InstalledArrayExpansion.Expand(team, donor, "color");
        Assert.Equal(8, Suite.Core.Frostbite.Ps4Codec.Slices(grown.Res.Header));
        Assert.True(grown.Stock.AsSpan(0, 6 * 1024).SequenceEqual(stock));
    }
}
