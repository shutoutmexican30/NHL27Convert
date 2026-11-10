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
