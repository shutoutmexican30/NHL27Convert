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
/// Teams &amp; lines editor: open a saved roster (or a roster database file), pick a team and edit its roster (numbers,
/// letters, player style), its lines (every slot the game knows, from the four lines to the shootout list) and its
/// strategies. Every edit is an in-place integer edit, so the database keeps NHL 24's layout exactly; before anything is
/// written the built-in verify must pass, the game must be closed, and a backup of the original is kept.
/// </summary>
public sealed class EditorPage : ScrollViewer
{
    readonly SuiteSettings _settings;
    readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x8c, 0x97, 0xa8)), TextWrapping = TextWrapping.Wrap };
    readonly ComboBox _sources = new() { MinWidth = 420, Padding = new Thickness(4, 3, 4, 3) };
    readonly ListBox _teams = new() { MinWidth = 250, MaxHeight = 620, Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x22, 0x30)) };
    readonly TabControl _tabs = new() { MinWidth = 780 };
    readonly Button _open = new() { Content = "Open" }, _save = new() { Content = "Save", IsEnabled = false }, _verify = new() { Content = "Verify", IsEnabled = false };
    readonly StackPanel _rosterPanel = new(), _linesPanel = new(), _stratPanel = new();
    readonly Dictionary<string, TeamEditor.Team> _teamByItem = [];

    ISource? _source;
    TeamEditor? _editor;
    byte[] _trailer = [];
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

    public EditorPage(SuiteSettings settings)
    {
        _settings = settings;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var root = new StackPanel { Margin = new Thickness(32, 26, 32, 26), MaxWidth = 1400, HorizontalAlignment = HorizontalAlignment.Left };
        Content = root;

        root.Children.Add(Heading("Teams & lines editor"));
        root.Children.Add(Text("Open a roster saved in game (or a roster database file from the work folder), pick a team, and edit " +
            "its roster, lines and strategies. Edits keep the game's own layout — nothing grows or moves, only values change. " +
            "Save runs the same checks as an update, refuses while NHL 24 is running, and keeps a backup. Load the roster in the game " +
            "with Customize > Save/Load/Delete > Load > Rosters."));
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(_sources);
        row.Children.Add(_open);
        var browse = new Button { Content = "Open .db file…", Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.Resources["Action"] };
        browse.Click += (_, _) => Browse();
        row.Children.Add(browse);
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
        _tabs.Items.Add(Tab("Lines", _linesPanel));
        _tabs.Items.Add(Tab("Strategy", _stratPanel));
        var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        bar.Children.Add(_verify);
        bar.Children.Add(_save);
        root.Children.Add(bar);

        RefreshSources();
    }

    static TabItem Tab(string title, UIElement content) => new() { Header = title, Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };

    static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };

    static TextBlock Text(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)), LineHeight = 20, Margin = new Thickness(0, 6, 0, 0),
    };

    // ---------------------------------------------------------------- sources

    void RefreshSources()
    {
        _sources.Items.Clear();
        if (!string.IsNullOrEmpty(_settings.ApplyTargetDir))
            foreach (var save in SavedRoster.SavedataRoots(_settings.ApplyTargetDir).SelectMany(SavedRoster.Find))
                _sources.Items.Add(new ListBoxItem { Content = new SaveSource(save).Label, Tag = new SaveSource(save) });
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

    void Open(ISource source)
    {
        try
        {
            if (_editor is { IsDirty: true } &&
                MessageBox.Show("Discard the unsaved edits?", "Teams & lines", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var bytes = source.ReadDb(out _trailer);
            var meta = FindMeta();
            _editor = TeamEditor.Load(bytes, meta);
            _source = source;
            _status.Text = "";
            _save.IsEnabled = _verify.IsEnabled = true;
            ShowTeams();
        }
        catch (Exception ex) { _status.Text = "Not opened: " + ex.Message; }
    }

    string FindMeta()
    {
        // A file source usually sits next to its meta (a stock export keeps both in the same folder).
        if (_source is FileSource f)
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
        BuildLines(team);
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
            MaxHeight = 560, BorderBrush = new SolidColorBrush(Color.FromRgb(0x1d, 0x2b, 0x44)),
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

    // ---------------------------------------------------------------- lines tab

    static readonly (string Title, (string Label, string Slot, char Role)[] Rows)[] LineSections =
    [
        ("Forward lines", [.. from i in new[] { 1, 2, 3, 4 } from s in new[] { ("C", "c", 'F'), ("LW", "lw", 'F'), ("RW", "rw", 'F') } select ($"L{i} {s.Item1}", $"l{i}{s.Item2}", s.Item3)]),
        ("Defense pairs", [.. from i in new[] { 1, 2, 3 } from s in new[] { ("LD", "ld", 'D'), ("RD", "rd", 'D') } select ($"Pair {i} {s.Item1}", $"l{i}{s.Item2}", s.Item3)]),
        ("Goalies", [("Starter", "g1", 'G'), ("Backup", "g2", 'G')]),
        ("Power play 1", [("LW", "pp_1lw", 'F'), ("C", "pp_1c", 'F'), ("RW", "pp_1rw", 'F'), ("LD", "pp_1ld", 'D'), ("RD", "pp_1rd", 'D')]),
        ("Power play 2", [("LW", "pp_2lw", 'F'), ("C", "pp_2c", 'F'), ("RW", "pp_2rw", 'F'), ("LD", "pp_2ld", 'D'), ("RD", "pp_2rd", 'D')]),
        ("Power play 4-man 1", [("LW", "pp4_1lw", 'F'), ("C", "pp4_1c", 'F'), ("RW", "pp4_1rw", 'F'), ("LD", "pp4_1ld", 'D'), ("RD", "pp4_1rd", 'D')]),
        ("Power play 4-man 2", [("LW", "pp4_2lw", 'F'), ("C", "pp4_2c", 'F'), ("RW", "pp4_2rw", 'F'), ("LD", "pp4_2ld", 'D'), ("RD", "pp4_2rd", 'D')]),
        ("Penalty kill 4-man 1", [("LW", "pk4_1lw", 'F'), ("C", "pk4_1c", 'F'), ("LD", "pk4_1ld", 'D'), ("RD", "pk4_1rd", 'D')]),
        ("Penalty kill 4-man 2", [("LW", "pk4_2lw", 'F'), ("C", "pk4_2c", 'F'), ("LD", "pk4_2ld", 'D'), ("RD", "pk4_2rd", 'D')]),
        ("Penalty kill 4-man 3", [("LW", "pk4_3lw", 'F'), ("C", "pk4_3c", 'F'), ("LD", "pk4_3ld", 'D'), ("RD", "pk4_3rd", 'D')]),
        ("Penalty kill 3-man 1", [("C", "pk3_1c", 'F'), ("LD", "pk3_1ld", 'D'), ("RD", "pk3_1rd", 'D')]),
        ("Penalty kill 3-man 2", [("C", "pk3_2c", 'F'), ("LD", "pk3_2ld", 'D'), ("RD", "pk3_2rd", 'D')]),
        ("Penalty kill 3-man 3", [("C", "pk3_3c", 'F'), ("LD", "pk3_3ld", 'D'), ("RD", "pk3_3rd", 'D')]),
        ("Even strength 5-man 1", [("LW", "ev4_1lw", 'F'), ("C", "ev4_1c", 'F'), ("RW", "ev4_1rw", 'F'), ("LD", "ev4_1ld", 'D'), ("RD", "ev4_1rd", 'D')]),
        ("Even strength 5-man 2", [("LW", "ev4_2lw", 'F'), ("C", "ev4_2c", 'F'), ("RW", "ev4_2rw", 'F'), ("LD", "ev4_2ld", 'D'), ("RD", "ev4_2rd", 'D')]),
        ("Even strength 5-man 3", [("LW", "ev4_3lw", 'F'), ("C", "ev4_3c", 'F'), ("RW", "ev4_3rw", 'F'), ("LD", "ev4_3ld", 'D'), ("RD", "ev4_3rd", 'D')]),
        ("3 on 3", [("C", "three_c", 'F'), ("W", "three_w", 'F'), ("D", "three_d", 'D'), ("G", "three_g1", 'G'), ("G2", "three_g2", 'G'), ("S1", "three_s1", 'F'), ("S2", "three_s2", 'F')]),
        ("Shootout", [.. from i in new[] { 1, 2, 3, 4, 5 } select ($"Shooter {i}", $"s{i}", 'F')]),
        ("Extra attackers", [("First", "x1", 'F'), ("Second", "x2", 'F')]),
    ];

    void BuildLines(TeamEditor.Team team)
    {
        _linesPanel.Children.Clear();
        if (_editor is null) return;
        _building = true;
        var players = _editor.Players(team.Index);
        foreach (var (title, rows) in LineSections)
        {
            var box = new GroupBox { Header = title, Foreground = Brushes.White, Margin = new Thickness(0, 0, 14, 10) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            int r = 0;
            foreach (var (label, slot, role) in rows)
            {
                var lab = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)), Margin = new Thickness(6, 4, 10, 4) };
                Grid.SetRow(lab, r); Grid.SetColumn(lab, 0);
                var combo = new ComboBox { MinWidth = 210, Margin = new Thickness(0, 2, 6, 2), Tag = (slot, team.Index) };
                combo.Items.Add(new ListBoxItem { Content = "—", Tag = null });
                foreach (var p in players.Where(p => role switch { 'F' => p.Pos <= 2, 'D' => p.Pos == 3, _ => p.Pos == 4 }))
                {
                    var item = new ListBoxItem { Content = $"{p.Name}  #{p.Jersey}", Tag = p.RosterRow };
                    combo.Items.Add(item);
                    if (_editor.HolderRow(team.Index, slot) == p.RosterRow) combo.SelectedItem = item;
                }
                if (combo.SelectedItem is null) combo.SelectedIndex = 0;
                combo.SelectionChanged += SlotChanged;
                Grid.SetRow(combo, r); Grid.SetColumn(combo, 1);
                grid.Children.Add(lab); grid.Children.Add(combo);
                r++;
            }
            box.Content = grid;
            _linesPanel.Children.Add(box);
        }
        _building = false;
        var wrap = new WrapPanel();
        while (_linesPanel.Children.Count > 0) wrap.Children.Add(_linesPanel.Children[0]);
        _linesPanel.Children.Add(wrap);
    }

    void SlotChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building || _editor is null || sender is not ComboBox { Tag: (string slot, int team) } combo) return;
        _building = true;
        try
        {
            var row = (combo.SelectedItem as ListBoxItem)?.Tag as int?;
            _editor.SetSlot(team, slot, row);
            _status.Text = "";
            RefreshLineSelections(team);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            RestoreSlot(combo, team, slot);
        }
        finally { _building = false; }
    }

    void RestoreSlot(ComboBox combo, int team, string slot)
    {
        var holder = _editor?.HolderRow(team, slot);
        foreach (var item in combo.Items.OfType<ListBoxItem>())
            if (Equals(item.Tag as int?, holder)) { combo.SelectedItem = item; return; }
        combo.SelectedIndex = 0;
    }

    void RefreshLineSelections(int team)
    {
        foreach (var combo in _linesPanel.Children.OfType<WrapPanel>().SelectMany(w => w.Children.OfType<GroupBox>())
                     .SelectMany(g => ((Grid)g.Content).Children.OfType<ComboBox>()))
        {
            var (slot, t) = ((string, int))combo.Tag;
            if (t != team) continue;
            var holder = _editor?.HolderRow(team, slot);
            var match = combo.Items.OfType<ListBoxItem>().FirstOrDefault(i => Equals(i.Tag as int?, holder));
            if (match is null) { if (combo.SelectedIndex != 0) combo.SelectedIndex = 0; }
            else if (!Equals(combo.SelectedItem, match)) combo.SelectedItem = match;
        }
    }

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
                var lab = new TextBlock { Text = f.Name, Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)), Margin = new Thickness(6, 4, 10, 4) };
                Grid.SetRow(lab, r); Grid.SetColumn(lab, 0);
                var combo = new ComboBox { MinWidth = 70, Margin = new Thickness(0, 2, 6, 2) };
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
        while (_stratPanel.Children.Count > 0) wrap.Children.Add(_stratPanel.Children[0]);
        _stratPanel.Children.Add(wrap);
        _stratPanel.Children.Add(Text("The values are the game's own strategy codes (0 = the first entry of the menu the game shows). " +
            "The meta XML names the fields but not the menu words; the groups follow the field names."));
    }

    // ---------------------------------------------------------------- verify & save

    void ShowVerify()
    {
        if (_editor is null) return;
        var problems = _editor.Verify();
        _status.Text = problems.Count == 0
            ? "Every check passes: 20 dressed where a team dresses 20, no slot held twice, no unit doubled, letters and numbers legal."
            : $"{problems.Count} problem(s):\n• " + string.Join("\n• ", problems.Take(20));
    }

    void Save()
    {
        if (_editor is null || _source is null) return;
        var problems = _editor.Verify();
        if (problems.Count > 0)
        {
            MessageBox.Show("The checks refuse this save:\n• " + string.Join("\n• ", problems.Take(20)), "Teams & lines", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ApplyTarget.RunningGame() is { } running)
        {
            MessageBox.Show($"{running} is running. Close NHL 24 before saving a roster.", "Teams & lines", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show($"Write the edits back to\n{_source.Label}\nwith a backup of the original?", "Teams & lines",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var bytes = _editor.Save();
            _status.Text = _source.WriteDb([.. bytes, .. _trailer]);
        }
        catch (Exception ex) { _status.Text = "Not written: " + ex.Message; }
    }
}
