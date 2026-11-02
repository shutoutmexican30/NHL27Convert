using Suite.Convert27;
using Suite.Core;
using Xunit;

namespace Suite.Tests;

/// <summary>A mod list is only appliable when every source it names still exists.</summary>
public class ModValidationTests
{
    static string TempFile(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "cims-mod-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void A_mod_whose_sources_exist_has_no_problems()
    {
        var png = TempFile("art.png");
        var mod = new ModFile { Name = "ok", Replacements =
            [new("a/b_color", png), new("a/b_color_array", "", new() { [0] = png, [2] = "coeff:" + png })] };
        Assert.Empty(ModApply.SourceProblems([mod]));
    }

    [Fact]
    public void A_mapped_layer_the_NHL24_array_does_not_have_is_dropped_from_the_mod()
    {
        const string slot = "content/x/y/branding_jersey_adidas_team_home_color_array";
        var mod = new ModFile { Name = "t", Replacements = [new(slot, "", new() { [1] = "a.png", [5] = "b.png" })] };
        ManufacturerBranding.PruneLayers(mod, slot[..^"_color_array".Length], [5]);
        var layers = mod.Replacements.Single().Layers!;
        Assert.Equal("a.png", layers[1]);
        Assert.False(layers.ContainsKey(5));
    }
    [Fact]
    public void Missing_sources_and_empty_array_entries_are_reported_with_the_slot_name()
    {
        var mod = new ModFile { Name = "broken", Replacements =
            [new("content/x/y/branding_jersey_adidas_team_home_color", @"C:\nope\missing.png"),
             new("content/x/y/branding_jersey_adidas_team_home_color_array", "", new() { [1] = @"C:\nope\gone.png" }),
             new("content/x/y/branding_jersey_adidas_team_home_normal_array", "")] };
        var problems = ModApply.SourceProblems([mod]);
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("branding_jersey_adidas_team_home_color ") && p.Contains("broken"));
        Assert.Contains(problems, p => p.Contains("layer 1"));
        Assert.Contains(problems, p => p.Contains("no layers"));
    }
}