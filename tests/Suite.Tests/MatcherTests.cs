using Suite.Convert27;
using Suite.Core;
using Xunit;

namespace Suite.Tests;

public class TextureNameTests
{
    [Fact]
    public void Parses_skater_uniform_paths()
    {
        var n = TextureName.Parse("content/character/skater/league/nhl/boston/adidas/home/branding_jersey_adidas_boston_home_3_color");
        Assert.Equal(("nhl", "boston", "adidas", "home"), (n.League, n.Team, n.Brand, n.Set));
        Assert.Equal("branding_jersey", n.Garment);
        Assert.Equal(3, n.Layer);
        Assert.Equal("color", n.Role);
        Assert.False(n.IsBase);
        Assert.Equal("branding_jersey_{brand}_{team}_{set}_3_color", n.Canonical);
    }

    [Fact]
    public void Base_and_array_names()
    {
        var b = TextureName.Parse("content/character/skater/league/nhl/utah/adidas/ten/jersey_adidas_utah_ten_base_color");
        Assert.True(b.IsBase);
        Assert.Equal("jersey", b.Garment);
        Assert.Equal("jersey_{brand}_{team}_{set}_base_color", b.Canonical);

        var a = TextureName.Parse("content/character/skater/league/nhl/boston/adidas/six/branding_pant_adidas_boston_six_normal_array");
        Assert.True(a.IsArrayName);
        Assert.Equal("normal", a.Role);
        Assert.Null(a.Layer);
    }

    [Fact]
    public void Sets_sort_home_away_then_numbers() =>
        Assert.Equal(["home", "away", "two", "ten", "twentythree"],
            new[] { "ten", "twentythree", "away", "two", "home" }.OrderBy(TextureName.SetOrder).ToArray());
}

public class MatcherTests
{
    const string P = "content/character/skater/league/nhl/";

    static AssetEntry E(string game, string path, AssetKind kind = AssetKind.Texture) => new() { Game = game, Kind = kind, Path = path, Width = 2048, Height = 2048 };

    static (Nhl24Catalog, Nhl27Catalog) Catalogs()
    {
        var n24 = new[]
        {
            $"{P}arizona/adidas/home/jersey_adidas_arizona_home_base_color",
            $"{P}arizona/adidas/home/jersey_adidas_arizona_home_base_coeff",
            $"{P}arizona/adidas/home/pant_adidas_arizona_home_base_normal",
            $"{P}arizona/adidas/home/branding_jersey_adidas_arizona_home_0_color",
            $"{P}arizona/adidas/home/branding_jersey_adidas_arizona_home_1_color",
            $"{P}boston/adidas/home/sock_adidas_boston_home_base_color",
        }.Select(p => E("nhl24", p)).Append(E("nhl24", $"{P}arizona/adidas/home/branding_jersey_adidas_arizona_home_color_array", AssetKind.TextureArray));
        var n27 = new[]
        {
            $"{P}utah/adidas/home/jersey_adidas_utah_home_base_color",
            $"{P}utah/adidas/home/jersey_adidas_utah_home_base_coeff",
            $"{P}utah/adidas/home/pant_adidas_utah_home_normal",
            $"{P}utah/adidas/home/branding_jersey_adidas_utah_home_0_color",
            $"{P}utah/adidas/home/branding_jersey_adidas_utah_home_1_color",
            $"{P}utah/adidas/ten/jersey_adidas_utah_ten_base_color",
            $"{P}boston/adidas/home/sock_adidas_boston_home_base_color",
        }.Select(p => E("nhl27", p));
        return (new Nhl24Catalog(new AssetIndex("nhl24", "x", DateTime.Now, n24)), new Nhl27Catalog(new AssetIndex("nhl27", "x", DateTime.Now, n27)));
    }

    [Fact]
    public void Arizona_takes_utah_art_through_the_team_map()
    {
        var (_, c27) = Catalogs();
        var best = Matcher.ProposeFolders($"{P}arizona/adidas/home", c27).First();
        Assert.Equal($"{P}utah/adidas/home", best.Folder27);
        Assert.True(best.Confidence is > 0.5 and < 0.95);
    }

