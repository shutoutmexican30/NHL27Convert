using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Suite.Core;
using Suite.Rosters;

namespace Suite.App.Pages;

/// <summary>
/// Contracts: every player's contract as an editable row (cap hit, years, two-way, extension, UFA year, retained trades,
/// NTC/NMC clauses per season). Rows live in %APPDATA%\CenterIceModdingSuite\data\contracts.json (CSV import/export).
/// Prefill from NHL 27 takes its 2026-27 contracts for every matched player. Preview shows the cell changes in each of
/// the five roster databases; Apply writes them in place into the CIMS game copy (backup, read-back, restore on failure).
/// Clauses are kept by the suite only: NHL 24 has no clause field.
/// </summary>
public sealed class ContractsPage : DockPanel
{
    /// <summary>A grid row over one contract. Money in dollars; clauses as "0:NMC;1:M-NTC/10".</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        public Contract C { get; }
        public Row(Contract c) { C = c; }
        public event PropertyChangedEventHandler? PropertyChanged;
        void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public long GameId => C.GameId;
        public string Player => C.Name;
        public int Team => C.Team;
        public bool Signed { get => C.Signed; set { C.Signed = value; Changed(nameof(Signed)); } }
        public int CapHit { get => C.CapHit; set => C.CapHit = Check(value, 0, 81_915_000, "Cap hit"); }
        public int Years { get => C.Years; set => C.Years = Check(value, 0, 15, "Years"); }
        public bool TwoWay { get => C.TwoWay; set => C.TwoWay = value; }
        public bool? MultiYear { get => C.MultiYear; set => C.MultiYear = value; }
        public int ExtCapHit { get => C.ExtCapHit; set => C.ExtCapHit = Check(value, 0, 81_915_000, "Extension cap hit"); }
        public int ExtYears { get => C.ExtYears; set => C.ExtYears = Check(value, 0, 15, "Extension years"); }
        public bool ExtTwoWay { get => C.ExtTwoWay; set => C.ExtTwoWay = value; }
        public int? YearUfa { get => C.YearUfa; set => C.YearUfa = value is int y ? Check(y, 1900, 2155, "UFA year") : null; }
        public int RetainedTrades { get => C.RetainedTrades; set => C.RetainedTrades = Check(value, 0, 3, "Retained trades"); }
        public string Clauses { get => Clause.FormatList(C.Clauses); set => C.Clauses = Clause.ParseList(value); }
        public string Source { get => C.Source; set => C.Source = value; }
        public string Note { get => C.Note; set => C.Note = value; }

