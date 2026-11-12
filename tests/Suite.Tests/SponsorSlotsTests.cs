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

public class MisplacedLogoTests
{
    [Fact]
    public void ALogoInAnotherKindOfSlotIsReportedInPlainWords()
    {
        string[] art = ["crest", "shoulder", "shield"];
        string[] physical = ["crest", "shield", "shoulder"];
        var straight = new UniformLayerPlacement(new Dictionary<int, int>(), new HashSet<int>());
        var wrong = UniformBrandingLayers.Misplaced(art, [0, 1, 2], physical, straight);
        Assert.Equal(["Shoulder patch would go in the nhl shield slot", "NHL shield would go in the shoulder patch slot"], wrong);
        var right = new UniformLayerPlacement(new Dictionary<int, int> { [1] = 2, [2] = 1 }, new HashSet<int>());
        Assert.Empty(UniformBrandingLayers.Misplaced(art, [0, 1, 2], physical, right));
        // A slot-map pin is the user's choice.
        Assert.Empty(UniformBrandingLayers.Misplaced(art, [0, 1, 2], physical, straight, new Dictionary<int, int> { [1] = 1, [2] = 2 }));
    }
}

public class KitDonorTests
{
    [Fact]
    public void LogosBeyondTheDonorsSpotsAreLeftOffNotAdded()
    {
        NewUniformTexture T(string kind) => new()
        {
            SourceSlot = KitDonor.Prefix + $"branding_pant_adidas_anaheim_home_{kind}_array",
            RequiredRoles = ["shield", "crest", "brand"],
            Artwork = new("", "", new Dictionary<int, string> { [0] = "s.png", [1] = "c.png", [2] = "b.png" }),
        };
        var a = new NewUniformAddition { Team = "edmonton", SourceKit = "home", Textures = [T("color"), T("normal")] };
        // Donor pants: NHL shield and team logo only. The NHL 27 brand mark has no spot of its kind and is left off.
        Assert.Equal(1, KitDonor.FitToSlots(a, _ => ["shield", "crest"]));
        Assert.All(a.Textures, t => Assert.Equal(["shield", "crest"], t.RequiredRoles));
        Assert.All(a.Textures, t => Assert.Equal(2, t.Artwork.Layers!.Count));
    }
}

public class HemFitTests
{
    static Rgba Hem(params (double From, byte Shade)[] bands)
    {
        int w = 4, h = 1000; var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            byte shade = bands.Last(b => y >= b.From * h).Shade;
            for (int x = 0; x < w; x++) { int at = (y * w + x) * 4; px[at] = px[at + 1] = px[at + 2] = shade; px[at + 3] = 255; }
        }
        return new Rgba(w, h, px);
    }

    [Fact]
    public void A_hem_that_matches_the_teams_nhl24_hem_is_not_moved()
    {
        // Florida: NHL 27's front hem (96.4%) and back hem (top 2.2%) are exactly where NHL 24's Florida has them.
        var florida = Hem((0, 30), (0.022, 160), (0.908, 160), (0.964, 30));
        Assert.All(HemFit.Plan(florida, florida), p => Assert.Null(p));
    }

    [Fact]
    public void Front_and_back_hems_move_together_to_the_teams_nhl24_hem()
    {
        // Boston: NHL 27 front 95.8% / back 2.8%; NHL 24 Boston 94.8% / 4.0%. Moving only the front stepped the stripe at the seams.
        var nhl27 = Hem((0, 200), (0.028, 120), (0.092, 250), (0.908, 120), (0.958, 200));
        var nhl24 = Hem((0, 200), (0.040, 120), (0.092, 250), (0.908, 120), (0.948, 200));
        var plan = HemFit.Plan(nhl27, nhl24);
        Assert.NotNull(plan[0]); Assert.NotNull(plan[1]);
        var fitted = HemFit.Fit(nhl27, plan);
        Assert.Equal(0.948, HemFit.LastChange(fitted)!.Value, 2);
        Assert.Equal(1 - 0.040, HemFit.LastChange(HemFit.Flip(fitted))!.Value, 2);
    }

    [Fact]
    public void A_high_nhl24_hem_design_is_not_a_target()
    {
        // St. Louis: NHL 24's Blues hem stripes end at 92.4%; NHL 27's at 96.5% only move to NHL 24's usual 94.8%.
        var nhl27 = Hem((0, 200), (0.023, 120), (0.092, 250), (0.908, 120), (0.965, 200));
        var nhl24 = Hem((0, 200), (0.064, 120), (0.092, 250), (0.908, 120), (0.924, 200));
        var plan = HemFit.Plan(nhl27, nhl24);
        Assert.Equal(HemFit.LastEdge, plan[0]!.Value.To, 3);
        Assert.Equal(HemFit.LastEdge + 0.012, plan[1]!.Value.To, 3);
    }

    [Fact]
    public void ALowHemIsMovedUpSoItsLastBandShows()
    {
        // Boston thirteen: last gold band starts at 95.8% — below what NHL 24's jersey shows.
        var boston = Hem((0, 250), (0.908, 120), (0.958, 200));
        Assert.Equal(0.958, HemFit.LastChange(boston)!.Value, 3);
        var moved = HemFit.Remap(boston, 0.958);
        Assert.Equal(HemFit.LastEdge, HemFit.LastChange(moved)!.Value, 2);
    }

    [Fact]
    public void AHemThatFitsIsLeftAlone()
    {
        var edmonton = Hem((0, 250), (0.902, 120), (0.946, 30));
        Assert.True(HemFit.LastChange(edmonton) <= HemFit.LastEdge);
    }
}

