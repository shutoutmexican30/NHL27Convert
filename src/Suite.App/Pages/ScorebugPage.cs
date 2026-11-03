using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Suite.Core;
using Suite.Scorebug;

namespace Suite.App.Pages;

/// <summary>
/// Scorebug: pick a custom scorebug (ESPN+ or NHL on Prime Video), change any of its settings with a live preview,
/// import or export .nhl24bug packs, and write it into the game folder (both scoreclock movies, backup and read-back).
///
/// The preview is the style's preview movie in Ruffle inside WebView2 (data\scorebug\preview): the settings text goes in
/// as the cfg FlashVar, so the preview shows exactly what the settings block will make the game movie draw.
/// Settings are kept per style in %APPDATA%\CenterIceModdingSuite\scorebug.
/// </summary>
public sealed class ScorebugPage : Grid
{
    static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x8f, 0xa3, 0xb8));
    static readonly Brush Body = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8));
    static readonly Brush CardBg = new SolidColorBrush(Color.FromRgb(0x1d, 0x24, 0x2c));
    static readonly Brush CardLine = new SolidColorBrush(Color.FromRgb(0x2c, 0x36, 0x42));
    static readonly (string Tag, string Label)[] States =
        [("pp", "Power play"), ("pk", "Penalty kill"), ("shots", "Shots"), ("info", "Player info"), ("goal", "Goal"), ("popup", "Message")];

    readonly SuiteSettings _settings;
    readonly ComboBox _style = new() { Width = 200, Margin = new Thickness(0, 0, 12, 0) };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = Body };
    readonly TextBlock _inGame = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = Dim, FontSize = 12 };
    readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x12, 0x16, 0x1c), Height = 160 };
    readonly TextBlock _webError = new() { Foreground = Body, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly Border _webBox;
    readonly CheckBox _animate = Check("Animate"), _full = Check("Whole screen"), _dark = Check("Dark background");
    readonly List<CheckBox> _states = [];
    readonly TextBox _away = Box("NYI", 50), _awayColour = Box("00539B", 70), _home = Box("DET", 50), _homeColour = Box("CE1126", 70);
    readonly TextBox _awayScore = Box("2", 34), _homeScore = Box("3", 34), _period = Box("2ND", 44), _clock = Box("12:34", 56);
    readonly TextBox _teamColours = new() { MinWidth = 520, Margin = new Thickness(0, 0, 8, 0) };
    readonly WrapPanel _groups = new();
    readonly Button _apply;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };

    ScorebugSettings _sb;
    bool _webReady, _loading;

    public ScorebugPage(SuiteSettings settings)
    {
        _settings = settings;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1c));
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new StackPanel { Margin = new Thickness(32, 22, 32, 8) };
        head.Children.Add(new TextBlock { Text = "Scorebug", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        head.Children.Add(new TextBlock
        {
            Text = "A custom scorebug replaces the game's score clock. Pick a style, change anything below (the preview follows), then write it to the game folder. " +
                   "Packs (.nhl24bug) carry a style, its settings and team colours.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12), Foreground = Dim, FontSize = 12,
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Label("Style", 44));
        foreach (var s in ScorebugStyle.All) _style.Items.Add(s);
        actions.Children.Add(_style);
        actions.Children.Add(Button("Import pack…", ImportPack));
        actions.Children.Add(Button("Export pack…", () => _ = ExportPackAsync()));
        actions.Children.Add(Button("Reset to defaults", ResetAll));
        _apply = Button("Write to game", () => _ = ApplyAsync());
        _apply.FontWeight = FontWeights.SemiBold;
        _apply.Margin = new Thickness(20, 0, 0, 0);
        actions.Children.Add(_apply);
        head.Children.Add(actions);
        head.Children.Add(_status);
        head.Children.Add(_inGame);
        Children.Add(head);

        // Preview: the WebView2 is a window of its own, so it sits outside the scroll viewer (it would not clip inside one).
        var preview = new StackPanel { Margin = new Thickness(32, 4, 32, 8) };
        var webGrid = new Grid();
        webGrid.Children.Add(_web);
        webGrid.Children.Add(_webError);
        _webBox = new Border { BorderBrush = CardLine, BorderThickness = new Thickness(1), Child = webGrid };
        _webBox.SizeChanged += (_, _) => SizePreview();
        preview.Children.Add(_webBox);
        var stateRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        stateRow.Children.Add(Label("Show", 44));
        foreach (var (tag, label) in States)
        {
            var c = Check(label);
            c.Tag = tag;
            c.Click += (_, _) => Navigate();
            _states.Add(c);
            stateRow.Children.Add(c);
        }
        foreach (var c in new[] { _animate, _full, _dark })
        {
            c.Click += (_, _) => { SizePreview(); Navigate(); };
            stateRow.Children.Add(c);
        }
        preview.Children.Add(stateRow);
        var sample = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        sample.Children.Add(Label("Away", 44)); sample.Children.Add(_away); sample.Children.Add(_awayColour); sample.Children.Add(_awayScore);
        sample.Children.Add(Label("Home", 50)); sample.Children.Add(_home); sample.Children.Add(_homeColour); sample.Children.Add(_homeScore);
        sample.Children.Add(Label("Period", 56)); sample.Children.Add(_period); sample.Children.Add(_clock);
        foreach (var b in new[] { _away, _awayColour, _home, _homeColour, _awayScore, _homeScore, _period, _clock })
            b.TextChanged += (_, _) => Restart();
        preview.Children.Add(sample);
        SetRow(preview, 1);
        Children.Add(preview);

        var body = new StackPanel { Margin = new Thickness(32, 4, 32, 26) };
        body.Children.Add(_groups);
        var teamRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        teamRow.Children.Add(Label("Team colours", 96));
        teamRow.Children.Add(_teamColours);
        _teamColours.TextChanged += (_, _) => { if (!_loading) { ReadTeamColours(); Restart(); } };
        body.Children.Add(teamRow);
        body.Children.Add(new TextBlock
        {
            Text = "Your own colours for any team: ABBR:primary:secondary, several separated by |  (for example UTA:6CACE4:010101|SEA:001628:99D9D9).",
            Foreground = Dim, FontSize = 12, Margin = new Thickness(96, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(scroll, 2);
        Children.Add(scroll);

        _debounce.Tick += (_, _) => { _debounce.Stop(); Persist(); Navigate(); };
        _sb = new ScorebugSettings(ScorebugSettings.LoadActiveStyle());
        UseStyle(_sb.Style);
        _style.SelectionChanged += (_, _) =>
        {
            if (_style.SelectedItem is ScorebugStyle st && st != _sb.Style)
            {
                UseStyle(st);
                Say(st.Name + ": its settings are loaded. Write to game to use it.");
                Navigate();
            }
        };
        Loaded += async (_, _) => await StartPreviewAsync();
        Loaded += async (_, _) => await ReadGameAsync();
    }

    static TextBlock Label(string text, double width) => new() { Text = text, Width = width, VerticalAlignment = VerticalAlignment.Center, Foreground = Dim };
    static CheckBox Check(string text) => new() { Content = text, Foreground = Body, Margin = new Thickness(0, 3, 14, 3), VerticalAlignment = VerticalAlignment.Center };
    static TextBox Box(string text, double width) => new() { Text = text, Width = width, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

    static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    void Say(string text) => _status.Text = text;
    void Restart() { _debounce.Stop(); _debounce.Start(); }

    void UseStyle(ScorebugStyle style)
    {
        _sb = new ScorebugSettings(style);
        _sb.Load();
        try { ScorebugSettings.SaveActiveStyle(style); } catch (Exception ex) { SuiteLog.Write("scorebug style not saved: " + ex.Message); }
        _style.SelectedItem = style;
        BuildEditors();
    }

    void Persist()
    {
        try { _sb.Save(); } catch (Exception ex) { Say("Could not save the settings: " + ex.Message); }
    }

    void ResetAll()
    {
        _sb.ResetAll();
        Persist();
        BuildEditors();
        Navigate();
        Say($"{_sb.Style.Name}: every setting back to the movie's default.");
    }

    // ---- Settings editors: one card per group, one row per setting ----

    void BuildEditors()
    {
        _loading = true;
        _groups.Children.Clear();
        foreach (var group in _sb.All.GroupBy(s => s.Group))
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = group.Key, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            foreach (var s in group) stack.Children.Add(Editor(s));
            _groups.Children.Add(new Border
            {
                Background = CardBg, BorderBrush = CardLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 12, 12), Width = 400, Child = stack,
            });
        }
        _teamColours.Text = string.Join("|", _sb.TeamColours.Select(t => $"{t.Abbr}:{t.Primary}:{t.Secondary}"));
        _loading = false;
    }

    FrameworkElement Editor(ScorebugSetting s)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2), ToolTip = s.Tip };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = s.Label, Foreground = Body, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        row.Children.Add(label);
        var reset = new Button { Content = "↺", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Default: " + s.Default };
        Grid.SetColumn(reset, 2);
        row.Children.Add(reset);
        void Mark() => label.Foreground = _sb.IsDefault(s) ? Body : Brushes.White;
        void Changed(string? v) { if (_loading) return; if (_sb.Set(s, v)) Restart(); Mark(); }
        Mark();

        FrameworkElement input;
        Action refresh = () => { };
        switch (s.Kind)
        {
            case SettingKind.Flag:
            {
                var c = new CheckBox { IsChecked = _sb.Get(s) == "1", VerticalAlignment = VerticalAlignment.Center };
                c.Click += (_, _) => Changed(c.IsChecked == true ? "1" : "0");
                input = c; refresh = () => c.IsChecked = _sb.Get(s) == "1";
                break;
            }
            case SettingKind.Number:
            {
                var panel = new DockPanel();
                var box = new TextBox { Text = _sb.Get(s), Width = 56, VerticalAlignment = VerticalAlignment.Center };
                var slider = new Slider { Minimum = s.Min, Maximum = s.Max, SmallChange = s.Step, LargeChange = s.Step * 10, IsSnapToTickEnabled = true, TickFrequency = s.Step,
                                          Value = Num(_sb.Get(s)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                DockPanel.SetDock(box, Dock.Right);
                panel.Children.Add(box);
                panel.Children.Add(slider);
                bool sync = false;
                slider.ValueChanged += (_, _) => { if (sync) return; sync = true; box.Text = Math.Round(slider.Value, 3).ToString("0.###", CultureInfo.InvariantCulture); sync = false; Changed(box.Text); };
                box.LostFocus += (_, _) => { Changed(box.Text); refresh!(); };
                box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Changed(box.Text); refresh!(); } };
                input = panel;
                refresh = () => { sync = true; box.Text = _sb.Get(s); slider.Value = Num(_sb.Get(s)); sync = false; };
                break;
            }
            case SettingKind.Colour:
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                var swatch = new Border { Width = 22, Height = 18, BorderBrush = CardLine, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0) };
                var box = new TextBox { Text = _sb.Get(s), Width = 70, VerticalAlignment = VerticalAlignment.Center, CharacterCasing = CharacterCasing.Upper };
                void Paint() => swatch.Background = ScorebugSettings.TryColour(_sb.Get(s), out var c) ? new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c)) : Brushes.Transparent;
                Paint();
                box.TextChanged += (_, _) => { if (box.Text.Trim().TrimStart('#').Length == 6) { Changed(box.Text); Paint(); } };
                box.LostFocus += (_, _) => { box.Text = _sb.Get(s); Paint(); };
                panel.Children.Add(swatch);
                panel.Children.Add(box);
                input = panel; refresh = () => { box.Text = _sb.Get(s); Paint(); };
                break;
            }
            default:
            {
                var box = new TextBox { Text = _sb.Get(s), VerticalAlignment = VerticalAlignment.Center, CharacterCasing = CharacterCasing.Upper };
                box.TextChanged += (_, _) => Changed(box.Text);
                box.LostFocus += (_, _) => box.Text = _sb.Get(s);
                input = box; refresh = () => box.Text = _sb.Get(s);
                break;
            }
        }
        Grid.SetColumn(input, 1);
        row.Children.Add(input);
        reset.Click += (_, _) => { _loading = true; _sb.Set(s, s.Default); refresh(); _loading = false; Mark(); Restart(); };
        return row;
    }

    static double Num(string v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    void ReadTeamColours()
    {
        _sb.TeamColours.Clear();
        foreach (var part in _teamColours.Text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.Split(':') is [var a, var p, var s] && ScorebugSettings.TryColour(p, out var pc) && ScorebugSettings.TryColour(s, out var sc) && ScorebugSettings.CleanAbbr(a).Length > 0)
                _sb.TeamColours.Add((ScorebugSettings.CleanAbbr(a), pc.ToString("X6"), sc.ToString("X6")));
    }

    // ---- Preview ----

    async Task StartPreviewAsync()
    {
        if (_webReady) return;
        try
        {
            if (!File.Exists(Path.Combine(ScorebugStyle.PreviewDir, "preview.html"))) throw new FileNotFoundException("data\\scorebug\\preview is missing next to the app.");
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(SuitePaths.Local, "webview2"));
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping("scorebug.cims", ScorebugStyle.PreviewDir, CoreWebView2HostResourceAccessKind.Allow);
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webReady = true;
            Navigate();
        }
        catch (Exception ex)
        {
            _webError.Text = "The preview needs the Microsoft Edge WebView2 runtime: " + ex.Message;
            _webError.Visibility = Visibility.Visible;
        }
    }

    void SizePreview()
    {
        double h = Math.Round(_webBox.ActualWidth * (_full.IsChecked == true ? 0.5625 : 0.135));
        if (h > 20 && Math.Abs(_web.Height - h) > 1) _web.Height = h;
    }

    static string Colour(string text, string fallback) => ScorebugSettings.TryColour(text, out var c) ? "0x" + c.ToString("X6") : fallback;

    static string Darker(string colour)
    {
        ScorebugSettings.TryColour(colour, out var c);
        uint D(int shift) => (uint)(((c >> shift) & 0xFF) * 0.55);
        return "0x" + ((D(16) << 16) | (D(8) << 8) | D(0)).ToString("X6");
    }

    void Navigate()
    {
        if (!_webReady) return;
        bool full = _full.IsChecked == true;
        var q = new Dictionary<string, string>
        {
            ["movie"] = _sb.Style.Preview,
            ["view"] = full ? "full" : "band",
            ["band"] = "0.135",
            ["state"] = string.Join(",", _states.Where(c => c.IsChecked == true).Select(c => (string)c.Tag)),
            ["instant"] = _animate.IsChecked == true ? "0" : "1",
            ["cfg"] = _sb.ToSettingsString(),
            ["awayName"] = ScorebugSettings.CleanAbbr(_away.Text),
            ["homeName"] = ScorebugSettings.CleanAbbr(_home.Text),
            ["awayColor"] = Colour(_awayColour.Text, "0x00539B"),
            ["homeColor"] = Colour(_homeColour.Text, "0xCE1126"),
            ["awaySecondary"] = Darker(_awayColour.Text),
            ["homeSecondary"] = Darker(_homeColour.Text),
            ["awayScore"] = _awayScore.Text.Trim(),
            ["homeScore"] = _homeScore.Text.Trim(),
            ["period"] = _period.Text.Trim(),
            ["clock"] = _clock.Text.Trim(),
        };
        if (!full) q["zoom"] = "1.6";
        if (_dark.IsChecked == true) q["bg"] = "dark";
        _web.CoreWebView2.Navigate("https://scorebug.cims/preview.html?" + string.Join("&", q.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value))));
    }

    async Task<byte[]?> CapturePreviewAsync()
    {
        if (!_webReady) return null;
        try
        {
            var ms = new MemoryStream();
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            return ms.ToArray();
        }
        catch (Exception ex) { SuiteLog.Write("scorebug preview capture failed: " + ex.Message); return null; }
    }

    // ---- Packs ----

    void ImportPack()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import a scorebug pack", Filter = "Scorebug packs (*.nhl24bug)|*.nhl24bug|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var pack = ScorebugPack.Read(dlg.FileName);
            UseStyle(pack.Style);
            _sb.FromStored(pack.Manifest.Scorebug);
            Persist();
            BuildEditors();
            Navigate();
            var m = pack.Manifest;
            Say($"Imported \"{m.Name}\"{(string.IsNullOrWhiteSpace(m.Author) ? "" : " by " + m.Author)} ({m.Created}): {pack.Style.Name}, " +
                $"{m.Scorebug?.Values.Count ?? 0} changed setting(s), {m.Scorebug?.TeamColours.Count ?? 0} team colour(s)" +
                (m.Mods.Count > 0 ? $"; its {m.Mods.Count} extra NHL24Kit mod(s) are not applied by the suite" : "") + ". Write to game to use it.");
        }
        catch (Exception ex) { Say("Could not import that pack: " + ex.Message); }
    }

    async Task ExportPackAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export a scorebug pack", Filter = "Scorebug packs (*.nhl24bug)|*.nhl24bug", FileName = _sb.Style.Name.Replace('+', 'P') + " scorebug" + ScorebugPack.Extension,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var preview = await CapturePreviewAsync();
            ScorebugPack.Write(dlg.FileName, Path.GetFileNameWithoutExtension(dlg.FileName), Environment.UserName, _sb, preview);
            Say($"Exported {Path.GetFileName(dlg.FileName)}: {_sb.Style.Name}, {_sb.ToStored().Values.Count} changed setting(s){(preview is null ? "" : ", preview.png from the preview")}.");
        }
        catch (Exception ex) { Say("Export failed: " + ex.Message); }
    }

    // ---- Game ----

    async Task ReadGameAsync()
    {
        var game = _settings.ApplyTargetDir;
        if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game)) { _inGame.Text = "No game folder: set ApplyTargetDir in Settings."; return; }
        _inGame.Text = "Reading what the game folder has...";
        try
        {
            var settings = _settings;
            var (style, text, bytes) = await Task.Run(() => ScorebugApply.Read(game, settings));
            _inGame.Text = $"In {game} now: " + (style is null
                ? $"the game's own scorebug (scoreclock.gfx {bytes:N0} bytes)."
                : $"{style.Name}, {(string.IsNullOrEmpty(text) ? "default settings" : text.Split(';').Length + " changed setting(s)")}.");
        }
        catch (Exception ex) { _inGame.Text = "Could not read the game's scorebug: " + ex.Message; }
    }

    async Task ApplyAsync()
    {
        var game = _settings.ApplyTargetDir;
        var answer = MessageBox.Show(
            $"Write the {_sb.Style.Name} scorebug into\n{game}\n\nBoth scoreclock movies are replaced. A backup is taken first and the result is read back; the game must be closed.",
            "Write scorebug", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            _apply.IsEnabled = false;
            _debounce.Stop();
            Persist();
            var sb = _sb;
            var settings = _settings;
            var (ok, message, _) = await Task.Run(() => ScorebugApply.Apply(settings, sb, p => Dispatcher.BeginInvoke(() => Say(p))));
            Say(message);
            if (ok) await ReadGameAsync();
        }
        catch (Exception ex) { Say("Write failed: " + ex.Message); }
        finally { _apply.IsEnabled = true; }
    }
}
