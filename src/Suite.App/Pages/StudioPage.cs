using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Suite.Convert27;
using Suite.Convert27.Controls;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

namespace Suite.App.Pages;

/// <summary>
/// The five game databases as one source: reads nhl_game and, on Save, copies the player tables into the other four (and the same club edits),
/// then makes nhl_game's online roster equal to its roster again. All five are built before any is written, so a bad edit never leaves a
/// half-written set; each one is written through the same checked, backed-up path as every other game database write.
/// </summary>
internal sealed class AllFiveSource(SuiteSettings settings, Func<IReadOnlyList<(int Team, string Field, long? Number, string? Text)>> clubEdits) : ISource
{
    byte[]? _original;
    public string Label => "All five game databases (kept identical)";

    public byte[] ReadDb(out byte[] trailer)
    {
        trailer = [];
        _original = GameDatabaseEdit.Read(settings.ApplyTargetDir, settings, "nhl_game");
        return (byte[])_original.Clone();
    }

    public string WriteDb(byte[] edited)
    {
        if (_original is null) throw new InvalidOperationException("Open the databases first.");
        var game = settings.ApplyTargetDir;
        var meta = DbSources.FindMeta(this, settings);
        var main = T3Db.Load((byte[])edited.Clone(), meta);
        PlayerMirror.SyncOnlineRoster(main);
        var built = new Dictionary<string, (byte[] Original, byte[] Edited)> { ["nhl_game"] = (_original, (byte[])main.Save().Clone()) };
        foreach (var name in GameDatabaseEdit.Databases.Where(n => n != "nhl_game"))
        {
            var original = GameDatabaseEdit.Read(game, settings, name);
            var other = T3Db.Load((byte[])original.Clone(), meta);
            PlayerMirror.CopyInto(main, other);
            ApplyClubEdits(other);
            built[name] = (original, (byte[])other.Save().Clone());
        }
        var log = new List<string>();
        foreach (var (name, pair) in built.OrderBy(p => p.Key == "nhl_game"))
            GameDatabaseEdit.Write(game, settings, name, pair.Original, pair.Edited, log.Add);
        _original = built["nhl_game"].Edited;
        return $"Wrote {built.Count} databases, all with the same players. Backups were kept for each.";
    }

    void ApplyClubEdits(T3Db db)
    {
        if (!db.Tables.TryGetValue("exhibitionteams", out var teams)) return;
        foreach (var e in clubEdits())
        {
            for (int r = 0; r < teams.InUse; r++)
            {
                if (db.GetInt(teams, r, "index") != e.Team) continue;
                if (e.Number is long n) db.SetInt(teams, r, e.Field, n); else if (e.Text != null) db.SetString(teams, r, e.Field, e.Text);
                break;
            }
        }
    }
}

