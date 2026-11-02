using Suite.Convert27;
using Suite.Core;
using Xunit;

namespace Suite.Tests;

/// <summary>A stamp must land where it was placed, blend over the art, and leave the destination alpha alone.</summary>
public class StampTests
{
    static string Dir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cims-stamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    static Rgba Solid(int w, int h, byte r, byte g, byte b, byte a)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = a; }
        return new Rgba(w, h, px);
    }

    static byte AlphaAt(Rgba img, int x, int y) => img.Pixels[(y * img.Width + x) * 4 + 3];
    static byte RedAt(Rgba img, int x, int y) => img.Pixels[(y * img.Width + x) * 4];

    [Fact]
    public void The_logo_lands_at_its_fraction_and_keeps_the_fabric_alpha()
    {
        var dir = Dir();
        try
        {
            var basePath = Path.Combine(dir, "base.png");
            var logoPath = Path.Combine(dir, "logo.png");
            ImageIO.SavePng(Solid(64, 64, 10, 20, 30, 51), basePath);
            ImageIO.SavePng(Solid(8, 8, 255, 255, 255, 255), logoPath);

            var outPath = TextureImages.Stamp(basePath, logoPath, 0.75, 0.5, 0.25, "stamp-test", dir);
            Assert.NotNull(outPath);
            var result = ImageIO.Load(outPath!);

            Assert.Equal(64, result.Width);
            Assert.Equal(255, RedAt(result, 48, 32));
            Assert.Equal(10, RedAt(result, 4, 4));
            Assert.Equal(51, AlphaAt(result, 48, 32));
            Assert.Equal(51, AlphaAt(result, 4, 4));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_logo_hanging_over_the_edge_is_clipped_not_wrapped()
    {
        var dir = Dir();
        try
        {
            var basePath = Path.Combine(dir, "base.png");
            var logoPath = Path.Combine(dir, "logo.png");
            ImageIO.SavePng(Solid(32, 32, 0, 0, 0, 255), basePath);
            ImageIO.SavePng(Solid(8, 8, 255, 0, 0, 255), logoPath);

            var outPath = TextureImages.Stamp(basePath, logoPath, 0.0, 0.0, 0.5, "edge-test", dir);
            var result = ImageIO.Load(outPath!);
            Assert.Equal(255, RedAt(result, 0, 0));
            Assert.Equal(0, RedAt(result, 31, 31));
        }
        finally { Directory.Delete(dir, true); }
    }
}