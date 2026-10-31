using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Suite.App.Pages;
using Suite.Convert27;
using Suite.Core;

namespace Suite.App;

public partial class MainWindow : Window
{
    readonly SuiteSettings _settings = SuiteSettings.Load();
    readonly Dictionary<string, FrameworkElement> _pages = [];

    public MainWindow()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"CIMS {v?.Major}.{v?.Minor}.{v?.Build}";
        Show("home");
        if (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() is { } start && start.StartsWith("--page="))
            Select(start["--page=".Length..]);
    }

    public void Select(string tag)
    {
        foreach (var rb in Nav.Children.OfType<RadioButton>())
            if ((string)rb.Tag == tag) rb.IsChecked = true;
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag }) Show(tag);
    }

    void Show(string tag)
    {
        if (Page is null) return;
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "home" => new HomePage(_settings, Select),
                "convert27" => new Convert27View(_settings),
                "studio" => new Suite.Convert27.Studio.UniformStudioView(_settings),
                "textures" => new TexturesPage(_settings),
                "settings" => new SettingsPage(_settings),
                _ => PlannedPage.For(tag),
            };
            _pages[tag] = page;
        }
        Page.Content = page;
        PageTitle.Text = tag switch
        {
            "home" => "Home",
            "convert27" => "Convert 27 — texture matcher and live uniform viewer",
            "studio" => "Uniform Studio — original vs new textures",
            "textures" => "Textures & logos — browse and replace any texture",
            "settings" => "Settings & about",
            _ => PlannedPage.TitleOf(tag),
        };
    }
}
