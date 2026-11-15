using Suite.ModPackage;

namespace Cims.Installer;

sealed class InstallerForm : Form
{
    readonly TextBox _game = new() { Dock = DockStyle.Fill };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
    readonly Button _browse = new() { Text = "Browse…", AutoSize = true };
    readonly Button _check = new() { Text = "Check game", AutoSize = true };
    readonly Button _install = new() { Text = "Install", AutoSize = true };
    readonly Button _uninstall = new() { Text = "Uninstall", AutoSize = true };
    readonly Label _title = new() { AutoSize = true, Font = new Font("Segoe UI", 13f, FontStyle.Bold) };
    readonly Label _info = new() { AutoSize = true, MaximumSize = new Size(640, 0) };
    readonly string? _pkg = FindPackage();
    PackageManifest? _m;

    public static string? FindPackage()
        => Directory.EnumerateFiles(AppContext.BaseDirectory, "*.cimspkg").OrderBy(f => f).FirstOrDefault();

    public InstallerForm()
    {
        Text = "Center Ice Mod Installer";
        Width = 720; Height = 540; StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1 };
        var gameRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        gameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gameRow.Controls.Add(_game); gameRow.Controls.Add(_browse);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        buttons.Controls.AddRange(new Control[] { _check, _install, _uninstall });
        grid.Controls.Add(_title);
        grid.Controls.Add(_info);
        grid.Controls.Add(new Label { Text = "NHL24PC folder (the one with game\\ and game-UPDATE\\ in it):", AutoSize = true, Padding = new Padding(0, 10, 0, 0) });
        grid.Controls.Add(gameRow);
        grid.Controls.Add(buttons);
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(_log);
        Controls.Add(grid);

        _browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "Pick your NHL24PC folder", UseDescriptionForTitle = true };
            if (d.ShowDialog(this) == DialogResult.OK) { _game.Text = d.SelectedPath; Refresh_(); }
        };
        _game.TextChanged += (_, _) => Refresh_();
        _check.Click += async (_, _) => await Run("Checking", ct =>
        {
            var r = ModInstaller.Check(Game, _m!, Log, ct);
            if (r.AllStock) Say("Your game matches the stock version. Ready to install.");
            else if (r.AllInstalled) Say("This mod is installed.");
            else { Say("Your game does not match the stock version this mod needs (" + _m!.Requires + "):"); foreach (var p in r.Problems) Say($"  {p.File.Path}: {p.State}"); }
        });
        _install.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, $"Install \"{_m!.Name}\" {_m.Version} into\n{Game}?\n\nThe stock bytes it replaces are backed up so Uninstall can put them back.", Text, MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            await Run("Installing", ct => ModInstaller.Install(Game, _pkg!, Log, ct));
        };
        _uninstall.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, $"Remove the mod from\n{Game}\nand put the stock files back?", Text, MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            await Run("Uninstalling", ct => ModInstaller.Uninstall(Game, Log, ct));
        };

        if (_pkg is null)
        {
            _title.Text = "No mod package found";
            _info.Text = "Put the .cimspkg file in the same folder as this installer and start it again.";
        }
        else
        {
            try
            {
                _m = PackageIo.ReadManifest(_pkg);
                _title.Text = $"{_m.Name}  {_m.Version}";
                _info.Text = (_m.Notes.Length > 0 ? _m.Notes + "\n\n" : "") + "Needs: " + _m.Requires
                    + $"\nChanges {_m.Files.Count} game files. Package: {Path.GetFileName(_pkg)} ({PackageIo.Size(new FileInfo(_pkg).Length)}).";
            }
            catch (Exception ex) { _title.Text = "The mod package can't be read"; _info.Text = ex.Message; }
        }
        var guess = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\')) }
            .FirstOrDefault(d => d is not null && ModInstaller.LooksLikeGameRoot(d));
        _game.Text = guess ?? "";
        Refresh_();
    }

    string Game => _game.Text.Trim().Trim('"');
    IProgress<string> Log => new Progress<string>(Say);
    void Say(string s) { if (InvokeRequired) { BeginInvoke(() => Say(s)); return; } _log.AppendText(s + Environment.NewLine); }

    void Refresh_()
    {
        bool ok = _m is not null && ModInstaller.LooksLikeGameRoot(Game);
        _check.Enabled = _install.Enabled = ok;
        _uninstall.Enabled = Game.Length > 0 && ModInstaller.ReadState(SafeGame()) is not null;
        if (ok && ModInstaller.ReadState(Game) is { } st)
            Say($"\"{st.Name}\" {st.Version} is {(st.Status == "installed" ? "installed" : "partly installed (run Uninstall)")} in this folder.");
    }

    string SafeGame() { try { return Path.GetFullPath(Game); } catch { return "?"; } }

    async Task Run(string what, Action<CancellationToken> work)
    {
        foreach (var b in new[] { _check, _install, _uninstall, _browse }) b.Enabled = false;
        _game.ReadOnly = true;
        Say($"--- {what} ---");
        try { await Task.Run(() => work(CancellationToken.None)); Say("Done."); }
        catch (Exception ex) { Say("ERROR: " + ex.Message); MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _browse.Enabled = true; _game.ReadOnly = false; Refresh_(); }
    }
}
