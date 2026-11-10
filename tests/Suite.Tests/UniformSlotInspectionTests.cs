using Suite.Convert27;
using Suite.Core;

namespace Suite.Tests;

public sealed class UniformSlotInspectionTests
{
    const string Prefix = "content/character/skater/league/nhl/anaheim/adidas/twentyfive/";
    const string Slot = Prefix + "anaheim_twentyfive_branding_jersey_color_array";

    static Dictionary<string, int> Index(int depth) => new(StringComparer.Ordinal) { [Slot] = depth };

    static NewUniformAddition Addition(params string[] roles) => new()
    {
        TeamId = 0, Team = "anaheim", SourceKit = "home", TargetVariant = 25, TargetPrefix = Prefix,
        Textures = [new NewUniformTexture { TargetSlot = Slot, RequiredRoles = roles.ToList() }],
    };

    [Fact]
    public void Ordered_roles_and_matching_installed_depth_pass()
    {
        var result = UniformSlotInspection.Build(Index(4), [Addition("crest", "sponsor", "brand", "shield")]);
        Assert.True(result.IsValid);
        var row = Assert.Single(result.Rows);
        Assert.Equal("0 crest  ·  1 sponsor  ·  2 brand  ·  3 shield", row.RoleOrder);
    }

    [Fact]
    public void A_doubled_crest_or_a_depth_mismatch_is_visible_and_blocked()
    {
        var doubled = UniformSlotInspection.Build(Index(4), [Addition("crest", "crest", "brand", "shield")]);
        Assert.False(doubled.IsValid);
        Assert.Contains(doubled.Errors, error => error.Contains("exactly one crest", StringComparison.Ordinal));
        var missing = UniformSlotInspection.Build(Index(3), [Addition("crest", "sponsor", "brand", "shield")]);
        Assert.False(missing.IsValid);
        Assert.Contains(missing.Errors, error => error.Contains("3 installed layers, expected 4", StringComparison.Ordinal));
    }

    static BrandingLayerEvidence Placed(string role, int layer, Guid chunk) =>
        new("anaheim", "home", Slot, chunk, layer, role, 0, 8, false);

    [Fact]
    public void Larger_arrays_require_the_actual_placed_roles_and_explicit_blank_spare_layers()
    {
        var chunk = Guid.Parse("df08a43e-faee-5a14-a93c-ba5da75463d8");
        var addition = Addition("crest", "sponsor", "brand", "shield");
        // Branding moved to match the same-team stock layout. The sponsor is transparent, and the vacated
        // structural slot plus the extra donor capacity are blank; this is not a shortened source-role list.
        string[] placed = ["brand", "crest", "blank", "shield", "blank"];
        var result = UniformSlotInspection.InspectCopies([addition], placed.Select((role, layer) => Placed(role, layer, chunk)), []);

        Assert.True(result.IsValid, string.Join("\n", result.Errors));
        var row = Assert.Single(result.Rows);
        Assert.Equal(5, row.Depth);
        Assert.Equal(4, row.RequiredDepth);
        Assert.Equal(placed, row.Roles);
        Assert.Equal("0 brand  ·  1 crest  ·  2 blank  ·  3 shield  ·  4 blank", row.RoleOrder);
        Assert.Equal("df08a43efaee", row.Copy);

        // Ignoring depth would accept this doubled crest in the additional structural layer.
        string[] wrong = ["brand", "crest", "blank", "shield", "crest"];
        var rejected = UniformSlotInspection.InspectCopies([addition], wrong.Select((role, layer) => Placed(role, layer, chunk)), []);
        Assert.False(rejected.IsValid);
        Assert.Contains(rejected.Errors, error => error.Contains("exactly one crest", StringComparison.Ordinal));
        Assert.Equal("Needs artwork/role repair", Assert.Single(rejected.Rows).Status);
    }

    [Fact]
    public void Repeated_identical_copy_evidence_is_one_copy_but_conflicts_are_blocked()
    {
        var chunk = Guid.Parse("2be0d73e-7882-50f7-9752-6b0296c89f27");
        var addition = Addition("crest", "sponsor", "brand", "shield");
        var layers = new[] { "crest", "shield", "brand", "sponsor" }
            .Select((role, layer) => Placed(role, layer, chunk)).ToArray();
        var repeated = layers.SelectMany(layer => new[] { layer, layer with { } });
        var result = UniformSlotInspection.InspectCopies([addition], repeated, []);
        Assert.True(result.IsValid, string.Join("\n", result.Errors));
        Assert.Single(result.Rows);

        var conflict = repeated.Concat([Placed("crest", 3, chunk) with { Difference = 1 }]);
        var rejected = UniformSlotInspection.InspectCopies([addition], conflict, []);
        Assert.False(rejected.IsValid);
        Assert.Contains(rejected.Errors, error => error.Contains("conflicting observations", StringComparison.Ordinal));
    }

    [Fact]
    public void Utah_display_name_is_not_reverted_to_Arizona()
    {
        var row = new UniformSlotInspection.Row("arizona", "home", Slot, 4, 4, [], "Validated");
        Assert.Equal("Utah", row.TeamName);
    }
}