public class FontRemapTests
{
    static Rgba Sheet(int shift)
    {
        int s = 512; var px = new byte[s * s * 4];
        void Box(int x1, int y1, int x2, int y2) { for (int y = y1; y <= y2; y++) for (int x = x1; x <= x2; x++) px[(y * s + x) * 4 + 3] = 255; }
        for (int i = 0; i < 15; i++) Box(4 + i * 32 + shift, 4, 4 + i * 32 + shift + 20, 40);       // A-O
        for (int i = 0; i < 11; i++) Box(4 + i * 36 + shift, 54, 4 + i * 36 + shift + 20, 90);      // P-Z
        for (int i = 0; i < 5; i++) { Box(4 + i * 80 + shift, 110, 4 + i * 80 + shift + 60, 230); Box(4 + i * 80 + shift, 240, 4 + i * 80 + shift + 60, 360); }
        Box(440 + shift / 2, 60, 500, 180); Box(440 + shift / 2, 200, 500, 310);                         // C, A
        for (int i = 0; i < 10; i++) { Box(4 + i * 48 + shift, 372, 4 + i * 48 + shift + 30, 430); Box(4 + i * 48 + shift, 444, 4 + i * 48 + shift + 30, 505); }
        return new Rgba(s, s, px);
    }

    [Fact]
    public void EveryNhl27GlyphLandsInItsNhl24Box()
    {
        var pairs = FontRemap.Map(Sheet(6), Sheet(0));
        Assert.NotNull(pairs);
        Assert.Equal(15 + 11 + 5 + 5 + 2 + 10 + 10, pairs!.Count);
        Assert.All(pairs, p => Assert.Equal(p.From.Y1, p.To.Y1));
    }

    [Fact]
    public void A_row_running_past_the_band_edge_does_not_stretch_the_row_below()
    {
        // Florida's big 0-4 end just past 46% of the sheet; the 5-9 row below must keep its own top (no gold sliver).
        int s = 512; var px = new byte[s * s * 4];
        void Box(int x1, int y1, int x2, int y2) { for (int y = y1; y <= y2; y++) for (int x = x1; x <= x2; x++) px[(y * s + x) * 4 + 3] = 255; }
        for (int i = 0; i < 5; i++) { Box(4 + i * 80, 110, 64 + i * 80, 238); Box(4 + i * 80, 250, 64 + i * 80, 360); }
        var img = new Rgba(s, s, px);
        Assert.All(FontRemap.Glyphs(img, 3), g => Assert.Equal(250, g.Y1));
        Assert.All(FontRemap.Glyphs(img, 2), g => Assert.Equal(238, g.Y2));
    }
}

public class Nhl24MapConversionTests
{
    static Suite.Core.Rgba Solid(byte r, byte g, byte b, byte a) =>
        new(2, 2, Enumerable.Range(0, 16).Select(i => (i % 4) switch { 0 => r, 1 => g, 2 => b, _ => a }).ToArray());

    [Fact]
    public void Colour_keeps_nhl27_rgb_and_takes_nhl24_fabric_alpha()
    {
        var o = Suite.Convert27.KitDonor.ConvertMap(Solid(200, 10, 30, 68), Solid(1, 2, 3, 58), normal: false);
        Assert.Equal(new byte[] { 200, 10, 30, 58 }, o.Pixels[..4]);
    }

    [Fact]
    public void Normal_keeps_nhl27_xy_and_takes_nhl24_fold_shading()
    {
        var o = Suite.Convert27.KitDonor.ConvertMap(Solid(120, 140, 255, 255), Solid(128, 128, 90, 200), normal: true);
        Assert.Equal(new byte[] { 120, 140, 90, 200 }, o.Pixels[..4]);
    }

    [Fact]
    public void Different_sizes_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Suite.Convert27.KitDonor.ConvertMap(Solid(0, 0, 0, 0),
            new Suite.Core.Rgba(1, 1, new byte[4]), normal: false));
    }
}

public class UnpaintedLayersTests
{
    static Suite.Core.Rgba Sheet(int w, Action<byte[], int> paint)
    {
        var px = new byte[w * w * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 20; px[i + 1] = 30; px[i + 2] = 60; px[i + 3] = 64; }
        paint(px, w);
        return new Suite.Core.Rgba(w, w, px);
    }

    static void Box(byte[] px, int w, int x1, int y1, int x2, int y2)
    {
        for (int y = y1; y < y2; y++) for (int x = x1; x < x2; x++) { int at = (y * w + x) * 4; px[at] = 250; px[at + 1] = 250; px[at + 2] = 250; }
    }

