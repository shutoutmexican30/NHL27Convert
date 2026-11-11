using System.Text.Json;
using Suite.Convert27;
using Suite.Core;
using Suite.Rosters;

namespace Suite.Tests;

public sealed class NewUniformExportTests
{
    static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    static string Fixture => Path.Combine(Root, "Debugging", "logo-slots");

    [Fact]
    public void Full_batch_builds_exactly_the_32_current_teams_default_pairs()
    {
        var variants = VariantMap.Parse(File.ReadAllText(Path.Combine(Root, "data", "variant_map.json")));
        var paths = new List<string>();
        foreach (var team in variants.Teams.Where(t => UniformViewerReference.TeamKey(t.Nhl24.Folder) is not null))
        foreach (var home in new[] { true, false })
        {
            string? set24 = home ? team.Nhl24.Home : team.Nhl24.Away;
            string? set27 = home ? team.Nhl27.Home : team.Nhl27.Away;
            paths.Add($"content/character/skater/league/nhl/{team.Nhl24.Folder}/adidas/{set24}/jersey_color");
            paths.Add($"content/character/skater/league/nhl/{team.Nhl24.Folder}/adidas/{set24}/branding_jersey_adidas_{team.Nhl24.Folder}_{set24}_color_array");
            paths.Add($"content/character/skater/league/nhl/{team.Nhl27.Folder}/adidas/{set27}/jersey_color");
        }
        var entries = paths.Select(path => new AssetEntry
        {
            Game = "test",
            Kind = path.EndsWith("_array", StringComparison.Ordinal) ? AssetKind.TextureArray : AssetKind.Texture,
            Path = path,
        }).ToList();
        var catalog24 = new Nhl24Catalog(new AssetIndex("nhl24", "", DateTime.Now, entries));
        var catalog27 = new Nhl27Catalog(new AssetIndex("nhl27", "", DateTime.Now, entries));

        var selected = NewUniformExport.BuildDefaultFolders(catalog24, catalog27, variants);

        Assert.Equal(64, selected.Count);
        Assert.Equal(32, selected.Select(f => NewUniformExport.CurrentSource(f, variants)!.Value.Team.Index).Distinct().Count());
        Assert.All(selected, f => Assert.NotNull(NewUniformExport.CurrentSource(f, variants)));
        Assert.Equal(2, selected.Count(f => f.Folder24 == "content/character/skater/league/nhl/carolina/adidas/two"
            || f.Folder24 == "content/character/skater/league/nhl/carolina/adidas/three"));
        Assert.Contains(selected, f => f.Folder24 == "content/character/skater/league/nhl/winnipeg/adidas/home"
            && f.Folder27 == "content/character/skater/league/nhl/winnipeg/adidas/ten");
        Assert.Contains(selected, f => f.Folder24 == "content/character/skater/league/nhl/chicago/adidas/home"
            && f.Folder27 == "content/character/skater/league/nhl/chicago/adidas/ten");
        Assert.DoesNotContain(selected, f => f.Folder24.Contains("/atlantic/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_single_uniform_without_its_own_jersey_logo_arrays_uses_the_teams_structural_set()
    {
        // Boston: the picked "home" set has no jersey logo arrays; NHL 24 keeps them in "five".
        var variants = VariantMap.Parse(File.ReadAllText(Path.Combine(Root, "data", "variant_map.json")));
        var boston = variants.Teams.First(t => t.Nhl24.Folder == "boston");
        const string nhl = "content/character/skater/league/nhl/";
        string home24 = $"{nhl}boston/adidas/{boston.Nhl24.Home}", five = $"{nhl}boston/adidas/five", home27 = $"{nhl}{boston.Nhl27.Folder}/adidas/{boston.Nhl27.Home}";
        string[] paths = [$"{home24}/jersey_color", $"{five}/jersey_color", $"{five}/branding_jersey_adidas_boston_five_color_array", $"{home27}/jersey_color"];
        var entries = paths.Select(path => new AssetEntry
        {
            Game = "test", Path = path,
            Kind = path.EndsWith("_array", StringComparison.Ordinal) ? AssetKind.TextureArray : AssetKind.Texture,
        }).ToList();
        var catalog24 = new Nhl24Catalog(new AssetIndex("nhl24", "", DateTime.Now, entries));
        var catalog27 = new Nhl27Catalog(new AssetIndex("nhl27", "", DateTime.Now, entries));
        var picked = new FolderMatch { Folder24 = home24, Folder27 = home27 };

        var used = NewUniformExport.WithJerseyStructure(picked, catalog24, catalog27, variants);

        Assert.Equal(five, used.Folder24);
        Assert.Equal(home27, used.Folder27);
        Assert.Contains(used.Rows, r => r.IsArray && r.Name.Garment == "branding_jersey");
        // A set with its own arrays is kept as picked.
        var own = new FolderMatch { Folder24 = five, Folder27 = home27 };
        Assert.Same(own, NewUniformExport.WithJerseyStructure(own, catalog24, catalog27, variants));
    }

    [Fact]
    public void Anaheim_complete_array_keeps_sponsor_and_two_shoulders_in_their_own_roles()
    {
        var result = BuildRealArray("anaheim", "home");

        Assert.Equal(["crest", "shoulder", "shoulder", "shield", "brand", "sponsor"], result.Color.RequiredRoles);
        Assert.Equal(result.Color.RequiredRoles, result.Normal.RequiredRoles);
        Assert.Equal(1, result.Color.RequiredRoles!.Count(r => r == "crest"));
        Assert.Equal(2, result.Color.RequiredRoles!.Count(r => r == "shoulder"));
        Assert.Equal(1, result.Color.RequiredRoles!.Count(r => r == "sponsor"));
        AssertComplete(result);
    }

    [Fact]
    public void Utah_complete_array_has_exactly_one_crest_and_no_shifted_roles()
    {
        var result = BuildRealArray("arizona", "home");

        Assert.Equal(["crest", "shoulder", "shoulder", "brand", "shield", "sponsor"], result.Color.RequiredRoles);
        Assert.Equal(1, result.Color.RequiredRoles!.Count(r => r == "crest"));
        Assert.Equal(result.Color.RequiredRoles, result.Normal.RequiredRoles);
        AssertComplete(result);
    }

    [Theory]
    [InlineData("color")]
    [InlineData("normal")]
    public void Missing_colour_or_normal_art_blocks_the_whole_array(string missingRole)
    {
        var ex = Assert.Throws<InvalidDataException>(() => BuildRealArray("anaheim", "home", name =>
            name.EndsWith($"_1_{missingRole}", StringComparison.Ordinal) ? Path.Combine(Fixture, "absent.png") : null));

        Assert.Contains("complete team colour and normal artwork is required", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no donor fallback", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fanatics_fallback_is_lazy_and_never_fills_a_sponsor_or_team_role()
    {
        // Every needed image belongs to the current folder. If fallback scans another folder, this resolver throws.
        var complete = BuildRealArray("anaheim", "home", name =>
        {
            if (!name.Contains("/anaheim/adidas/ten/", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fallback scan.");
            return null;
        });
        Assert.Contains("brand", complete.Color.RequiredRoles!);

        // A missing sponsor normal must fail; it must never borrow Fanatics or another team's sponsor.
        var ex = Assert.Throws<InvalidDataException>(() => BuildRealArray("anaheim", "home", name =>
        {
            if (!name.Contains("/anaheim/adidas/ten/", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fallback scan.");
            return name.EndsWith("_5_normal", StringComparison.Ordinal) ? Path.Combine(Fixture, "absent.png") : null;
        }));
        Assert.Contains("(sponsor)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_manufacturer_layer_uses_only_a_real_fanatics_pair()
    {
        var result = BuildRealArray("arizona", "home", name => null, removeManufacturerFromSource: true);

        int brand = Assert.Single(result.Color.RequiredRoles!.Select((role, index) => (role, index)).Where(x => x.role == "brand")).index;
        string color = result.Color.Artwork.Layers![brand];
        string normal = result.Normal.Artwork.Layers![brand];
        Assert.Equal("brand_fanatics", SlotRoles.KnownLogo(color));
        Assert.True(File.Exists(color));
        Assert.True(File.Exists(normal));
        Assert.Equal(1, result.Color.RequiredRoles!.Count(r => r == "crest"));
        Assert.Equal(2, result.Color.RequiredRoles!.Count(r => r == "shoulder"));
        Assert.Equal(1, result.Color.RequiredRoles!.Count(r => r == "sponsor"));
    }

    [Fact]
    public void Actual_source_array_excludes_unused_numbered_logos()
    {
        const string source = "content/character/skater/league/nhl/detroit/adidas/ten";
        const string target = "content/character/skater/league/nhl/detroit/adidas/home/branding_jersey_adidas_detroit_home_color_array";
        string cache = Path.Combine(Root, "Debugging", "new-uniform-integration", "cache", "nhl27png");
        var entries = new List<AssetEntry>();
        for (int i = 0; i < 6; i++)
        foreach (string role in new[] { "color", "normal" })
            entries.Add(new AssetEntry { Game = "nhl27", Kind = AssetKind.Texture,
                Path = $"{source}/branding_jersey_adidas_detroit_ten_{i}_{role}" });
        for (int i = 0; i < 2; i++)
        {
            string role = i == 0 ? "color" : "normal";
            entries.Add(new AssetEntry { Game = "nhl27", Kind = AssetKind.TextureArray, Slices = 4, Width = 512, Height = 512,
                Path = $"{source}/branding_jersey_adidas_detroit_ten_{role}_array" });
        }
        var catalog = new Nhl27Catalog(new AssetIndex("nhl27", "", DateTime.Now, entries));
        var folder = new FolderMatch { Folder24 = "content/character/skater/league/nhl/detroit/adidas/home", Folder27 = source,
            Rows = [new SlotMatch { Slot = target, IsArray = true }] };

        var result = NewUniformExport.CompleteArray(catalog, new SlotMapStore(), folder, folder.Rows[0],
            name => Path.Combine(cache, SuitePaths.Flatten(name) + ".png"));

        Assert.Equal(["crest", "shield", "brand", "sponsor"], result.Color.RequiredRoles);
        Assert.DoesNotContain("epw_tag", result.Color.RequiredRoles!);
        Assert.Equal(1, result.Color.RequiredRoles!.Count(r => r == "shield"));
        Assert.All(result.Color.Artwork.Layers!.Values, path => Assert.Contains("color_array@", path, StringComparison.Ordinal));
        AssertComplete(result);
    }

    [Theory]
    [InlineData("buffalo", true, "crest,shield,brand")]
    [InlineData("buffalo", false, "crest,shield,brand")]
    [InlineData("calgary", true, "crest,shield,brand")]
    [InlineData("carolina", true, "crest,shoulder,shoulder,shield,brand,sponsor")]
    [InlineData("edmonton", true, "crest,shield,brand")]
    [InlineData("nashville", true, "crest,shield,brand,sponsor,shoulder,shoulder")]
    [InlineData("nashville", false, "crest,shield,brand,sponsor,shoulder,shoulder")]
    [InlineData("newjersey", false, "crest,shield,brand")]
    [InlineData("ottawa", true, "crest,shield,brand,sponsor,shoulder,shoulder")]
    [InlineData("ottawa", false, "crest,shield,brand,sponsor,shoulder,shoulder")]
    [InlineData("stlouis", false, "crest,sponsor,brand,shield")]
    public void Numbered_fallback_uses_only_reviewed_active_jersey_layers(string team, bool home, string expected)
    {
        var result = BuildIntegrationArray(team, home);
        Assert.Equal(expected.Split(','), result.Color.RequiredRoles);
        Assert.Equal(result.Color.RequiredRoles, result.Normal.RequiredRoles);
        Assert.DoesNotContain("epw_tag", result.Color.RequiredRoles!);
        Assert.Equal(1, result.Color.RequiredRoles!.Count(r => r == "shield"));
        AssertComplete(result);
    }

    static (NewUniformTexture Color, NewUniformTexture Normal) BuildIntegrationArray(string team, bool home)
    {
        string fixture = Path.Combine(Root, "Debugging", "new-uniform-integration");
        using var stream = File.OpenRead(Path.Combine(fixture, "cache", "index", "nhl27", "assets.json.gz"));
        using var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
        var stored = JsonSerializer.Deserialize<IntegrationIndex>(gzip, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        })!;
        var catalog = new Nhl27Catalog(new AssetIndex(stored.Game, stored.Fingerprint, stored.Built, stored.Entries));
        var variants = VariantMap.Parse(File.ReadAllText(Path.Combine(Root, "data", "variant_map.json")));
        var variant = variants.Teams.Single(t => t.Index >= 0 && t.Index <= 31 && t.Nhl27.Folder == team);
        string set24 = (home ? variant.Nhl24.Home : variant.Nhl24.Away)!;
        string set27 = (home ? variant.Nhl27.Home : variant.Nhl27.Away)!;
        string folder24 = $"content/character/skater/league/nhl/{variant.Nhl24.Folder}/adidas/{set24}";
        string folder27 = $"content/character/skater/league/nhl/{variant.Nhl27.Folder}/adidas/{set27}";
        var folder = new FolderMatch { Folder24 = folder24, Folder27 = folder27 };
        var array = new SlotMatch
        {
            Slot = $"{folder24}/branding_jersey_adidas_{variant.Nhl24.Folder}_{set24}_color_array",
            IsArray = true,
        };
        var maps = JsonSerializer.Deserialize<SlotMapStore>(File.ReadAllText(Path.Combine(Fixture, "slot_map.json")))!;
        return NewUniformExport.CompleteArray(catalog, maps, folder, array,
            name => Path.Combine(fixture, "cache", "nhl27png", SuitePaths.Flatten(name) + ".png"));
    }

    sealed record IntegrationIndex(int Schema, string Game, string Fingerprint, DateTime Built, List<AssetEntry> Entries);

    static void AssertComplete((NewUniformTexture Color, NewUniformTexture Normal) result)
    {
        Assert.Equal(result.Color.RequiredRoles!.Count, result.Color.Artwork.Layers!.Count);
        Assert.Equal(result.Normal.RequiredRoles!.Count, result.Normal.Artwork.Layers!.Count);
        Assert.Equal(Enumerable.Range(0, result.Color.RequiredRoles!.Count), result.Color.Artwork.Layers.Keys.Order());
        Assert.All(result.Color.Artwork.Layers.Values.Concat(result.Normal.Artwork.Layers.Values), path => Assert.True(File.Exists(path), path));
    }

    static (NewUniformTexture Color, NewUniformTexture Normal) BuildRealArray(
        string team24, string set24, Func<string, string?>? overridePath = null, bool removeManufacturerFromSource = false)
    {
        var project = MatchProject.Load(Path.Combine(Fixture, "convert27.json"));
        var folder = project.Folders.Single(f => f.Folder24 == $"content/character/skater/league/nhl/{team24}/adidas/{set24}");
        var maps = JsonSerializer.Deserialize<SlotMapStore>(File.ReadAllText(Path.Combine(Fixture, "slot_map.json"))) ?? new();
        var sourceFolders = project.Folders.Where(f => f.Folder27 is not null
                && (f.Folder24.Contains("/anaheim/", StringComparison.Ordinal) || f.Folder24.Contains("/arizona/", StringComparison.Ordinal)))
            .Select(f => f.Folder27!).Distinct(StringComparer.Ordinal).ToList();
        var entries = new List<AssetEntry>();
        foreach (string sourceFolder in sourceFolders)
        {
            string prefix = SuitePaths.Flatten(sourceFolder) + "_";
            foreach (string file in Directory.GetFiles(Path.Combine(Fixture, "nhl27"), prefix + "*.png"))
            {
                string leaf = Path.GetFileNameWithoutExtension(file)[prefix.Length..];
                string name = sourceFolder + "/" + leaf;
                if (removeManufacturerFromSource && sourceFolder == folder.Folder27
                    && leaf.Contains("_3_color", StringComparison.Ordinal) || removeManufacturerFromSource && sourceFolder == folder.Folder27
                    && leaf.Contains("_3_normal", StringComparison.Ordinal)) continue;
                entries.Add(new AssetEntry { Game = "nhl27", Kind = AssetKind.Texture, Path = name });
            }
        }
        var catalog = new Nhl27Catalog(new AssetIndex("nhl27", "", DateTime.Now, entries));
        string Png(string name) => overridePath?.Invoke(name) ?? Path.Combine(Fixture, "nhl27", SuitePaths.Flatten(name) + ".png");
        var array = folder.Rows.Single(r => r.IsArray && r.Role == "color" && r.Garment == "branding_jersey");
        return NewUniformExport.CompleteArray(catalog, maps, folder, array, Png);
    }
}
