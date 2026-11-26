using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Suite.Convert27.Controls;

/// <summary>
/// Colours the pickers offer besides the free hue square: the game's own swatches (creation_zone_csv\colorswatches.csv, loaded
/// once from the game by the main window), colours picked this session, and any team colours the page passes in.
/// </summary>
public static class ColorPalette
{
    static readonly List<int> RecentList = [];
    public static IReadOnlyList<int> Game { get; private set; } = [];
    public static IReadOnlyList<int> Recent => RecentList;

    /// <summary>Called with the game's swatch colours (decimal RRGGBB in the CSV). Pickers opened later show them.</summary>
    public static void SetGame(IEnumerable<int> colours) => Game = colours.Where(c => c is >= 0 and <= 0xFFFFFF).Distinct().ToList();

    public static void Remember(int rgb)
    {
        RecentList.Remove(rgb);
        RecentList.Insert(0, rgb);
        if (RecentList.Count > 14) RecentList.RemoveAt(RecentList.Count - 1);
    }

    public static Color ToColor(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    public static string Hex(int rgb) => (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    /// <summary>"0072CE", "#0072ce" or the three-digit "07C" (= 0077CC).</summary>
    public static bool TryParse(string? text, out int rgb)
    {
        rgb = 0;
        var t = (text ?? "").Trim().TrimStart('#');
        if (t.Length == 3) t = string.Concat(t.Select(c => new string(c, 2)));
        return t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
    }

    public static (double H, double S, double V) ToHsv(int rgb)
    {
        double r = (rgb >> 16 & 255) / 255.0, g = (rgb >> 8 & 255) / 255.0, b = (rgb & 255) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min, h = 0;
        if (d > 0)
            h = max == r ? 60 * (((g - b) / d + 6) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static int FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c, r, g, b;
        (r, g, b) = (int)(h / 60) switch { 0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x) };
        int R = (int)Math.Round((r + m) * 255), G = (int)Math.Round((g + m) * 255), B = (int)Math.Round((b + m) * 255);
        return R << 16 | G << 8 | B;
    }
}

/// <summary>
/// The colour picker: a saturation/value square and a hue bar, a hex box with red/green/blue numbers, the colours of the team
/// you are working on, the game's own swatches and the colours you used last. Every change raises <see cref="Picked"/> at once,
/// so a page can show the result live; <see cref="Committed"/> fires when the mouse is released or a swatch is clicked.
/// </summary>
public sealed class ColorPickerPanel : Border
{
    const int Size = 188;
    static readonly Brush Edge = new SolidColorBrush(Color.FromRgb(0x38, 0x46, 0x5c));
    static readonly Brush Soft = new SolidColorBrush(Color.FromRgb(0x9f, 0xb4, 0xcf));

    readonly WriteableBitmap _square = new(Size, Size, 96, 96, PixelFormats.Bgra32, null);
    readonly Image _squareImage;
    readonly Canvas _squareMarks = new() { Width = Size, Height = Size, IsHitTestVisible = false };
    readonly Ellipse _marker = new() { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, Fill = Brushes.Transparent };
    readonly Border _hueBar = new() { Width = Size, Height = 16, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 8, 0, 0) };
    readonly Border _hueMark = new() { Width = 4, Height = 20, Background = Brushes.White, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1) };
    readonly Canvas _hueMarks = new() { Width = Size, Height = 20, IsHitTestVisible = false };
    readonly Border _old = new() { Width = 46, Height = 26, CornerRadius = new CornerRadius(3, 0, 0, 3) };
    readonly Border _new = new() { Width = 46, Height = 26, CornerRadius = new CornerRadius(0, 3, 3, 0) };
    readonly TextBox _hex = new() { Width = 68, FontFamily = new FontFamily("Consolas"), MaxLength = 7, Padding = new Thickness(4, 3, 4, 3) };
    readonly TextBox _r = Number(), _g = Number(), _b = Number();
    readonly StackPanel _lists = new() { Margin = new Thickness(0, 8, 0, 0) };
    double _h, _s, _v;
    int _original;
    bool _syncing, _dragSquare, _dragHue;

