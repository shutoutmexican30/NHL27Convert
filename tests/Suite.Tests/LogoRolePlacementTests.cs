using Suite.Convert27;
using Xunit;

namespace Suite.Tests;

/// <summary>Regression for Boston's giant Fanatics logo: logos go to the slot whose physical role matches, never by slot number.</summary>
public class LogoRolePlacementTests
{
    static readonly string[] BostonArt = ["crest", "shoulder", "shoulder", "sponsor", "brand", "shield"];

    [Fact]
    public void BostonsBrandLogoGoesToTheBrandSlotNotTheCrest()
    {
        string[] physical = ["crest", "shield", "brand", "epw_tag", "shoulder", "shoulder", "sponsor"];
        var placement = UniformBrandingLayers.PlanRoles(BostonArt, physical);
        for (int layer = 0; layer < BostonArt.Length; layer++)
            Assert.Equal(BostonArt[layer], physical[placement.Destination(layer)]);
        Assert.Equal(2, placement.Destination(4));
    }

    [Fact]
    public void ALogoWithNoSlotOfItsRoleStopsTheInstall()
    {
        string[] physical = ["crest", "shield", "brand", "epw_tag", "crest", "shoulder"];
        Assert.Throws<InvalidDataException>(() => UniformBrandingLayers.PlanRoles(BostonArt, physical));
    }

    [Fact]
    public void EveryStandardLayoutKeepsRolesAligned()
    {
        string[] art = ["crest", "shield", "brand", "sponsor", "shoulder", "shoulder"];
        string[] physical = ["crest", "shield", "brand", "epw_tag", "shoulder", "shoulder", "sponsor"];
        var placement = UniformBrandingLayers.PlanRoles(art, physical);
        for (int layer = 0; layer < art.Length; layer++)
            Assert.Equal(art[layer], physical[placement.Destination(layer)]);
        Assert.Equal(6, placement.Destination(3));
    }
}

public class MatcherOverrideTests
{
    [Fact]
    public void AManualLinkMovesALogoAgainstItsRole()
    {
        string[] art = ["crest", "shield", "brand", "sponsor"];
        string[] physical = ["crest", "shield", "brand", "epw_tag", "sponsor", "shoulder"];
        var placement = Suite.Convert27.UniformBrandingLayers.PlanRoles(art, physical, null, new Dictionary<int, int> { [3] = 5 });
        Assert.Equal(5, placement.Destination(3));
        Assert.Equal(0, placement.Destination(0));
    }

    [Fact]
    public void AManualRoleOverrideReplacesTheAutomaticLabel()
    {
        var texture = new Suite.Core.NewUniformTexture
        {
            SourceSlot = "content/character/skater/league/nhl/chicago/adidas/home/branding_jersey_adidas_chicago_home_color_array",
            RequiredRoles = ["crest", "sponsor"],
            Artwork = new Suite.Core.TextureReplacement("", "", new Dictionary<int, string>
                { [0] = @"x\branding_jersey_adidas_chicago_seven_0_color.png", [1] = @"x\branding_jersey_adidas_chicago_seven_1_color.png" }),
        };
        var addition = new Suite.Core.NewUniformAddition
        {
            Team = "chicago", SourceKit = "home", SourcePrefix = "content/character/skater/league/nhl/chicago/adidas/home/",
            SourceFolder27 = "content/character/skater/league/nhl/chicago/adidas/seven", Textures = [texture],
        };
        var store = new Suite.Convert27.SlotMapStore();
        store.Entries.Add(new Suite.Convert27.SlotMapEntry
        {
            Folder24 = "content/character/skater/league/nhl/chicago/adidas/home", Folder27 = "content/character/skater/league/nhl/chicago/adidas/seven",
            Garment = "branding_jersey", Layers27 = [new() { Layer = 1, Role = "shoulder", Manual = true }],
            Links = [new() { Nhl24 = 4, Nhl27 = 1, Manual = true }],
        });
        Assert.True(Suite.Convert27.MatcherOverrides.Apply([addition], store) > 0);
        Assert.Equal("shoulder", texture.RequiredRoles[1]);
        Assert.Equal(4, texture.PinnedSlots![1]);
    }
}
