using Suite.Convert27;
using Suite.Core;

namespace Suite.Tests;

public class LogoSlotRegressionTests
{
    const string Prefix = "content/character/skater/league/nhl";

    static Nhl27Texture Tex(string path) => new(new AssetEntry
    {
        Game = "nhl27", Kind = AssetKind.Texture, Path = path, Width = 2048, Height = 2048,
    });

    static SlotMatch AnaheimHomeArray() => new()
    {
        Slot = $"{Prefix}/anaheim/adidas/home/branding_jersey_adidas_anaheim_home_color_array",
        IsArray = true,
        Kind = SourceKind.Nhl27,
        Layers = Enumerable.Range(0, 7).Select(i => new LayerMatch { Layer = i }).ToList(),
    };

    static SlotMatch AnaheimNormalArray() => new()
    {
        Slot = $"{Prefix}/anaheim/adidas/home/branding_jersey_adidas_anaheim_home_normal_array",
        IsArray = true,
        Kind = SourceKind.Nhl27,
        Layers = Enumerable.Range(0, 7).Select(i => new LayerMatch { Layer = i }).ToList(),
    };

    static SlotMapEntry AnaheimHomeMap()
    {
        string[] roles = ["crest", "shield", "brand", SlotRoles.EpwRole, "shoulder", "shoulder", "sponsor"];
        int?[] sources = [0, 3, 4, null, 1, 2, 5];
        return new SlotMapEntry
        {
            Folder24 = $"{Prefix}/anaheim/adidas/home",
            Folder27 = $"{Prefix}/anaheim/adidas/ten",
            Garment = "branding_jersey",
            Links = Enumerable.Range(0, roles.Length)
                .Select(i => new LayerLink { Nhl24 = i, Nhl27 = sources[i], Role = roles[i] }).ToList(),
        };
    }

    static Dictionary<int, string> AnaheimSources() => Enumerable.Range(0, 6).ToDictionary(
        i => i, i => $"{Prefix}/anaheim/adidas/ten/branding_jersey_adidas_anaheim_ten_{i}_color");

    [Fact]
    public void Anaheim_home_array_repairs_stale_crest_and_fills_both_D_shoulder_slots()
    {
        var row = AnaheimHomeArray();
        var sources = AnaheimSources();
        row.Layers[0].Source27 = sources[1]; // Old saved conversion: shoulder D where the crest belongs.
        row.Layers[1].Source27 = sources[3];
        row.Layers[2].Source27 = sources[4];
        row.Layers[6].Source27 = sources[5];

        int changed = SlotRoles.ReconcileColourArray(row, AnaheimHomeMap(), sources);

        Assert.Equal(3, changed);
        Assert.Equal(sources[0], row.Layers[0].Source27);
        Assert.Equal(sources[1], row.Layers[4].Source27);
        Assert.Equal(sources[2], row.Layers[5].Source27);
        Assert.Equal(sources[5], row.Layers[6].Source27);
        Assert.All(row.Layers, l => Assert.Null(l.FilePath));
    }

    [Fact]
    public void Role_map_clears_stale_automatic_stock_layers_but_never_overrides_a_manual_array()
    {
        var sources = AnaheimSources();
        var map = AnaheimHomeMap();
        var row = AnaheimHomeArray();
        foreach (var link in map.Links.Where(l => l is { Nhl24: not 3, Nhl27: not null }))
            row.Layers[link.Nhl24].Source27 = sources[link.Nhl27!.Value];
        row.Layers[3].Source27 = sources[5]; // EPW back-tag slot must stay stock.
        Assert.Equal(1, SlotRoles.ReconcileColourArray(row, map, sources));
        Assert.Null(row.Layers[3].Source27);

        var manual = AnaheimHomeArray();
        manual.Manual = true;
        manual.Layers[0].Source27 = sources[1];
        manual.Layers[4].FilePath = "custom-shoulder.png";
        Assert.Equal(0, SlotRoles.ReconcileColourArray(manual, AnaheimHomeMap(), sources));
        Assert.Equal(sources[1], manual.Layers[0].Source27);
        Assert.Equal("custom-shoulder.png", manual.Layers[4].FilePath);
    }

