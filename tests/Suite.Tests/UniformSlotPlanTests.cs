using Suite.Convert27;

namespace Suite.Tests;

public sealed class UniformSlotPlanTests
{
    const string Donor = "content/character/skater/league/nhl/arizona/adidas/four";
    const string Target = "content/character/skater/league/nhl/anaheim/adidas/four";

    static string At(string prefix, string leaf) => $"{prefix}/{leaf}";

    [Fact]
    public void Complete_team_resources_are_reused_without_a_donor()
    {
        string color = At(Target, "branding_jersey_adidas_anaheim_four_color_array");
        var plan = UniformSlotPlan.Build(Donor, Target,
            [new UniformSlotState(At(Donor, "branding_jersey_adidas_arizona_four_color_array"), 8)],
            [new UniformSlotState(color, 6)],
            [new UniformSlotNeed(color, 6, LayerArtwork: Art(0, 6))]);

        var slot = Assert.Single(plan.Slots);
        Assert.Equal(UniformSlotAction.ReuseTeamSlot, slot.Action);
        Assert.Null(slot.DonorName);
        Assert.Empty(plan.StructuralChanges);
        Assert.True(plan.IsValid);
    }

    [Fact]
    public void Only_missing_structure_is_borrowed_not_the_donor_uniform_or_fabric()
    {
        string color = At(Target, "branding_jersey_adidas_anaheim_four_6_color");
        var plan = UniformSlotPlan.Build(Donor, Target,
            [
                new UniformSlotState(At(Donor, "branding_jersey_adidas_arizona_four_6_color")),
                new UniformSlotState(At(Donor, "jersey_adidas_arizona_four_base_color")),
                new UniformSlotState(At(Donor, "font_adidas_arizona_four_color")),
            ],
            [
                new UniformSlotState(At(Target, "jersey_adidas_anaheim_four_base_color")),
                new UniformSlotState(At(Target, "font_adidas_anaheim_four_color")),
            ],
            [new UniformSlotNeed(color, Artwork: "anaheim-6-color.png")]);

        var slot = Assert.Single(plan.StructuralChanges);
        Assert.Equal(At(Donor, "branding_jersey_adidas_arizona_four_6_color"), slot.DonorName);
        Assert.DoesNotContain(plan.Slots, s => s.TargetName.Contains("/jersey_adidas", StringComparison.Ordinal)
            || s.TargetName.Contains("/font_adidas", StringComparison.Ordinal));
        Assert.True(plan.IsValid);
    }

