using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// Paints one logo onto a garment colour map: choose an image, click where it goes, set its size. The result is a
/// <see cref="StampEntry"/> that the matcher bakes into the exported texture, so a sponsor can reach a kit whose
/// branding array has no free slice at all.
/// </summary>
public sealed class StampWindow : Window
{
    readonly double _w, _h;
    readonly Canvas _canvas = new();
    readonly Image _logo = new() { Stretch = Stretch.Fill, Opacity = 0.92, IsHitTestVisible = false };
    readonly Slider _size = new() { Minimum = 0.05, Maximum = 0.95, Value = 0.25, Width = 240, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _where = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x8c, 0x97, 0xa8)), VerticalAlignment = VerticalAlignment.Center };
    double _x = 0.5, _y = 0.5, _aspect = 1;
    string? _logoPath;

    public StampEntry? Result { get; private set; }

    public StampWindow(string slot, string basePath, StampEntry? existing)
    {
        Title = "Stamp a logo on the colour map";
        Width = 760; Height = 820;
        Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1a, 0x22));
        Foreground = new SolidColorBrush(Color.FromRgb(0xe6, 0xeb, 0xf2));
        var baseBmp = Load(basePath) ?? throw new InvalidOperationException("Cannot read " + basePath);
        _aspect = (double)baseBmp.PixelWidth / Math.Max(1, baseBmp.PixelHeight);
        const double box = 520;
        _w = _aspect >= 1 ? box : box * _aspect;
        _h = _aspect >= 1 ? box / _aspect : box;

        _canvas.Width = _w;
        _canvas.Height = _h;
        _canvas.Background = new SolidColorBrush(Color.FromRgb(0x2a, 0x31, 0x3d));
        _canvas.Children.Add(new Image { Source = baseBmp, Stretch = Stretch.Fill, Width = _w, Height = _h, IsHitTestVisible = false });
        _canvas.Children.Add(_logo);
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            var p = e.GetPosition(_canvas);
            _x = Math.Clamp(p.X / _w, 0, 1);
            _y = Math.Clamp(p.Y / _h, 0, 1);
            Redraw();
        };
        _size.ValueChanged += (_, _) => Redraw();

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = "Click on the map to place the logo, then set its size. It is baked into the exported colour texture " +
                   "for this slot, so it reaches the game even when the branding array has no free slice.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8c, 0x97, 0xa8)),
        });
        root.Children.Add(new TextBlock { Text = slot.Split('/')[^1], FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        root.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x36, 0x40, 0x4f)), BorderThickness = new Thickness(1),
            Child = _canvas, HorizontalAlignment = HorizontalAlignment.Left,
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var choose = new Button { Content = "Choose logo…", Padding = new Thickness(12, 4, 12, 4) };
        choose.Click += (_, _) => Choose();
        row.Children.Add(choose);
        row.Children.Add(new TextBlock { Text = "   size ", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_size);
        row.Children.Add(_where);
        root.Children.Add(row);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var apply = new Button { Content = "Stamp it", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        apply.Click += (_, _) => Apply(slot, basePath);
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;

        if (existing is not null)
        {
            _x = existing.X; _y = existing.Y; _size.Value = existing.Size;
            _logoPath = File.Exists(existing.Image) ? existing.Image : null;
            if (_logoPath is not null) _logo.Source = Load(_logoPath);
        }
        Redraw();
    }

    static BitmapImage? Load(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    void Choose()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Logo to paint onto the colour map", Filter = "Images (*.png;*.dds;*.jpg;*.tga)|*.png;*.dds;*.jpg;*.jpeg;*.tga;*.bmp|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        var bmp = Load(dlg.FileName);
        if (bmp is null) { MessageBox.Show(this, "That image cannot be read.", "Stamp"); return; }
        _logoPath = dlg.FileName;
        _logo.Source = bmp;
        Redraw();
    }

    void Redraw()
    {
        var size = _size.Value * Math.Min(_w, _h);
        var bmp = _logo.Source as BitmapSource;
        var aspect = bmp is null ? 1 : (double)bmp.PixelWidth / Math.Max(1, bmp.PixelHeight);
        _logo.Width = size;
        _logo.Height = size / Math.Max(0.01, aspect);
        Canvas.SetLeft(_logo, _x * _w - _logo.Width / 2);
        Canvas.SetTop(_logo, _y * _h - _logo.Height / 2);
        _where.Text = string.Format("   x {0:P0}   y {1:P0}", _x, _y);
    }

    void Apply(string slot, string basePath)
    {
        if (_logoPath is null) { MessageBox.Show(this, "Choose a logo first.", "Stamp"); return; }
        Result = new StampEntry { Slot = slot, Image = _logoPath, Base = basePath, X = _x, Y = _y, Size = _size.Value };
        DialogResult = true;
    }
}