    public event Action<int>? Picked;
    public event Action<int>? Committed;
    public int Rgb { get; private set; }
    public IReadOnlyList<int> TeamColours { get; set; } = [];

    static TextBox Number() => new() { Width = 36, MaxLength = 3, Padding = new Thickness(2, 3, 2, 3), Margin = new Thickness(3, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };

    public ColorPickerPanel()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1b, 0x25, 0x36));
        BorderBrush = Edge;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(10);
        Width = Size + 22;

        _squareImage = new Image { Source = _square, Width = Size, Height = Size, Stretch = Stretch.None, Cursor = Cursors.Cross };
        _squareMarks.Children.Add(_marker);
        var squareHost = new Grid { Width = Size, Height = Size, ClipToBounds = true };
        squareHost.Children.Add(_squareImage);
        squareHost.Children.Add(_squareMarks);
        squareHost.MouseLeftButtonDown += (_, e) => { _dragSquare = true; squareHost.CaptureMouse(); MoveSquare(e.GetPosition(squareHost)); };
        squareHost.MouseMove += (_, e) => { if (_dragSquare) MoveSquare(e.GetPosition(squareHost)); };
        squareHost.MouseLeftButtonUp += (_, _) => { if (_dragSquare) { _dragSquare = false; squareHost.ReleaseMouseCapture(); Commit(); } };

        _hueBar.Background = new LinearGradientBrush(new GradientStopCollection(Enumerable.Range(0, 7).Select(i => new GradientStop(ColorPalette.ToColor(ColorPalette.FromHsv(i * 60, 1, 1)), i / 6.0))), 0);
        _hueMarks.Children.Add(_hueMark);
        var hueHost = new Grid { Width = Size, Height = 20, Margin = new Thickness(0, 4, 0, 0), Cursor = Cursors.Hand };
        hueHost.Children.Add(new Border { Height = 16, CornerRadius = new CornerRadius(3), Background = _hueBar.Background });
        hueHost.Children.Add(_hueMarks);
        hueHost.MouseLeftButtonDown += (_, e) => { _dragHue = true; hueHost.CaptureMouse(); MoveHue(e.GetPosition(hueHost)); };
        hueHost.MouseMove += (_, e) => { if (_dragHue) MoveHue(e.GetPosition(hueHost)); };
        hueHost.MouseLeftButtonUp += (_, _) => { if (_dragHue) { _dragHue = false; hueHost.ReleaseMouseCapture(); Commit(); } };

        var compare = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        compare.Children.Add(Label("was"));
        compare.Children.Add(_old);
        compare.Children.Add(_new);
        compare.Children.Add(Label("now"));
        var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        entry.Children.Add(_hex);
        entry.Children.Add(_r);
        entry.Children.Add(_g);
        entry.Children.Add(_b);
        _hex.ToolTip = "RRGGBB";
        _r.ToolTip = "Red 0-255"; _g.ToolTip = "Green 0-255"; _b.ToolTip = "Blue 0-255";
        _hex.TextChanged += (_, _) => { if (!_syncing && ColorPalette.TryParse(_hex.Text, out var rgb)) Set(rgb, fromText: true, commit: false); };
        _hex.LostKeyboardFocus += (_, _) => { ShowText(); Commit(); };
        foreach (var box in new[] { _r, _g, _b })
        {
            box.TextChanged += (_, _) =>
            {
                if (_syncing || !int.TryParse(_r.Text, out var r) || !int.TryParse(_g.Text, out var g) || !int.TryParse(_b.Text, out var b)) return;
                Set(Math.Clamp(r, 0, 255) << 16 | Math.Clamp(g, 0, 255) << 8 | Math.Clamp(b, 0, 255), fromText: true, commit: false);
            };
            box.LostKeyboardFocus += (_, _) => { ShowText(); Commit(); };
        }

        var stack = new StackPanel();
        stack.Children.Add(squareHost);
        stack.Children.Add(hueHost);
        stack.Children.Add(new Grid { Margin = new Thickness(0, 8, 0, 0), Children = { compare } });
        stack.Children.Add(entry);
        stack.Children.Add(_lists);
        Child = stack;
    }

    static TextBlock Label(string text) => new() { Text = text, Foreground = Soft, FontSize = 11, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Shows the picker on <paramref name="rgb"/>; "was" keeps that colour for comparison.</summary>
    public void Open(int rgb)
    {
        _original = rgb & 0xFFFFFF;
        _old.Background = new SolidColorBrush(ColorPalette.ToColor(_original));
        Set(_original, fromText: false, commit: false);
        BuildLists();
    }

    void BuildLists()
    {
        _lists.Children.Clear();
        void Row(string title, IEnumerable<int> colours)
        {
            var list = colours.Distinct().ToList();
            if (list.Count == 0) return;
            _lists.Children.Add(new TextBlock { Text = title, Foreground = Soft, FontSize = 11, Margin = new Thickness(0, 6, 0, 3) });
            var wrap = new WrapPanel();
            foreach (var c in list) wrap.Children.Add(Chip(c));
            _lists.Children.Add(wrap);
        }
        Row("Team colours", TeamColours);
        Row("Used lately", ColorPalette.Recent);
        if (ColorPalette.Game.Count > 0)
        {
            _lists.Children.Add(new TextBlock { Text = $"The game's own swatches ({ColorPalette.Game.Count})", Foreground = Soft, FontSize = 11, Margin = new Thickness(0, 6, 0, 3) });
            var wrap = new WrapPanel { MaxHeight = 104 };
            foreach (var c in ColorPalette.Game) wrap.Children.Add(Chip(c));
            _lists.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 104, Content = wrap });
        }
    }

    Border Chip(int rgb)
    {
        var chip = new Border
        {
            Width = 16, Height = 16, Margin = new Thickness(0, 0, 3, 3), CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1), BorderBrush = Edge,
            Background = new SolidColorBrush(ColorPalette.ToColor(rgb)), Cursor = Cursors.Hand, ToolTip = "#" + ColorPalette.Hex(rgb),
        };
        chip.MouseLeftButtonUp += (_, _) => { Set(rgb, fromText: false, commit: true); };
        return chip;
    }

    void MoveSquare(Point p)
    {
        _s = Math.Clamp(p.X / (Size - 1), 0, 1);
        _v = 1 - Math.Clamp(p.Y / (Size - 1), 0, 1);
        Apply(commit: false);
    }

    void MoveHue(Point p)
    {
        _h = Math.Clamp(p.X / (Size - 1), 0, 1) * 360;
        RenderSquare();
        Apply(commit: false);
    }

    void Set(int rgb, bool fromText, bool commit)
    {
        (_h, _s, _v) = ColorPalette.ToHsv(rgb);
        if (_s == 0 && !fromText) _h = _h; // greys keep the hue the bar last had
        Rgb = rgb & 0xFFFFFF;
        RenderSquare();
        Show(fromText);
        Picked?.Invoke(Rgb);
        if (commit) Commit();
    }

    void Apply(bool commit)
    {
        Rgb = ColorPalette.FromHsv(_h, _s, _v);
        Show(false);
        Picked?.Invoke(Rgb);
        if (commit) Commit();
    }

    void Commit()
    {
        ColorPalette.Remember(Rgb);
        Committed?.Invoke(Rgb);
    }

    void Show(bool keepText)
    {
        _new.Background = new SolidColorBrush(ColorPalette.ToColor(Rgb));
        Canvas.SetLeft(_marker, _s * (Size - 1) - 6);
        Canvas.SetTop(_marker, (1 - _v) * (Size - 1) - 6);
        Canvas.SetLeft(_hueMark, _h / 360 * (Size - 1) - 2);
        Canvas.SetTop(_hueMark, 0);
        if (!keepText) ShowText();
    }

    void ShowText()
    {
        _syncing = true;
        _hex.Text = ColorPalette.Hex(Rgb);
        _r.Text = (Rgb >> 16 & 255).ToString(CultureInfo.InvariantCulture);
        _g.Text = (Rgb >> 8 & 255).ToString(CultureInfo.InvariantCulture);
        _b.Text = (Rgb & 255).ToString(CultureInfo.InvariantCulture);
        _syncing = false;
    }

    void RenderSquare()
    {
        var pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            double v = 1 - y / (double)(Size - 1);
            for (int x = 0; x < Size; x++)
            {
                int rgb = ColorPalette.FromHsv(_h, x / (double)(Size - 1), v), i = (y * Size + x) * 4;
                pixels[i] = (byte)rgb; pixels[i + 1] = (byte)(rgb >> 8); pixels[i + 2] = (byte)(rgb >> 16); pixels[i + 3] = 255;
            }
        }
        _square.WritePixels(new Int32Rect(0, 0, Size, Size), pixels, Size * 4, 0);
    }
}