    [Fact]
    public void Proposes_by_role_and_follows_the_in_game_rules()
    {
        var (c24, c27) = Catalogs();
        var rows = Matcher.Propose(c24.ByFolder[$"{P}arizona/adidas/home"], $"{P}utah/adidas/home", 0.85, c27).ToDictionary(r => r.Leaf);

        var color = rows["jersey_adidas_arizona_home_base_color"];
        Assert.Equal(SourceKind.Nhl27, color.Kind);
        Assert.Equal($"{P}utah/adidas/home/jersey_adidas_utah_home_base_color", color.Source27);
        Assert.Equal("auto", color.Alpha); // rule 11: keep the game's fabric-region alpha

        var coeff = rows["jersey_adidas_arizona_home_base_coeff"];
        Assert.Equal(SourceKind.Stock, coeff.Kind); // rule 10: base coeff stays stock
        Assert.Contains("rule 10", coeff.Reason);

        var pant = rows["pant_adidas_arizona_home_base_normal"];
        Assert.Equal($"{P}utah/adidas/home/pant_adidas_utah_home_normal", pant.Source27); // plain fallback for a base slot
        Assert.True(pant.Confidence < color.Confidence);

        var array = rows["branding_jersey_adidas_arizona_home_color_array"];
        Assert.Equal(2, array.Layers.Count); // rule 12: logos from arrays, one source per layer
        Assert.All(array.Layers, l => Assert.NotNull(l.Source27));
    }

    [Fact]
    public void Mod_file_uses_the_worker_plan_shape()
    {
        var (c24, c27) = Catalogs();
        var project = new MatchProject();
        project.Folders.Add(new FolderMatch
        {
            Folder24 = $"{P}arizona/adidas/home", Folder27 = $"{P}utah/adidas/home",
            Rows = Matcher.Propose(c24.ByFolder[$"{P}arizona/adidas/home"], $"{P}utah/adidas/home", 0.85, c27),
        });
        var mod = project.ToModFile("test");
        Assert.DoesNotContain(mod.Replacements, r => r.Slot.EndsWith("_base_coeff"));
        var arr = Assert.Single(mod.Replacements, r => r.Slot.EndsWith("_color_array"));
        Assert.Equal(2, arr.Layers!.Count);
        Assert.Contains("\"Replacements\"", mod.ToWorkerPlan());
    }
}

public class AssetIndexTests
{
    [Fact]
    public void Queries_by_typed_tokens_words_and_prefixes()
    {
        var idx = new AssetIndex("nhl24", "fp", DateTime.Now, new[]
        {
            "content/character/skater/league/nhl/arizona/adidas/away/jersey_adidas_arizona_away_color",
            "content/character/skater/league/nhl/arizona/adidas/home/jersey_adidas_arizona_home_base_color",
            "content/character/skater/league/nhl/boston/adidas/home/jersey_adidas_boston_home_base_color",
            "content/common/logo/arizona/logo_arizona_recolor2",
            "ui/textures/common/teamlogos/large/t22",
        }.Select(p => new AssetEntry { Game = "nhl24", Kind = AssetKind.Texture, Path = p }));

        Assert.Equal(2, idx.Query("team:arizona").Count());
        Assert.Single(idx.Query("team:arizona base"));
        Assert.Equal(3, idx.Query("arizona").Count());
        Assert.Single(idx.Query("t22"));
        Assert.Equal(2, idx.Query("set:home role:color").Count());
        Assert.Equal(3, idx.Query("arizon*").Count());
        Assert.Single(idx.Query("recolor"));            // substring fallback
        Assert.NotNull(idx.GetById("nhl24:ui/textures/common/teamlogos/large/t22"));
        Assert.Equal(3, idx.InFolder("content/character/skater/league/nhl/arizona/adidas/home").Count + 2);
    }

    [Fact]
    public void Fingerprint_changes_when_a_file_changes()
    {
        var f = Path.GetTempFileName();
        try
        {
            var a = AssetIndex.FingerprintOf([f]);
            File.AppendAllText(f, "x");
            Assert.NotEqual(a, AssetIndex.FingerprintOf([f]));
        }
        finally { File.Delete(f); }
    }
}
