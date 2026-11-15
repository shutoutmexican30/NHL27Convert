using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Suite.Core;
using Suite.ModPackage;

namespace Suite.App.Pages;

/// <summary>"Build mod installer": diff the modded NHL24PC against a fresh stock install and write a folder with
/// CIMS-Mod-Installer.exe + a .cimspkg holding only the changed files and byte ranges. Both games are only read.</summary>
public sealed class ModInstallerPage : ScrollViewer
{
    public const string InstallerExe = "CIMS-Mod-Installer.exe";
    readonly SuiteSettings _settings;
    readonly TextBox _stock, _modded, _out, _name, _version, _notes;
    readonly TextBox _log = new() { IsReadOnly = true, Height = 260, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0) };
    readonly Button _build;

    public ModInstallerPage(SuiteSettings settings)
    {
        _settings = settings;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var root = new StackPanel { Margin = new Thickness(32, 26, 32, 26), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };
        Content = root;
        root.Children.Add(new TextBlock { Text = "Build mod installer", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        root.Children.Add(new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)), TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 8, 0, 8),
            Text = "Compares your modded NHL24PC with a fresh stock install and packs only what changed (new files, and the changed byte ranges of the big cas/toc files). "
                 + "Players run CIMS-Mod-Installer.exe next to the .cimspkg: it checks their files are the same stock version, backs up the bytes it replaces, installs, and can uninstall. "
                 + "eboot.bin is left out because NHL24PC's setup patches it per machine. Both games are only read.",
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TextBox Row(string label, string value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 4) };
            var b = new TextBox { Text = value, Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(4, 3, 4, 3), FontFamily = new FontFamily("Consolas") };
            Grid.SetRow(l, grid.RowDefinitions.Count - 1); Grid.SetRow(b, grid.RowDefinitions.Count - 1); Grid.SetColumn(b, 1);
            grid.Children.Add(l); grid.Children.Add(b);
            return b;
        }
        _modded = Row("Modded NHL24PC", settings.Nhl24GameDir);
        _stock = Row("Stock NHL24PC (fresh)", settings.StockGameDir);
        _out = Row("Output folder", settings.ModInstallerOutDir.Length > 0 ? settings.ModInstallerOutDir : Path.Combine(SuitePaths.Local, "mod-installers"));
        _name = Row("Mod name", "Center Ice mod");
        _version = Row("Version", DateTime.Now.ToString("yyyy.MM.dd"));
        _notes = Row("Notes for players", "");
        root.Children.Add(grid);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        _build = Button("Build mod installer", Build);
        buttons.Children.Add(_build);
        buttons.Children.Add(Button("Open output folder", () => { Directory.CreateDirectory(_out.Text); Process.Start("explorer.exe", _out.Text); }));
        root.Children.Add(buttons);
        root.Children.Add(_log);
        if (FindInstallerExe() is null) Say($"Note: {InstallerExe} was not found next to the suite. Run tools\\publish-installer.ps1 and rebuild the suite.");
    }

    void Say(string s) => Dispatcher.BeginInvoke(() => { _log.AppendText(s + "\n"); _log.ScrollToEnd(); });

    static string? FindInstallerExe()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "installer", InstallerExe);
        return File.Exists(p) ? p : null;
    }

    async void Build()
    {
        string stock = _stock.Text.Trim(), modded = _modded.Text.Trim(), outDir = _out.Text.Trim();
        string name = _name.Text.Trim(), version = _version.Text.Trim();
        var exe = FindInstallerExe();
        string? err =
            exe is null ? $"{InstallerExe} is missing from the suite's installer folder (run tools\\publish-installer.ps1, then rebuild)." :
            !ModInstaller.LooksLikeGameRoot(modded) ? "The modded folder is not an NHL24PC folder (no game\\eboot.bin)." :
            !ModInstaller.LooksLikeGameRoot(stock) ? "Pick a fresh stock NHL24PC install (V3.2 with the 1.00 PKG and 1.60 update extracted)." :
            SamePath(stock, modded) ? "Stock and modded must be different folders." :
            name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "Name and version can't be empty or hold \\ / : * ? \" < > |." : null;
        if (err is not null) { MessageBox.Show(err, "Build mod installer"); return; }

        _settings.StockGameDir = stock;
        _settings.ModInstallerOutDir = outDir;
        try { _settings.Save(); } catch { }
        var folder = Path.Combine(outDir, $"{name} {version}");
        var pkg = Path.Combine(folder, $"{name} {version}.cimspkg");
        _build.IsEnabled = false;
        Say($"Building {folder} ...");
        try
        {
            var notes = _notes.Text.Trim();
            var m = await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                foreach (var old in Directory.GetFiles(folder, "*.cimspkg")) File.Delete(old);
                var man = PackageBuilder.Build(new BuildOptions { StockRoot = stock, ModdedRoot = modded, OutputPath = pkg, Name = name, Version = version, Notes = notes }, new SyncLog(Say));
                File.Copy(exe!, Path.Combine(folder, InstallerExe), overwrite: true);
                File.WriteAllText(Path.Combine(folder, "README.txt"),
                    $"{name} {version}\r\n\r\nNeeds: {man.Requires}.\r\n\r\n" +
                    "1. Close the game.\r\n2. Keep CIMS-Mod-Installer.exe and the .cimspkg file in the same folder and run CIMS-Mod-Installer.exe.\r\n" +
                    "3. Pick your NHL24PC folder (the one with game and game-UPDATE in it), press Check game, then Install.\r\n\r\n" +
                    "The installer refuses to run if your game files are not the stock version it was built for. It backs up every byte it replaces to " +
                    "user\\cims_mod_backup inside your NHL24PC folder; Uninstall puts the stock files back and removes that backup.\r\n");
                return man;
            });
            Say($"Done: {m.Files.Count} files. Hand out the whole folder: {folder}");
        }
        catch (Exception ex) { Say("ERROR: " + ex.Message); }
        finally { _build.IsEnabled = true; }
    }

    static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources["Action"] };
        b.Click += (_, _) => click();
        return b;
    }

    sealed class SyncLog(Action<string> say) : IProgress<string> { public void Report(string v) => say(v); }
}
