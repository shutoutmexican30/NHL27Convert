using Suite.Convert27.Studio;
using Xunit;

namespace Suite.Tests;

public class StudioMatcherTests
{
    const string Folder = "content/character/skater/league/nhl/detroit/adidas/home/";
    const string Folder27 = "content/character/skater/league/nhl/detroit/adidas/ten/";

    static readonly IReadOnlySet<string> Teams = new HashSet<string> { "detroit", "boston" };

    static StudioCard Card(string leaf, int? layer = null) => new()
    {
        Slot = Folder + leaf,
        Layer = layer,
        Key = MaterialKey.Of(leaf, Teams),
    };

    static ReplacementFile File(string leaf) => new()
    {
        Path = @"C:\cache\" + leaf + ".png",
        Source = "NHL 27 ten",
        Key = MaterialKey.Of(leaf, Teams),
    };

    [Fact]
    public void Numbered_branding_layers_are_not_auto_replaced()
    {
        var cards = new List<StudioCard> { Card("branding_jersey_adidas_detroit_home_0_color") };
        var files = new List<ReplacementFile> { File("branding_jersey_adidas_detroit_ten_0_color") };

        StudioMatcher.AutoMatch(cards, files);

        var card = Assert.Single(cards);
        Assert.False(card.Replace);
        Assert.Null(card.New);
        Assert.Contains("numbered layer", card.Reason);
    }

    [Fact]
    public void Numbered_layers_still_take_an_exact_asset_name_match()
    {
        var cards = new List<StudioCard> { Card("branding_jersey_adidas_detroit_home_3_color") };
        var files = new List<ReplacementFile> { File("branding_jersey_adidas_detroit_home_3_color") };

        StudioMatcher.AutoMatch(cards, files);

        Assert.True(cards[0].Replace);
        Assert.Equal(1.0, cards[0].Score);
    }

    [Fact]
    public void Main_uniform_pieces_still_auto_match_across_sets()
    {
        var cards = new List<StudioCard> { Card("jersey_adidas_detroit_home_color") };
        var files = new List<ReplacementFile> { File("jersey_adidas_detroit_ten_color") };

        StudioMatcher.AutoMatch(cards, files);

        Assert.True(cards[0].Replace);
        Assert.Equal(0.9, cards[0].Score);
    }

    [Fact]
    public void Socks_are_never_matched_to_the_number_font()
    {
        var cards = new List<StudioCard> { Card("sock_adidas_detroit_home_color") };
        var files = new List<ReplacementFile> { File("font_adidas_detroit_ten_color") };

        StudioMatcher.AutoMatch(cards, files);

        Assert.False(cards[0].Replace);
        Assert.Null(cards[0].New);
    }
}
