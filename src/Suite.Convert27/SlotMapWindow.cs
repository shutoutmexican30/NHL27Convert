using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Suite.Convert27;

/// <summary>
/// Editable slot map for the current NHL 24 set: per branding garment, each NHL 24 layer with its role and the NHL 27
/// layer that fills it, with thumbnails from both games. Changes are marked manual and saved to slot_map.json.
/// </summary>
public sealed class SlotMapWindow : Window
{
    readonly SlotMapStore _store;
    readonly List<(SlotMapEntry Entry, Func<int, string?> Png24, Func<int, string?> Png27)> _items;
    readonly PathToImageConverter _img = new();
    static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xe6, 0xeb, 0xf2));
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8c, 0x97, 0xa8));

    public bool Changed { get; private set; }

    public SlotMapWindow(SlotMapStore store, List<(SlotMapEntry Entry, Func<int, string?> Png24, Func<int, string?> Png27)> items)
    {
        _store = store;
        _items = items;
        Title = "Slot map — branding layers by role";
        Width = 1050; Height = 780;
        Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1a, 0x22));
        Foreground = Text;
        var root = new DockPanel { Margin = new Thickness(14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var save = new Button { Content = "Save and re-match", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) => { _store.Save(); Changed = true; Close(); };
        var cancel = new Button { Content = "Close", Padding = new Thickness(12, 4, 12, 4) };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 0, 0, 10),
            Text = "Each NHL 24 layer is filled by the NHL 27 layer with the same role (crest → crest, brand adidas → Fanatics, shield → shield…). " +
                   "Change a role or pick another NHL 27 layer (or \"keep NHL 24\"); edits are kept when you re-match. File: " + SlotMapStore.PathName,
        });
        foreach (var item in _items) body.Children.Add(Section(item.Entry, item.Png24, item.Png27));
        if (_items.Count == 0) body.Children.Add(new TextBlock { Text = "No branding arrays with images in this set yet (decode or match first).", Foreground = Muted });
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    FrameworkElement Section(SlotMapEntry e, Func<int, string?> png24, Func<int, string?> png27)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(new TextBlock { Text = $"{e.Garment}   ·   NHL 24 {Short(e.Folder24)}  ←  NHL 27 {Short(e.Folder27)}", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var options27 = new List<object> { "keep NHL 24" };
        options27.AddRange(e.Layers27.OrderBy(l => l.Layer).Select(l => (object)$"{l.Layer}: {l.Role}{(l.Detail is null ? "" : " " + l.Detail)}"));
        foreach (var link in e.Links.OrderBy(l => l.Nhl24))
        {
            var label24 = e.Layers24.FirstOrDefault(l => l.Layer == link.Nhl24);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(Thumb(png24(link.Nhl24)));
            row.Children.Add(new TextBlock
            {
                Text = $"NHL 24 layer {link.Nhl24}" + (label24?.Placement is { } place ? $"\n{place}" : ""),
                Width = 110, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0),
            });
            bool isTag = label24?.Role == SlotRoles.EpwRole;
            bool isAdidas = label24 is not null && ManufacturerBranding.IsAdidas(label24);
            bool isPantsCrest = e.Garment == "branding_pant" && label24?.Role == "crest";
            var role = new ComboBox
            {
                ItemsSource = SlotRoles.Roles, SelectedItem = label24?.Role, Width = 95, VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = !isTag && !isAdidas && !isPantsCrest,
                ToolTip = isTag ? "End Plastic Waste: drawn as the tag on the very back hem of every jersey. Locked."
                    : isAdidas ? "Adidas branding must become Fanatics, including its normal map/mask."
                    : isPantsCrest ? "The pants team-logo slot takes the club mark, never the NHL shield." : null,
            };
            ToolTipService.SetShowOnDisabled(role, true);
            role.SelectionChanged += (_, _) =>
            {
                if (label24 is null) return;
                if (role.SelectedItem is not string selectedRole) return;
                label24.Role = selectedRole; label24.Manual = true; link.Role = label24.Role;
            };
            row.Children.Add(role);
            row.Children.Add(new TextBlock { Text = "  ←  ", VerticalAlignment = VerticalAlignment.Center, Foreground = Muted });
            var pick = new ComboBox { ItemsSource = options27, Width = 180, VerticalAlignment = VerticalAlignment.Center };
            pick.SelectedIndex = link.Nhl27 is { } n ? 1 + e.Layers27.OrderBy(l => l.Layer).ToList().FindIndex(l => l.Layer == n) : 0;
            var thumb27 = Thumb(link.Nhl27 is { } m ? png27(m) : null);
            var previous = pick.SelectedIndex;
            pick.SelectionChanged += (_, _) =>
            {
                var before = link.Nhl27;
                link.Nhl27 = pick.SelectedIndex <= 0 ? null : e.Layers27.OrderBy(l => l.Layer).ElementAt(pick.SelectedIndex - 1).Layer;
                if (SlotRoles.EnforceEpwLock(e) > 0)
                {
                    MessageBox.Show(this, "The End Plastic Waste layer is drawn as the tag on the very back hem of the jersey. " +
                        "Only an End Plastic Waste (back tag) image can go there, and it cannot go into any other layer.",
                        "Slot map", MessageBoxButton.OK, MessageBoxImage.Information);
                    link.Nhl27 = before;
                    pick.SelectedIndex = previous;
                    return;
                }
                var chosen = e.Layers27.FirstOrDefault(l => l.Layer == link.Nhl27);
                if ((isAdidas && (chosen is null || !ManufacturerBranding.IsFanatics(chosen)))
                    || (isPantsCrest && chosen is not null && chosen.Role != "crest"))
                {
                    link.Nhl27 = before; pick.SelectedIndex = previous;
                    MessageBox.Show(this, isAdidas ? "Adidas branding must be replaced by Fanatics, including the normal map/mask."
                        : "This pants slot is the team logo. Pick a club crest/wordmark, not the NHL shield.", "Branding rule");
                    return;
                }
                previous = pick.SelectedIndex;
                link.Manual = true;
                thumb27.Child = Image(link.Nhl27 is { } k ? png27(k) : null);
            };
            row.Children.Add(pick);
            row.Children.Add(new Border { Width = 8 });
            row.Children.Add(thumb27);
            panel.Children.Add(row);
        }
        var extras = e.Layers27.Where(l => e.NoSlot27.Contains(l.Layer)).ToList();
        if (extras.Count > 0)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(new TextBlock { Text = "NHL 27 layers with no NHL 24 slot:", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            foreach (var x in extras)
            {
                row.Children.Add(Thumb(png27(x.Layer)));
                row.Children.Add(new TextBlock { Text = $" {x.Layer}: {x.Role}  ", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            }
            panel.Children.Add(row);
        }
        return panel;
    }

    Border Thumb(string? path) => new()
    {
        Width = 64, Height = 64, Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x31, 0x3d)), CornerRadius = new CornerRadius(3),
        Child = Image(path),
    };

    UIElement Image(string? path) =>
        new Image { Source = path is null ? null : (ImageSource?)_img.Convert(path, typeof(ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture), Stretch = Stretch.Uniform, Margin = new Thickness(2) };

    static string Short(string folder) => folder.StartsWith(TextureName.SkaterPrefix) ? folder[TextureName.SkaterPrefix.Length..] : folder;
}