/// <summary>
/// A colour cell: a swatch and a hex box. Clicking the swatch opens the picker. <see cref="Hex"/> is six hex digits and binds two
/// ways, so a page replaces its swatch + text box pair with this one control.
/// </summary>
public sealed class ColorField : UserControl
{
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(nameof(Hex), typeof(string), typeof(ColorField),
        new FrameworkPropertyMetadata("000000", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ColorField)d).FromHex((string?)e.NewValue)));

    public static readonly DependencyProperty TeamColoursProperty = DependencyProperty.Register(nameof(TeamColours), typeof(IEnumerable<int>), typeof(ColorField));

    public string Hex { get => (string)GetValue(HexProperty); set => SetValue(HexProperty, value); }
    public IEnumerable<int>? TeamColours { get => (IEnumerable<int>?)GetValue(TeamColoursProperty); set => SetValue(TeamColoursProperty, value); }

    readonly Border _swatch = new() { Width = 24, Height = 24, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(0x62, 0x71, 0x8a)), Cursor = Cursors.Hand, ToolTip = "Open the colour picker" };
    readonly TextBox _box = new() { Width = 70, MaxLength = 7, FontFamily = new FontFamily("Consolas"), Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(6, 0, 0, 0), ToolTip = "A colour as RRGGBB, for example 0072CE" };
    readonly Popup _popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, VerticalOffset = 4 };
    readonly ColorPickerPanel _panel = new();
    bool _syncing;

    public ColorField()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_swatch);
        row.Children.Add(_box);
        Content = row;
        _popup.PlacementTarget = _swatch;
        _popup.Child = _panel;
        _swatch.MouseLeftButtonUp += (_, _) => Toggle();
        _box.TextChanged += (_, _) =>
        {
            if (_syncing || !ColorPalette.TryParse(_box.Text, out var rgb)) return;
            Paint(rgb);
            WriteBack(rgb);
        };
        _box.LostKeyboardFocus += (_, _) => { if (ColorPalette.TryParse(Hex, out var rgb)) ShowText(rgb); };
        _panel.Picked += rgb => { Paint(rgb); WriteBack(rgb); ShowText(rgb); };
        FromHex(Hex);
    }

    void Toggle()
    {
        if (_popup.IsOpen) { _popup.IsOpen = false; return; }
        _panel.TeamColours = TeamColours?.ToList() ?? [];
        _panel.Open(ColorPalette.TryParse(Hex, out var rgb) ? rgb : 0);
        _popup.IsOpen = true;
    }

    void FromHex(string? hex)
    {
        if (!ColorPalette.TryParse(hex, out var rgb)) { _swatch.Background = new SolidColorBrush(Color.FromRgb(0x59, 0x62, 0x73)); return; }
        Paint(rgb);
        if (!_box.IsKeyboardFocused) ShowText(rgb);
    }

    void Paint(int rgb) => _swatch.Background = new SolidColorBrush(ColorPalette.ToColor(rgb));

    void ShowText(int rgb)
    {
        _syncing = true;
        _box.Text = ColorPalette.Hex(rgb);
        _syncing = false;
    }

    void WriteBack(int rgb)
    {
        var hex = ColorPalette.Hex(rgb);
        if (!string.Equals(Hex, hex, StringComparison.Ordinal)) SetCurrentValue(HexProperty, hex);
    }
}
