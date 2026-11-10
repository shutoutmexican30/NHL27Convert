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
    string _currentTag = "home";
    bool _buildEventsReady;

    public MainWindow()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"CIMS {v?.Major}.{v?.Minor}.{v?.Build}";
        RefreshBuilds();
        _buildEventsReady = true;
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

    void RefreshBuilds()
    {
        if (BuildSelector is null) return;
        BuildSelector.ItemsSource = null;
        BuildSelector.ItemsSource = _settings.GameBuilds;
        BuildSelector.SelectedValue = _settings.ActiveGameBuildId;
        var build = _settings.ActiveGameBuild;
        BuildTargetText.Text = build is null ? "No build configured" :
            string.IsNullOrWhiteSpace(build.ApplyTargetDir) ? "Apply target not set" : "Apply → " + build.ApplyTargetDir;
        BuildTargetText.ToolTip = build is null ? "Add an NHL24PC build in settings.json." : $"Source: {build.GameDir}\nApply: {build.ApplyTargetDir}";
    }

    void Build_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_buildEventsReady || BuildSelector.SelectedValue is not string id || id == _settings.ActiveGameBuildId) return;
        var previous = _settings.ActiveGameBuildId;
        if (!_settings.SelectGameBuild(id)) return;
        try { _settings.Save(); }
        catch (Exception ex)
        {
            _settings.SelectGameBuild(previous);
            RefreshBuilds();
            MessageBox.Show("The build selection could not be saved:\n" + ex.Message, "Center Ice Modding Suite", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _pages.Clear();
        Page.Content = null;
        Show(_currentTag);
    }

    void Show(string tag)
    {
        if (Page is null) return;
        _currentTag = tag;
        RefreshBuilds();
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "home" => new HomePage(_settings, Select),
                "convert27" => new Convert27View(_settings),
                "studio" => new Suite.Convert27.Studio.UniformStudioView(_settings),
                "centerice" => new CenterIcePage(_settings),
                "scorebug" => new ScorebugPage(_settings),
                "textures" => new TexturesPage(_settings),
                "teams" => new RostersPage(_settings),
                "editor" => new EditorPage(_settings),
                "settings" => new SettingsPage(_settings),
                _ => PlannedPage.For(tag),
            };
            _pages[tag] = page;
        }
        Page.Content = page;
        PageTitle.Text = tag switch
        {
            "home" => "Home",
            "convert27" => "Convert 27 — guided uniform conversion and live preview",
            "studio" => "Uniform Studio — original vs new textures",
            "centerice" => "Center ice",
            "scorebug" => "Scorebug — ESPN+ and Prime styles, live preview, packs",
            "textures" => "Textures & logos — browse and replace any texture",
            "teams" => "Teams & roster — sync every roster database with the NHL",
            "editor" => "Teams & lines — edit rosters, lines and strategies",
            "settings" => "Settings & about",
            _ => PlannedPage.TitleOf(tag),
        };
    }
}