    [Fact]
    public void Corrected_colour_slots_reconcile_matching_normals_and_clear_stock_layers()
    {
        var sources = AnaheimSources();
        var map = AnaheimHomeMap();
        var color = AnaheimHomeArray();
        var normal = AnaheimNormalArray();
        string Normal(string source) => source[..^"_color".Length] + "_normal";

        foreach (var link in map.Links.Where(l => l.Nhl27 is not null))
        {
            color.Layers[link.Nhl24].Source27 = sources[link.Nhl27!.Value];
            normal.Layers[link.Nhl24].Source27 = Normal(sources[link.Nhl27!.Value]);
        }
        normal.Layers[0].Source27 = Normal(sources[1]); // Stale D relief under the crest.
        normal.Layers[4].Source27 = Normal(sources[0]);
        normal.Layers[5].Source27 = Normal(sources[0]);
        normal.Layers[3].Source27 = Normal(sources[5]); // Colour returned to stock; normal must follow.

        Assert.Equal(4, ManufacturerBranding.PropagateNormals([color, normal], _ => true));
        Assert.Equal(Normal(sources[0]), normal.Layers[0].Source27);
        Assert.Equal(Normal(sources[1]), normal.Layers[4].Source27);
        Assert.Equal(Normal(sources[2]), normal.Layers[5].Source27);
        Assert.Null(normal.Layers[3].Source27);
        Assert.Null(normal.Layers[3].FilePath);
    }

    [Fact]
    public void Normal_reconciliation_preserves_manual_rows_and_explicit_manufacturer_files()
    {
        var sources = AnaheimSources();
        var color = AnaheimHomeArray();
        var normal = AnaheimNormalArray();
        color.Layers[0].Source27 = sources[0];
        normal.Layers[0].FilePath = "fanatics-normal.png";

        Assert.Equal(0, ManufacturerBranding.PropagateNormals([color, normal], _ => true));
        Assert.Equal("fanatics-normal.png", normal.Layers[0].FilePath);

        normal.Manual = true;
        normal.Layers[0].FilePath = null;
        normal.Layers[0].Source27 = "custom-normal";
        Assert.Equal(0, ManufacturerBranding.PropagateNormals([color, normal], _ => true));
        Assert.Equal("custom-normal", normal.Layers[0].Source27);
    }

    [Fact]
    public void Utah_saved_base_composite_uses_unbaked_base_art_not_the_plain_crest_map()
    {
        const string folder = Prefix + "/utah/adidas/thirteen";
        const string basePath = folder + "/jersey_adidas_utah_thirteen_base_color";
        var row = new SlotMatch
        {
            Slot = Prefix + "/arizona/adidas/away/jersey_adidas_arizona_away_base_color",
            Kind = SourceKind.File,
            FilePath = "old-composite.png",
            Reason = BaseColourRules.LegacyCompositeReason + " (keeps a sponsor that has no logo slot)",
        };
        var textures = new[]
        {
            Tex(folder + "/jersey_adidas_utah_thirteen_color"),
            Tex(basePath),
        };

        Assert.Equal(1, BaseColourRules.RepairAutomaticBaseColours([row], textures));
        Assert.Equal(SourceKind.Nhl27, row.Kind);
        Assert.Equal(basePath, row.Source27);
        Assert.Null(row.FilePath);
        Assert.Equal("auto", row.Alpha);
    }

    [Fact]
    public void Old_composite_without_an_exact_base_map_returns_to_stock_and_manual_art_is_preserved()
    {
        const string slot = Prefix + "/arizona/adidas/away/jersey_adidas_arizona_away_base_color";
        var automatic = new SlotMatch
        {
            Slot = slot, Kind = SourceKind.File, FilePath = "old-composite.png",
            Reason = BaseColourRules.LegacyCompositeReason,
        };
        Assert.Equal(1, BaseColourRules.RepairAutomaticBaseColours([automatic], []));
        Assert.Equal(SourceKind.Stock, automatic.Kind);
        Assert.Null(automatic.FilePath);

        var manual = new SlotMatch
        {
            Slot = slot, Kind = SourceKind.File, FilePath = "my-custom-base.png", Manual = true,
            Reason = BaseColourRules.LegacyCompositeReason,
        };
        Assert.Equal(0, BaseColourRules.RepairAutomaticBaseColours([manual], []));
        Assert.Equal("my-custom-base.png", manual.FilePath);
    }

    [Fact]
    public void Automatic_plain_preview_fallback_returns_to_stock_when_no_unbaked_base_exists()
    {
        const string folder = Prefix + "/utah/adidas/thirteen";
        var row = new SlotMatch
        {
            Slot = Prefix + "/arizona/adidas/away/jersey_adidas_arizona_away_base_color",
            Kind = SourceKind.Nhl27,
            Source27 = folder + "/jersey_adidas_utah_thirteen_color",
        };

        Assert.Equal(1, BaseColourRules.RepairAutomaticBaseColours([row],
            [Tex(folder + "/jersey_adidas_utah_thirteen_color")]));
        Assert.Equal(SourceKind.Stock, row.Kind);
        Assert.Null(row.Source27);
        Assert.Contains("duplicate", row.Reason);
    }
}