        static int Check(int v, int lo, int hi, string what) =>
            v < lo || v > hi ? throw new ArgumentOutOfRangeException(what, $"{what} must be {lo:N0}-{hi:N0}.") : v;
    }

    /// <summary>A grid row over one club's cap charges; each list is per season from this season on, e.g. "2600000,1500000".</summary>
    public sealed class TeamRow
    {
        public TeamCap T { get; }
        public TeamRow(TeamCap t) { T = t; }
        public int Team => T.Team;
        public string Club { get => T.Name; set => T.Name = value; }
        public string DeadCap { get => TeamCap.FormatList(T.DeadCap); set => T.DeadCap = TeamCap.ParseList(value, int.MaxValue, "Dead cap"); }
        public string Retained { get => TeamCap.FormatList(T.Retained); set => T.Retained = TeamCap.ParseList(value, int.MaxValue, "Retained salary"); }
        public string RetainedCount { get => TeamCap.FormatList(T.RetainedCount); set => T.RetainedCount = TeamCap.ParseList(value, 7, "Retained contracts"); }
        public string Source { get => T.Source; set => T.Source = value; }
    }

    readonly SuiteSettings _settings;
    readonly ObservableCollection<Row> _rows = [];
    readonly ObservableCollection<TeamRow> _teams = [];
    readonly ICollectionView _view;
    readonly DataGrid _grid;
    readonly TextBox _filter = new() { Width = 180, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(4, 3, 4, 3), ToolTip = "Filter by name, game_id or clause" };
    readonly TextBox _season = new() { Width = 50, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(4, 3, 4, 3), Text = "2023", ToolTip = "The game's current season start year (NHL 24: 2023). Contract data describes 2026-27; UFA years shift by the difference." };
    readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x8c, 0x97, 0xa8)), Margin = new Thickness(0, 6, 0, 6), TextWrapping = TextWrapping.Wrap };
    readonly TextBox _preview = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, Height = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x20)), Foreground = Brushes.Gainsboro,
    };
    Dictionary<string, byte[]>? _game;
    List<ContractRun.DbPlan>? _plans;

    public ContractsPage(SuiteSettings settings)
    {
        _settings = settings;
        Margin = new Thickness(24, 18, 24, 18);

        var top = new StackPanel();
        top.Children.Add(new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = "Every contract is editable here: cap hit and extension in dollars (NHL 24 stores $5,000 steps), seasons left, two-way, " +
                   "UFA year, retained trades, and clauses per contract season (0 = this season): NMC, NTC, M-NTC/list size, NMC+M-NTC/list size. " +
                   "Clauses stay in the suite (NHL 24 has no clause field). Apply writes all five roster databases of the CIMS game copy in place, " +
                   "Data and Patch copies, after a backup; nothing changes until you press Apply.",
        });
        var bar = new WrapPanel();
        bar.Children.Add(Btn("Load from game", () => _ = LoadGameAsync(false)));
        bar.Children.Add(Btn("Prefill from NHL 27", () => _ = PrefillAsync()));
        bar.Children.Add(Btn("Import CSV", ImportCsv));
        bar.Children.Add(Btn("Export CSV", ExportCsv));
        bar.Children.Add(Btn("Save", Save));
        bar.Children.Add(Btn("Preview changes", () => _ = PreviewAsync()));
        bar.Children.Add(Btn("Apply to game", () => _ = ApplyAsync()));
        bar.Children.Add(new TextBlock { Text = "Filter:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) });
        bar.Children.Add(_filter);
        bar.Children.Add(new TextBlock { Text = "Season:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) });
        bar.Children.Add(_season);
        top.Children.Add(bar);
        var allow = new CheckBox
        {
            Content = "Allow in-place writes to this game folder (" + _settings.ApplyTargetDir + ")", Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0),
            IsChecked = _settings.InPlaceWriteAllowed(_settings.ApplyTargetDir),
            ToolTip = "Off by default. Lets Apply write the contracts into this folder even when it is the original game. " +
                      "The running-game check, the backup, the read-back and the restore on failure still apply.",
        };
        allow.Click += (_, _) =>
        {
            var dir = Path.GetFullPath(_settings.ApplyTargetDir).TrimEnd(Path.DirectorySeparatorChar);
            _settings.InPlaceWriteAllowedDirs ??= [];
            _settings.InPlaceWriteAllowedDirs.RemoveAll(d => string.Equals(Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar), dir, StringComparison.OrdinalIgnoreCase));
            if (allow.IsChecked == true) _settings.InPlaceWriteAllowedDirs.Add(dir);
            _settings.Save();
        };
        top.Children.Add(allow);
        top.Children.Add(_status);
        SetDock(top, Dock.Top);
        Children.Add(top);

        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(new TextBlock { Text = "Preview (cells that Apply would change)", Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4) });
        bottom.Children.Add(_preview);
        SetDock(bottom, Dock.Bottom);
        Children.Add(bottom);

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = o => o is Row r && Matches(r, _filter.Text);
        _filter.TextChanged += (_, _) => _view.Refresh();
        _grid = new DataGrid
        {
            ItemsSource = _view, AutoGenerateColumns = true, CanUserAddRows = false, CanUserDeleteRows = true, FrozenColumnCount = 2,
            EnableRowVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = 22,
        };
        _grid.AutoGeneratingColumn += (_, e) => { if (e.PropertyName == nameof(Row.C)) e.Cancel = true; };
        _grid.CellEditEnding += (_, _) => _plans = null;
        var teamGrid = new DataGrid
        {
            ItemsSource = _teams, AutoGenerateColumns = true, CanUserAddRows = false, CanUserDeleteRows = false, FrozenColumnCount = 2,
            HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = 22,
            ToolTip = "Per season from this season on, comma separated: dead cap (buyouts, recapture, buried money) and retained salary in dollars, retained contracts 0-7.",
        };
        teamGrid.AutoGeneratingColumn += (_, e) => { if (e.PropertyName == nameof(TeamRow.T)) e.Cancel = true; };
        teamGrid.CellEditEnding += (_, _) => _plans = null;
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Players", Content = _grid });
        tabs.Items.Add(new TabItem { Header = "Teams: dead cap and retained", Content = teamGrid });
        Children.Add(tabs);

        foreach (var c in ContractStore.Load()) _rows.Add(new Row(c));
        foreach (var t in TeamCapStore.Load()) _teams.Add(new TeamRow(t));
        _status.Text = _rows.Count > 0 ? $"{_rows.Count} contracts from {ContractStore.UserPath}." : "No contracts yet: Load from game, then Prefill from NHL 27 or Import CSV.";
    }

    static bool Matches(Row r, string f) => string.IsNullOrWhiteSpace(f)
        || r.Player.Contains(f, StringComparison.OrdinalIgnoreCase) || r.GameId.ToString() == f.Trim() || r.Clauses.Contains(f, StringComparison.OrdinalIgnoreCase);

    static Button Btn(string text, Action a)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(10, 4, 10, 4) };
        b.Click += (_, _) => a();
        return b;
    }

    ContractMapping Map() => new(GameSeason: int.TryParse(_season.Text, out var s) && s is >= 1990 and <= 2100 ? s : 2023);

    List<Contract> Contracts() => _rows.Select(r => r.C).ToList();
    List<TeamCap> Teams() => _teams.Select(t => t.T).ToList();

    void ReplaceTeams(IEnumerable<TeamCap> list)
    {
        var names = TeamNames();
        _teams.Clear();
        foreach (var t in list.OrderBy(t => t.Team))
        {
            if (t.Name.Length == 0) t.Name = names.GetValueOrDefault(t.Team, "");
            _teams.Add(new TeamRow(t));
        }
        _plans = null;
    }

    /// <summary>Club abbreviations from NHL 27's team table (team numbers 0-31 are the same in both games).</summary>
    static Dictionary<int, string> TeamNames()
    {
        var csv = Nhl27Contracts.FindTable("exhibitionteams");
        if (csv is null) return [];
        var map = new Dictionary<int, string>();
        foreach (var r in Nhl27Contracts.ReadCsv(csv))
            if (int.TryParse(r.GetValueOrDefault("index"), out var i) && r.GetValueOrDefault("artabbr", "") is { Length: > 0 } a) map.TryAdd(i, a);
        return map;
    }

    void Replace(IEnumerable<Contract> list)
    {
        _rows.Clear();
        foreach (var c in list.OrderBy(c => c.Last).ThenBy(c => c.First)) _rows.Add(new Row(c));
        _plans = null;
    }

    /// <summary>Reads the five databases from the CIMS game copy. With keepRows the grid stays; otherwise it shows the game's contracts (clauses kept from the saved rows).</summary>
    async Task<bool> LoadGameAsync(bool keepRows)
    {
        var target = _settings.ApplyTargetDir;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target)) { _status.Text = "Set the CIMS game copy (Apply target) in Settings first."; return false; }
        _status.Text = "Reading the roster databases from " + target + "...";
        try
        {
            var map = Map();
            _game = await Task.Run(() => ContractRun.ReadGame(target, _settings));
            if (!keepRows)
            {
                var saved = Contracts().ToDictionary(c => c.GameId);
                var main = T3Db.Load((byte[])_game["nhl_game"].Clone(), ContractRun.MetaPath);
                var list = ContractDb.Read(main, map, "NHL 24 game").Where(c => c.Signed || saved.ContainsKey(c.GameId)).ToList();
                foreach (var c in list) if (saved.TryGetValue(c.GameId, out var s)) c.Clauses = s.Clauses;
                Replace(list);
                ReplaceTeams(TeamCap.Read(main, "NHL 24 game").Where(t => t.Team is >= 0 and < 32));
            }
            _status.Text = $"Read {_game.Count} databases from the game copy." + (keepRows ? "" : $" {_rows.Count} signed players shown.");
            return true;
        }
        catch (Exception e) { _status.Text = "Could not read the game: " + e.Message; return false; }
    }

    async Task PrefillAsync()
    {
        if (_game is null && !await LoadGameAsync(true)) return;
        var csv = Nhl27Contracts.DefaultCsv;
        if (!File.Exists(csv)) { _status.Text = "No NHL 27 player table at " + csv + " (export the NHL 27 database first)."; return; }
        _status.Text = "Reading NHL 27 contracts...";
        var pre = await Task.Run(() => Nhl27Contracts.Prefill(Nhl27Contracts.Load(csv), T3Db.Load((byte[])_game!["nhl_game"].Clone(), ContractRun.MetaPath)));
        var byId = Contracts().ToDictionary(c => c.GameId);
        foreach (var c in pre.Contracts) byId[c.GameId] = c;   // NHL 27 replaces the row; hand edits made after this stay
        Replace(byId.Values);
        string teamNote = "";
        if (Nhl27Contracts.FindTable(TeamCap.Table) is { } teamCsv)
        {
            var t27 = TeamCap.LoadNhl27(teamCsv).Where(t => t.Team is >= 0 and < 32).ToList();
            ReplaceTeams(t27);
            teamNote = $"Teams: dead cap and retained salary for {t27.Count} clubs from NHL 27 (season 0 = this season). ";
        }
        _status.Text = teamNote + $"NHL 27: {pre.Nhl27Signed} signed players, {pre.Matched} matched to this game ({pre.ByName} by name and birth date), " +
                       $"{pre.Unmatched.Count} not in NHL 24's database. {pre.Contracts.Count(c => c.Clauses.Count > 0)} carry clauses. Preview, then Save or Apply.";
        File.WriteAllLines(Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "contracts")), "nhl27-unmatched.txt"),
            pre.Unmatched.Select(u => $"{u.GameId}\t{u.Name}\t{u.Note}\tteam {u.Team}"));
    }

    void ImportCsv()
    {
        var dlg = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var incoming = ContractStore.FromCsv(File.ReadAllText(dlg.FileName));
            var byId = Contracts().ToDictionary(c => c.GameId);
            foreach (var c in incoming) byId[c.GameId] = c;
            Replace(byId.Values);
            _status.Text = $"Imported {incoming.Count} rows from {dlg.FileName}.";
        }
        catch (Exception e) { _status.Text = "Import failed: " + e.Message; }
    }

    void ExportCsv()
    {
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "contracts.csv" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, ContractStore.ToCsv(Contracts()));
        _status.Text = $"Exported {_rows.Count} rows to {dlg.FileName}.";
    }

    void Save()
    {
        ContractStore.Save(Contracts());
        TeamCapStore.Save(Teams());
        _status.Text = $"Saved {_rows.Count} contracts to {ContractStore.UserPath} and {_teams.Count} clubs to {TeamCapStore.UserPath}.";
    }

    async Task PreviewAsync()
    {
        if (_game is null && !await LoadGameAsync(true)) return;
        var list = Contracts(); var teams = Teams(); var map = Map();
        _status.Text = "Planning...";
        try
        {
            _plans = await Task.Run(() => ContractRun.Plan(_game!, list, map, null, teams));
            var lines = new List<string>();
            foreach (var p in _plans) lines.Add($"{p.Db}: {p.Changes.Count} cells, {p.Changes.Select(c => c.GameId).Distinct().Count()} players");
            lines.Add("");
            static bool IsTeam(ContractChange c) => c.Field.StartsWith("cbaexpense") || c.Field.StartsWith("retainedtradesalary") || c.Field.StartsWith("numretained");
            var first = _plans.FirstOrDefault()?.Changes ?? [];
            lines.AddRange(first.Where(IsTeam).Select(c => $"{c.Player,-26} {c.Field,-28} {c.Old,10} -> {c.New}"));
            lines.AddRange(first.Where(c => !IsTeam(c)).Take(2000).Select(c => $"{c.Player,-26} {c.Field,-28} {c.Old,10} -> {c.New}"));
            _preview.Text = string.Join("\n", lines);
            _status.Text = _plans.Count == 0 ? "The game already holds these contracts." : "Preview ready (first database's cells listed). Apply writes exactly this.";
        }
        catch (Exception e) { _status.Text = "Preview failed: " + e.Message; }
    }

    async Task ApplyAsync()
    {
        if (_plans is null) await PreviewAsync();
        if (_plans is null || _plans.Count == 0) return;
        var target = _settings.ApplyTargetDir;
        if (ContractRun.Refusal(target, _settings) is { } why) { _status.Text = why; return; }
        if (MessageBox.Show($"Write contracts into all {_plans.Count} changed roster databases of\n{target}?\nA backup is taken first.",
                "Contracts", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        ContractStore.Save(Contracts());
        TeamCapStore.Save(Teams());
        _status.Text = "Writing...";
        try
        {
            var plans = _plans;
            var report = await Task.Run(() => ContractRun.Write(target, _settings, plans));
            _status.Text = string.Join("  ", report);
            _game = null; _plans = null;
        }
        catch (Exception e) { _status.Text = "Apply failed (backup restored): " + e.Message; }
    }
}
