using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Suite.App.Pages;

/// <summary>Placeholder for a page that is planned but not built yet. Lists what it will do (from docs\HANDOFF.md).</summary>
public static class PlannedPage
{
    static readonly Dictionary<string, (string Title, string[] Items)> Plans = new()
    {
        ["textures"] = ("Textures & logos", [
            "Browser for every NHL 24 texture (162,796 slots): search, filter by team/brand/set/role, thumbnails, replace any texture by hand or drag and drop.",
            "adidas → Fanatics swap on branding arrays (needs Fanatics art).",
            "Arizona → Utah Mammoth: overwrite t22 and every Arizona texture in place (artid 22 kept); slot list in RosterConversion\\arizona_slots_full.json.",
            "Logo arrays with clean coeff layers built from alpha (rule 13).",
        ]),
        ["teams"] = ("Teams & roster", [
            "Teams: searchable team list, every exhibitionteams/exhibitionarena field, colour pickers, loc strings per language, live validation (ranges, string bytes); saves data\\teams_overrides.json.",
            "RosterPort from NHL 27 (specs A–D): team/arena text and colours only, gear colours, masks, X-Factor map, meta-driven validator as the only write path.",
            "Roster: NHL API sync with a diff to confirm (spec E), player matching, headshots.",
        ]),
        ["gameplay"] = ("Gameplay tweakables", ["Sliders and tuning values, each hand-editable with the stock value shown."]),
        ["sounds"] = ("Sounds", ["Goal horns, arena audio and bank sounds: list, preview, replace."]),
        ["ui"] = ("UI scripts", ["Lua/layout files from the legacy file system: view, edit, diff against stock."]),
        ["apply"] = ("Apply & restore", [
            "The suite's own writer, reimplementing the documented approach as new code: append to CAS, repoint TOC chunk table, bundle entries, every RES copy and duplicate bundle copy, SHA1s; all 15 in-game rules.",
            "Mod list (what to apply) and kept list (what is in the game), stock.json / stock TOC backup for a full restore.",
            "Refuses to run while the game is open. Never lets FMT write backups into the game folder.",
            "Byte-for-byte comparison against the NHL24Kit Worker's output for the same plan.",
        ]),
    };

    public static string TitleOf(string tag) => Plans.TryGetValue(tag, out var p) ? p.Title : tag;

    public static FrameworkElement For(string tag)
    {
        var (title, items) = Plans.TryGetValue(tag, out var p) ? p : (tag, Array.Empty<string>());
        var panel = new StackPanel { Margin = new Thickness(32, 26, 32, 26), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3a, 0x2f, 0x12)), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 8, 0, 16), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = "Planned — not built yet", Foreground = new SolidColorBrush(Color.FromRgb(0xf0, 0xc0, 0x4c)) },
        });
        foreach (var item in items)
            panel.Children.Add(new TextBlock
            {
                Text = "•  " + item, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)), FontSize = 13,
            });
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
