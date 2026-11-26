using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;
using Microsoft.Win32;
using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.App.Pages;

/// <summary>
/// Patch studio: every logo/patch painted on a uniform's jersey sheet, editable in place. Pick a team and set, and the
/// jersey's colour sheet comes up with the same look it has in game. Drag a box around a patch, then:
///   Erase — click clean fabric next to it; the box is filled with that fabric.
///   Move  — click where the patch should sit; the old spot is filled with fabric from just below.
///   Add   — drop a picture from disk anywhere on the sheet.
/// The same pixel edits are mirrored onto the normal and coefficient sheets so erased patches leave no relief or sheen
/// behind. "Apply to game" writes with the suite's own backup + read-back (TextureSlots.Apply) and refuses a running game.
/// </summary>
public sealed class PatchStudioPage : Grid
{
    sealed record Kit(string Set, string Color, string Normal, string Coeff, List<string> Branding);

    sealed class Op // one edit, kept for undo
    {
        public required string Kind { get; init; }                 // erase | move | add
        public required (int X, int Y, int W, int H)[] Rects { get; init; } // colour-sheet rects the op touched
        public required Dictionary<string, byte[]>[] Before { get; init; }  // per rect, per sheet ("color"/"normal"/"coeff") -> bytes before
    }

    readonly SuiteSettings _settings;
    readonly ComboBox _team = new() { Width = 240, Margin = new Thickness(0, 0, 12, 0) };
    readonly ComboBox _set = new() { Width = 200, Margin = new Thickness(0, 0, 12, 0) };
    readonly Button _load = new() { Content = "Load uniform", Padding = new Thickness(14, 5, 14, 5) };
    readonly Slider _zoom = new() { Minimum = 0.2, Maximum = 1.5, Value = 0.5, Width = 140, Margin = new Thickness(12, 0, 12, 0) };
    readonly Button _erase = new() { Content = "Erase selection", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false };
    readonly Button _move = new() { Content = "Move selection", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    readonly Button _add = new() { Content = "Add picture…", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    readonly Button _undo = new() { Content = "Undo", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    readonly Button _export = new() { Content = "Export PNG…", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    readonly Button _apply = new() { Content = "Apply to game", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)), Margin = new Thickness(0, 10, 0, 0) };
    readonly ListBox _layers = new()
    {
        Height = 180, Margin = new Thickness(0, 8, 0, 8),
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1a, 0x24)), Foreground = new SolidColorBrush(Color.FromRgb(0xe6, 0xeb, 0xf2)),
    };
    readonly TextBlock _help = new()
    {
        TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x9f, 0xb4, 0xcf)), Margin = new Thickness(0, 8, 0, 0),
        Text = "Drag a box around a patch. Erase: click clean fabric to copy over it. Move: click the new spot. Add picture: click where it goes. " +
               "The same edits are made on the normal and coefficient sheets. Apply writes into the selected NHL 24 build with a backup and read-back.",
    };

    // canvas
    readonly Canvas _canvas = new();
    readonly Image _img = new() { Stretch = Stretch.None };
    readonly ScaleTransform _scale = new(0.5, 0.5);
    readonly Rectangle _selRect = new()
    {
        Stroke = Brushes.Gold, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 },
        Fill = new SolidColorBrush(Color.FromArgb(30, 255, 215, 0)), Visibility = Visibility.Collapsed,
    };
    readonly Rectangle _ghost = new() { Stroke = Brushes.LightGreen, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 2, 3 }, Visibility = Visibility.Collapsed };

    BundleIndex? _index;
    Kit? _kit;
    readonly Dictionary<string, Rgba?> _sheets = new(); // color/normal/coeff decoded mip0
    readonly List<Op> _ops = [];
    (int X, int Y, int W, int H)? _sel;
    string? _mode;                                   // null | "erase" | "move" | "add"
    Rgba? _addImg; double _addScale = 1.0;
    Point _dragStart; bool _dragging;

    public PatchStudioPage(SuiteSettings settings)
    {
        _settings = settings;
        var top = new WrapPanel { Margin = new Thickness(24, 20, 24, 8) };
        top.Children.Add(_team); top.Children.Add(_set); top.Children.Add(_load);
        top.Children.Add(new TextBlock { Text = "Zoom", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(_zoom);

        var tools = new StackPanel { Width = 240, Margin = new Thickness(0, 0, 24, 24) };
        tools.Children.Add(new TextBlock { Text = "Patches on this uniform", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6) });
        tools.Children.Add(_erase); tools.Children.Add(_move); tools.Children.Add(_add);
        tools.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10) });
        tools.Children.Add(_undo); tools.Children.Add(_export); tools.Children.Add(_apply);
        tools.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10) });
        tools.Children.Add(new TextBlock { Text = "Logo layers on this uniform (information only)", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x9f, 0xb4, 0xcf)) });
        tools.Children.Add(_layers);
        tools.Children.Add(_help);

        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 24, 24) };
        _canvas.Children.Add(_img); _canvas.Children.Add(_ghost); _canvas.Children.Add(_selRect);
        _canvas.LayoutTransform = _scale;
        _canvas.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1c));
        scroll.Content = _canvas;

        var body = new DockPanel();
        DockPanel.SetDock(tools, Dock.Left);
        body.Children.Add(tools); body.Children.Add(scroll);

        var main = new DockPanel { Margin = new Thickness(0, 0, 0, 24) };
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(_status, Dock.Bottom);
        main.Children.Add(top); main.Children.Add(_status); main.Children.Add(body);
        var outer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = main };
        Children.Add(outer);

        foreach (var t in VariantMap.Load().Teams.OrderBy(t => t.Name24))
            _team.Items.Add(new ComboBoxItem { Content = t.Name24, Tag = t.Nhl24.Folder });

        _load.Click += (_, _) => LoadKit();
        _zoom.ValueChanged += (_, _) => _scale.ScaleX = _scale.ScaleY = _zoom.Value;
        _canvas.MouseLeftButtonDown += CanvasDown;
        _canvas.MouseMove += CanvasMove;
        _canvas.MouseLeftButtonUp += CanvasUp;
        _erase.Click += (_, _) => { if (_sel is not null) { _mode = "erase"; _status.Text = "Erase: click clean fabric to copy over the selected box."; } };
        _move.Click += (_, _) => { if (_sel is not null) { _mode = "move"; _status.Text = "Move: click the new top-left spot for the selected box."; } };
        _add.Click += AddPicture;
        _undo.Click += (_, _) => Undo();
        _export.Click += (_, _) => ExportPng();
        _apply.Click += (_, _) => Apply();
    }

    void LoadKit()
    {
        try
        {
            string game = _settings.ApplyTargetDir;
            string folder = (_team.SelectedItem as ComboBoxItem)?.Tag as string ?? throw new InvalidOperationException("Pick a team first.");
            if (_index is null || _layers.Tag as string != game) { _index = BundleIndex.Build(game); _layers.Tag = game; }
            string base_ = $"content/character/skater/league/nhl/{folder}/adidas/";
            var sets = _index.ResByName.Keys.Where(n => n.StartsWith(base_, StringComparison.Ordinal) && n.EndsWith("_color", StringComparison.Ordinal))
                .Select(n => n[base_.Length..].Split('/')).Where(p => p.Length == 2)
                .Where(p => p[1] == $"jersey_adidas_{folder}_{p[0]}_color")
                .Select(p => p[0]).Distinct(StringComparer.Ordinal).OrderBy(s => s).ToList();
            if (sets.Count == 0) throw new InvalidDataException($"No jersey sheets found for {folder}.");
            if (_set.Tag as string != folder)
            {
                _set.Items.Clear();
                foreach (var s in sets) _set.Items.Add(s);
                _set.SelectedIndex = 0; _set.Tag = folder;
            }
            string set = _set.SelectedItem as string ?? sets[0];
            string prefix = $"{base_}{set}/jersey_adidas_{folder}_{set}";
            string? Find(string kind) => _index.ResByName.ContainsKey($"{prefix}_{kind}") ? $"{prefix}_{kind}" : null;
            string color = Find("color") ?? throw new InvalidDataException($"{prefix}_color is not in this game.");
            var branding = _index.ResByName.Keys.Where(n => n.StartsWith($"{base_}{set}/branding_", StringComparison.Ordinal))
                .OrderBy(n => n).ToList();
            _kit = new Kit(set, color, Find("normal") ?? "", Find("coeff") ?? "", branding);
            _sheets.Clear(); _ops.Clear(); _sel = null; _mode = null; _addImg = null;
            _sheets["color"] = Ps4Codec.DecodeSlices(TextureSlots.Read(game, _index, _kit.Color).Res, TextureSlots.Read(game, _index, _kit.Color).Stock)[0];
            if (_kit.Normal != "") _sheets["normal"] = Ps4Codec.DecodeSlices(TextureSlots.Read(game, _index, _kit.Normal).Res, TextureSlots.Read(game, _index, _kit.Normal).Stock)[0];
            if (_kit.Coeff != "") _sheets["coeff"] = Ps4Codec.DecodeSlices(TextureSlots.Read(game, _index, _kit.Coeff).Res, TextureSlots.Read(game, _index, _kit.Coeff).Stock)[0];
            _layers.Items.Clear();
            foreach (var b in branding) _layers.Items.Add(b[(base_.Length + set.Length + 1)..]);
            _status.Text = $"{_team.Text} — {set}: {(_sheets["color"] is { } c ? $"{c.Width}×{c.Height}" : "")} colour sheet" +
                           $"{(_sheets["normal"] is null ? "" : " + normal")}{(_sheets["coeff"] is null ? "" : " + coeff")}; {branding.Count} logo layer(s).";
            _erase.IsEnabled = _move.IsEnabled = _add.IsEnabled = _export.IsEnabled = true;
            _undo.IsEnabled = false; _apply.IsEnabled = true;
            Redraw();
        }
        catch (Exception ex) { _status.Text = "Load failed: " + ex.Message; }
    }

    void Redraw()
    {
        if (_sheets["color"] is not { } img) return;
        var bmp = new WriteableBitmap(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null);
        var px = new byte[img.Pixels.Length];
        for (int i = 0; i < px.Length; i += 4) { px[i] = img.Pixels[i + 2]; px[i + 1] = img.Pixels[i + 1]; px[i + 2] = img.Pixels[i]; px[i + 3] = 255; }
        bmp.WritePixels(new Int32Rect(0, 0, img.Width, img.Height), px, img.Width * 4, 0);
        bmp.Freeze();
        _img.Source = bmp;
        _canvas.Width = img.Width; _canvas.Height = img.Height;
        ShowSel();
    }

    void ShowSel()
    {
        if (_sel is { } s)
        {
            _selRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(_selRect, s.X); Canvas.SetTop(_selRect, s.Y);
            _selRect.Width = s.W; _selRect.Height = s.H;
        }
        else _selRect.Visibility = Visibility.Collapsed;
    }

    (int X, int Y) CanvasPoint(MouseEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        return ((int)Math.Clamp(p.X, 0, (_sheets["color"]?.Width ?? 1) - 1), (int)Math.Clamp(p.Y, 0, (_sheets["color"]?.Height ?? 1) - 1));
    }

    void CanvasDown(object s, MouseButtonEventArgs e)
    {
        if (_sheets["color"] is null) return;
        var p = CanvasPoint(e);
        if (_mode is "erase" && _sel is { } eraseSel) { DoErase(eraseSel, p); _mode = null; return; }
        if (_mode is "move" && _sel is { } moveSel) { DoMove(moveSel, p); _mode = null; return; }
        if (_mode is "add" && _addImg is not null) { DoAdd(p); _mode = null; return; }
        _dragStart = e.GetPosition(_canvas); _dragging = true;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    void CanvasMove(object s, MouseEventArgs e)
    {
        if (!_dragging || _sheets["color"] is not { } img) return;
        var p = e.GetPosition(_canvas);
        int x = (int)Math.Clamp(Math.Min(_dragStart.X, p.X), 0, img.Width - 1);
        int y = (int)Math.Clamp(Math.Min(_dragStart.Y, p.Y), 0, img.Height - 1);
        int w = (int)Math.Clamp(Math.Abs(p.X - _dragStart.X), 1, img.Width - x);
        int h = (int)Math.Clamp(Math.Abs(p.Y - _dragStart.Y), 1, img.Height - y);
        _sel = (x, y, w, h); ShowSel();
    }

    void CanvasUp(object s, MouseButtonEventArgs e)
    {
        if (_dragging) { _dragging = false; _canvas.ReleaseMouseCapture(); }
    }

    // ---- pixel ops (mirrored onto every decoded sheet) ----

    static void CopyRect(Rgba img, int sx, int sy, int dx, int dy, int w, int h)
    {
        var px = img.Pixels;
        var buf = new byte[w * h * 4];
        for (int r = 0; r < h; r++) Buffer.BlockCopy(px, ((sy + r) * img.Width + sx) * 4, buf, r * w * 4, w * 4);
        for (int r = 0; r < h; r++) Buffer.BlockCopy(buf, r * w * 4, px, ((dy + r) * img.Width + dx) * 4, w * 4);
    }

    Op BeginOp(string kind, params (int X, int Y, int W, int H)[] rects)
    {
        var before = rects.Select(r =>
        {
            var d = new Dictionary<string, byte[]>();
            foreach (var (k, sheet) in _sheets)
                if (sheet is not null)
                {
                    var b = new byte[r.W * r.H * 4];
                    for (int row = 0; row < r.H; row++)
                        Buffer.BlockCopy(sheet.Pixels, ((r.Y + row) * sheet.Width + r.X) * 4, b, row * r.W * 4, r.W * 4);
                    d[k] = b;
                }
            return d;
        }).ToArray();
        return new Op { Kind = kind, Rects = rects, Before = before };
    }

    void EndOp(Op op, string note)
    {
        _ops.Add(op); if (_ops.Count > 30) _ops.RemoveAt(0);
        _undo.IsEnabled = true; _apply.IsEnabled = true;
        _status.Text = note;
        Redraw();
    }

    void DoErase((int X, int Y, int W, int H) sel, (int X, int Y) src)
    {
        // copy the same-size area starting at src over the selection, on every decoded sheet
        int sx = Math.Clamp(src.X, 0, _sheets["color"]!.Width - sel.W);
        int sy = Math.Clamp(src.Y, 0, _sheets["color"]!.Height - sel.H);
        var op = BeginOp("erase", sel);
        foreach (var sheet in _sheets.Values) if (sheet is not null) CopyRect(sheet, sx, sy, sel.X, sel.Y, sel.W, sel.H);
        EndOp(op, $"Erased ({sel.W}×{sel.H}) at {sel.X},{sel.Y} with fabric from {sx},{sy}.");
    }

    void DoMove((int X, int Y, int W, int H) sel, (int X, int Y) dst)
    {
        int dx = Math.Clamp(dst.X, 0, _sheets["color"]!.Width - sel.W);
        int dy = Math.Clamp(dst.Y, 0, _sheets["color"]!.Height - sel.H);
        int fy = sel.Y + sel.H + 2; // fill the old spot with fabric from just below (above if that runs off the sheet)
        if (fy + sel.H > _sheets["color"]!.Height) fy = sel.Y - sel.H - 2;
        fy = Math.Clamp(fy, 0, _sheets["color"]!.Height - sel.H);
        var op = BeginOp("move", sel, (dx, dy, sel.W, sel.H));
        foreach (var sheet in _sheets.Values)
            if (sheet is not null) { CopyRect(sheet, sel.X, sel.Y, dx, dy, sel.W, sel.H); CopyRect(sheet, sel.X, fy, sel.X, sel.Y, sel.W, sel.H); }
        EndOp(op, $"Moved ({sel.W}×{sel.H}) to {dx},{dy}.");
    }

    void AddPicture(object s, RoutedEventArgs e)
    {
        if (_sheets["color"] is null) return;
        var dlg = new OpenFileDialog { Filter = "Pictures (png, dds)|*.png;*.dds" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _addImg = ImageIO.CanLoad(dlg.FileName) ? ImageIO.Load(dlg.FileName) : Dds.Load(dlg.FileName);
            _mode = "add";
            _status.Text = $"Picture loaded ({_addImg.Width}×{_addImg.Height}): click where its top-left corner goes.";
        }
        catch (Exception ex) { _status.Text = "Could not load that picture: " + ex.Message; }
    }

    void DoAdd((int X, int Y) dst)
    {
        if (_addImg is null || _sheets["color"] is not { } color) return;
        var img = _addScale == 1.0 ? _addImg : ImageIO.Resize(_addImg, Math.Max(1, (int)(_addImg.Width * _addScale)), Math.Max(1, (int)(_addImg.Height * _addScale)));
        int w = Math.Min(img.Width, color.Width - dst.X), h = Math.Min(img.Height, color.Height - dst.Y);
        var op = BeginOp("add", (dst.X, dst.Y, w, h));
        for (int r = 0; r < h; r++)
            Buffer.BlockCopy(img.Pixels, r * img.Width * 4, color.Pixels, ((dst.Y + r) * color.Width + dst.X) * 4, w * 4);
        _addImg = null;
        EndOp(op, $"Picture added at {dst.X},{dst.Y} ({w}×{h}). Note: added pictures paint the colour sheet only — erase/move also fix normal and coeff.");
    }

    void Undo()
    {
        if (_ops.Count == 0) return;
        var op = _ops[^1]; _ops.RemoveAt(_ops.Count - 1);
        for (int i = 0; i < op.Rects.Length; i++)
        {
            var r = op.Rects[i];
            foreach (var (k, sheet) in _sheets)
                if (sheet is not null && op.Before[i].TryGetValue(k, out var b))
                    for (int row = 0; row < r.H; row++)
                        Buffer.BlockCopy(b, row * r.W * 4, sheet.Pixels, ((r.Y + row) * sheet.Width + r.X) * 4, r.W * 4);
        }
        _undo.IsEnabled = _ops.Count > 0;
        _status.Text = $"Undid {op.Kind}.";
        Redraw();
    }

    void ExportPng()
    {
        if (_sheets["color"] is not { } color || _kit is null) return;
        var dlg = new SaveFileDialog { Filter = "PNG|*.png", FileName = $"jersey_{_kit.Set}_color.png" };
        if (dlg.ShowDialog() != true) return;
        ImageIO.SavePng(color, dlg.FileName);
        _status.Text = "Saved " + dlg.FileName;
    }

    void Apply()
    {
        if (_kit is null || _ops.Count == 0) { _status.Text = _kit is null ? "Load a uniform first." : "No edits to apply."; return; }
        string game = _settings.ApplyTargetDir;
        if (ApplyTarget.Problems(game) is { Count: > 0 } problems) { _status.Text = "Apply refused: " + string.Join("; ", problems); return; }
        try
        {
            string tmp = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "patchstudio")), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var reps = new List<TextureReplacement>();
            var edited = _ops.SelectMany(o => o.Before.SelectMany(b => b.Keys)).Distinct().ToHashSet(StringComparer.Ordinal);
            foreach (var (kind, slot) in new[] { ("color", _kit.Color), ("normal", _kit.Normal), ("coeff", _kit.Coeff) })
            {
                if (slot == "" || _sheets[kind] is null || !edited.Contains(kind)) continue;
                string p = Path.Combine(tmp, kind + ".png");
                ImageIO.SavePng(_sheets[kind]!, p);
                reps.Add(new TextureReplacement(slot, p));
            }
            _status.Text = "Writing…";
            var (ok, msg, backup) = TextureSlots.Apply(game, reps, s => _status.Dispatcher.Invoke(() => _status.Text = s));
            _status.Text = (ok ? "Applied. " : "FAILED. ") + msg + (backup is null ? "" : $" Backup: {backup}");
            if (ok) { _ops.Clear(); _undo.IsEnabled = false; }
        }
        catch (Exception ex) { _status.Text = "Apply failed: " + ex.Message; }
    }
}