/// <summary>
/// Database studio: players, clubs, contracts and every raw table in one place. Players and clubs are shown the way the game talks about them
/// (named positions and archetypes, 6'2" heights, dollar contracts, colour pickers) instead of numeric codes; the Tables tab is the full grid.
/// Every edit is in place, so the database keeps NHL 24's layout; Save runs the roster checks, refuses while NHL 24 runs, and keeps a backup.
/// </summary>
public sealed class StudioPage : Grid
{
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8c, 0x97, 0xa8));
    static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x20));

    readonly SuiteSettings _settings;
    readonly TextBlock _status = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    readonly ComboBox _sources = new() { MinWidth = 420, Padding = new Thickness(4, 3, 4, 3), DisplayMemberPath = nameof(ISource.Label) };
    readonly Button _open = new() { Content = "Open" }, _save = new() { Content = "Save", IsEnabled = false }, _check = new() { Content = "Check", IsEnabled = false };
    readonly TabControl _tabs = new();
    readonly DbEditorPanel _tables;
    readonly TextBox _filter = new() { Width = 200, Margin = new Thickness(0, 0, 8, 0) };
    readonly ComboBox _teamFilter = new() { Width = 170, DisplayMemberPath = nameof(ChoiceSets.Choice.Name), Margin = new Thickness(0, 0, 8, 0) };
    readonly ComboBox _posFilter = new() { Width = 120, DisplayMemberPath = nameof(ChoiceSets.Choice.Name) };
    readonly DataGrid _playerGrid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
        Width = 580, MinHeight = 480, MaxHeight = 760, EnableRowVirtualization = true, Background = Panel, Foreground = Brushes.White, CanUserAddRows = false,
    };
    readonly ScrollViewer _playerEditors = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760, MinWidth = 640, Margin = new Thickness(14, 0, 0, 0) };
    readonly ListBox _teamList = new() { Width = 260, MaxHeight = 760, DisplayMemberPath = nameof(ChoiceSets.Choice.Name), Background = Panel };
    readonly ScrollViewer _teamEditors = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760, MinWidth = 640, Margin = new Thickness(14, 0, 0, 0) };

    ISource? _source;
    TeamEditor? _editor;
    PlayerBook? _book;
    ChoiceSets _choices = new();
    byte[] _trailer = [];
    HashSet<string> _baseline = [];
    IReadOnlyList<PlayerSummary> _all = [];
    readonly List<(int Team, string Field, long? Number, string? Text)> _clubEdits = [];
    bool _syncing;

    public StudioPage(SuiteSettings settings)
    {
        _settings = settings;
        System.Windows.Documents.TextElement.SetFontSize(this, 14);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Margin = new Thickness(24, 20, 24, 16);

        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Database studio", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        head.Children.Add(new TextBlock
        {
            Text = "Players, clubs, contracts and every table of a roster database, read the way the game names things. Open the five game databases together to keep them identical " +
                "(a mismatch between them crashes the game), or any one database or saved roster on its own. Edits are in place; Save checks the rosters, refuses while NHL 24 is running and keeps a backup.",
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)), Margin = new Thickness(0, 4, 0, 8),
        });
        var row = new WrapPanel();
        row.Children.Add(_sources);
        var action = (Style)Application.Current.Resources["Action"];
        _open.Style = _save.Style = _check.Style = action;
        _open.Margin = new Thickness(8, 0, 0, 0); _save.Margin = _check.Margin = new Thickness(8, 0, 0, 0);
        var browse = new Button { Content = "Open .db file…", Margin = new Thickness(8, 0, 0, 0), Style = action };
        browse.Click += (_, _) => Browse();
        row.Children.Add(_open); row.Children.Add(browse); row.Children.Add(_check); row.Children.Add(_save);
        head.Children.Add(row);
        head.Children.Add(_status);
        SetRow(head, 0); Children.Add(head);

        _tables = new DbEditorPanel(s => _status.Text = s, () => _editor?.MarkDirty());
        _tabs.Margin = new Thickness(0, 10, 0, 0);
        _tabs.Items.Add(new TabItem { Header = "Players", Content = BuildPlayersTab() });
        _tabs.Items.Add(new TabItem { Header = "Clubs", Content = BuildTeamsTab() });
        _tabs.Items.Add(new TabItem { Header = "Contracts", Content = new ContractsPage(settings) });
        _tabs.Items.Add(new TabItem { Header = "Tables", Content = _tables });
        SetRow(_tabs, 1); Children.Add(_tabs);

        _open.Click += (_, _) => { if (_sources.SelectedItem is ISource s) Open(s); };
        _save.Click += (_, _) => Save();
        _check.Click += (_, _) => ShowCheck();
        RefreshSources();
    }

    // ---------------------------------------------------------------- sources

    void RefreshSources()
    {
        var list = new List<ISource>();
        if (!string.IsNullOrEmpty(_settings.ApplyTargetDir) && Directory.Exists(_settings.ApplyTargetDir))
            list.Add(new AllFiveSource(_settings, () => _clubEdits));
        list.AddRange(DbSources.List(_settings));
        _sources.ItemsSource = list;
        if (list.Count > 0) _sources.SelectedIndex = 0;
        _status.Text = list.Count == 0 ? "No game folder is set (Settings) and no stock export is in the work folder." : "";
    }

    void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Roster databases (*.db)|*.db", Title = "Open a roster database" };
        if (dlg.ShowDialog() != true) return;
        var s = new FileSource(dlg.FileName);
        _sources.ItemsSource = ((IEnumerable<ISource>)_sources.ItemsSource).Append(s).ToList();
        _sources.SelectedItem = s;
        Open(s);
    }

    void Open(ISource source)
    {
        try
        {
            if (_editor is { IsDirty: true } &&
                MessageBox.Show("Discard the unsaved edits?", "Database studio", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var bytes = source.ReadDb(out _trailer);
            var meta = DbSources.FindMeta(source, _settings);
            _editor = TeamEditor.Load(bytes, meta);
            _baseline = _editor.Verify().ToHashSet(StringComparer.Ordinal);
            _book = new PlayerBook(_editor.Db);
            _source = source;
            _clubEdits.Clear();
            _choices = new ChoiceSets();
            _choices.Put(ChoiceSets.Team, _book.Teams());
            _save.IsEnabled = _check.IsEnabled = true;
            _tables.Load(_editor);
            _all = _book.Players();
            FillFilters();
            ApplyFilter();
            FillTeams();
            _playerEditors.Content = null; _teamEditors.Content = null;
            _status.Text = $"{_all.Count} players and {_book.Teams().Count - 1} clubs loaded." +
                (source is AllFiveSource ? " Saving writes all five databases with the same players. Changes made in the Tables tab go to nhl_game only." : "");
            LoadChoicesInBackground();
        }
        catch (Exception ex) { _status.Text = "Not opened: " + ex.Message; }
    }

    /// <summary>The named lists (archetypes, hair, helmets...) come from the game's own create-a-player tables; the first look uses the built-in lists.</summary>
    void LoadChoicesInBackground()
    {
        var book = _book; var game = _settings.ApplyTargetDir;
        if (book is null || string.IsNullOrEmpty(game) || !Directory.Exists(game)) return;
        var teams = book.Teams();
        Task.Run(() =>
        {
            try
            {
                var files = new LegacyFiles(game, _settings);
                LanguageStrings? loc = null;
                try { loc = LanguageStrings.Load(files.Read("loc/eng_us.db")); } catch { /* names fall back to the keys */ }
                return ChoiceSets.Load(name => { try { return files.Read(name); } catch { return null; } }, key => loc?.Find(key), teams);
            }
            catch { return null; }
        }).ContinueWith(t =>
        {
            if (t.IsFaulted || t.Result is null || book != _book) return;
            _choices = t.Result;
            if (_playerGrid.SelectedItem is PlayerSummary p) ShowPlayer(p);
            if (_teamList.SelectedItem is ChoiceSets.Choice c) ShowTeam(c);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---------------------------------------------------------------- players

    UIElement BuildPlayersTab()
    {
        var left = new DockPanel();
        var bar = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        bar.Children.Add(_filter); bar.Children.Add(_teamFilter); bar.Children.Add(_posFilter);
        DockPanel.SetDock(bar, Dock.Top); left.Children.Add(bar);
        void Col(string header, string path, double w) => _playerGrid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = w });
        Col("Name", nameof(PlayerSummary.Name), 170); Col("Pos", nameof(PlayerSummary.PositionName), 60); Col("Team", nameof(PlayerSummary.TeamName), 170);
        Col("#", nameof(PlayerSummary.Jersey), 40); Col("OVR", nameof(PlayerSummary.Overall), 50); Col("Born", nameof(PlayerSummary.BirthYear), 56);
        left.Children.Add(_playerGrid);
        _filter.TextChanged += (_, _) => ApplyFilter();
        _teamFilter.SelectionChanged += (_, _) => ApplyFilter();
        _posFilter.SelectionChanged += (_, _) => ApplyFilter();
        _playerGrid.SelectionChanged += (_, _) => { if (_playerGrid.SelectedItem is PlayerSummary p) ShowPlayer(p); };
        var root = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(left, Dock.Left); root.Children.Add(left);
        root.Children.Add(_playerEditors);
        return root;
    }

    void FillFilters()
    {
        if (_book is null) return;
        _syncing = true;
        _teamFilter.ItemsSource = new[] { new ChoiceSets.Choice(-2, "All teams") }.Concat(_book.Teams().Where(t => t.Value >= 0).Select(t => new ChoiceSets.Choice(t.Value, t.Name)))
            .Append(new ChoiceSets.Choice(-1, "No team")).ToList();
        _teamFilter.SelectedIndex = 0;
        _posFilter.ItemsSource = new[] { new ChoiceSets.Choice(-1, "Any position") }.Concat(_choices.Get(ChoiceSets.Position)!).ToList();
        _posFilter.SelectedIndex = 0;
        _filter.Text = "";
        _syncing = false;
    }

    void ApplyFilter()
    {
        if (_syncing || _book is null) return;
        var text = _filter.Text.Trim();
        long team = (_teamFilter.SelectedItem as ChoiceSets.Choice)?.Value ?? -2, pos = (_posFilter.SelectedItem as ChoiceSets.Choice)?.Value ?? -1;
        var rows = _all.Where(p => (text.Length == 0 || p.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || p.GameId.ToString() == text)
            && (team == -2 || p.Team == team) && (pos < 0 || p.Position == pos)).Take(2000).ToList();
        _playerGrid.ItemsSource = rows;
    }

    void ShowPlayer(PlayerSummary p)
    {
        if (_book is null) return;
        _playerEditors.Content = Editors(_book.FieldsOf(p.GameId), FieldCatalog.PlayerGroups, null, $"{p.Name} — {p.PositionName}");
    }

    // ---------------------------------------------------------------- clubs

    UIElement BuildTeamsTab()
    {
        _teamList.SelectionChanged += (_, _) => { if (_teamList.SelectedItem is ChoiceSets.Choice c) ShowTeam(c); };
        var root = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(_teamList, Dock.Left); root.Children.Add(_teamList);
        root.Children.Add(_teamEditors);
        return root;
    }

    void FillTeams()
    {
        if (_book is null) return;
        _teamList.ItemsSource = _book.Teams().Where(t => t.Value >= 0).Select(t => new ChoiceSets.Choice(t.Value, t.Name)).ToList();
    }

    void ShowTeam(ChoiceSets.Choice c)
    {
        if (_book is null) return;
        var fields = _book.TeamFields((int)c.Value);
        var palette = fields.Where(f => f.Spec.Kind == FieldKind.Colour).Select(f => (int)(_book.GetInt(f) & 0xFFFFFF)).Distinct().ToList();
        _teamEditors.Content = Editors(fields, FieldCatalog.TeamGroups, palette, c.Name);
    }

    // ---------------------------------------------------------------- field editors

    UIElement Editors(IReadOnlyList<PlayerField> fields, IReadOnlyList<string> groupOrder, IEnumerable<int>? teamColours, string title)
    {
        var host = new StackPanel { Margin = new Thickness(0, 0, 8, 12) };
        host.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6) });
        var order = groupOrder.Select((g, i) => (g, i)).ToDictionary(p => p.g, p => p.i);
        int shown = 0;
        foreach (var group in fields.GroupBy(f => f.Spec.Group).OrderBy(g => order.GetValueOrDefault(g.Key, order.Count)))
        {
            var inner = new StackPanel { Margin = new Thickness(6, 4, 0, 10) };
            foreach (var f in group) inner.Children.Add(FieldRow(f, teamColours));
            host.Children.Add(new Expander
            {
                IsExpanded = shown++ < 2, Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 2),
                Header = new TextBlock { Text = $"{group.Key}  ({group.Count()})", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold }, Content = inner,
            });
        }
        return host;
    }

    UIElement FieldRow(PlayerField f, IEnumerable<int>? teamColours)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = f.Label, Foreground = Brushes.Gainsboro, VerticalAlignment = VerticalAlignment.Center, ToolTip = f.Spec.Hint ?? f.Field.Name });
        var editor = Editor(f, teamColours);
        SetColumn(editor, 1); grid.Children.Add(editor);
        return grid;
    }

    UIElement Editor(PlayerField f, IEnumerable<int>? teamColours)
    {
        var book = _book!;
        var kind = f.Spec.Kind;
        if (f.Field.Type == 0 || kind == FieldKind.Text)
        {
            var box = new TextBox { Width = 260, Text = book.GetText(f), IsReadOnly = f.ReadOnly, MaxLength = Math.Max(1, f.Field.Depth / 8 - 1) };
            void CommitBox() { if (box.Text != book.GetText(f) && Commit(f, null, box.Text)) box.Text = book.GetText(f); }
            box.LostFocus += (_, _) => CommitBox(); box.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitBox(); };
            return box;
        }
        var (low, high) = PlayerBook.Range(f.Field);
        if (f.ReadOnly) return new TextBlock { Text = FieldFormat.Show(kind, book.GetInt(f)), Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        if (kind == FieldKind.Flag)
        {
            var cb = new CheckBox { IsChecked = book.GetInt(f) != 0, VerticalAlignment = VerticalAlignment.Center };
            cb.Click += (_, _) => { if (!Commit(f, cb.IsChecked == true ? 1 : 0, null)) cb.IsChecked = book.GetInt(f) != 0; };
            return cb;
        }
        if (kind == FieldKind.Colour)
        {
            var cf = new ColorField { Hex = ColorPalette.Hex((int)(book.GetInt(f) & 0xFFFFFF)), TeamColours = teamColours };
            DependencyPropertyDescriptor.FromProperty(ColorField.HexProperty, typeof(ColorField)).AddValueChanged(cf, (_, _) =>
            {
                if (ColorPalette.TryParse(cf.Hex, out var rgb) && rgb != (book.GetInt(f) & 0xFFFFFF)) Commit(f, rgb, null);
            });
            return cf;
        }
        if (kind is FieldKind.Choice or FieldKind.Team && _choices.Get(f.Spec.Choices ?? (kind == FieldKind.Team ? ChoiceSets.Team : null)) is { } list)
        {
            var items = list.ToList();
            long cur = book.GetInt(f);
            if (items.All(c => c.Value != cur)) items.Insert(0, new ChoiceSets.Choice(cur, cur + " (not in the list)"));
            var combo = new ComboBox { Width = 260, ItemsSource = items, DisplayMemberPath = nameof(ChoiceSets.Choice.Name), SelectedItem = items.First(c => c.Value == cur) };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ChoiceSets.Choice c && c.Value != book.GetInt(f) && !Commit(f, c.Value, null)) combo.SelectedItem = items.FirstOrDefault(i => i.Value == book.GetInt(f));
            };
            return combo;
        }
        // Number-like: a box in the friendly format; ratings also get a slider.
        var tb = new TextBox { Width = kind == FieldKind.Rating ? 56 : 140, Text = FieldFormat.Show(kind, book.GetInt(f)) };
        Slider? slider = kind == FieldKind.Rating && high - low <= 255
            ? new Slider { Minimum = low, Maximum = high, Value = book.GetInt(f), Width = 200, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
            : null;
        void Refresh() { tb.Text = FieldFormat.Show(kind, book.GetInt(f)); if (slider != null) { _syncing = true; slider.Value = book.GetInt(f); _syncing = false; } }
        void CommitText()
        {
            if (!FieldFormat.TryParse(kind, tb.Text, out var raw, out var err)) { _status.Text = $"{f.Label}: {err}"; Refresh(); return; }
            if (raw < low || raw > high) { _status.Text = $"{f.Label}: allowed {FieldFormat.Show(kind, low)} to {FieldFormat.Show(kind, high)}."; Refresh(); return; }
            if (raw != book.GetInt(f)) Commit(f, raw, null);
            Refresh();
        }
        tb.LostFocus += (_, _) => CommitText(); tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitText(); };
        if (slider == null) return tb;
        slider.ValueChanged += (_, e) => { if (!_syncing && (long)e.NewValue != book.GetInt(f)) { Commit(f, (long)e.NewValue, null); tb.Text = FieldFormat.Show(kind, book.GetInt(f)); } };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(tb); row.Children.Add(slider);
        return row;
    }

    bool Commit(PlayerField f, long? number, string? text)
    {
        try
        {
            if (number is long n) _book!.SetInt(f, n); else _book!.SetText(f, text ?? "");
            _editor!.MarkDirty();
            if (f.TableKey == "team")
            {
                var team = (int)_book.Db.GetInt(f.Table, f.Row, "index");
                _clubEdits.RemoveAll(e => e.Team == team && e.Field == f.Field.Name);
                _clubEdits.Add((team, f.Field.Name, number, text));
            }
            _status.Text = "";
            return true;
        }
        catch (Exception ex) { _status.Text = $"{f.Label}: {ex.Message}"; return false; }
    }

    // ---------------------------------------------------------------- checks and save

    List<string> NewProblems() => _editor is null ? [] : _editor.Verify().Where(p => !_baseline.Contains(p)).ToList();

    void ShowCheck()
    {
        var problems = NewProblems();
        _status.Text = problems.Count == 0 ? "Every check passes." : $"{problems.Count} new problem(s):\n• " + string.Join("\n• ", problems.Take(20));
    }

    void Save()
    {
        if (_editor is null || _source is null) return;
        var problems = NewProblems();
        if (problems.Count > 0)
        {
            MessageBox.Show("The checks refuse this save:\n• " + string.Join("\n• ", problems.Take(20)), "Database studio", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ApplyTarget.RunningGame() is { } running)
        {
            MessageBox.Show($"{running} is running. Close NHL 24 before saving.", "Database studio", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show($"Write the edits back to\n{_source.Label}\nwith a backup of the originals?", "Database studio", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            _status.Text = _source.WriteDb([.. _editor.Save(), .. _trailer]);
            _all = _book!.Players();
            ApplyFilter();
        }
        catch (Exception ex) { _status.Text = "Not written: " + ex.Message; }
    }
}
