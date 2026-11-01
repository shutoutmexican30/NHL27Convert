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
            row.Children.Add(new TextBlock { Text = $"NHL 24 layer {link.Nhl24}", Width = 95, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
            var role = new ComboBox { ItemsSource = SlotRoles.Roles, SelectedItem = label24?.Role, Width = 95, VerticalAlignment = VerticalAlignment.Center };
            role.SelectionChanged += (_, _) =>
            {
                if (label24 is null) return;
                label24.Role = (string)role.SelectedItem; label24.Manual = true; link.Role = label24.Role;
            };
            row.Children.Add(role);
            row.Children.Add(new TextBlock { Text = "  ←  ", VerticalAlignment = VerticalAlignment.Center, Foreground = Muted });
            var pick = new ComboBox { ItemsSource = options27, Width = 180, VerticalAlignment = VerticalAlignment.Center };
            pick.SelectedIndex = link.Nhl27 is { } n ? 1 + e.Layers27.OrderBy(l => l.Layer).ToList().FindIndex(l => l.Layer == n) : 0;
            var thumb27 = Thumb(link.Nhl27 is { } m ? png27(m) : null);
            pick.SelectionChanged += (_, _) =>
            {
                link.Nhl27 = pick.SelectedIndex <= 0 ? null : e.Layers27.OrderBy(l => l.Layer).ElementAt(pick.SelectedIndex - 1).Layer;
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
