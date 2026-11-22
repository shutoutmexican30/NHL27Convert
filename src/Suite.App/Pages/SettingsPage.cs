using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Suite.Core;

namespace Suite.App.Pages;

/// <summary>Every setting as an editable field (the same values are in settings.json), plus About.</summary>
public sealed class SettingsPage : ScrollViewer
{
    readonly SuiteSettings _settings;
    readonly Dictionary<PropertyInfo, TextBox> _fields = [];
    readonly Action? _buildsChanged;
    readonly TextBox _gameDir = new() { Style = (Style)Application.Current.Resources["PathBox"] };
    readonly TextBox _applyDir = new() { Style = (Style)Application.Current.Resources["PathBox"] };
    readonly TextBlock _buildName = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xd6, 0xe0, 0xec)), Margin = new Thickness(0, 6, 0, 2) };
    readonly TextBlock _folderStatus = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xc3, 0xcf, 0xdd)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x8c, 0x97, 0xa8)), Margin = new Thickness(0, 8, 0, 0) };

    public SettingsPage(SuiteSettings settings, Action? buildsChanged = null)
    {
        _settings = settings;
        _buildsChanged = buildsChanged;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var root = new StackPanel { Margin = new Thickness(32, 26, 32, 26), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };
        Content = root;

        root.Children.Add(Heading("Settings"));
        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x28, 0x43)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x2b, 0x4b, 0x70)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 8, 0, 4),
            Child = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)), TextWrapping = TextWrapping.Wrap, LineHeight = 20,
                Text = "NHL 24 builds are managed from the selector at the top of the main window. Selecting a build switches both its read source and its Apply destination, preventing cross-build writes.",
            },
        });
        root.Children.Add(GameFolderSection());
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        string[] buildManaged = [nameof(SuiteSettings.GameBuilds), nameof(SuiteSettings.ActiveGameBuildId), nameof(SuiteSettings.Nhl24GameDir), nameof(SuiteSettings.ApplyTargetDir)];
        foreach (var p in typeof(SuiteSettings).GetProperties().Where(p => p.CanWrite && !buildManaged.Contains(p.Name)))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = p.Name, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 4) };
            var box = new TextBox { Text = p.GetValue(settings)?.ToString() ?? "", Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(4, 3, 4, 3), FontFamily = new FontFamily("Consolas") };
            Grid.SetRow(label, grid.RowDefinitions.Count - 1);
            Grid.SetRow(box, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(box, 1);
            grid.Children.Add(label);
            grid.Children.Add(box);
            _fields[p] = box;
        }
        root.Children.Add(grid);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Button("Save", Save));
        buttons.Children.Add(Button("Open settings.json", () => Process.Start(new ProcessStartInfo(SuitePaths.SettingsFile) { UseShellExecute = true })));
        buttons.Children.Add(Button("Open suite data folder", () => Process.Start("explorer.exe", SuitePaths.Local)));
        root.Children.Add(buttons);
        root.Children.Add(_status);

        root.Children.Add(Heading("About"));
        var about = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/CenterIceModdingSuite;component/Assets/cims-logo-128.png")), Width = 96, Height = 96, Margin = new Thickness(0, 0, 18, 0) };
        DockPanel.SetDock(logo, Dock.Left);
        about.Children.Add(logo);
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        about.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)), LineHeight = 20,
            Text = $"Center Ice Modding Suite (CIMS) {v?.Major}.{v?.Minor}.{v?.Build}\n" +
                   "Supports: NHL 27 (PS5, read) · NHL 24 (PS4, read/write)\n" +
                   "NHL 27 textures are read with the NHL27 texture reader (D:\\Tools\\NHL27TextureViewer). Viewer: three.js r160 (MIT) in WebView2.\n" +
                   "Logo and app are original work; no league, team or publisher marks are used.\n" +
                   "Hand-off notes for developers: docs\\HANDOFF.md in the repository.",
        });
        root.Children.Add(about);
    }

    /// <summary>The active build's game folder and Apply folder, each with a Browse… button that saves to settings.json.</summary>
    FrameworkElement GameFolderSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 4) };
        panel.Children.Add(new TextBlock { Text = "NHL 24 game folder", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        panel.Children.Add(_buildName);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(string label, TextBox box, string tip, Action browse)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 4), ToolTip = tip };
            var btn = new Button { Content = "Browse…", Style = (Style)Application.Current.Resources["BrowseButton"], ToolTip = tip };
            btn.Click += (_, _) => browse();
            var dock = new DockPanel();
            DockPanel.SetDock(btn, Dock.Right);
            dock.Children.Add(btn);
            dock.Children.Add(box);
            Grid.SetRow(l, grid.RowDefinitions.Count - 1); Grid.SetRow(dock, grid.RowDefinitions.Count - 1); Grid.SetColumn(dock, 1);
            grid.Children.Add(l); grid.Children.Add(dock);
        }
        Row("Game folder (read)", _gameDir, "The NHL24PC folder the suite reads from (the one holding game\\ and game-UPDATE\\).", BrowseGame);
        Row("Apply folder (write)", _applyDir, "The one game folder Apply/Restore may write to. Follows the game folder when they were the same.", BrowseApply);
        panel.Children.Add(grid);
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(Button("Save folders", SaveTypedFolders));
        buttons.Children.Add(Button("Add another build…", AddBuild));
        panel.Children.Add(buttons);
        panel.Children.Add(_folderStatus);
        ShowFolders();
        return panel;
    }

    void ShowFolders()
    {
        var build = _settings.ActiveGameBuild;
        _buildName.Text = build is null ? "No build yet: Browse to your NHL24PC folder to add one." : "Active build: " + build.Name;
        _gameDir.Text = build?.GameDir ?? _settings.Nhl24GameDir;
        _applyDir.Text = build?.ApplyTargetDir ?? _settings.ApplyTargetDir;
        if (_gameDir.Text.Length > 0 && !SuiteSettings.IsNhl24Dir(_gameDir.Text))
            _folderStatus.Text = "The game folder was not found or isn't an NHL 24 folder (moved?). Use Browse… to point at it.";
    }

    void FoldersSaved(string what)
    {
        ShowFolders();
        _folderStatus.Text = what + " Saved to " + SuitePaths.SettingsFile + ".";
        _buildsChanged?.Invoke();
    }

    void BrowseGame()
    {
        if (GameFolderPicker.PickNhl24Folder("Choose your NHL24PC game folder", _gameDir.Text) is not { } folder) return;
        try { GameFolderPicker.SetActiveGameFolder(_settings, folder); FoldersSaved("Game folder set to " + folder + "."); }
        catch (Exception ex) { _folderStatus.Text = "Not saved: " + ex.Message; }
    }

    void BrowseApply()
    {
        if (GameFolderPicker.PickNhl24Folder("Choose the game folder Apply may write to", _applyDir.Text.Length > 0 ? _applyDir.Text : _gameDir.Text) is not { } folder) return;
        _applyDir.Text = folder;
        SaveTypedFolders();
    }

    void SaveTypedFolders()
    {
        try
        {
            string game = _gameDir.Text.Trim(), apply = _applyDir.Text.Trim();
            if (game.Length == 0) { _folderStatus.Text = "Choose a game folder first."; return; }
            if (_settings.ActiveGameBuild is { } build)
            {
                build.GameDir = game;
                build.ApplyTargetDir = apply;
                _settings.SelectGameBuild(build.Id);
            }
            else _settings.UseGameBuild(game, apply);
            _settings.Save();
            FoldersSaved("Folders updated.");
        }
        catch (Exception ex) { _folderStatus.Text = "Not saved: " + ex.Message; }
    }

    void AddBuild()
    {
        if (GameFolderPicker.PickNhl24Folder("Add an NHL 24 build: choose its folder", _gameDir.Text) is not { } folder) return;
        try { _settings.UseGameBuild(folder, folder); _settings.Save(); FoldersSaved("Added and selected " + folder + "."); }
        catch (Exception ex) { _folderStatus.Text = "Not saved: " + ex.Message; }
    }

    void Save()
    {
        try
        {
            foreach (var (p, box) in _fields)
                p.SetValue(_settings, Convert.ChangeType(box.Text.Trim(), p.PropertyType));
            _settings.Save();
            _status.Text = "Saved to " + SuitePaths.SettingsFile + ". Restart the app for path changes to take effect everywhere.";
        }
        catch (Exception ex)
        {
            _status.Text = "Not saved: " + ex.Message;
        }
    }

    static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 14, 0, 0) };

    static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources["Action"] };
        b.Click += (_, _) => click();
        return b;
    }
}
