using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Suite.Core;

namespace Suite.App.Pages;

/// <summary>
/// A full-size look at one texture, the way a texture viewer shows it: each channel on its own (R, G, B, alpha), the colour over a checkerboard or
/// opaque, every slice of an array, and zoom. Read-only: it only reads the decoded PNGs, so it can never change the game or the writers.
/// </summary>
public sealed class TexturePreviewWindow : Window
{
    enum Mode { Rgb, RgbOverChecker, Red, Green, Blue, Alpha }

    readonly Image _image = new() { Stretch = Stretch.Fill };
    readonly TextBlock _info = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly ComboBox _slice = new() { Width = 150, Margin = new Thickness(12, 0, 0, 0) };
    readonly Slider _zoom = new() { Minimum = 0.25, Maximum = 4, Value = 1, Width = 140, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly List<(string Label, string Path)> _sources = [];
    byte[] _bgra = [];
    int _w, _h;
    Mode _mode = Mode.RgbOverChecker;
    bool _loading;

    public TexturePreviewWindow(string slot, string? preview, bool game24)
    {
        Title = slot[(slot.LastIndexOf('/') + 1)..];
        Width = 1000; Height = 820;
        Background = new SolidColorBrush(Color.FromRgb(0x0e, 0x14, 0x1f));
        Foreground = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Every slice of an array (name#0, name#1...), else the one picture; the overview sheet is listed first when there are several.
        if (game24)
        {
            for (int i = 0; ; i++)
            {
                var p = Path.Combine(SuitePaths.Nhl24Images, SuitePaths.Flatten($"{slot}#{i}") + ".png");
                if (!File.Exists(p)) break;
                _sources.Add(($"Slice {i}", p));
            }
            var sheet = Path.Combine(SuitePaths.Nhl24Images, SuitePaths.Flatten(slot + Suite.Convert27.TextureImages.SheetSuffix) + ".png");
            if (_sources.Count > 1 && File.Exists(sheet)) _sources.Insert(0, ("All slices", sheet));
        }
        if (_sources.Count == 0 && preview != null && File.Exists(preview)) _sources.Add(("Image", preview));

        var bar = new WrapPanel { Margin = new Thickness(10, 8, 10, 8) };
        bool first = true;
        foreach (var (label, mode) in new[] { ("Colour", Mode.RgbOverChecker), ("Opaque", Mode.Rgb), ("R", Mode.Red), ("G", Mode.Green), ("B", Mode.Blue), ("Alpha", Mode.Alpha) })
        {
            var rb = new RadioButton { Content = label, GroupName = "mode", IsChecked = first, Margin = new Thickness(0, 0, 12, 0), Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            first = false;
            var m = mode;
            rb.Checked += (_, _) => { _mode = m; Render(); };
            bar.Children.Add(rb);
        }
        _slice.ItemsSource = _sources.Select(s => s.Label).ToList();
        _slice.SelectedIndex = 0;
        _slice.Visibility = _sources.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _slice.SelectionChanged += (_, _) => LoadSelected();
        bar.Children.Add(_slice);
        bar.Children.Add(new TextBlock { Text = "Zoom", Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_zoom);
        bar.Children.Add(_info);
        _zoom.ValueChanged += (_, _) => ApplyZoom();

        _scroll.Content = _image;
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            _zoom.Value = Math.Clamp(_zoom.Value * (e.Delta > 0 ? 1.15 : 1 / 1.15), _zoom.Minimum, _zoom.Maximum);
            e.Handled = true;
        };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_scroll);
        Content = root;
        Loaded += (_, _) => LoadSelected();
    }

    void LoadSelected()
    {
        if (_sources.Count == 0) { _info.Text = "No decoded picture for this texture yet."; return; }
        var path = _sources[Math.Max(0, _slice.SelectedIndex)].Path;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(path); bmp.EndInit(); bmp.Freeze();
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            _w = conv.PixelWidth; _h = conv.PixelHeight;
            _bgra = new byte[_w * _h * 4];
            conv.CopyPixels(_bgra, _w * 4, 0);
            _info.Text = $"{_w}×{_h}";
            Render();
        }
        catch (Exception ex) { _info.Text = "Could not open the picture: " + ex.Message; }
    }

    void Render()
    {
        if (_loading || _bgra.Length == 0) return;
        _loading = true;
        try
        {
            var px = new byte[_bgra.Length];
            for (int y = 0; y < _h; y++)
                for (int x = 0; x < _w; x++)
                {
                    int i = (y * _w + x) * 4;
                    byte b = _bgra[i], g = _bgra[i + 1], r = _bgra[i + 2], a = _bgra[i + 3];
                    switch (_mode)
                    {
                        case Mode.Red: b = g = r; break;
                        case Mode.Green: r = b = g; break;
                        case Mode.Blue: r = g = b; break;
                        case Mode.Alpha: r = g = b = a; break;
                    }
                    if (_mode == Mode.RgbOverChecker)
                    {
                        int bg = ((x >> 4) + (y >> 4)) % 2 == 0 ? 0x80 : 0x58;
                        b = (byte)((b * a + bg * (255 - a)) / 255); g = (byte)((g * a + bg * (255 - a)) / 255); r = (byte)((r * a + bg * (255 - a)) / 255);
                    }
                    px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
                }
            var src = BitmapSource.Create(_w, _h, 96, 96, PixelFormats.Bgra32, null, px, _w * 4);
            src.Freeze();
            _image.Source = src;
            ApplyZoom();
        }
        finally { _loading = false; }
    }

    void ApplyZoom()
    {
        if (_w == 0) return;
        _image.Width = _w * _zoom.Value; _image.Height = _h * _zoom.Value;
    }
}
