using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Suite.Core;

namespace Suite.App.Pages;

public partial class HomePage : UserControl
{
    readonly Action<string> _navigate;

    public HomePage(SuiteSettings settings, Action<string> navigate)
    {
        InitializeComponent();
        _navigate = navigate;
        Games.ItemsSource = GameDetector.Detect(settings).Select(g => new GameRow(g)).ToList();
    }

    void Convert_Click(object sender, RoutedEventArgs e) => _navigate("convert27");
    void Settings_Click(object sender, RoutedEventArgs e) => _navigate("settings");
    void Mods_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", SuitePaths.Mods);

    public sealed class GameRow(GameInfo g)
    {
        public string Title => g.Title;
        public string Platform => g.Platform;
        public string Path => g.Path;
        public string Status => g.Status;
        public string AccessText => g.Access switch { GameAccess.Read => "READ", GameAccess.ReadWrite => "READ / WRITE", _ => "NOT FOUND" };
        public Brush AccessBrush => new SolidColorBrush(g.Access switch
        {
            GameAccess.Read => Color.FromRgb(0x1d, 0x3d, 0x6b),
            GameAccess.ReadWrite => Color.FromRgb(0x2e, 0x7d, 0x4f),
            _ => Color.FromRgb(0x7a, 0x2a, 0x2a),
        });
    }
}
