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

/// <summary>Regression: Nashville's 2026 home has one shoulder patch (player's right), not a mirrored pair.</summary>
public class NashvilleOneShoulderTests
{
    static Suite.Core.NewUniformAddition Nashville()
    {
        const string stem = "content/character/skater/league/nhl/nashville/adidas/home/branding_jersey_adidas_nashville_home";
        string P(int n, string kind) => $@"cache\content_character_skater_league_nhl_nashville_adidas_ten_branding_jersey_adidas_nashville_ten_{n}_{kind}.png";
        Suite.Core.NewUniformTexture T(string suffix, string kind) => new()
        {
            SourceSlot = stem + suffix,
            RequiredRoles = ["crest", "shield", "brand", "sponsor", "shoulder", "shoulder"],
            Artwork = new Suite.Core.TextureReplacement("", "", new Dictionary<int, string>
                { [0] = P(0, kind), [1] = P(1, kind), [2] = P(2, kind), [3] = P(3, kind), [4] = P(4, kind), [5] = P(4, kind) }),
        };
        return new Suite.Core.NewUniformAddition { Team = "nashville", SourceKit = "home", Textures = [T("_color_array", "color"), T("_normal_array", "normal")] };
    }

    [Fact]
    public void TheRepeatedShoulderPictureIsOneLogo()
    {
        var addition = Nashville();
        Assert.Equal(1, Suite.Convert27.DuplicateLogoLayers.Apply([addition]));
        foreach (var texture in addition.Textures)
        {
            Assert.Equal(["crest", "shield", "brand", "sponsor", "shoulder"], texture.RequiredRoles);
            Assert.Equal(5, texture.Artwork.Layers!.Count);
            Assert.Equal([0, 1, 2, 3, 4], texture.Artwork.Layers.Keys.Order());
        }
    }

    [Fact]
    public void WithOneShoulderTheStockRightShoulderIsEnoughAndNothingIsAdded()
    {
        var addition = Nashville();
        Suite.Convert27.DuplicateLogoLayers.Apply([addition]);
        string[] physical = ["crest", "shield", "brand", "epw_tag", "shoulder", "sponsor"];
        var roles = addition.Textures[0].RequiredRoles!;
        int shouldersNeeded = roles.Count(r => r == "shoulder"), shouldersThere = physical.Count(r => r == "shoulder");
        Assert.Equal(shouldersThere, shouldersNeeded);
        var placement = Suite.Convert27.UniformBrandingLayers.PlanRoles(roles, physical);
        for (int layer = 0; layer < roles.Count; layer++) Assert.Equal(roles[layer], physical[placement.Destination(layer)]);
    }

    [Fact]
    public void ARepeatThatFillsAnExistingStockSlotIsKept()
    {
        // Nashville: one stock shoulder, so the second copy goes. Boston five: two stock crest hotspots, so both stay.
        var nashville = Nashville();
        Assert.Equal([5], Suite.Convert27.DuplicateLogoLayers.Duplicates(nashville.Textures[0],
            ["crest", "shield", "brand", "epw_tag", "shoulder", "sponsor"]));
        var boston = Nashville();
        boston.Textures[0].RequiredRoles = ["crest", "crest", "shield", "brand", "sponsor", "shoulder"];
        boston.Textures[0].Artwork = boston.Textures[0].Artwork with { Layers = new Dictionary<int, string> { [0] = "b_1_color.png", [1] = "b_1_color.png", [2] = "b_2_color.png", [3] = "b_3_color.png", [4] = "b_4_color.png", [5] = "b_5_color.png" } };
        Assert.Empty(Suite.Convert27.DuplicateLogoLayers.Duplicates(boston.Textures[0],
            ["crest", "shield", "brand", "epw_tag", "crest", "shoulder"]));
    }

    [Fact]
    public void APinnedRepeatIsKept()
    {
        var addition = Nashville();
        addition.Textures[0].PinnedSlots = new Dictionary<int, int> { [5] = 6 };
        Assert.Empty(Suite.Convert27.DuplicateLogoLayers.Duplicates(addition.Textures[0]));
    }
}

/// <summary>The Shoulder patches choice: sides from the player's point of view, matching the stock hotspot names.</summary>
public class ShoulderPatchesTests
{
    static UniformHotspotPlacement Slot(int id, string name, string role, string tx = "0.5", string r = "0.0") =>
        new() { Id = id, Name = name, Role = role, TranslateX = tx, Rotation = r, ScaleX = "0.05", ScaleY = "0.05", TranslateY = "0.2", Enabled = true };

    // Nashville home's stock stamp: one right shoulder (player's right sits at translate X 0.3937, turned 90 degrees).
    static UniformHotspotLayout NashvilleStock() => new()
    {
        SourceSlot = "content/character/skater/league/nhl/nashville/adidas/home/stamp_jersey_adidas_nashville_home",
        Branding = [Slot(8, "mainlogo", "crest"), Slot(9, "collar_nhl", "shield"), Slot(10, "back_adidas", "brand"),
            Slot(11, "adidas primegreen", "epw_tag"), Slot(12, "right_shoulder", "shoulder", "0.3937", "90.0000"), Slot(13, "sponsor", "sponsor")],
    };

