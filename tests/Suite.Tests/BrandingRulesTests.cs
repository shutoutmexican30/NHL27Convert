using System.Text.Json;
using Suite.Convert27;
using Suite.Core;

namespace Suite.Tests;

public class BrandingRulesTests
{
    const string ColorSlot = "content/character/skater/league/nhl/stlouis/adidas/home/branding_jersey_adidas_stlouis_home_color_array";
    static LayerLabel L(int layer, string role, string? detail = null) => new() { Layer = layer, Role = role, Detail = detail };

    [Fact]
    public void Adidas_is_replaced_by_Fanatics_even_when_layer_numbers_differ_or_old_pick_is_manual()
    {
        var e = new SlotMapEntry
        {
            Layers24 = [L(2, "brand", "adidas"), L(3, SlotRoles.EpwRole)],
            Layers27 = [L(2, "shield"), L(4, "brand", "fanatics")],
            Links = [new() { Nhl24 = 2, Nhl27 = 2, Manual = true }, new() { Nhl24 = 3 }],
        };
        ManufacturerBranding.RequireFanaticsLinks(e);
        Assert.Equal(4, e.Links[0].Nhl27);
        Assert.Null(e.Links[1].Nhl27);
    }

    [Fact]
    public void Missing_Fanatics_never_falls_back_to_an_Adidas_or_shield_image()
    {
        var e = new SlotMapEntry { Layers24 = [L(2, "brand", "adidas_stripes")],
            Layers27 = [L(2, "shield"), L(3, "brand", "adidas")], Links = [new() { Nhl24 = 2, Nhl27 = 2 }] };
        ManufacturerBranding.RequireFanaticsLinks(e);
        Assert.Null(e.Links[0].Nhl27);
    }

    [Fact]
    public void Export_replaces_colour_alpha_and_normal_mask_and_rebuilds_coeff_from_Fanatics()
    {
        var normal = ColorSlot.Replace("_color_array", "_normal_array");
        var mod = new ModFile { Replacements = [new(ColorSlot, "", new() { [0] = "club.png", [2] = "adidas.png" }),
            new(normal, "", new() { [0] = "club-normal.png", [2] = "adidas-normal.png" })] };
        ManufacturerBranding.ApplyToMod(mod, [new(ColorSlot, 2, "fanatics.png", "fanatics-normal.png")]);
        Assert.Equal("fanatics.png", mod.Replacements.Single(r => r.Slot == ColorSlot).Layers![2]);
        Assert.Equal("fanatics-normal.png", mod.Replacements.Single(r => r.Slot == normal).Layers![2]);
        Assert.Equal("club-normal.png", mod.Replacements.Single(r => r.Slot == normal).Layers![0]);
        Assert.Equal("coeff:fanatics.png", mod.Replacements.Single(r => r.Slot.EndsWith("_coeff_array")).Layers![2]);
        Assert.Equal(3, mod.Replacements.Count);
    }

    [Fact]
    public void Matcher_shows_the_matching_Fanatics_normal_instead_of_the_old_Adidas_normal()
    {
        var normal = ColorSlot.Replace("_color_array", "_normal_array");
        var rows = new[] { new SlotMatch { Slot = ColorSlot, IsArray = true, Layers = [new() { Layer = 2, Source27 = "wrong-color" }] },
            new SlotMatch { Slot = normal, IsArray = true, Layers = [new() { Layer = 2, Source27 = "adidas-normal" }] } };
        ManufacturerBranding.ApplyToRows(rows, new(ColorSlot, 2, "fanatics.png", "fanatics-normal.png"));
        Assert.Equal("fanatics-normal.png", rows[1].Layers[0].FilePath);
        Assert.Null(rows[1].Layers[0].Source27);
    }

    [Fact]
    public void Pants_club_logo_cannot_link_to_NHL_shield_at_the_same_index()
    {
        var e = new SlotMapEntry { Garment = "branding_pant", Layers24 = [L(0, "crest"), L(2, "shield")],
            Layers27 = [L(0, "shield"), L(1, "crest")], Links = [new() { Nhl24 = 0, Nhl27 = 0, Manual = true }] };
        Assert.Equal(1, SlotRoles.EnforcePantsCrestLock(e));
        Assert.Null(e.Links[0].Nhl27);
        var (links, _) = SlotRoles.Map(e.Layers24, e.Layers27);
        Assert.Equal(1, links.Single(l => l.Nhl24 == 0).Nhl27);
    }

    [Fact]
    public void Pants_never_take_the_NHL_shield_and_shield_slots_stay_stock()
    {
        // Every pants set already has the shield on the back: shield→shield is not converted either.
        var e = new SlotMapEntry { Garment = "branding_pant", Layers24 = [L(0, "shield"), L(1, "crest"), L(2, "sponsor")],
            Layers27 = [L(0, "crest"), L(1, "shield"), L(2, "shield")],
            Links = [new() { Nhl24 = 0, Nhl27 = 1 }, new() { Nhl24 = 1, Nhl27 = 0 }, new() { Nhl24 = 2, Nhl27 = 2, Manual = true }] };
        Assert.Equal(2, SlotRoles.EnforcePantsCrestLock(e));
        Assert.Null(e.Links[0].Nhl27);
        Assert.Equal(0, e.Links[1].Nhl27);
        Assert.Null(e.Links[2].Nhl27);
    }

    [Fact]
    public void Unmatched_pants_club_slot_keeps_stock_across_colour_normal_and_coeff()
    {
        var mod = new ModFile { Replacements = [new(ColorSlot, "", new() { [0] = "wrong-shield", [3] = "tag" }),
            new(ColorSlot.Replace("_color_array", "_normal_array"), "", new() { [0] = "wrong-shield-normal" })] };
        ManufacturerBranding.KeepStockLayer(mod, ColorSlot, 0);
        Assert.Single(mod.Replacements);
        Assert.Equal("tag", mod.Replacements[0].Layers![3]);
        Assert.False(mod.Replacements[0].Layers!.ContainsKey(0));
    }

    [Fact]
    public void Pants_classification_finds_club_mark_after_the_shield()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "test-artifacts", "pants-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            using var refs = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "logo_refs.json")));
            var alpha = refs.RootElement.GetProperty("refs").GetProperty("shield").EnumerateArray().Select(v => (byte)Math.Round(v.GetDouble())).ToArray();
            var shield = new byte[32 * 32 * 4]; var club = new byte[shield.Length];
            for (int i = 0; i < alpha.Length; i++) { shield[i * 4 + 3] = alpha[i]; club[i * 4 + 3] = (byte)(i % 2 * 255); }
            var shieldPath = Path.Combine(dir, "shield.png"); var clubPath = Path.Combine(dir, "club.png");
            ImageIO.SavePng(new Rgba(32, 32, shield), shieldPath); ImageIO.SavePng(new Rgba(32, 32, club), clubPath);
            var labels = SlotRoles.Label([(0, shieldPath), (1, clubPath)], "branding_pant");
            Assert.Equal("shield", labels.Single(l => l.Layer == 0).Role);
            Assert.Equal("crest", labels.Single(l => l.Layer == 1).Role);
        }
        finally { Directory.Delete(dir, true); }
    }
}