    [Fact]
    public void Missing_array_must_supply_every_layer_so_no_donor_logo_can_survive()
    {
        string color = At(Target, "branding_jersey_adidas_anaheim_four_color_array");
        var plan = UniformSlotPlan.Build(Donor, Target,
            [new UniformSlotState(At(Donor, "branding_jersey_adidas_arizona_four_color_array"), 7)],
            [],
            [new UniformSlotNeed(color, 7, LayerArtwork: Art(0, 6))]);

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("layer 6", StringComparison.Ordinal)
            && e.Contains("donor artwork cannot be used", StringComparison.Ordinal));
    }

    [Fact]
    public void Expansion_preserves_team_layers_and_requires_art_only_for_new_layers()
    {
        string color = At(Target, "branding_jersey_adidas_anaheim_four_color_array");
        var plan = UniformSlotPlan.Build(Donor, Target,
            [new UniformSlotState(At(Donor, "branding_jersey_adidas_arizona_four_color_array"), 7)],
            [new UniformSlotState(color, 6)],
            [new UniformSlotNeed(color, 7, LayerArtwork: Art(6, 7))]);

        var slot = Assert.Single(plan.StructuralChanges);
        Assert.Equal(UniformSlotAction.ExpandTeamArray, slot.Action);
        Assert.Equal(6, slot.PreservedTeamLayers);
        Assert.Equal(7, slot.ResultLayers);
        Assert.True(plan.IsValid);
    }

    [Fact]
    public void Expansion_rejects_a_gap_that_would_keep_donor_art()
    {
        string color = At(Target, "branding_jersey_adidas_anaheim_four_color_array");
        var plan = UniformSlotPlan.Build(Donor, Target,
            [new UniformSlotState(At(Donor, "branding_jersey_adidas_arizona_four_color_array"), 8)],
            [new UniformSlotState(color, 6)],
            [new UniformSlotNeed(color, 8, LayerArtwork: Art(6, 7))]);

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("layer 7", StringComparison.Ordinal));
    }

    [Fact]
    public void Shallowest_duplicate_target_copy_drives_the_safety_decision()
    {
        string color = At(Target, "branding_jersey_adidas_anaheim_four_color_array");
        var plan = UniformSlotPlan.Build(Donor, Target,
            [new UniformSlotState(At(Donor, "branding_jersey_adidas_arizona_four_color_array"), 7)],
            [new UniformSlotState(color, 7), new UniformSlotState(color, 5)],
            [new UniformSlotNeed(color, 7, LayerArtwork: Art(5, 7))]);

        Assert.Equal(UniformSlotAction.ExpandTeamArray, Assert.Single(plan.Slots).Action);
        Assert.Equal(5, plan.Slots[0].PreservedTeamLayers);
        Assert.True(plan.IsValid);
    }

    [Fact]
    public void Shared_or_other_team_resources_are_refused()
    {
        const string shared = "content/character/skater/league/shared/textile/weave/adidas/weave_jersey_adidas_default_color_array";
        var plan = UniformSlotPlan.Build(Donor, Target, [], [],
            [new UniformSlotNeed(shared, 2, LayerArtwork: Art(0, 2))]);

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("shared resources cannot be cloned", StringComparison.Ordinal));
        Assert.Empty(plan.Slots);
    }

    [Fact]
    public void Too_shallow_donor_is_an_explicit_preflight_failure()
    {
        string normal = At(Target, "branding_jersey_adidas_anaheim_four_normal_array");
        var plan = UniformSlotPlan.Build(Donor, Target,
            [new UniformSlotState(At(Donor, "branding_jersey_adidas_arizona_four_normal_array"), 4)],
            [new UniformSlotState(normal, 3)],
            [new UniformSlotNeed(normal, 6, LayerArtwork: Art(3, 6))]);

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("donor has 4 layers but 6 are required", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(plan.ThrowIfInvalid);
    }

    static readonly string[] SixRoles = ["crest", "shoulder", "shoulder", "brand", "shield", "sponsor"];

    [Fact]
    public void Role_aware_plan_prefers_same_team_then_matching_kit_not_array_depth()
    {
        const string target = "content/character/skater/league/nhl/utah/adidas/four";
        const string sameTeam = "content/character/skater/league/nhl/utah/adidas/three";
        const string anaheimHome = "content/character/skater/league/nhl/anaheim/adidas/four";
        string name = At(target, "branding_jersey_adidas_utah_four_color_array");
        var plan = UniformSlotPlan.BuildRoleAware(target,
        [
            DonorWithRoles(anaheimHome, "anaheim_four", SixRoles),
            DonorWithRoles(sameTeam, "utah_three", SixRoles),
        ],
        [new UniformSlotState(name, 5, SixRoles.Take(5).ToArray())],
        [new UniformSlotNeed(name, 6, LayerArtwork: Art(5, 6), RequiredRoles: SixRoles)]);

        Assert.True(plan.IsValid);
        var slot = Assert.Single(plan.StructuralChanges);
        Assert.Equal(At(sameTeam, "branding_jersey_adidas_utah_three_color_array"), slot.DonorName);
    }

    [Fact]
    public void Arizona_without_a_sponsor_role_is_not_a_universal_donor()
    {
        const string target = "content/character/skater/league/nhl/utah/adidas/four";
        string name = At(target, "branding_jersey_adidas_utah_four_color_array");
        string[] arizonaRoles = ["crest", "shoulder", "shoulder", "brand", "shield", "epw_tag"];
        var plan = UniformSlotPlan.BuildRoleAware(target,
            [DonorWithRoles(Donor, "arizona_four", arizonaRoles)], [],
            [new UniformSlotNeed(name, 6, LayerArtwork: Art(0, 6), RequiredRoles: SixRoles)]);

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("ordered semantic roles", StringComparison.Ordinal));
        Assert.Empty(plan.Slots);
    }

    [Fact]
    public void Existing_doubled_or_shifted_roles_block_even_when_depth_is_sufficient()
    {
        const string target = "content/character/skater/league/nhl/utah/adidas/four";
        string name = At(target, "branding_jersey_adidas_utah_four_color_array");
        string[] wrong = ["crest", "crest", "shoulder", "brand", "shield", "sponsor"];
        var plan = UniformSlotPlan.BuildRoleAware(target,
            [DonorWithRoles(Donor, "arizona_four", SixRoles)],
            [new UniformSlotState(name, 6, wrong)],
            [new UniformSlotNeed(name, 6, RequiredRoles: SixRoles)]);

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("duplicated or shifted", StringComparison.Ordinal));
    }

    [Fact]
    public void Mapped_plan_puts_each_role_in_the_donors_real_position()
    {
        const string target = "content/character/skater/league/nhl/utah/adidas/four";
        const string anaheim = "content/character/skater/league/nhl/anaheim/adidas/away";
        string[] donorRoles = ["crest", "shield", "brand", "sponsor", "shoulder", "shoulder"];
        string name = At(target, "branding_jersey_adidas_utah_four_color_array");
        var (plan, mapped) = UniformSlotPlan.BuildRoleMapped(target,
            [DonorWithRoles(anaheim, "anaheim_away", donorRoles)], [],
            new UniformSlotNeed(name, 6, LayerArtwork: Art(0, 6), RequiredRoles: SixRoles), _ => "transparent.png");

        Assert.True(plan.IsValid);
        Assert.Equal(donorRoles, mapped.RequiredRoles);
        Assert.Equal("team-layer-0.png", mapped.LayerArtwork![0]);
        Assert.Equal("team-layer-4.png", mapped.LayerArtwork[1]);
        Assert.Equal("team-layer-3.png", mapped.LayerArtwork[2]);
        Assert.Equal("team-layer-5.png", mapped.LayerArtwork[3]);
        Assert.Equal("team-layer-1.png", mapped.LayerArtwork[4]);
        Assert.Equal("team-layer-2.png", mapped.LayerArtwork[5]);
    }

    [Fact]
    public void Mapped_plan_pads_only_an_obsolete_tag_with_transparency()
    {
        const string target = "content/character/skater/league/nhl/detroit/adidas/four";
        string[] required = ["crest", "shield", "brand", "sponsor"];
        string[] donorRoles = ["crest", "shield", "brand", "epw_tag", "sponsor"];
        string name = At(target, "branding_jersey_adidas_detroit_four_color_array");
        var (plan, mapped) = UniformSlotPlan.BuildRoleMapped(target,
            [DonorWithRoles(Donor, "arizona_four", donorRoles)], [],
            new UniformSlotNeed(name, 4, LayerArtwork: Art(0, 4), RequiredRoles: required), layer => $"transparent-{layer}.png");

        Assert.True(plan.IsValid);
        Assert.Equal(["crest", "shield", "brand", "blank", "sponsor"], mapped.RequiredRoles);
        Assert.Equal("transparent-3.png", mapped.LayerArtwork![3]);
        Assert.Equal("team-layer-3.png", mapped.LayerArtwork[4]);
    }

    [Fact]
    public void Mapped_plan_blanks_optional_donor_slots_without_inventing_team_artwork()
    {
        const string target = "content/character/skater/league/nhl/buffalo/adidas/four";
        const string anaheim = "content/character/skater/league/nhl/anaheim/adidas/away";
        string[] required = ["crest", "shield", "brand"];
        string[] donorRoles = ["crest", "shield", "brand", "sponsor", "shoulder", "shoulder"];
        string name = At(target, "branding_jersey_adidas_buffalo_four_color_array");
        var (plan, mapped) = UniformSlotPlan.BuildRoleMapped(target,
            [DonorWithRoles(anaheim, "anaheim_away", donorRoles)], [],
            new UniformSlotNeed(name, 3, LayerArtwork: Art(0, 3), RequiredRoles: required), layer => $"transparent-{layer}.png");

        Assert.True(plan.IsValid);
        Assert.Equal(["crest", "shield", "brand", "blank", "blank", "blank"], mapped.RequiredRoles);
        Assert.Equal("team-layer-0.png", mapped.LayerArtwork![0]);
        Assert.Equal("team-layer-1.png", mapped.LayerArtwork[1]);
        Assert.Equal("team-layer-2.png", mapped.LayerArtwork[2]);
        Assert.Equal("transparent-3.png", mapped.LayerArtwork[3]);
        Assert.Equal("transparent-4.png", mapped.LayerArtwork[4]);
        Assert.Equal("transparent-5.png", mapped.LayerArtwork[5]);
    }

    [Fact]
    public void Mapped_plan_never_pads_a_missing_crest_or_sponsor()
    {
        const string target = "content/character/skater/league/nhl/utah/adidas/four";
        string[] donorRoles = ["shield", "brand", "sponsor", "shoulder", "shoulder", "epw_tag"];
        string name = At(target, "branding_jersey_adidas_utah_four_color_array");
        var (plan, _) = UniformSlotPlan.BuildRoleMapped(target,
            [DonorWithRoles(Donor, "arizona_four", donorRoles)], [],
            new UniformSlotNeed(name, 6, LayerArtwork: Art(0, 6), RequiredRoles: SixRoles), _ => "transparent.png");

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, e => e.Contains("ordered semantic roles", StringComparison.Ordinal));
    }

    static UniformDonor DonorWithRoles(string prefix, string identity, string[] roles) =>
        new(prefix, [new UniformSlotState(At(prefix, $"branding_jersey_adidas_{identity}_color_array"), roles.Length, roles)]);

    static Dictionary<int, string> Art(int first, int end) =>
        Enumerable.Range(first, end - first).ToDictionary(i => i, i => $"team-layer-{i}.png");
}
