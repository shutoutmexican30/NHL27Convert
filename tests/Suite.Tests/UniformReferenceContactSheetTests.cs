using Suite.Convert27;

namespace Suite.Tests;

public sealed class UniformReferenceContactSheetTests
{
    [Fact]
    public void Contact_sheet_links_full_drawings_report_and_matcher_comparisons()
    {
        var ducks = new UniformReference.Drawing("Ducks", 19,
            "https://www.nhluniforms.com/Mobile/Ducks/Ducks19.png", @"C:\reference\Ducks.png");
        var mammoth = new UniformReference.Drawing("Mammoth", 2,
            "https://www.nhluniforms.com/Mobile/Mammoth/Mammoth02.png", @"C:\reference\Mammoth.png");

        string html = UniformReference.ContactSheetHtml("2026-27", [ducks, mammoth],
            d => d == ducks ? [@"C:\reference\look_Ducks_home.png"] : []);

        Assert.Contains("href=\"https://www.nhluniforms.com/Mobile/Ducks/Ducks19.png\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://www.nhluniforms.com/Mobile/Mammoth/Mammoth02.png\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"Ducks.png\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"Mammoth.png\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"report.txt\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"look_Ducks_home.png\"", html, StringComparison.Ordinal);
        Assert.Contains("Utah Mammoth", html, StringComparison.Ordinal);
        Assert.Contains("data-team=\"anaheim ducks\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Contact_sheet_escapes_team_content_and_uses_only_local_image_filenames()
    {
        var drawing = new UniformReference.Drawing("Team<script>", 1,
            "https://example.invalid/reference?a=1&b=2", @"C:\outside\Team.png");
        string html = UniformReference.ContactSheetHtml("2026-27", [drawing], _ => []);

        Assert.DoesNotContain("Team<script>", html, StringComparison.Ordinal);
        Assert.Contains("reference?a=1&amp;b=2", html, StringComparison.Ordinal);
        Assert.Contains("src=\"Team.png\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"C:\\outside", html, StringComparison.Ordinal);
    }
}
