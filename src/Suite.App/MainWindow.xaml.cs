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
        // Warm the shared game index in the background (from its disk cache, ~2 s) so the first page that reads the game
        // does not wait for it. Failures are ignored here; the page that needs it reports them.
        var warm = _settings.ApplyTargetDir;
        if (!string.IsNullOrWhiteSpace(warm) && System.IO.Directory.Exists(warm))
            _ = Task.Run(() =>
            {
                try { Suite.Core.Frostbite.BundleIndex.Shared(warm); } catch { }
                // The game's own colour swatches (create-a-player), offered by every colour picker.
                try
                {
                    var csv = System.Text.Encoding.UTF8.GetString(new Suite.Core.Frostbite.LegacyFiles(warm, _settings).Read("gamemodes/creation_zone_csv/colorswatches.csv"));
                    var colours = csv.Split('\n').Skip(1).Select(l => l.Split(',')).Where(c => c.Length > 3 && int.TryParse(c[3].Trim(), out _)).Select(c => int.Parse(c[3].Trim()));
                    Suite.Convert27.Controls.ColorPalette.SetGame(colours);
                }
                catch { }
            });
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
                "patches" => new PatchStudioPage(_settings),
                "teams" => new RostersPage(_settings),
                "dbstudio" => new StudioPage(_settings),
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
            "patches" => "Patches — erase, move or add logos painted on a uniform",
            "teams" => "Teams & roster — sync every roster database with the NHL",
            "dbstudio" => "Database studio — players, clubs, contracts and every table in one place",
            "settings" => "Settings & about",
            "installer" => "Build mod installer — share the mod without the game",
            _ => PlannedPage.TitleOf(tag),
        };
    }
}
