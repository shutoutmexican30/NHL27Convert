using Suite.Convert27;
using Suite.Core;

namespace Suite.Tests;

public sealed class UniformBrandingLayersTests
{
    static Rgba Image(byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = a;
        }
        return new Rgba(8, 8, pixels);
    }

    [Fact]
    public void Picture_matching_moves_the_whole_complete_array_without_retaining_stock()
    {
        var crest = Image(200, 10, 10, 255);
        var sponsor = Image(10, 200, 10, 255);
        var shield = Image(10, 10, 200, 255);
        var placement = UniformBrandingLayers.Plan(new Dictionary<int, Rgba> { [0] = crest, [1] = sponsor, [2] = shield },
            [sponsor, shield, crest]);
        Assert.Equal(2, placement.Destination(0));
        Assert.Equal(0, placement.Destination(1));
        Assert.Equal(1, placement.Destination(2));
        Assert.Empty(placement.KeepStock);
        var placed = UniformBrandingLayers.Place(new Dictionary<int, Rgba> { [0] = crest, [1] = sponsor, [2] = shield },
            [sponsor, shield, crest], placement, "color");
        Assert.Same(crest, placed[2]);
        Assert.Same(sponsor, placed[0]);
        Assert.Same(shield, placed[1]);
    }

    [Fact]
    public void Moving_a_picture_blanks_its_vacated_stock_slot_instead_of_duplicating_it()
    {
        var crest = Image(200, 10, 10, 255);
        var sponsor = Image(10, 200, 10, 255);
        var displacedStock = Image(10, 10, 200, 255);
        var art = new Dictionary<int, Rgba> { [0] = crest, [1] = sponsor };
        var stock = new[] { displacedStock, sponsor, crest };
        var placement = UniformBrandingLayers.Plan(art, stock);
        Assert.Equal(2, placement.Destination(0));
        Assert.Equal(1, placement.Destination(1));
        Assert.Empty(placement.KeepStock);
        var placed = UniformBrandingLayers.Place(art, stock, placement, "color");
        Assert.Same(crest, placed[2]);
        Assert.Same(sponsor, placed[1]);
        Assert.All(placed[0].Pixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Blank_material_slices_use_safe_normal_and_coefficient_values()
    {
        var normal = UniformBrandingLayers.Blank(1, 1, "normal");
        Assert.Equal(new byte[] { 128, 128, 255, 0 }, normal.Pixels);
        var coeff = UniformBrandingLayers.Blank(1, 1, "coeff");
        Assert.Equal(new byte[] { 126, 0, 255, 0 }, coeff.Pixels);
    }

    [Fact]
    public void Coefficient_follows_the_converted_logo_alpha_not_an_old_mask()
    {
        var pixels = new byte[8];
        pixels[3] = 17;  // visible logo
        pixels[7] = 16;  // below the visibility cutoff
        var coeff = LogoCoefficient.FromColor(new Rgba(2, 1, pixels));
        Assert.Equal(new byte[] { 128, 54, 225, 0, 126, 0, 255, 0 }, coeff.Pixels);
    }

    [Fact]
    public void Coefficient_audit_tolerates_small_compression_rounding()
    {
        var expected = LogoCoefficient.FromColor(Image(200, 10, 10, 255));
        var actual = Image(124, 50, 229, 0);
        Assert.Equal(0, InstalledBrandingAudit.CoefficientDifference(expected, actual));
    }

    [Fact]
    public void Coefficient_audit_rejects_same_shape_noisy_material_values()
    {
        var expected = LogoCoefficient.FromColor(Image(200, 10, 10, 255));
        var actual = Image(128, 120, 225, 0); // Still the same inside/outside class, but not a clean coefficient.
        Assert.Equal(100, InstalledBrandingAudit.CoefficientDifference(expected, actual));
    }
}