    static (NewUniformTexture Color, NewUniformTexture Normal) Art(int shoulders)
    {
        NewUniformTexture T(string kind)
        {
            var roles = new List<string> { "crest", "shield", "brand", "sponsor" };
            var layers = new Dictionary<int, string> { [0] = $"c_0_{kind}.png", [1] = $"c_1_{kind}.png", [2] = $"c_2_{kind}.png", [3] = $"c_3_{kind}.png" };
            for (int i = 0; i < shoulders; i++) { layers[roles.Count] = $"c_4_{kind}.png"; roles.Add("shoulder"); }
            return new() { SourceSlot = "x/branding_jersey_adidas_nashville_home_" + kind + "_array", RequiredRoles = roles, Artwork = new("", "", layers) };
        }
        return (T("color"), T("normal"));
    }

    [Fact]
    public void RightOnlyKeepsTheStockRightShoulderAndAddsNothing()
    {
        var (color, normal) = Art(1);
        var layout = Suite.Convert27.ShoulderPatches.Place(NashvilleStock(), "right", color, [color, normal], () => null);
        Assert.Equal(6, layout.Branding.Count);
        var shoulder = Assert.Single(layout.Branding, s => s.Role == "shoulder");
        Assert.True(shoulder.Enabled);
        Assert.False(Suite.Convert27.ShoulderPatches.IsLeft(shoulder));
        Assert.Equal(4, color.PinnedSlots![4]);
        Assert.Equal(4, normal.PinnedSlots![4]);
    }

    [Fact]
    public void LeftOnlyAddsTheMirroredLeftAndSwitchesTheRightOff()
    {
        var (color, normal) = Art(1);
        var layout = Suite.Convert27.ShoulderPatches.Place(NashvilleStock(), "left", color, [color, normal], () => null);
        var left = Assert.Single(layout.Branding, s => s.Role == "shoulder" && s.Enabled);
        Assert.Equal("left_shoulder", left.Name);
        Assert.Equal("0.6063", left.TranslateX);
        Assert.Equal("270.0000", left.Rotation);
        Assert.False(layout.Branding.Single(s => s.Name == "right_shoulder").Enabled);
        Assert.Equal(left.Id - 8, color.PinnedSlots![4]);
    }

    [Fact]
    public void BothGivesEachSideOneLayer()
    {
        var addition = new NewUniformAddition { Team = "nashville", SourceKit = "home", SourcePrefix = "a", SourceFolder27 = "f27" };
        var (color, normal) = Art(1);
        addition.Textures = [color, normal];
        var store = new Suite.Convert27.SlotMapStore();
        store.Entries.Add(new Suite.Convert27.SlotMapEntry { Folder24 = "a", Folder27 = "f27", Garment = "branding_jersey", Shoulders = "both" });
        Suite.Convert27.ShoulderPatches.FitArt([addition], store);
        Assert.Equal(2, color.RequiredRoles!.Count(r => r == "shoulder"));
        Assert.Equal(color.Artwork.Layers![4], color.Artwork.Layers[5]);
        var layout = Suite.Convert27.ShoulderPatches.Place(NashvilleStock(), "both", color, [color, normal], () => null);
        var on = layout.Branding.Where(s => s.Role == "shoulder" && s.Enabled).ToList();
        Assert.Equal(2, on.Count);
        Assert.Single(on, Suite.Convert27.ShoulderPatches.IsLeft);
        Assert.NotEqual(color.PinnedSlots![4], color.PinnedSlots[5]);
    }

    [Fact]
    public void NoneDropsTheArtAndSwitchesTheSlotOff()
    {
        var addition = new NewUniformAddition { Team = "nashville", SourceKit = "home", SourcePrefix = "a", SourceFolder27 = "f27" };
        var (color, normal) = Art(2);
        addition.Textures = [color, normal];
        var store = new Suite.Convert27.SlotMapStore();
        store.Entries.Add(new Suite.Convert27.SlotMapEntry { Folder24 = "a", Folder27 = "f27", Garment = "branding_jersey", Shoulders = "none" });
        Suite.Convert27.ShoulderPatches.FitArt([addition], store);
        Assert.DoesNotContain("shoulder", color.RequiredRoles!);
        Assert.DoesNotContain("shoulder", normal.RequiredRoles!);
        var layout = Suite.Convert27.ShoulderPatches.Place(NashvilleStock(), "none", color, [color, normal], () => null);
        Assert.All(layout.Branding.Where(s => s.Role == "shoulder"), s => Assert.False(s.Enabled));
    }

