using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.App.Pages;

/// <summary>
/// On-ice sponsor hotspots: the numbered ice_hotspot_<set>_<n> patches the game paints on the ice. Browse the stock
/// art of a set as thumbnails, pick one, replace it with a picture from disk (transparency kept; a picture without
/// transparency gets its white background keyed out — check the preview), then apply with the same backup and
/// read-back every other write uses. Which numbered patch an arena shows is the game's data; this page replaces the
/// art of the numbered slot.
/// </summary>
public sealed class HotspotsPage : Grid
{
    sealed class Target(string label, string game)
    {
        public string Label { get; } = label;
        public string Game { get; } = game;
        public override string ToString() => Label;
    }

    sealed class Thumb
    {
        public required int Index { get; init; }
        public required ImageSource Image { get; init; }
        public override string ToString() => Index.ToString();
    }

    readonly SuiteSettings _settings;
    readonly ComboBox _target = new() { Width = 460 };
    readonly ComboBox _set = new() { Width = 220, Margin = new Thickness(0, 0, 12, 0) };
    readonly TextBox _filter = new() { Width = 160, Margin = new Thickness(0, 0, 12, 0) };
    readonly ListBox _grid = new()
    {
        Margin = new Thickness(0, 8, 0, 0),
        Width = 980, Height = 420,
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1a, 0x24)), Foreground = new SolidColorBrush(Color.FromRgb(0xe6, 0xeb, 0xf2)),
        HorizontalContentAlignment = HorizontalAlignment.Left,
    };
    readonly Image _stock = new() { Stretch = Stretch.Uniform, Width = 380, Height = 380 };
    readonly Image _result = new() { Stretch = Stretch.Uniform, Width = 380, Height = 380 };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)) };
    readonly Button _export = new() { Content = "Export PNG…", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false, Margin = new Thickness(0, 0, 0, 8) };
    readonly Button _exportSet = new() { Content = "Export whole set…", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false, Margin = new Thickness(0, 0, 0, 8) };
    readonly Button _file = new() { Content = "From file…", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false };
    readonly Button _apply = new() { Content = "Apply to this game", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false };

    BundleIndex? _index;
    string? _indexGame;
    List<(int Set, int Index)> _slots = [];
    (int Set, int Index)? _selected;
    IceHotspots.Slot? _stockSlot;
    byte[]? _built;
    Rgba? _builtPreview;

    public HotspotsPage(SuiteSettings settings)
    {
        _settings = settings;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1c));

        var panel = new StackPanel { Margin = new Thickness(32, 26, 32, 26), MaxWidth = 1700, HorizontalAlignment = HorizontalAlignment.Left };

        panel.Children.Add(new TextBlock { Text = "Ice hotspots", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        panel.Children.Add(new TextBlock
        {
            Text = "The numbered sponsor patches painted on the ice. Pick a set and a patch, then replace its art. " +
                   "A picture with transparency is used as it is; a flat picture gets its white background keyed out — check the preview. " +
                   "Which patch an arena shows is decided by the game's arena data, so if a change does not show, that arena uses a different number.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8f, 0xa3, 0xb8)), FontSize = 12,
        });

        FillTargets();
        _target.SelectionChanged += async (_, _) => { ClearSelection(); await LoadSetsAsync(); };
        var targetRow = new StackPanel { Orientation = Orientation.Horizontal };
        targetRow.Children.Add(new TextBlock { Text = "Write to", Width = 76, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x8f, 0xa3, 0xb8)) });
        targetRow.Children.Add(_target);
        panel.Children.Add(targetRow);

        _set.SelectionChanged += SetChangedHandler;
        var setRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        setRow.Children.Add(new TextBlock { Text = "Set", Width = 76, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x8f, 0xa3, 0xb8)) });
        setRow.Children.Add(_set);
        setRow.Children.Add(new TextBlock { Text = "Filter", Width = 40, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x8f, 0xa3, 0xb8)) });
        setRow.Children.Add(_filter);
        panel.Children.Add(setRow);

        // thumbnails flow left-to-right and wrap, so a whole set is visible at a glance
        var wrapFactory = new FrameworkElementFactory(typeof(WrapPanel));
        _grid.ItemsPanel = new ItemsPanelTemplate(wrapFactory);
        // each patch: its picture on a dark square with its number underneath (a list with no template showed only bare numbers)
        var picture = new FrameworkElementFactory(typeof(Image));
        picture.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(Thumb.Image)));
        picture.SetValue(FrameworkElement.WidthProperty, 96.0); picture.SetValue(FrameworkElement.HeightProperty, 96.0);
        var number = new FrameworkElementFactory(typeof(TextBlock));
        number.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Thumb.Index)));
        number.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xe6, 0xeb, 0xf2)));
        number.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        number.SetValue(TextBlock.FontSizeProperty, 12.0);
        var cell = new FrameworkElementFactory(typeof(StackPanel));
        cell.AppendChild(picture); cell.AppendChild(number);
        _grid.ItemTemplate = new DataTemplate { VisualTree = cell };
        ScrollViewer.SetHorizontalScrollBarVisibility(_grid, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_grid, ScrollBarVisibility.Auto);
        _filter.TextChanged += (_, _) => RefilterSets();

        _grid.SelectionChanged += async (_, _) =>
        {
            if (_grid.SelectedItem is Thumb t) await SelectAsync(t.Index);
        };
        panel.Children.Add(_grid);

        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var stockCard = Card($"Stock patch", _stock);
        var resultCard = Card("Replacement (what will be written)", _result);
        bottom.Children.Add(stockCard);
        bottom.Children.Add(resultCard);
        var buttons = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(_export);
        buttons.Children.Add(_exportSet);
        buttons.Children.Add(_file);
        buttons.Children.Add(_apply);
        buttons.Children.Add(_status);
        bottom.Children.Add(buttons);
        panel.Children.Add(bottom);

        _export.Click += (_, _) => ExportSelected();
        _exportSet.Click += async (_, _) => await ExportSetAsync();
        _file.Click += (_, _) => FromFile();
        _apply.Click += async (_, _) => await ApplyAsync();

        Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Loaded += async (_, _) => await LoadSetsAsync();
    }

    void FillTargets()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ApplyTargetDir) && Directory.Exists(_settings.ApplyTargetDir))
            _target.Items.Add(new Target("Game folder  —  " + _settings.ApplyTargetDir, _settings.ApplyTargetDir));
        if (_target.Items.Count == 0) _target.Items.Add(new Target("(no Apply destination — configure the NHL 24 build selector)", ""));
        _target.SelectedIndex = 0;
    }

    string Game => (_target.SelectedItem as Target)?.Game ?? "";
    int Set => _set.SelectedItem is int i ? i : 0;

    BundleIndex Index(string game)
    {
        // Shared across pages and rebuilt only when the game's TOCs change, so a write is always seen.
        _indexGame = game;
        return _index = BundleIndex.Shared(game);
    }

    void ClearSelection()
    {
        _selected = null; _stockSlot = null; _built = null; _builtPreview = null;
        _stock.Source = null; _result.Source = null;
        _file.IsEnabled = false; _apply.IsEnabled = false; _export.IsEnabled = false; _exportSet.IsEnabled = false;
    }

    async Task LoadSetsAsync()
    {
        _set.Items.Clear(); _grid.Items.Clear();
        var game = Game;
        if (game.Length == 0) { Say("Choose a game folder first."); return; }
        try
        {
            Say("Reading the game's bundles...");
            _slots = await Task.Run(() => IceHotspots.Slots(Index(game)));
            RefilterSets();
            Say($"{_slots.Count} hotspot patch(es) in {(_set.Items.Count)} set(s). Filter by number, or pick a set.");
        }
        catch (Exception ex) { Say("Could not list hotspots: " + ex.Message); }
    }

    /// <summary>Rebuild the set dropdown through the filter; a filter that lands on one set opens it straight away.</summary>
    void RefilterSets()
    {
        var filter = _filter.Text.Trim();
        var sets = _slots.GroupBy(t => t.Set).OrderBy(g => g.Key)
            .Where(g => filter.Length == 0 || g.Key.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _set.SelectionChanged -= SetChangedHandler;
        _set.Items.Clear();
        foreach (var g in sets) _set.Items.Add(g.Key);
        _set.SelectionChanged += SetChangedHandler;
        if (sets.Count == 1) _set.SelectedIndex = 0;
        else { ClearSelection(); _grid.Items.Clear(); Say(filter.Length == 0
            ? $"{_slots.Count} hotspot patch(es) in {sets.Count} set(s)."
            : $"Filter '{filter}': {sets.Count} set(s)."); }
    }

    async void SetChangedHandler(object? s, SelectionChangedEventArgs e)
    {
        ClearSelection();
        await LoadThumbsAsync();
    }

    async Task LoadThumbsAsync()
    {
        _grid.Items.Clear();
        var game = Game; var set = Set;
        var wanted = _slots.Where(t => t.Set == set).ToList();
        if (game.Length == 0 || wanted.Count == 0) return;
        try
        {
            Say($"Decoding set {set} ({wanted.Count} patches)...");
            var items = await Task.Run(() =>
            {
                var index = Index(game);
                var list = new List<Thumb>();
                foreach (var t in wanted)
                {
                    try
                    {
                        var s = IceHotspots.Read(game, index, t.Set, t.Index);
                        list.Add(new Thumb { Index = t.Index, Image = Show(SquareOnDark(s.Image), 96) });
                    }
                    catch { /* unreadable slot: skip the thumbnail; selecting it reports the error */ }
                }
                return list;
            });
            foreach (var t in items) _grid.Items.Add(t);
            _exportSet.IsEnabled = true;
            Say($"Set {set}: {items.Count}/{wanted.Count} decoded. Pick a patch.");
        }
        catch (Exception ex) { Say("Could not decode the set: " + ex.Message); }
    }

    async Task SelectAsync(int index)
    {
        var game = Game; var set = Set;
        if (game.Length == 0) return;
        try
        {
            Say($"Reading hotspot {set}/{index}...");
            var slot = await Task.Run(() => IceHotspots.Read(game, Index(game), set, index));
            _selected = (set, index);
            _stockSlot = slot;
            _stock.Source = Show(SquareOnDark(slot.Image));
            _built = null; _builtPreview = null; _result.Source = null;
            _file.IsEnabled = true; _apply.IsEnabled = false; _export.IsEnabled = true;
            Say($"Hotspot {set}/{index}: {slot.Res.Width}x{slot.Res.Height}, {slot.Res.MipCount} mips, BC7. Pick a replacement picture.");
        }
        catch (Exception ex) { Say("Hotspot " + set + "/" + index + ": " + ex.Message); }
    }

    /// <summary>The patch's own art, native resolution, alpha preserved (not the checker-keyed preview), for editing in a photo tool.</summary>
    void ExportSelected()
    {
        if (_selected is null || _stockSlot is null) return;
        var (set, index) = _selected.Value;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"Export hotspot {set}/{index}",
            FileName = $"ice_hotspot_{set}_{index}_color.png",
            Filter = "PNG (*.png)|*.png",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ImageIO.SavePng(_stockSlot.Image, dlg.FileName);
            Say($"Exported {set}/{index} ({_stockSlot.Res.Width}x{_stockSlot.Res.Height}) to {dlg.FileName}.");
        }
        catch (Exception ex) { Say("Export failed: " + ex.Message); }
    }

    /// <summary>Every patch of the current set as ice_hotspot_&lt;set&gt;_&lt;n&gt;_color.png in a folder the user picks.</summary>
    async Task ExportSetAsync()
    {
        var game = Game; var set = Set;
        var wanted = _slots.Where(t => t.Set == set).ToList();
        if (game.Length == 0 || wanted.Count == 0) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Export hotspot set {set} to…" };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.FolderName)) return;
        var dir = dlg.FolderName;
        _exportSet.IsEnabled = false;
        Say($"Exporting set {set} ({wanted.Count} patches) to {dir}...");
        try
        {
            var done = await Task.Run(() =>
            {
                var index = Index(game);
                int n = 0;
                Parallel.ForEach(wanted, t =>
                {
                    try
                    {
                        var s = IceHotspots.Read(game, index, t.Set, t.Index);
                        ImageIO.SavePng(s.Image, Path.Combine(dir, $"ice_hotspot_{t.Set}_{t.Index}_color.png"));
                        Interlocked.Increment(ref n);
                    }
                    catch (Exception ex) { SuiteLog.Write($"hotspot export {t.Set}/{t.Index}: {ex.Message}"); }
                });
                return n;
            });
            Say($"Exported {done}/{wanted.Count} patches of set {set} to {dir}." +
                (done < wanted.Count ? " Some slots failed; see the log." : " Edit them, then use From file… to put one back."));
        }
        catch (Exception ex) { Say("Export failed: " + ex.Message); }
        finally { _exportSet.IsEnabled = true; }
    }

    void FromFile()
    {
        if (_selected is null || _stockSlot is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"Replacement for hotspot {_selected.Value.Set}/{_selected.Value.Index}", Filter = "Images (*.png;*.jpg;*.jpeg;*.dds;*.tga;*.bmp)|*.png;*.jpg;*.jpeg;*.dds;*.tga;*.bmp" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var img = ImageIO.Load(dlg.FileName);
            var built = IceHotspots.Build(_stockSlot.Res, img);
            // The exact bytes that will be written, decoded back for the preview.
            var tr = _stockSlot.Res;
            var check = TextureChunk.Parse(built, tr);
            var decoded = Bc7.DecodeTiled(check.Mips[0], tr.Width, tr.Height);
            _built = built; _builtPreview = decoded;
            _result.Source = Show(SquareOnDark(decoded));
            _apply.IsEnabled = true;
            Say($"{Path.GetFileName(dlg.FileName)} ({img.Width}x{img.Height}): encoded BC7 and decoded back for the preview. Check it, then apply.");
        }
        catch (Exception ex) { Say("Could not use that file: " + ex.Message); _apply.IsEnabled = false; }
    }

    async Task ApplyAsync()
    {
        if (_selected is null || _built is null) return;
        var (set, index) = _selected.Value;
        var answer = MessageBox.Show(
            $"Write hotspot {set}/{index} into\n{Game}\n\nA backup is taken first and the result is read back; the game must be closed.",
            "Apply hotspot", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            _apply.IsEnabled = false;
            Say("Applying...");
            var data = _built;
            var game = Game;
            var (ok, message, _) = await Task.Run(() => IceHotspots.Apply(game,
                new Dictionary<(int Set, int Index), byte[]> { [(set, index)] = data },
                p => Dispatcher.BeginInvoke(() => _status.Text = p)));
            Say(message);
        }
        catch (Exception ex) { Say("Apply failed: " + ex.Message); }
        finally { _apply.IsEnabled = _built is not null; }
    }

    void Say(string text) => _status.Text = text;

    /// <summary>Transparent pixels read badly on the page's dark background, so the preview checker-keys them.</summary>
    static Rgba SquareOnDark(Rgba img)
    {
        var px = (byte[])img.Pixels.Clone();
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            if (px[i + 3] >= 250) continue;
            bool dark = ((i / 4 / img.Width + i / 4 % img.Width) & 8) == 0;
            byte v = dark ? (byte)0x2a : (byte)0x3d;
            px[i] = v; px[i + 1] = v; px[i + 2] = v;
            px[i + 3] = Math.Max(px[i + 3], (byte)255);
        }
        return new Rgba(img.Width, img.Height, px);
    }

    static Border Card(string title, Image image)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, Foreground = new SolidColorBrush(Color.FromRgb(0x8f, 0xa3, 0xb8)), FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1d, 0x24, 0x2c)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x2c, 0x36, 0x42)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Child = image,
        });
        return new Border { Child = stack, Margin = new Thickness(0, 0, 16, 0) };
    }

    static ImageSource Show(Rgba img, int? max = null)
    {
        var file = Path.Combine(Path.GetTempPath(), "cims-hotspot-" + Guid.NewGuid().ToString("N") + ".png");
        ImageIO.SavePng(img, file);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(file);
        if (max is not null) { bmp.DecodePixelWidth = max.Value; }
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
