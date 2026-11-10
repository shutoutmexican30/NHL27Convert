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
    readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x8c, 0x97, 0xa8)), Margin = new Thickness(0, 8, 0, 0) };

    public SettingsPage(SuiteSettings settings)
    {
        _settings = settings;
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