    [Fact]
    public void SlotsHavePlainNames()
    {
        Assert.Equal("Right shoulder (player's)", Suite.Convert27.SlotNames.Name("shoulder", false));
        Assert.Equal("Left shoulder (player's)", Suite.Convert27.SlotNames.Name("shoulder", true));
        Assert.Equal("EPW back-hem tag", Suite.Convert27.SlotNames.Name("epw_tag"));
        Assert.Equal("Brand (Fanatics)", Suite.Convert27.SlotNames.Name("brand", null, "fanatics"));
        Assert.Equal("Pants logo", Suite.Convert27.SlotNames.Name("crest", null, null, "branding_pant"));
        Assert.Equal("NHL shield", Suite.Convert27.SlotNames.Name("shield"));
    }
}

/// <summary>Pants hotspots have their own position map (Washington 'branding1', Edmonton 'ccmfront', 35 'branding0' shields).</summary>
public class PantsHotspotRoleTests
{
    static UniformHotspotPlacement P(string name, string r, string s, string tx, string ty) =>
        new() { Id = 9, Name = name, Rotation = r, ScaleX = s, ScaleY = s, TranslateX = tx, TranslateY = ty, Enabled = true };

    [Fact]
    public void PantsPositionsHaveTheirOwnRoles()
    {
        Assert.Equal("crest", UniformHotspots.PositionRole(P("branding1", "180.0", "0.1", "0.151", "0.0556"), true));
        Assert.Equal("crest", UniformHotspots.PositionRole(P("frontrightedmonton0", "0.0", "0.056", "0.1561", "0.0617"), true));
        Assert.Equal("shield", UniformHotspots.PositionRole(P("branding0", "180.0", "0.04", "0.3709", "0.4949"), true));
        // The same shield position on a jersey reads as a sponsor, so the garment matters.
        Assert.Equal("sponsor", UniformHotspots.PositionRole(P("branding0", "180.0", "0.04", "0.3709", "0.4949"), false));
        Assert.Equal("shield", UniformHotspots.Role("NHLShieldEmbroidered"));
        Assert.Equal("brand", UniformHotspots.Role("ccmfront"));
    }
}

/// <summary>All teams skips a uniform the game already has, while its kit is still there.</summary>
public class InstalledKitsTests
{
    [Fact]
    public void AnAddedUniformIsSkippedUntilItsKitIsGone()
    {
        static NewUniformAddition Boston(string kit) => new() { Team = "boston", SourceKit = kit, SourceFolder27 = "nhl/boston/adidas/thirteen" };
        var record = new InstalledKits();
        var added = Boston("home");
        added.TargetPrefix = "nhl/boston/adidas/twentyfive/"; added.TargetVariant = 25;
        record.Record(@"C:\Game", [added]);

        var batch = new List<NewUniformAddition> { Boston("home"), Boston("away") };
        var skipped = record.SkipPresent(@"c:\game\", batch, prefix => prefix == "nhl/boston/adidas/twentyfive/");
        Assert.Single(skipped);
        Assert.Equal("away", Assert.Single(batch).SourceKit);

        // After an undo the kit's folder is empty, so the uniform can be added again.
        var again = new List<NewUniformAddition> { Boston("home") };
        Assert.Empty(record.SkipPresent(@"C:\Game", again, _ => false));
        Assert.Single(again);
    }
}

public class NewKitVariantTests
{
    [Fact]
    public void HomeAlwaysTakes25AndAway24()
    {
        Assert.Equal(25, InstalledKits.VariantFor("home"));
        Assert.Equal(24, InstalledKits.VariantFor("away"));
    }
}

/// <summary>Regressions from the live All teams runs (2026-11-11).</summary>
public class AllTeamsInstallRuleTests
{
    [Fact]
    public void OnlyTheSourceKitsOwnBundlesAreClonedAndCheckedBack()
    {
        const string montreal = "content/character/skater/league/nhl/montreal/adidas/home/";
        Assert.True(FullKitInstall.OwnsBundle("gen4b/content/character/skater/league/nhl/montreal/adidas/home/jersey_adidas_montreal_home_player_team_brt", montreal));
        // A broken earlier Detroit install holds copies of Montreal's arrays; that bundle is not Montreal's kit.
        Assert.False(FullKitInstall.OwnsBundle("gen4b/content/character/skater/league/nhl/detroit/adidas/twentyfive/jersey_adidas_detroit_twentyfive_hq_doublesided_asp_player_team_brt", montreal));
        // "home" must not match "homeplate"-style neighbours.
        Assert.False(FullKitInstall.OwnsBundle("gen4b/content/character/skater/league/nhl/montreal/adidas/homex/b", montreal));
    }

    [Fact]
    public void BeforePlanningThereIsNoTargetToCompare()
    {
        Assert.False(RegisteredKitSource.TargetIsPlanned("", ""));
        Assert.False(RegisteredKitSource.TargetIsPlanned(null, ""));
        Assert.False(RegisteredKitSource.TargetIsPlanned("", "content/x/adidas/twentyfive/"));
        Assert.True(RegisteredKitSource.TargetIsPlanned("content/x/adidas/twentyfive/a", "content/x/adidas/twentyfive/"));
        Assert.True(RegisteredKitSource.TargetIsPlanned(null, "content/x/adidas/twentyfive/"));
    }
}
