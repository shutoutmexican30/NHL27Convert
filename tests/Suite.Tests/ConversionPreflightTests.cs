using Suite.Convert27;
using Suite.Core;

namespace Suite.Tests;

public class ConversionPreflightTests
{
    const string Folder = "content/character/skater/league/nhl/anaheim/adidas";
    const string ColorSlot = Folder + "/home/branding_jersey_adidas_anaheim_home_color_array";
    const string NormalSlot = Folder + "/home/branding_jersey_adidas_anaheim_home_normal_array";
    const string Color27 = Folder + "/ten/branding_jersey_adidas_anaheim_ten_0_color";
    const string Normal27 = Folder + "/ten/branding_jersey_adidas_anaheim_ten_0_normal";

    static SlotMatch Base(bool manual = false, bool baked = true) => new()
    {
        Slot = Folder + "/home/jersey_adidas_anaheim_home_base_color",
        Kind = SourceKind.Nhl27,
        Source27 = Folder + "/ten/jersey_adidas_anaheim_ten_" + (baked ? "color" : "base_color"),
        Manual = manual,
    };

    static SlotMatch Array(string slot, string? source, bool manual = false) => new()
    {
        Slot = slot, IsArray = true, Manual = manual,
        Layers = [new LayerMatch { Layer = 0, Source27 = source }],
    };

    [Fact]
    public void Standard_mode_is_the_default_and_folder_names_are_friendly()
    {
        var vm = new Convert27ViewModel(new SuiteSettings());
        Assert.False(vm.AdvancedMode);
        Assert.Equal("Choose a uniform, then select Check & convert.", vm.PreflightSummary);
        Assert.Equal("Anaheim · Home", Convert27ViewModel.FriendlyFolder(Color27[..Color27.LastIndexOf('/')]));
        Assert.Equal("Utah · Away", Convert27ViewModel.FriendlyFolder(Folder.Replace("anaheim", "utah") + "/eleven"));
    }

    [Fact]
    public void Unbaked_base_and_matching_logo_normal_pass_preflight()
    {
        var items = ConversionPreflight.InspectRows(
            [Base(baked: false), Array(ColorSlot, Color27), Array(NormalSlot, Normal27)], _ => true, _ => true).ToList();
        Assert.Empty(items);
        var report = new PreflightReport(items);
        Assert.True(report.CanExport);
        Assert.Equal("Ready to export — all checks passed", report.Summary);
    }

    [Fact]
    public void Baked_base_and_stale_normal_are_explained_and_block_automatic_export()
    {
        var wrongNormal = Folder + "/ten/branding_jersey_adidas_anaheim_ten_1_normal";
        var items = ConversionPreflight.InspectRows(
            [Base(), Array(ColorSlot, Color27), Array(NormalSlot, wrongNormal)], _ => true, _ => true).ToList();
        var report = new PreflightReport(items);
        Assert.Equal(2, report.ErrorCount);
        Assert.False(report.CanExport);
        Assert.Contains(items, i => i.Title.Contains("duplicate"));
        Assert.Contains(items, i => i.Title.Contains("surface relief"));
    }

    [Fact]
    public void Manual_risk_is_a_warning_not_an_automatic_overwrite()
    {
        var items = ConversionPreflight.InspectRows(
            [Base(manual: true), Array(ColorSlot, Color27, manual: true), Array(NormalSlot, null)], _ => true, _ => true).ToList();
        var report = new PreflightReport(items);
        Assert.Equal(2, report.WarningCount);
        Assert.Equal(0, report.ErrorCount);
        Assert.True(report.CanExport);
    }

    [Fact]
    public void Missing_artwork_is_a_blocking_plain_english_result()
    {
        var items = ConversionPreflight.InspectRows([Base(baked: false)], _ => false, _ => true).ToList();
        var issue = Assert.Single(items);
        Assert.Equal(PreflightSeverity.Error, issue.Severity);
        Assert.Equal("Artwork is unavailable", issue.Title);
        Assert.Contains("base_color", issue.Detail);
    }

    [Fact]
    public void Logos_without_a_stock_slot_get_one_added_and_are_reported()
    {
        var entry = new SlotMapEntry
        {
            Garment = "branding_jersey",
            NoSlot27 = [1, 5],
            Layers27 = [new LayerLabel { Layer = 1, Role = "crest" }, new LayerLabel { Layer = 5, Role = "sponsor" }],
        };
        var items = ConversionPreflight.InspectUnplacedLogos(entry).ToList();
        Assert.Equal(2, items.Count);
        // Add to game now adds the missing position instead of dropping the logo or blocking the export.
        Assert.All(items, i => Assert.Equal(PreflightSeverity.Repaired, i.Severity));
        Assert.Contains(items, i => i.Title.Contains("crest") && i.Detail.Contains("slot map"));
        Assert.Contains(items, i => i.Title.Contains("sponsor"));
    }
}
