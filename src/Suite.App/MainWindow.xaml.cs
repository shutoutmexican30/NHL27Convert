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
        BuildTargetText.ToolTip = build is null ? "Use Browse… to choose your NHL24PC folder." : $"Source: {build.GameDir}\nApply: {build.ApplyTargetDir}";
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

    /// <summary>Header "Browse…": re-point the active build at its NHL24PC folder (e.g. after the game moved) and reload the pages.</summary>
    void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var current = _settings.ActiveGameBuild?.GameDir ?? _settings.Nhl24GameDir;
        if (GameFolderPicker.PickNhl24Folder("Choose your NHL24PC game folder", current) is not { } folder) return;
        try { GameFolderPicker.SetActiveGameFolder(_settings, folder); }
        catch (Exception ex)
        {
            MessageBox.Show("The game folder could not be saved:\n" + ex.Message, "Center Ice Modding Suite", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _pages.Clear();
        Page.Content = null;
        Show(_currentTag);
    }

    /// <summary>The Settings page changed a build's folders: drop every other cached page so they reload from the new folder.</summary>
    void BuildsChanged()
    {
        _pages.TryGetValue("settings", out var settingsPage);
        _pages.Clear();
        if (settingsPage is not null) _pages["settings"] = settingsPage;
        RefreshBuilds();
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
                "hotspots" => new HotspotsPage(_settings),
                "scorebug" => new ScorebugPage(_settings),
                "textures" => new TexturesPage(_settings),
                "teams" => new RostersPage(_settings),
                "editor" => new EditorPage(_settings),
                "contracts" => new ContractsPage(_settings),
                "settings" => new SettingsPage(_settings, BuildsChanged),
                "installer" => new ModInstallerPage(_settings),
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
            "hotspots" => "Ice hotspots — on-ice sponsor patches",
            "scorebug" => "Scorebug — ESPN+ and Prime styles, live preview, packs",
            "textures" => "Texture suite — every texture of the game, organized like FMT",
            "teams" => "Teams & roster — sync every roster database with the NHL",
            "editor" => "Teams & database — edit rosters, strategies and every table of the database",
            "contracts" => "Contracts — cap hits, terms, extensions and clauses",
            "settings" => "Settings & about",
            "installer" => "Build mod installer — share the mod without the game",
            _ => PlannedPage.TitleOf(tag),
        };
    }
}
