using Suite.Convert27;
using Xunit;

namespace Suite.Tests;

/// <summary>End Plastic Waste is the back hem tag on every jersey: no mapping may put anything else there, or put it elsewhere.</summary>
public class EpwLockTests
{
    static LayerLabel L(int layer, string role) => new() { Layer = layer, Role = role };

    // St. Louis-like NHL 24 jersey and a Utah-like NHL 27 jersey (sponsor where NHL 24 has the tag).
    static SlotMapEntry Entry() => new()
    {
        Folder24 = "a", Folder27 = "b", Garment = "branding_jersey",
        Layers24 = [L(0, "crest"), L(1, "shield"), L(2, "brand"), L(3, SlotRoles.EpwRole), L(4, "sponsor"), L(5, "shield")],
        Layers27 = [L(0, "crest"), L(1, "sponsor"), L(2, "brand"), L(3, "shield"), L(4, "sponsor"), L(5, SlotRoles.EpwRole)],
    };

    [Fact]
    public void Automatic_map_only_links_tag_to_tag()
    {
        var e = Entry();
        var (links, _) = SlotRoles.Map(e.Layers24, e.Layers27);
        e.Links = links;
        Assert.Equal(5, links.Single(l => l.Nhl24 == 3).Nhl27);
        Assert.Equal(0, SlotRoles.EnforceEpwLock(e));
        AssertNoCrossing(e);
    }

    [Fact]
    public void A_sponsor_put_into_the_tag_layer_by_hand_is_refused()
    {
        var e = Entry();
        e.Links = [new LayerLink { Nhl24 = 3, Nhl27 = 1, Role = "sponsor", Manual = true }];
        Assert.Equal(1, SlotRoles.EnforceEpwLock(e));
        Assert.Null(e.Links[0].Nhl27);
        Assert.Equal(SlotRoles.EpwRole, e.Links[0].Role);
    }

    [Fact]
    public void The_tag_cannot_be_moved_into_another_layer()
    {
        var e = Entry();
        e.Links = [new LayerLink { Nhl24 = 4, Nhl27 = 5, Role = "sponsor", Manual = true }];
        Assert.Equal(1, SlotRoles.EnforceEpwLock(e));
        Assert.Null(e.Links[0].Nhl27);
    }

    [Fact]
    public void Every_possible_link_respects_the_lock()
    {
        foreach (var target in Enumerable.Range(0, 6))
        foreach (var source in Enumerable.Range(0, 6))
        {
            var e = Entry();
            e.Links = [new LayerLink { Nhl24 = target, Nhl27 = source, Role = "x", Manual = true }];
            SlotRoles.EnforceEpwLock(e);
            AssertNoCrossing(e);
        }
    }

    static void AssertNoCrossing(SlotMapEntry e)
    {
        foreach (var link in e.Links.Where(l => l.Nhl27 is not null))
        {
            bool tag24 = e.Layers24.Any(l => l.Layer == link.Nhl24 && l.Role == SlotRoles.EpwRole);
            bool tag27 = e.Layers27.Any(l => l.Layer == link.Nhl27 && l.Role == SlotRoles.EpwRole);
            Assert.Equal(tag24, tag27);
        }
    }
}
