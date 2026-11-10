using Suite.Core;
using Xunit;

namespace Suite.Tests;

/// <summary>The PSD template's measured geometry is the fixed pass/fail reference for every center-ice decal.</summary>
public class CenterIceTemplateTests
{
    /// <summary>A synthetic 1024x1024 decal: a blue ring on the template circle, red-line gap cut like stock.</summary>
    static Rgba Decal(double dx = 0, double dy = 0, double radiusScale = 1.0)
    {
        const int size = 1024;
        var px = new byte[size * size * 4];
        var c = CenterIceTemplate.ReferenceCircle(size, size);
        double cx = c.X + dx, cy = c.Y + dy, r = c.R * radiusScale;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (Math.Abs(d - r) > 2.5) continue;
                int p = (y * size + x) * 4;
                px[p] = 20; px[p + 1] = 60; px[p + 2] = 180; px[p + 3] = 255;
            }
        return CenterIceImage.CutRedLine(new Rgba(size, size, px));
    }

    [Fact]
    public void A_decal_on_the_template_circle_passes()
    {
        var report = CenterIceTemplate.Validate(Decal());
        Assert.True(report.Ok, report.Summary);
        Assert.Equal(0, report.RedLineOpaquePixels);
        Assert.NotNull(report.Measured);
        Assert.True(report.CenterErrorPx < 3, $"centre error {report.CenterErrorPx}");
        Assert.True(Math.Abs(report.RadiusErrorPct) < 1.5, $"radius error {report.RadiusErrorPct}%");
    }

    [Fact]
    public void A_shifted_circle_is_caught()
    {
        var report = CenterIceTemplate.Validate(Decal(dx: 40));
        Assert.False(report.Ok);
        Assert.Contains(report.Issues, i => i.StartsWith("Off-centre"));
    }

    [Fact]
    public void A_wrong_sized_circle_is_caught()
    {
        var report = CenterIceTemplate.Validate(Decal(radiusScale: 0.8));
        Assert.False(report.Ok);
        Assert.Contains(report.Issues, i => i.StartsWith("Wrong size"));
    }

    [Fact]
    public void Ink_over_the_red_line_is_caught()
    {
        var img = Decal();
        // Paint one opaque pixel inside the centre-line stripe, as an uncut logo would have.
        int x = 510, y = 512;
        img.Pixels[(y * img.Width + x) * 4 + 3] = 255;
        var report = CenterIceTemplate.Validate(img);
        Assert.False(report.Ok);
        Assert.Contains(report.Issues, i => i.StartsWith("Red line covered"));
        Assert.True(report.RedLineOpaquePixels > 0);
    }

    [Fact]
    public void Art_without_any_blue_skips_the_circle_check()
    {
        var blank = new Rgba(1024, 1024, new byte[1024 * 1024 * 4]);
        var report = CenterIceTemplate.Validate(blank);
        Assert.True(report.Ok, report.Summary);
        Assert.False(report.CircleChecked);
        Assert.Contains("skipped", report.Summary);
    }

    [Fact]
    public void Blue_ink_without_a_ring_needs_review()
    {
        // An off-centre solid blue blob: blue ink exists, but there is no circle to align to.
        var px = new byte[1024 * 1024 * 4];
        for (int y = 100; y < 140; y++)
            for (int x = 100; x < 140; x++)
            {
                int p = (y * 1024 + x) * 4;
                px[p] = 20; px[p + 1] = 60; px[p + 2] = 180; px[p + 3] = 255;
            }
        var report = CenterIceTemplate.Validate(new Rgba(1024, 1024, px));
        Assert.False(report.Ok);
        Assert.Contains(report.Issues, i => i.StartsWith("Circle not measurable"));
    }

    [Fact]
    public void A_dense_inner_logo_does_not_corrupt_the_outer_circle_measurement()
    {
        // Tampa Bay's failure mode: a big solid navy logo inside a thin outer circle. The outer ring must win.
        var img = Decal();
        var c = CenterIceTemplate.ReferenceCircle(1024, 1024);
        for (int y = 0; y < 1024; y++)
            for (int x = 0; x < 1024; x++)
            {
                double d = Math.Sqrt((x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y));
                if (d > 200) continue;
                int p = (y * 1024 + x) * 4;
                img.Pixels[p] = 10; img.Pixels[p + 1] = 30; img.Pixels[p + 2] = 200; img.Pixels[p + 3] = 255;
            }
        var report = CenterIceTemplate.Validate(CenterIceImage.CutRedLine(img));
        Assert.True(report.Ok, report.Summary);
        Assert.True(Math.Abs(report.RadiusErrorPct) < 1.5, $"radius error {report.RadiusErrorPct}%");
    }

    [Fact]
    public void Geometry_scales_with_texture_size()
    {
        var c = CenterIceTemplate.ReferenceCircle(512, 512);
        Assert.Equal(255.75, c.X, 3);
        Assert.Equal(191.25, c.R, 3);
        var (x0, x1) = CenterIceTemplate.RedLineColumns(512);
        Assert.Equal(246, x0);
        Assert.Equal(266, x1);
    }

    [Fact]
    public void Verification_cell_keeps_size_and_opaque_ice()
    {
        var cell = CenterIceTemplate.VerificationCell(Decal());
        Assert.Equal(1024, cell.Width);
        Assert.Equal(1024, cell.Height);
        for (int i = 3; i < cell.Pixels.Length; i += 4) Assert.Equal(255, cell.Pixels[i]);
        // The guide ring sits on the decal ring, so blue ink must survive at the reference circle.
        var c = CenterIceTemplate.ReferenceCircle(1024, 1024);
        int x = (int)Math.Round(c.X + c.R), y = (int)c.Y;
        int p = (y * 1024 + x) * 4;
        Assert.True(cell.Pixels[p + 2] > cell.Pixels[p] + 40, "the guide/decal circle should be blue");
    }
}
