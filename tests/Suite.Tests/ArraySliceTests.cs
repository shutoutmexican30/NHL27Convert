using Suite.Convert27;
using Xunit;

namespace Suite.Tests;

/// <summary>The array's real slice count decides how many layer rows may exist; filled rows are never discarded.</summary>
public class ArraySliceTests
{
    static Dictionary<string, string> Stock(string slot, int slices)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < slices; i++) d[$"{slot}#{i}"] = $@"C:\fake\{i}.png";
        return d;
    }

    [Fact]
    public void Slice_count_walks_from_zero_until_the_first_gap()
    {
        const string slot = "content/x/branding_jersey_adidas_team_home_color_array";
        Assert.Equal(4, Matcher.SliceCount(Stock(slot, 4), slot));
        Assert.Equal(0, Matcher.SliceCount(new Dictionary<string, string>(), slot));
        Assert.Equal(0, Matcher.SliceCount(Stock("something/else", 4), slot));
    }

    [Fact]
    public void Clamping_hides_empty_rows_past_the_last_slice_and_keeps_filled_ones()
    {
        var row = new SlotMatch
        {
            Slot = "content/x/branding_jersey_adidas_team_home_color_array",
            IsArray = true,
            Layers =
            [
                new() { Layer = 0 },
                new() { Layer = 1 },
                new() { Layer = 2 },
                new() { Layer = 3 },
                new() { Layer = 4 },
                new() { Layer = 5, Source27 = "content/x/tex" },
            ],
        };
        Assert.Equal(1, Matcher.ClampToSlices(row, 4));
        Assert.Equal(new[] { 0, 1, 2, 3, 5 }, row.Layers.Select(l => l.Layer).Order().ToArray());
    }

    [Fact]
    public void A_row_that_never_had_layers_is_left_alone()
    {
        var row = new SlotMatch { Slot = "a/b_color_array", IsArray = true };
        Assert.Equal(0, Matcher.ClampToSlices(row, 4));
        Assert.Empty(row.Layers);
    }
}