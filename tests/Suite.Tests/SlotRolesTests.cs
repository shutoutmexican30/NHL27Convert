using Suite.Convert27;
using Suite.Core;
using Xunit;

namespace Suite.Tests;

/// <summary>
/// Labels real branding layers decoded on this PC (NHL 24 refs dumps + NHL 27 PNGs). On a machine without them the
/// test returns early: the images are the user's game data and are not part of the repository.
/// </summary>
public class SlotRolesTests
{
    static readonly string Local = SuitePaths.Local;

    static List<(int, string?)> Nhl24(string folder, string garment, int n)
    {
        var (t, b, s) = Split(folder);
        var dir = Path.Combine(Local, "nhl24refs", "content_character_skater_league_nhl_" + folder.Replace('/', '_'));
        return Enumerable.Range(0, n).Select(i => (i, Existing(Path.Combine(dir, $"branding_{garment}_{b}_{t}_{s}_{i}_color.dds")))).ToList();
    }

    static List<(int, string?)> Nhl27(string folder, string garment, int n)
    {
        var (t, b, s) = Split(folder);
        return Enumerable.Range(0, n).Select(i => (i, Existing(Path.Combine(Local, "nhl27png",
            "content_character_skater_league_nhl_" + folder.Replace('/', '_') + $"_branding_{garment}_{b}_{t}_{s}_{i}_color.png")))).ToList();
    }

    static (string, string, string) Split(string f) { var p = f.Split('/'); return (p[0], p[1], p[2]); }
    static string? Existing(string p) => File.Exists(p) ? p : null;
    static string Roles(List<LayerLabel> l) => string.Join(",", l.OrderBy(x => x.Layer).Select(x => x.Role));

    [Fact]
    public void Labels_and_maps_real_layers_by_role()
    {
        var stl24 = Nhl24("stlouis/adidas/home", "jersey", 6);
        var stl27 = Nhl27("stlouis/adidas/fourteen", "jersey", 6);
        var ari24 = Nhl24("arizona/adidas/home", "jersey", 6);
        var uta27 = Nhl27("utah/adidas/twelve", "jersey", 6);
        if (new[] { stl24, stl27, ari24, uta27 }.Any(l => l.Any(x => x.Item2 is null))) return; // no game data here

        var a = SlotRoles.Label(stl24);
        var b = SlotRoles.Label(stl27);
        Assert.Equal("crest,shield,brand,epw_tag,sponsor,shield", Roles(a));
        Assert.Equal("adidas", a[2].Detail);
        Assert.Equal("fanatics", b.Single(l => l.Role == "brand").Detail);
        var (links, noSlot) = SlotRoles.Map(a, b);
        Assert.Equal(2, links.Single(l => l.Nhl24 == 2).Nhl27);     // adidas → Fanatics
        Assert.Equal(3, links.Single(l => l.Nhl24 == 1).Nhl27);     // first shield → NHL 27's first shield
        Assert.Null(links.Single(l => l.Nhl24 == 3).Nhl27);         // End Plastic Waste: NHL 27 has none, stays stock
        Assert.NotEmpty(noSlot);                                     // NHL 27's second Stifel has no NHL 24 slot

        var c = SlotRoles.Label(ari24);
        var d = SlotRoles.Label(uta27);
        Assert.Equal("crest,shoulder,shoulder,brand,shield,epw_tag", Roles(c));
        var (l2, _) = SlotRoles.Map(c, d);
        Assert.Equal(0, l2.Single(l => l.Nhl24 == 0).Nhl27);        // coyote crest → mammoth crest
        Assert.Equal(3, l2.Single(l => l.Nhl24 == 3).Nhl27);        // adidas → Fanatics
        Assert.Equal(4, l2.Single(l => l.Nhl24 == 4).Nhl27);        // shield → shield
    }
}
