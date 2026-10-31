using Suite.Convert27;
using Xunit;

namespace Suite.Tests;

public class LogoMatcherTests
{
    static double[] Shape(Func<int, int, bool> inside)
    {
        var a = new double[32 * 32];
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
            a[y * 32 + x] = inside(x, y) ? 255 : 0;
        return a;
    }

    [Fact]
    public void Picks_the_same_shape_and_rejects_a_different_one()
    {
        var square = Shape((x, y) => x is >= 6 and < 26 && y is >= 6 and < 26);
        var smallCircle = Shape((x, y) => (x - 16) * (x - 16) + (y - 16) * (y - 16) < 36);
        var border = Shape((x, y) => x is < 6 or >= 26 || y is < 6 or >= 26);

        Assert.Equal(1, LogoMatcher.Best(square, [smallCircle, square]));
        Assert.Null(LogoMatcher.Best(square, [smallCircle]));
        Assert.Null(LogoMatcher.Best(square, [border]));
    }

    [Fact]
    public void Difference_is_zero_for_identical_silhouettes()
    {
        var a = Shape((x, y) => x + y < 32);
        Assert.Equal(0, LogoMatcher.Difference(a, Shape((x, y) => x + y < 32)));
    }
}
