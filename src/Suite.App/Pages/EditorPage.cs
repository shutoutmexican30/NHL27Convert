using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

namespace Suite.App.Pages;

/// <summary>
/// Teams &amp; database editor: open a saved roster (or a roster database file), pick a team and edit its roster (numbers,
/// letters, player style) and strategies, or edit any table of the database directly in the Database tab. Every edit is
/// in place, so the database keeps NHL 24's layout exactly; before anything is written the built-in verify must pass,
/// the game must be closed, and a backup of the original is kept.
/// </summary>
public sealed class EditorPage : ScrollViewer
{
    readonly SuiteSettings _settings;
    readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x8c, 0x97, 0xa8)), TextWrapping = TextWrapping.Wrap };
    readonly ComboBox _sources = new() { MinWidth = 420, Padding = new Thickness(4, 3, 4, 3) };
    readonly ListBox _teams = new() { MinWidth = 300, MaxHeight = 820, Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x22, 0x30)) };
    readonly TabControl _tabs = new() { MinWidth = 940 };
    readonly Button _open = new() { Content = "Open" }, _save = new() { Content = "Save", IsEnabled = false }, _verify = new() { Content = "Verify", IsEnabled = false };
    readonly StackPanel _rosterPanel = new(), _stratPanel = new();
    readonly DbEditorPanel _dbPanel;
    readonly TabItem _dbTab;
    bool _dbTouched;
    readonly Dictionary<string, TeamEditor.Team> _teamByItem = [];

    ISource? _source;
    TeamEditor? _editor;
    byte[] _trailer = [];
    HashSet<string> _baseline = [];

    List<string> NewProblems() => _editor is null ? [] : _editor.Verify().Where(p => !_baseline.Contains(p)).ToList();
    bool _building;

    interface ISource
    {
        string Label { get; }
        byte[] ReadDb(out byte[] trailer);
        string WriteDb(byte[] dbWithTrailer);
    }

    sealed class SaveSource(SavedRoster save) : ISource
    {
        public string Label => $"Saved roster: {Path.GetFileName(Path.GetDirectoryName(save.Dir)!)}/{Path.GetFileName(save.Dir)}";
        public byte[] ReadDb(out byte[] trailer)
        {
            var blob = save.ReadDb();
            var n = SavedRoster.DbLength(blob);
            trailer = blob[n..];
            return blob[..n];
        }
        public string WriteDb(byte[] dbWithTrailer)
        {
            var backup = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "save-backups"));
            save.WriteDb(dbWithTrailer, backup);
            return $"Wrote {save.Dir}\nBackup: {Path.Combine(backup, save.Name)}";
        }
    }

    sealed class FileSource(string path) : ISource
    {
        public string Path => path;
        public string Label => $"File: {path}";
        public byte[] ReadDb(out byte[] trailer)
        {
            var blob = File.ReadAllBytes(path);
            var n = SavedRoster.DbLength(blob);
            trailer = blob.Length - n is > 0 and <= 64 ? blob[n..] : [];
            return blob[..n];
        }
        public string WriteDb(byte[] dbWithTrailer)
        {
            var backup = path + ".before-edit";
            if (!File.Exists(backup)) File.Copy(path, backup);
            var tmp = path + ".cims-tmp";
            File.WriteAllBytes(tmp, dbWithTrailer);
            File.Move(tmp, path, true);
            return $"Wrote {path}\nBackup: {backup}";
        }
    }

    /// <summary>One of the game's own databases (nhl_game holds stockteamjerseys / stockteamequipmenttable, the tables the
    /// game builds its uniform and equipment lists from), read from and written back into the apply target's game files.</summary>
    sealed class GameDbSource(SuiteSettings settings, string game, string database) : ISource
    {
        byte[]? _original;
        public string Label => $"Game database: {database} ({game})";
        public byte[] ReadDb(out byte[] trailer)
        {
            trailer = [];
            _original = Suite.Convert27.GameDatabaseEdit.Read(game, settings, database);
            return (byte[])_original.Clone();
        }
        public string WriteDb(byte[] dbWithTrailer)
        {
            if (_original is null) throw new InvalidOperationException("Open the database first.");
            var log = new List<string>();
            var backup = Suite.Convert27.GameDatabaseEdit.Write(game, settings, database, _original, dbWithTrailer, log.Add);
            _original = (byte[])dbWithTrailer.Clone();
            return log.LastOrDefault() ?? $"Wrote {database}. Backup: {backup}";
        }
    }

    public EditorPage(SuiteSettings settings)
    {
        _settings = settings;
        // Bigger base text for the whole page: rosters, strategy boxes and the database grid all inherit it.
        FontSize = 14;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var root = new StackPanel { Margin = new Thickness(32, 26, 32, 26), MaxWidth = 1400, HorizontalAlignment = HorizontalAlignment.Left };
        Content = root;

        root.Children.Add(Heading("Teams & database editor"));
        root.Children.Add(Text("Open a roster saved in game (or a roster database file from the work folder), pick a team, and edit " +
            "its roster and strategies — or edit any table of the database in the Database tab. Edits keep the game's own layout — " +
            "the file never grows or moves, only values change. " +
            "Save runs the same checks as an update, refuses while NHL 24 is running, and keeps a backup. Load the roster in the game " +
            "with Customize > Save/Load/Delete > Load > Rosters."));
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(_sources);
        row.Children.Add(_open);
        var browse = new Button { Content = "Open .db file…", Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.Resources["Action"] };
        browse.Click += (_, _) => Browse();
        row.Children.Add(browse);
        var makeSave = new Button { Content = "Write into game as a new roster…", Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.Resources["Action"] };
        makeSave.Click += (_, _) => WriteIntoGameAsNewSave();
        row.Children.Add(makeSave);
        var extract = new Button { Content = "Extract .db…", Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.Resources["Action"],
            ToolTip = "Save the open database (with any edits) to a .db file, with its meta XML beside it" };
        extract.Click += (_, _) => Extract();
        row.Children.Add(extract);
        var import = new Button { Content = "Import .db…", Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.Resources["Action"],
            ToolTip = "Replace the open database with a .db file (same size and layout); Save then writes it to the open source" };
        import.Click += (_, _) => Import();
        row.Children.Add(import);
        root.Children.Add(row);
        root.Children.Add(_status);

        _open.Click += (_, _) => OpenSelected();
        _teams.SelectionChanged += (_, _) => { if (!_building && _teams.SelectedItem is ListBoxItem { Tag: TeamEditor.Team t }) Show(t); };
        _save.Style = _verify.Style = (Style)Application.Current.Resources["Action"];
        _save.Margin = new Thickness(8, 0, 0, 0);
        _verify.Click += (_, _) => ShowVerify();
        _save.Click += (_, _) => Save();

        var body = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_teams);
        var tabsParent = new Grid { Margin = new Thickness(14, 0, 0, 0) };
        tabsParent.Children.Add(_tabs);
        Grid.SetColumn(tabsParent, 1);
        body.Children.Add(tabsParent);
        root.Children.Add(body);

        _tabs.Items.Add(Tab("Roster", _rosterPanel));
        _tabs.Items.Add(Tab("Strategy", _stratPanel));
        _dbPanel = new DbEditorPanel(m => _status.Text = m, () => { _editor?.MarkDirty(); _dbTouched = true; });
        _dbTab = new TabItem { Header = "Database", Content = _dbPanel };
        _tabs.Items.Add(_dbTab);
        _tabs.SelectionChanged += (_, e) =>
        {
            // Inner lists and grids bubble SelectionChanged too; only a tab switch away from Database refreshes the team views.
            if (!ReferenceEquals(e.OriginalSource, _tabs) || !_dbTouched || ReferenceEquals(_tabs.SelectedItem, _dbTab)) return;
            _dbTouched = false;
            if (_teams.SelectedItem is ListBoxItem { Tag: TeamEditor.Team t }) Show(t);
        };
        var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        bar.Children.Add(_verify);
        bar.Children.Add(_save);
        root.Children.Add(bar);

        RefreshSources();
    }

    static TabItem Tab(string title, UIElement content) => new() { Header = title, Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };

    static UIElement Heading(string text) => new Border
    {
        BorderBrush = (SolidColorBrush)Application.Current.Resources["Accent"],
        BorderThickness = new Thickness(3, 0, 0, 0),
        Padding = new Thickness(12, 0, 0, 0),
        Margin = new Thickness(0, 0, 0, 2),
        Child = new TextBlock { Text = text, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
    };

    static TextBlock Text(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)), LineHeight = 22, Margin = new Thickness(0, 6, 0, 0),
    };

    // ---------------------------------------------------------------- sources

    void RefreshSources()
    {
        _sources.Items.Clear();
        if (!string.IsNullOrEmpty(_settings.ApplyTargetDir))
            foreach (var save in SavedRoster.SavedataRoots(_settings.ApplyTargetDir).SelectMany(SavedRoster.Find))
                _sources.Items.Add(new ListBoxItem { Content = new SaveSource(save).Label, Tag = new SaveSource(save) });
        if (!string.IsNullOrEmpty(_settings.ApplyTargetDir) && Directory.Exists(_settings.ApplyTargetDir))
            foreach (var name in Suite.Convert27.GameDatabaseEdit.Databases)
            {
                var g = new GameDbSource(_settings, _settings.ApplyTargetDir, name);
                _sources.Items.Add(new ListBoxItem { Content = g.Label, Tag = g });
            }
        var stock = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes", "database", "t3db", "nhl_game.db");
        if (File.Exists(stock)) _sources.Items.Add(new ListBoxItem { Content = new FileSource(stock).Label, Tag = new FileSource(stock) });
        if (_sources.Items.Count > 0) _sources.SelectedIndex = 0;
        _status.Text = _sources.Items.Count == 0
            ? "No saved roster in the CIMS game copy yet (save one in game first) and no stock export in the work folder."
            : "";
    }

    void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Roster databases (*.db)|*.db", Title = "Open a roster database" };
        if (dlg.ShowDialog() == true)
        {
            var s = new FileSource(dlg.FileName);
            _sources.Items.Add(new ListBoxItem { Content = s.Label, Tag = s });
            _sources.SelectedIndex = _sources.Items.Count - 1;
            Open(s);
        }
    }

    void OpenSelected() { if (_sources.SelectedItem is ListBoxItem { Tag: ISource s }) Open(s); }

    static string SourceFileName(ISource s) => s switch
    {
        GameDbSource => s.Label.Split(':')[1].Trim().Split(' ')[0] + ".db",
        FileSource f => Path.GetFileName(f.Path),
        _ => "roster.db",
    };

    /// <summary>Writes the open database (edits included, CRCs recomputed) to a .db file, plus nhl_game-meta.xml beside it,
    /// so it can be edited elsewhere and imported back.</summary>
    void Extract()
    {
        if (_editor is null || _source is null) { _status.Text = "Open a database first."; return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Roster databases (*.db)|*.db", FileName = SourceFileName(_source), Title = "Extract database" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bytes = _editor.Save();
            File.WriteAllBytes(dlg.FileName, bytes);
            var meta = Path.Combine(Path.GetDirectoryName(dlg.FileName)!, "nhl_game-meta.xml");
            var src = FindMeta(_source);
            if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(meta), StringComparison.OrdinalIgnoreCase)) File.Copy(src, meta, true);
            T3Db.Load(File.ReadAllBytes(dlg.FileName), meta);   // read back: every CRC must check
            _status.Text = $"Extracted {bytes.Length:n0} bytes to {dlg.FileName} (meta XML beside it). Read back OK.";
        }
        catch (Exception ex) { _status.Text = "Not extracted: " + ex.Message; }
    }

    /// <summary>Loads a .db file in place of the open database. It must be a valid T3DB of the same size, so it keeps the
    /// game's layout; nothing is written until Save, which goes to the open source (game, saved roster or file) with its backup.</summary>
    void Import()
    {
        if (_editor is null || _source is null) { _status.Text = "Open the database to import into first (for example Game database: nhl_game)."; return; }
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Roster databases (*.db)|*.db", Title = "Import database into " + _source.Label };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var blob = File.ReadAllBytes(dlg.FileName);
            var n = SavedRoster.DbLength(blob);
            if (n <= 0 || n > blob.Length) throw new InvalidDataException("Not a T3DB database.");
            var bytes = blob[..n];
            var current = _editor.Db.Bytes.Length;
            if (bytes.Length != current)
                throw new InvalidDataException($"The file is {bytes.Length:n0} bytes, the open database is {current:n0}. Only a database with the same layout can be imported (extract this one, edit it, import it back).");
            var meta = FindMeta(_source);
            var editor = TeamEditor.Load(bytes, meta);   // throws on any CRC or meta mismatch
            var oldTables = _editor.Db.Tables.Values.Select(t => (t.Short, t.Start, t.RecordBytes, t.Slots)).OrderBy(t => t.Start).ToList();
            var newTables = editor.Db.Tables.Values.Select(t => (t.Short, t.Start, t.RecordBytes, t.Slots)).OrderBy(t => t.Start).ToList();
            if (!oldTables.SequenceEqual(newTables)) throw new InvalidDataException("The file's tables are laid out differently from the open database.");
            if (MessageBox.Show($"Replace the open database with\n{dlg.FileName}?\n\nNothing is written until you press Save, which writes to\n{_source.Label}\nwith a backup.",
                    "Import database", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _editor = editor;
            _baseline = editor.Verify().ToHashSet(StringComparer.Ordinal);
            _editor.MarkDirty();
            _dbTouched = false;
            _dbPanel.Load(_editor);
            ShowTeams();
            _status.Text = $"Imported {dlg.FileName}. Press Save to write it to {_source.Label}.";
        }
        catch (Exception ex) { _status.Text = "Not imported: " + ex.Message; }
    }

    /// <summary>Creates a RosterCustomN save in the active build from the currently open database — the same
    /// as saving a roster in game — so it shows under Load &gt; Rosters and can be edited here from then on.</summary>
    void WriteIntoGameAsNewSave()
    {
        if (_editor is null || _source is null) { _status.Text = "Open a roster (a saved roster or a .db file) first."; return; }
        try
        {
            if (ApplyTarget.RunningGame() is { } running) { _status.Text = $"{running} is running. Close the game first."; return; }
            var roots = string.IsNullOrEmpty(_settings.ApplyTargetDir) ? [] : SavedRoster.SavedataRoots(_settings.ApplyTargetDir).ToList();
            if (roots.Count == 0) { _status.Text = "No savedata user folder found next to the apply target game."; return; }
            var db = _editor.Save();
            foreach (var root in roots)
            {
                int n = 0;
                while (Directory.Exists(Path.Combine(root, $"RosterCustom{n}"))) n++;
                if (n > 20) continue;
                var donor = Directory.GetDirectories(root).FirstOrDefault(d => Directory.Exists(Path.Combine(d, "sce_sys")));
                var save = SavedRoster.Create(root, $"RosterCustom{n}", db, donor);
                _status.Text = $"Created {save.Dir}\nIt shows in the game under Customize > Save/Load/Delete > Load > Rosters.";
                RefreshSources();
                return;
            }
            _status.Text = "No savedata root had a free RosterCustom slot.";
        }
        catch (Exception ex) { _status.Text = "Not created: " + ex.Message; }
    }

    void Open(ISource source)
    {
        try
        {
            if (_editor is { IsDirty: true } &&
                MessageBox.Show("Discard the unsaved edits?", "Teams & database", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var bytes = source.ReadDb(out _trailer);
            var meta = FindMeta(source);
            _editor = TeamEditor.Load(bytes, meta);
            // The game's own databases ship with things the roster checks flag (duplicate numbers on non-NHL teams);
            // only problems an edit introduces block a save.
            _baseline = _editor.Verify().ToHashSet(StringComparer.Ordinal);
            _source = source;
            _status.Text = "";
            _save.IsEnabled = _verify.IsEnabled = true;
            _dbTouched = false;
            _dbPanel.Load(_editor);
            ShowTeams();
        }
        catch (Exception ex) { _status.Text = "Not opened: " + ex.Message; }
    }

    string FindMeta(ISource source)
    {
        // A file source usually sits next to its meta (a stock export keeps both in the same folder).
        if (source is FileSource f)
        {
            var beside = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Path)!, "nhl_game-meta.xml");
            if (File.Exists(beside)) return beside;
        }
        var stock = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes", "database", "t3db", "nhl_game-meta.xml");
        if (File.Exists(stock)) return stock;
        // Last resort: pull the meta straight out of the game files and cache it under the suite's local folder.
        if (!string.IsNullOrEmpty(_settings.ApplyTargetDir))
        {
            try
            {
                var bytes = new LegacyFiles(_settings.ApplyTargetDir, _settings).Read("gamemodes/database/t3db/nhl_game-meta.xml");
                var cache = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "meta-cache")), "nhl_game-meta.xml");
                File.WriteAllBytes(cache, bytes);
                return cache;
            }
            catch { /* reported below */ }
        }
        throw new FileNotFoundException("nhl_game-meta.xml was not found next to the database, in the work folder, or in the game. Run a roster sync once (Teams & roster page) and try again.");
    }

    // ---------------------------------------------------------------- teams & tabs

    void ShowTeams()
    {
        if (_editor is null) return;
        _building = true;
        _teams.Items.Clear();
        _teamByItem.Clear();
        foreach (var t in _editor.Teams())
        {
            var item = new ListBoxItem { Content = $"{t.Name}  ({t.Index})", Tag = t };
            _teams.Items.Add(item);
            _teamByItem[item.Content.ToString()!] = t;
        }
        _building = false;
        if (_teams.Items.Count > 0) _teams.SelectedIndex = 0;
        else _status.Text = _editor.HasTeams ? "No teams in this database." : "This database has no team table (a mode roster database, not the main one).";
    }

    void Show(TeamEditor.Team team)
    {
        if (_editor is null) return;
        // Edits accumulate across teams until Save (the same model as the editor in the Puck Peak updater).
        BuildRoster(team);
        BuildStrategy(team);
    }

    TeamEditor.Team Current => (TeamEditor.Team)((ListBoxItem)_teams.SelectedItem).Tag;

    // ---------------------------------------------------------------- roster tab

    sealed class RowVm(TeamEditor editor, TeamEditor.Player p)
    {
        public TeamEditor.Player P = p;
        public string Name => P.Name;
        public string Position => P.Pos switch { 0 => "C", 1 => "LW", 2 => "RW", 3 => "D", 4 => "G", _ => "?" };
        public int Jersey { get => P.Jersey; set { editor.SetJersey(P.RosterRow, value); P = P with { Jersey = value }; } }
        public int Style { get => P.Style; set { editor.SetPlayerStyle(P.RosterRow, value); P = P with { Style = value }; } }
        public int Letter { get => P.Captain; set { editor.SetCaptain(P.RosterRow, value); P = P with { Captain = value }; } }
        public bool Dressed { get => P.Dressed == 1; set { editor.SetDressed(P.RosterRow, value); P = P with { Dressed = value ? 1 : 0 }; } }
    }

    void BuildRoster(TeamEditor.Team team)
    {
        _rosterPanel.Children.Clear();
        if (_editor is null) return;
        var players = _editor.Players(team.Index);
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            ItemsSource = players.Select(p => new RowVm(_editor, p)).ToList(),
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x22, 0x30)), Foreground = Brushes.White,
            RowBackground = new SolidColorBrush(Color.FromRgb(0x15, 0x1a, 0x22)), AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0x1a, 0x22, 0x30)),
            MaxHeight = 780, BorderBrush = new SolidColorBrush(Color.FromRgb(0x1d, 0x2b, 0x44)),
        };
        grid.Columns.Add(ReadOnly("Name", nameof(RowVm.Name), 190));
        grid.Columns.Add(ReadOnly("Pos", nameof(RowVm.Position), 44));
        grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(RowVm.Jersey)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true }, Width = 52 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Style", Binding = new Binding(nameof(RowVm.Style)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true }, Width = 52 });
        var letter = new DataGridComboBoxColumn { Header = "Letter", SelectedItemBinding = new Binding(nameof(RowVm.Letter)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 70 };
        letter.ItemsSource = new[] { 0, 1, 2 };
        grid.Columns.Add(letter);
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Dressed", Binding = new Binding(nameof(RowVm.Dressed)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 70 });
        _rosterPanel.Children.Add(grid);
        _rosterPanel.Children.Add(Text($"{players.Count} players on {team.Name}. Style and letter are the game's numeric codes " +
            "(letter: 0 none, 1 captain, 2 alternate; the words the game shows depend on the mode). Dressed marks the 20 who play."));
    }

    static DataGridTextColumn ReadOnly(string header, string path, double width) =>
        new() { Header = header, Binding = new Binding(path), IsReadOnly = true, Width = width };

    // ---------------------------------------------------------------- strategy tab

    void BuildStrategy(TeamEditor.Team team)
    {
        _stratPanel.Children.Clear();
        if (_editor is null) return;
        if (!_editor.HasStrategies) { _stratPanel.Children.Add(Text("This database has no strategy table.")); return; }
        var groups = _editor.StrategyGroups(team.Index);
        if (groups.Count == 0) { _stratPanel.Children.Add(Text($"{team.Name} has no strategy row in this database.")); return; }
        _building = true;
        foreach (var g in groups)
        {
            var box = new GroupBox { Header = g.Title, Foreground = Brushes.White, Margin = new Thickness(0, 0, 14, 10) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int r = 0;
            foreach (var f in g.Fields)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var lab = new TextBlock { Text = f.Name, Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)), Margin = new Thickness(6, 4, 10, 4) };
                Grid.SetRow(lab, r); Grid.SetColumn(lab, 0);
                var combo = new ComboBox { MinWidth = 92, Margin = new Thickness(0, 2, 6, 2) };
                for (int v = 0; v <= Math.Min(f.High, 31); v++) combo.Items.Add(new ListBoxItem { Content = v.ToString(), Tag = v });
                combo.SelectedIndex = Math.Clamp(f.Value, 0, combo.Items.Count - 1);
                var (name, teamIndex) = (f.Name, team.Index);
                combo.SelectionChanged += (_, _) =>
                {
                    if (_building || _editor is null || combo.SelectedItem is not ListBoxItem { Tag: int v }) return;
                    try { _editor.SetStrategy(teamIndex, name, v); _status.Text = ""; }
                    catch (Exception ex)
                    {
                        _status.Text = ex.Message;
                        var current = _editor.StrategyGroups(teamIndex).SelectMany(x => x.Fields).First(x => x.Name == name).Value;
                        _building = true; combo.SelectedIndex = Math.Clamp(current, 0, combo.Items.Count - 1); _building = false;
                    }
                };
                Grid.SetRow(combo, r); Grid.SetColumn(combo, 1);
                grid.Children.Add(lab); grid.Children.Add(combo);
                r++;
            }
            box.Content = grid;
            _stratPanel.Children.Add(box);
        }
        _building = false;
        var wrap = new WrapPanel();
        while (_stratPanel.Children.Count > 0)
        {
            var child = _stratPanel.Children[0];
            _stratPanel.Children.RemoveAt(0); // a WPF element must be disconnected from its old logical parent before it can be re-parented
            wrap.Children.Add(child);
        }
        _stratPanel.Children.Add(wrap);
        _stratPanel.Children.Add(Text("The values are the game's own strategy codes (0 = the first entry of the menu the game shows). " +
            "The meta XML names the fields but not the menu words; the groups follow the field names."));
    }

    // ---------------------------------------------------------------- verify & save

    void ShowVerify()
    {
        if (_editor is null) return;
        var problems = NewProblems();
        var known = _baseline.Count == 0 ? "" : $" ({_baseline.Count} problem(s) already in the database when it was opened are not counted.)";
        _status.Text = problems.Count == 0
            ? "Every check passes: 20 dressed where a team dresses 20, no slot held twice, no unit doubled, letters and numbers legal." + known
            : $"{problems.Count} new problem(s):\n• " + string.Join("\n• ", problems.Take(20)) + known;
    }

    void Save()
    {
        if (_editor is null || _source is null) return;
        var problems = NewProblems();
        if (problems.Count > 0)
        {
            MessageBox.Show("The checks refuse this save:\n• " + string.Join("\n• ", problems.Take(20)), "Teams & database", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ApplyTarget.RunningGame() is { } running)
        {
            MessageBox.Show($"{running} is running. Close NHL 24 before saving a roster.", "Teams & database", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show($"Write the edits back to\n{_source.Label}\nwith a backup of the original?", "Teams & database",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var bytes = _editor.Save();
            _status.Text = _source.WriteDb([.. bytes, .. _trailer]);
        }
        catch (Exception ex) { _status.Text = "Not written: " + ex.Message; }
    }
}
