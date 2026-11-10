using Suite.Core;

namespace Suite.Tests;

public sealed class TeamColorsTests
{
    [Fact]
    public void Viewer_palette_uses_NHL24_goalie_colors_not_NHL27_uniform_colors()
    {
        Assert.Equal("#FC6519", TeamColors.Primary(0));    // Anaheim: not NHL 27's black table entry.
        Assert.Equal("#9F0D1A", TeamColors.Primary(10));   // Detroit: red, not NHL 27's white first entry.
        Assert.Equal("#202020", TeamColors.Secondary(2)); // Boston's NHL 24 goalie-pad dark neutral.
        for (int team = 0; team < 32; team++)
        {
            Assert.Matches("^#[0-9A-F]{6}$", TeamColors.Primary(team));
            Assert.Matches("^#[0-9A-F]{6}$", TeamColors.Secondary(team));
        }
    }

    [Fact]
    public void Utah_identity_override_is_applied_after_the_NHL24_palette()
    {
        Assert.Equal("#6CACE4", TeamColors.Primary(22));
        Assert.Equal("#202020", TeamColors.Secondary(22));
    }
}