    static readonly Suite.Convert27.UnpaintedLayers.Spot[] Jersey =
    [
        new("crest", .5, .7, .2), new("shield", .5, .58, .03), new("brand", .5, .44, .03), new("epw_tag", .39, .02, .02),
        new("shoulder", .39, .5, .08), new("shoulder", .61, .5, .08), new("sponsor", .43, .61, .05),
    ];

    [Fact]
    public void A_sponsor_layer_the_sheet_does_not_paint_goes_in_the_donors_sponsor_spot()
    {
        var plain = Sheet(200, (_, _) => { });
        var place = Suite.Convert27.UnpaintedLayers.Place(false, Jersey, ["crest", "shield", "sponsor", "epw_tag"], [0, 1, 2, 3], plain, plain);
        Assert.Equal(new Dictionary<int, int> { [6] = 2 }, place);
    }

    [Fact]
    public void A_painted_chest_sponsor_is_not_added_twice()
    {
        var plain = Sheet(200, (_, _) => { });
        var finished = Sheet(200, (px, w) => Box(px, w, 70, 115, 90, 128));
        Assert.Empty(Suite.Convert27.UnpaintedLayers.Place(false, Jersey, ["crest", "sponsor"], [0, 1], finished, plain));
    }

    [Fact]
    public void Pants_get_the_team_logo_never_the_shield()
    {
        var plain = Sheet(200, (_, _) => { });
        var finished = Sheet(200, (px, w) => Box(px, w, 95, 95, 105, 105)); // only the shield is painted
        Suite.Convert27.UnpaintedLayers.Spot[] pants = [new("shield", .5, .5, .05), new("crest", .3, .3, .1)];
        Assert.Equal(new Dictionary<int, int> { [1] = 1 }, Suite.Convert27.UnpaintedLayers.Place(true, pants, ["shield", "crest"], [0, 1], finished, plain));
        Assert.Empty(Suite.Convert27.UnpaintedLayers.Place(true, pants, ["shield"], [0], finished, plain));
    }
}

public class FabricZoneMapTests
{
    // 10x10 sheets: NHL 24 has body 36, a shoulder-yoke row 73, side panels 109 (column 0) and a collar 146 (column 9).
    static Suite.Core.Rgba Zones(Func<int, int, byte> zone)
    {
        var px = new byte[400];
        for (int y = 0; y < 10; y++) for (int x = 0; x < 10; x++) px[(y * 10 + x) * 4 + 3] = zone(x, y);
        return new Suite.Core.Rgba(10, 10, px);
    }

    [Fact]
    public void Nhl27_zone_shapes_take_nhl24_values_and_no_yoke()
    {
        var nhl24 = Zones((x, y) => x == 0 ? (byte)109 : x == 9 ? (byte)146 : y == 2 ? (byte)73 : (byte)36);
        var nhl27 = Zones((x, y) => x == 0 ? (byte)127 : x == 9 ? (byte)191 : (byte)64);
        var map = Suite.Convert27.KitDonor.ZoneMap(nhl27, nhl24);
        Assert.Equal(36, map[64]);
        Assert.Equal(109, map[127]);
        Assert.Equal(146, map[191]);
        Assert.Equal(36, map[70]); // an edge value takes the nearest real zone
        var converted = Suite.Convert27.KitDonor.ConvertMap(nhl27, nhl24, normal: false);
        Assert.Equal(36, converted.Pixels[(2 * 10 + 5) * 4 + 3]); // where NHL 24 had the yoke, NHL 27's body stays body
    }

    [Fact]
    public void Jersey_shading_is_flattened_to_the_donors_plain_fabric_level()
    {
        var nhl24 = new Suite.Core.Rgba(2, 2, [0, 0, 170, 255, 0, 0, 170, 255, 0, 0, 170, 255, 0, 0, 40, 255]);
        var nhl27 = new Suite.Core.Rgba(2, 2, [10, 20, 255, 255, 10, 20, 255, 255, 10, 20, 255, 255, 10, 20, 255, 255]);
        var o = Suite.Convert27.KitDonor.ConvertMap(nhl27, nhl24, normal: true, flattenShading: true);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(170, o.Pixels[i * 4 + 2]));
        Assert.Equal(10, o.Pixels[0]);
    }
}

public class StandardTagTests
{
    static NewUniformAddition Kit(string team, string png) => new()
    {
        Team = team, SourceKit = "home",
        Textures =
        [
            new NewUniformTexture
            {
                SourceSlot = $"content/x/branding_jersey_adidas_{team}_home_color_array",
                RequiredRoles = ["crest", "epw_tag"],
                Artwork = new TextureReplacement("s", "", new Dictionary<int, string> { [0] = png, [1] = png }),
            },
        ],
    };

    [Fact]
    public void The_standard_back_hem_tag_never_comes_from_chicago()
    {
        string png = Path.GetTempFileName();
        try
        {
            Assert.Null(KitDonor.FindStandardTag([Kit("chicago", png)]));
            var found = KitDonor.FindStandardTag([Kit("chicago", png), Kit("seattle", png)]);
            Assert.Equal(png, found!["color"]);
        }
        finally { File.Delete(png); }
    }
}
