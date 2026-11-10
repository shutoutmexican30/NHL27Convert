using Suite.Convert27;
using Xunit;

namespace Suite.Tests;

public class RegisteredKitSourceTests
{
    const string Root = "content/character/skater/league/nhl/";

    static RegisteredKitSource.Candidate Candidate(string folder, int rows, int layers, int textures = 19, int versions = 2,
        bool everyCopyFits = true, bool canExpand = false) => new(Root + folder, versions, textures, layers, rows, everyCopyFits, canExpand);

    [Fact]
    public void RealIncompleteSourcesUseFullSameTeamLayouts()
    {
        var cases = new[]
        {
            (Source: "florida/adidas/away", Expected: "florida/adidas/home", Textures: 19, Layers: 7,
                Candidates: new[] { Candidate("florida/adidas/home", 57, 7), Candidate("florida/adidas/twentythree", 57, 6), Candidate("florida/adidas/twentytwo", 12, 0, 10) }),
            (Source: "minnesota/adidas/away", Expected: "minnesota/adidas/home", Textures: 19, Layers: 6,
                Candidates: new[] { Candidate("minnesota/adidas/home", 57, 6), Candidate("minnesota/adidas/three", 56, 6, 19, 1), Candidate("minnesota/adidas/twentythree", 57, 6), Candidate("minnesota/adidas/two", 46, 4) }),
            (Source: "arizona/adidas/away", Expected: "arizona/adidas/home", Textures: 19, Layers: 6,
                Candidates: new[] { Candidate("arizona/adidas/four", 45, 4, 19, 1), Candidate("arizona/adidas/home", 60, 6), Candidate("arizona/adidas/twentythree", 56, 6) }),
            (Source: "vegas/adidas/two", Expected: "vegas/adidas/twentythree", Textures: 22, Layers: 6,
                Candidates: new[] { Candidate("vegas/adidas/four", 56, 5, 19, 1), Candidate("vegas/adidas/three", 54, 6, 22), Candidate("vegas/adidas/twentythree", 60, 6, 22) }),
            (Source: "vegas/adidas/away", Expected: "vegas/adidas/twentythree", Textures: 22, Layers: 6,
                Candidates: new[] { Candidate("vegas/adidas/four", 56, 5, 19, 1), Candidate("vegas/adidas/three", 54, 6, 22), Candidate("vegas/adidas/twentythree", 60, 6, 22) }),
        };
        foreach (var item in cases)
            Assert.Equal(Root + item.Expected, RegisteredKitSource.Choose(Root + item.Source, item.Textures, item.Layers, 2, item.Candidates));
    }

    [Fact]
    public void IncompleteOrAmbiguousSourcesAreRefused()
    {
        Assert.Throws<InvalidDataException>(() => RegisteredKitSource.Choose(Root + "vegas/adidas/away", 22, 6, 2,
            [Candidate("vegas/adidas/three", 54, 6, 19), Candidate("vegas/adidas/four", 56, 5, 19)]));
        Assert.Throws<InvalidDataException>(() => RegisteredKitSource.Choose(Root + "florida/adidas/away", 19, 6, 2,
            [Candidate("florida/adidas/three", 57, 6), Candidate("florida/adidas/twentythree", 57, 6)]));
        Assert.Throws<InvalidDataException>(() => RegisteredKitSource.Choose(Root + "florida/adidas/away", 19, 6, 2,
            [Candidate("minnesota/adidas/home", 60, 6)]));
    }

    [Fact]
    public void TheTeamsOwnKitWinsWhenItCanGrow()
    {
        // A sibling kit's layout put Nashville's logos in the wrong places; the mapped kit is kept and grown instead.
        string selected = RegisteredKitSource.Choose(Root + "nashville/adidas/home", 22, 6, 2,
        [
            Candidate("nashville/adidas/home", 60, 6, 22, everyCopyFits: false, canExpand: true),
            Candidate("nashville/adidas/two", 55, 6, 22, everyCopyFits: true),
        ]);
        Assert.Equal(Root + "nashville/adidas/home", selected);
        Assert.Throws<InvalidDataException>(() => RegisteredKitSource.Choose(Root + "nashville/adidas/home", 22, 6, 2,
            [Candidate("nashville/adidas/home", 60, 6, 22, everyCopyFits: false)]));
    }

    [Fact]
    public void CompatibleBlankSlotExpansionIsAllowedOnlyAfterItIsVerified()
    {
        string selected = RegisteredKitSource.Choose(Root + "arizona/adidas/home", 19, 6, 2,
            [Candidate("arizona/adidas/home", 60, 5, 19, everyCopyFits: false, canExpand: true)]);
        Assert.Equal(Root + "arizona/adidas/home", selected);
    }
}
