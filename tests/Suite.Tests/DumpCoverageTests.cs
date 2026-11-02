using Suite.Convert27;
using Xunit;

namespace Suite.Tests;

/// <summary>A partial stock dump must not be trusted: Carolina and Calgary had no branding art cached at all.</summary>
public class DumpCoverageTests
{
    static string Dir(params string[] names)
    {
        var d = Path.Combine(Path.GetTempPath(), "cims-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        foreach (var n in names) File.WriteAllText(Path.Combine(d, n + ".dds"), "x");
        return d;
    }

    static readonly string[] Expected =
    [
        "content/x/branding_jersey_adidas_team_home_color_array",
        "content/x/branding_jersey_adidas_team_home_0_color",
        "content/x/jersey_adidas_team_home_base_color",
    ];

    [Fact]
    public void A_dump_with_the_array_slices_is_accepted()
    {
        var d = Dir("branding_jersey_adidas_team_home_color_array#0", "branding_jersey_adidas_team_home_color_array#1",
                    "branding_jersey_adidas_team_home_0_color", "jersey_adidas_team_home_base_color");
        try { Assert.True(TextureImages.DumpCovers(d, Expected)); } finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void A_dump_without_the_branding_array_is_rejected()
    {
        var d = Dir("jersey_adidas_team_home_base_color", "font_adidas_team_home_color");
        try { Assert.False(TextureImages.DumpCovers(d, Expected)); } finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void No_expectation_means_no_opinion()
    {
        var d = Dir("anything");
        try { Assert.True(TextureImages.DumpCovers(d, null)); } finally { Directory.Delete(d, true); }
    }
}