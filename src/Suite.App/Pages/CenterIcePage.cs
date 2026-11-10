using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.App.Pages;

/// <summary>
/// Center ice: pick a slot, fetch that arena's rink from TheFaceoff.net on click, convert it to the slot's own layout
/// (blue circle kept, ice white keyed out, red-line gap cut) and preview it over nothing but the stock slot, then write it
/// with a backup and a read-back. The write goes to the test copy unless the user picks the game folder.
/// Every conversion is validated against the Centerlogo_Template.psd geometry (<see cref="CenterIceTemplate"/>), and
/// "Verify all vs template" renders every team's NHL 27 art over the template guides as one contact sheet.
///
/// Nothing is fetched until the button is pressed, and the credit line is always shown (site terms: personal use).
/// </summary>
public sealed class CenterIcePage : Grid
{
    sealed class Target(string label, string game)
    {
        public string Label { get; } = label;
        public string Game { get; } = game;
        public override string ToString() => Label;
    }

    readonly SuiteSettings _settings;
    readonly ComboBox _slot = new() { Width = 120, Margin = new Thickness(0, 0, 12, 0) };
    readonly ComboBox _target = new() { Width = 460 };
    readonly TextBox _link = new() { MinWidth = 520, Margin = new Thickness(0, 0, 8, 0) };
    readonly Image _stock = new() { Stretch = Stretch.Uniform };
    readonly Image _result = new() { Stretch = Stretch.Uniform };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)) };
    readonly Button _fetch = new() { Content = "Fetch from TheFaceoff.net", Padding = new Thickness(14, 5, 14, 5) };
    readonly Button _file = new() { Content = "From file…", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
    readonly Button _nhl27 = new() { Content = "From NHL 27", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
    readonly Button _apply = new() { Content = "Apply to this game", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false, Margin = new Thickness(0, 0, 12, 0) };
    readonly Button _verifyAll = new() { Content = "Verify all vs template", Padding = new Thickness(14, 5, 14, 5) };

    BundleIndex? _index;
    string? _indexGame;
    CenterIceSlots.Slot? _stockSlot;
    Rgba? _rink, _converted;

    public CenterIcePage(SuiteSettings settings)
    {
        _settings = settings;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1c));

        var panel = new StackPanel { Margin = new Thickness(32, 26, 32, 26), MaxWidth = 1600, HorizontalAlignment = HorizontalAlignment.Left };

        panel.Children.Add(new TextBlock { Text = "Center ice", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        panel.Children.Add(new TextBlock
        {
            Text = Faceoff.Credit + "  Pick a slot, then fetch its TheFaceoff.net link, pick a file, or take NHL 27's art; look at the preview, then apply.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8f, 0xa3, 0xb8)), FontSize = 12,
        });

        for (int i = 0; i < 34; i++) _slot.Items.Add(i);
        _slot.SelectedItem = 22;                      // Utah, the one slot the user asked for first
        _slot.SelectionChanged += async (_, _) => await RefreshStockAsync();
        _target.SelectionChanged += async (_, _) => await RefreshStockAsync();

        panel.Children.Add(Row("Slot", _slot, "Write to", _target));

        var linkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        linkRow.Children.Add(new TextBlock { Text = "Link", Width = 52, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x8f, 0xa3, 0xb8)) });
        linkRow.Children.Add(_link);
        linkRow.Children.Add(_fetch);
        linkRow.Children.Add(_file);
        linkRow.Children.Add(_nhl27);
        panel.Children.Add(linkRow);
        // Right under the buttons that report into it: below the two 320-pixel previews it sat out of sight, and in a
        // horizontal row it never wrapped, so a click looked like nothing happened.
        panel.Children.Add(_status);

        // The two previews share the page width and stay square, so they grow with the window (they were 320 px).
        var previews = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        previews.ColumnDefinitions.Add(new ColumnDefinition());
        previews.ColumnDefinitions.Add(new ColumnDefinition());
        var stockCard = Card("Stock slot (what is in the game now)", _stock);
        var resultCard = Card("Result (relaid out for this slot)", _result);
        Grid.SetColumn(resultCard, 1);
        previews.Children.Add(stockCard);
        previews.Children.Add(resultCard);
        panel.Children.Add(previews);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(_apply);
        actions.Children.Add(_verifyAll);
        panel.Children.Add(actions);

        _fetch.Click += async (_, _) => await FetchAsync();
        _file.Click += (_, _) => FromFile();
        _nhl27.Click += async (_, _) => await FromNhl27Async();
        _apply.Click += async (_, _) => await ApplyAsync();
        _verifyAll.Click += async (_, _) => await VerifyAllAsync();
        panel.Children.Add(new TextBlock
        {
            Text = "Slot = the arena id: Utah is 22 (NHL 27's art is 30), All-Star logos are 30 and 31, Vegas 32, Seattle 33. " +
                   "The stock preview tells you which arena a slot belongs to. " +
                   "Every result is checked against the Centerlogo_Template.psd circle and red-line gap; " +
                   "“Verify all vs template” renders all 32 teams over those guides as one sheet.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x6f, 0x80, 0x92)), FontSize = 12,
        });

        Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        FillTargets();
        Loaded += async (_, _) => await RefreshStockAsync();
    }

    static StackPanel Row(string leftLabel, Control left, string rightLabel, Control right)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(new TextBlock { Text = leftLabel, Width = 52, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x8f, 0xa3, 0xb8)) });
        row.Children.Add(left);
        row.Children.Add(new TextBlock { Text = rightLabel, Width = 76, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x8f, 0xa3, 0xb8)) });
        row.Children.Add(right);
        return row;
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

    void FillTargets()
    {
        // The game folder from Settings, the one every other write goes to.  (A hardcoded test copy used to come first and
        // stayed selected after that folder was deleted, so every read and fetch failed.)
        if (!string.IsNullOrWhiteSpace(_settings.ApplyTargetDir) && Directory.Exists(_settings.ApplyTargetDir))
            _target.Items.Add(new Target("Game folder  —  " + _settings.ApplyTargetDir, _settings.ApplyTargetDir));
        if (_target.Items.Count == 0) _target.Items.Add(new Target("(no Apply destination — configure the NHL 24 build selector)", ""));
        _target.SelectedIndex = 0;
    }

    string Game => (_target.SelectedItem as Target)?.Game ?? "";
    int Slot => _slot.SelectedItem is int i ? i : 22;

    async Task RefreshStockAsync()
    {
        _converted = null; _apply.IsEnabled = false; _result.Source = null;
        // The dropdowns belong to the UI thread: read them here, and give the background task plain values ("The calling
        // thread cannot access this object" otherwise, and the stock slot never loaded).
        var game = Game; var slot = Slot;
        if (game.Length == 0) { Say("Choose a game folder first."); return; }
        try
        {
            _status.Text = "Reading the stock slot...";
            _stockSlot = await Task.Run(() => CenterIceSlots.Read(game, Index(game), slot));
            _stock.Source = Show(_stockSlot.Image);
            Say($"Slot {slot}: {_stockSlot.Res.Width}x{_stockSlot.Res.Height}, {_stockSlot.Res.MipCount} mips, BC7.");
        }
        catch (Exception ex) { _stockSlot = null; _stock.Source = null; Say("Slot " + slot + ": " + ex.Message); }
    }

    BundleIndex Index(string game)
    {
        if (_index is not null && _indexGame == game) return _index;
        _indexGame = game;
        return _index = BundleIndex.Build(game);
    }

    /// <summary>The template check every conversion goes through before it may be applied.</summary>
    CenterIceTemplate.Report Check(Rgba converted)
    {
        var report = CenterIceTemplate.Validate(converted);
        SuiteLog.Write("center ice slot " + Slot + " template check: " + report.Summary);
        return report;
    }

    async Task FetchAsync()
    {
        if (string.IsNullOrWhiteSpace(_link.Text)) { Say("Paste the arena's TheFaceoff.net link into the Link box first."); return; }
        if (_stockSlot is null) await RefreshStockAsync();   // the slot preview may never have loaded (game folder moved, app opened early)
        if (_stockSlot is null) { Say(_status.Text + "  Fetch needs the slot's stock texture to lay the picture out; fix that first."); return; }
        try
        {
            _fetch.IsEnabled = false;
            Say("Fetching (one request at a time, credited)...");
            var file = await Faceoff.FetchAsync(_link.Text.Trim());
            _rink = ImageIO.Load(file);
            _converted = CenterIceImage.ToSlot(_rink, _stockSlot.Image);
            _stock.Source = Show(_stockSlot.Image);
            _result.Source = Show(_converted);
            _apply.IsEnabled = true;
            Say($"Fetched {Path.GetFileName(file)} ({_rink.Width}x{_rink.Height}); relaid out to {_converted.Width}x{_converted.Height}. " +
                "Template check: " + Check(_converted).Summary + " Check the preview, then apply.");
        }
        catch (Exception ex) { Say("Fetch failed: " + ex.Message); _apply.IsEnabled = false; }
        finally { _fetch.IsEnabled = true; }
    }

    /// <summary>
    /// A picture on disk: a ready decal (it has transparency) is used as it is, resized to the slot; a flat rink picture
    /// goes through the same conversion as a TheFaceoff.net fetch. The red-line strip is cut either way when written.
    /// </summary>
    void FromFile()
    {
        if (_stockSlot is null) { Say("Pick a slot with a readable stock texture first."); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"Center ice for slot {Slot}", Filter = "Images (*.png;*.jpg;*.jpeg;*.dds;*.tga;*.bmp)|*.png;*.jpg;*.jpeg;*.dds;*.tga;*.bmp" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _rink = ImageIO.Load(dlg.FileName);
            bool decal = Enumerable.Range(0, _rink.Width * _rink.Height).Any(i => _rink.Pixels[i * 4 + 3] < 250);
            _converted = CenterIceImage.CutRedLine(decal ? ImageIO.ResizeSmooth(_rink, _stockSlot.Image.Width, _stockSlot.Image.Height) : CenterIceImage.ToSlot(_rink, _stockSlot.Image));
            _result.Source = Show(_converted);
            _apply.IsEnabled = true;
            Say($"{Path.GetFileName(dlg.FileName)}: {(decal ? "transparent decal, used as it is" : "rink picture, ice keyed out and relaid out")}. " +
                "Template check: " + Check(_converted).Summary + " Check the preview, then apply.");
        }
        catch (Exception ex) { Say("Could not use that file: " + ex.Message); _apply.IsEnabled = false; }
    }

    /// <summary>NHL 27's center ice for this team (same slot, except Utah: NHL 27 slot 30), decoded with the NHL 27 reader.</summary>
    async Task FromNhl27Async()
    {
        if (_stockSlot is null) { Say("Pick a slot with a readable stock texture first."); return; }
        try
        {
            _nhl27.IsEnabled = false;
            var name = CenterIceSlots.Name(CenterIceSlots.Nhl27Slot(Slot));
            var index = await IndexStore.GetAsync(IndexStore.Nhl27, _settings);
            var entry = index.Entries.FirstOrDefault(e => e.Path == name);
            if (entry is null) { Say($"NHL 27 has no {name.Split('/')[^1]}."); return; }
            Say("Decoding NHL 27's art...");
            var pngs = await new Suite.Convert27.TextureImages(_settings).Nhl27Async([new Suite.Convert27.Nhl27Texture(entry)]);
            if (!pngs.TryGetValue(name, out var png)) { Say("The NHL 27 reader could not decode it."); return; }
            _rink = ImageIO.Load(png);
            _converted = CenterIceImage.CutRedLine(ImageIO.ResizeSmooth(_rink, _stockSlot.Image.Width, _stockSlot.Image.Height));
            _result.Source = Show(_converted);
            _apply.IsEnabled = true;
            Say($"NHL 27 slot {CenterIceSlots.Nhl27Slot(Slot)} (same layout as NHL 24; red-line strip cut). " +
                "Template check: " + Check(_converted).Summary + " Check the preview, then apply.");
        }
        catch (Exception ex) { Say("NHL 27 art failed: " + ex.Message); _apply.IsEnabled = false; }
        finally { _nhl27.IsEnabled = true; }
    }

    /// <summary>
    /// Batch check: every team's NHL 27 center ice, converted exactly as "From NHL 27" does, validated against the
    /// template and rendered over its guides. Writes slot-N.png per team, report.txt and a sheet.html contact sheet
    /// to %LOCALAPPDATA%\CenterIceModdingSuite\centerice-verify. Read-only: nothing is written to the game.
    /// </summary>
    async Task VerifyAllAsync()
    {
        var game = Game;
        if (game.Length == 0) { Say("Choose a game folder first."); return; }
        try
        {
            _verifyAll.IsEnabled = false;
            Say("Reading the game's bundles...");
            var index = await Task.Run(() => Index(game));
            // 32 NHL teams: slots 0-29 plus Vegas 32 and Seattle 33; 30/31 are All-Star logos.
            var slots = CenterIceTemplate.TeamSlots();
            var nhl27 = await IndexStore.GetAsync(IndexStore.Nhl27, _settings);
            var wanted = new List<Suite.Convert27.Nhl27Texture>();
            foreach (var slot in slots)
            {
                var name = CenterIceSlots.Name(CenterIceSlots.Nhl27Slot(slot));
                var entry = nhl27.Entries.FirstOrDefault(e => e.Path == name);
                if (entry is not null) wanted.Add(new Suite.Convert27.Nhl27Texture(entry));
            }
            Say("Decoding NHL 27 center-ice art...");
            var pngs = await new Suite.Convert27.TextureImages(_settings).Nhl27Async(wanted,
                p => Dispatcher.BeginInvoke(() => _status.Text = p));
            string dir = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "centerice-verify"));
            var reportLines = new List<string>();
            int passed = 0;
            foreach (var slot in slots)
            {
                try
                {
                    Say($"Checking slot {slot} against the template...");
                    var name = CenterIceSlots.Name(CenterIceSlots.Nhl27Slot(slot));
                    if (!pngs.TryGetValue(name, out var png)) { reportLines.Add($"slot {slot}: no NHL 27 art decoded"); continue; }
                    var stock = await Task.Run(() => CenterIceSlots.Read(game, index, slot));
                    var decal = CenterIceImage.CutRedLine(ImageIO.ResizeSmooth(ImageIO.Load(png), stock.Image.Width, stock.Image.Height));
                    var report = CenterIceTemplate.Validate(decal);
                    ImageIO.SavePng(CenterIceTemplate.VerificationCell(decal), Path.Combine(dir, $"slot-{slot}.png"));
                    if (report.Ok) passed++;
                    string line = $"slot {slot} (NHL 27 slot {CenterIceSlots.Nhl27Slot(slot)}): {report.Summary}";
                    reportLines.Add(line);
                    SuiteLog.Write("center ice verify: " + line);
                }
                catch (Exception ex)
                {
                    reportLines.Add($"slot {slot}: ERROR {ex.Message}");
                    SuiteLog.Write($"center ice verify slot {slot} failed: {ex.Message}");
                }
            }
            File.WriteAllLines(Path.Combine(dir, "report.txt"), reportLines);
            CenterIceTemplate.WriteSheet(dir, slots, reportLines);
            Say($"{passed}/{slots.Length} slots match the template circle. The rest are only flagged for a look — " +
                $"teams whose art has no blue circle are skipped, not failed. Nothing was written to the game. Sheet: {Path.Combine(dir, "sheet.html")}");
        }
        catch (Exception ex) { Say("Verify all failed: " + ex.Message); }
        finally { _verifyAll.IsEnabled = true; }
    }

    async Task ApplyAsync()
    {
        if (_converted is null) return;
        var answer = MessageBox.Show(
            $"Write slot {Slot} into\n{Game}\n\nA backup is taken first and the result is read back; the game must be closed.",
            "Apply center ice", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            _apply.IsEnabled = false;
            Say("Applying...");
            var slot = Slot;
            var image = _converted;
            var game = Game;
            var (ok, message, _) = await Task.Run(() => CenterIceSlots.Apply(game, new Dictionary<int, Rgba> { [slot] = image },
                p => Dispatcher.BeginInvoke(() => _status.Text = p)));
            Say(message);
            if (ok) _stock.Source = Show(_stockSlot is null ? image : _stockSlot.Image);
        }
        catch (Exception ex) { Say("Apply failed: " + ex.Message); }
        finally { _apply.IsEnabled = _converted is not null; }
    }

    void Say(string text) => _status.Text = text;

    /// <summary>A preview image from an Rgba, through a temporary PNG the image control can load.</summary>
    static ImageSource Show(Rgba img)
    {
        var file = Path.Combine(Path.GetTempPath(), "cims-centerice-" + Guid.NewGuid().ToString("N") + ".png");
        ImageIO.SavePng(img, file);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(file);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
