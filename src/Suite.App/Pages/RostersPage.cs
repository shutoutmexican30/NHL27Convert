using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Suite.Core;
using Suite.Rosters;

namespace Suite.App.Pages;

/// <summary>
/// Teams &amp; roster: one button syncs every roster database in NHL 24 with today's NHL API rosters. The databases are
/// read from the game, changed on copies, checked (layout guard, read-back, cross-database agreement) and shown per
/// database; "Add to mod list" makes a mod that Apply writes to the CIMS game copy. Nothing touches a game before that.
/// </summary>
public sealed class RostersPage : ScrollViewer
{
    readonly SuiteSettings _settings;
    readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x8c, 0x97, 0xa8)), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly TextBox _teams = new() { Width = 160, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(4, 3, 4, 3), FontFamily = new FontFamily("Consolas"), ToolTip = "Optional, e.g. BOS or BOS,TOR: only players joining or leaving these clubs are synced (a trial run)." };
    readonly Button _sync, _add, _saves, _apply;
    readonly StackPanel _results = new() { Margin = new Thickness(0, 10, 0, 0) };
    SyncRunResult? _last;
    CancellationTokenSource? _cts;

    public RostersPage(SuiteSettings settings)
    {
        _settings = settings;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var root = new StackPanel { Margin = new Thickness(32, 26, 32, 26), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };
        Content = root;

        root.Children.Add(Heading("Sync rosters with the NHL"));
        root.Children.Add(Text(
            "Puts every player on today's NHL rosters on his team, with his number and position, in every roster database in " +
            "NHL 24: nhl_game (play now, franchise, seasons), initial_experience, entry_draft, expansion_draft and threes_campaign. " +
            "Players no longer on an NHL roster go to their club's AHL team when the club still lists them as a prospect, " +
            "otherwise to free agency; injured players stay with their club. Lines and letters are repaired on every team that changed. " +
            "Each database keeps NHL 24's own layout (same size, same tables, same slots) — the check below refuses anything else. " +
            "A roster you saved in game in the CIMS copy (the sixth location) is synced too and written back with Write saved roster " +
            "(backed up first; the original NHL24PC and its saves are never touched)."));

        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        _sync = Button("Sync rosters", () => _ = SyncAsync());
        _add = Button("Add to mod list", AddToMods);
        _add.IsEnabled = false;
        _apply = Button("Apply to CIMS copy", ApplyRosters);
        _apply.IsEnabled = false;
        _saves = Button("Write saved roster", WriteSaves);
        _saves.IsEnabled = false;
        row.Children.Add(_sync);
        row.Children.Add(new TextBlock { Text = "Only clubs:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) });
        row.Children.Add(_teams);
        row.Children.Add(_add);
        row.Children.Add(_apply);
        row.Children.Add(_saves);
        row.Children.Add(Button("Open work folder", () => Process.Start("explorer.exe", RosterDbJob.WorkDir)));
        root.Children.Add(row);
        root.Children.Add(_status);
        root.Children.Add(_results);
        root.Children.Add(Text("Team map (NHL API abbreviation → NHL 24 team): data\\nhlapi_teams.tsv, hand-editable. " +
            "Portraits from the NHL API and the saved-roster copy are the next steps of this button."));

        root.Children.Add(Heading("Goalie gear"));
        root.Children.Add(Text("Every goalie on an NHL roster: NHL 27's gear colours (or his team's), his current team's mask when his gear " +
            "still names an old team, and readable brand lettering (TRUE on red pads). All five databases, in place. " +
            "Preview only shows what would change; Write needs NHL 24 closed and backs up first."));
        var goalies = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        goalies.Children.Add(Button("Preview goalie changes", () => _ = GoaliesAsync(false)));
        goalies.Children.Add(Button("Write goalie gear to game", () => _ = GoaliesAsync(true)));
        root.Children.Add(goalies);
        root.Children.Add(_goalieStatus);
    }

    readonly TextBlock _goalieStatus = new() { Foreground = new SolidColorBrush(Color.FromRgb(0x8c, 0x97, 0xa8)), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") };

    const string Nhl27Csv = @"C:\Users\devin\Documents\Codex\2026-10-20\where-are-the-imported-rosters-located\RosterConversion\nhl27-csv";

    /// <summary>Runs a built tool (tools\&lt;name&gt;\bin\Release\net8.0) and returns its exit code and output; null when not built.</summary>
    static async Task<(int Code, string Output)?> RunToolAsync(string name, params string[] args)
    {
        var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", name, "bin", "Release", "net8.0", name + ".exe"));
        if (!File.Exists(exe)) return null;
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return await Task.Run(() => { using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(); return (p.ExitCode, o); });
    }

    static string LastLines(string output, int n) => string.Join("\n", output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(n));

    /// <summary>Runs tools\GoalieFix (a dry run, or --apply on the game folder from Settings) and shows its summary.</summary>
    async Task GoaliesAsync(bool write)
    {
        if (write && MessageBox.Show($"Write goalie gear into {_settings.ApplyTargetDir}?\nClose NHL 24 first; a backup is taken.", "Goalie gear",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _goalieStatus.Text = write ? "Writing..." : "Checking...";
        var run = await RunToolAsync("GoalieFix", write ? ["--target", _settings.ApplyTargetDir, "--nhl27", Nhl27Csv, "--apply"] : ["--target", _settings.ApplyTargetDir, "--nhl27", Nhl27Csv]);
        if (run is not { } r) { _goalieStatus.Text = @"GoalieFix is not built: dotnet build tools\GoalieFix -c Release"; return; }
        var review = Path.Combine(SuitePaths.Local, "goaliefix", "review.txt");
        _goalieStatus.Text = LastLines(r.Output, 8) + (File.Exists(review) ? $"\nFull list: {review}" : "");
    }

    /// <summary>
    /// The in-place database edits that a whole-database roster write replaces (a sync always starts from NHL 24's own
    /// databases): goalie gear, the extra jersey rows, portrait slots.  Run, in that order, right after the roster apply,
    /// so applying new rosters never takes them back out.  Each takes its own backup and reads its write back.
    /// </summary>
    async Task<string> ReapplyDatabaseFixesAsync()
    {
        var target = _settings.ApplyTargetDir;
        var steps = new (string Tool, string What, string[] Args)[]
        {
            ("GoalieFix", "goalie gear", ["--target", target, "--nhl27", Nhl27Csv, "--apply"]),
            ("JerseyRows", "extra jersey slots", ["--target", target, "--apply"]),
            ("PortraitFix", "portrait slots", ["--target", target, "--apply"]),
        };
        var report = new List<string>();
        foreach (var (tool, what, args) in steps)
        {
            _status.Text = $"Putting the {what} back...";
            var run = await RunToolAsync(tool, args);
            report.Add(run is not { } r ? $"{what}: NOT RE-APPLIED ({tool} is not built: dotnet build tools\\{tool} -c Release)"
                : r.Code != 0 ? $"{what}: FAILED - {LastLines(r.Output, 2)}"
                : $"{what}: {LastLines(r.Output, 1)}");
        }
        return string.Join("\n", report);
    }

    static string TeamsFile => Path.Combine(AppContext.BaseDirectory, "data", "nhlapi_teams.tsv");

    async Task SyncAsync()
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        _cts = new CancellationTokenSource();
        _sync.Content = "Cancel";
        _add.IsEnabled = false;
        _saves.IsEnabled = false;
        _apply.IsEnabled = false;
        _results.Children.Clear();
        _last = null;
        try
        {
            if (!File.Exists(TeamsFile)) throw new FileNotFoundException("Team map not found: " + TeamsFile);
            var only = _teams.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            void Progress(string s) => Dispatcher.BeginInvoke(() => _status.Text = s);
            var result = await Task.Run(async () =>
            {
                var dbs = await RosterSyncRun.ExportStockAsync(_settings, Progress, _cts.Token);
                return await RosterSyncRun.RunAsync(_settings, dbs, RosterSync.LoadTeamMap(TeamsFile), only.Count > 0 ? only : null, Progress, _cts.Token, FindSaves());
            });
            _last = result;
            Show(result);
            _add.IsEnabled = result.Ok;
            _saves.IsEnabled = result.Ok && result.Dbs.Any(d => d.Save is not null);
            _status.Text = result.Ok
                ? $"All {result.Dbs.Count} databases synced and checked (NHL API {result.Day:yyyy-MM-dd}). Review below, then Add to mod list."
                : "The sync did not pass every check; nothing can be added. See the problems below.";
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled."; }
        catch (Exception ex)
        {
            SuiteLog.Write("roster sync: " + ex);
            _status.Text = "Sync failed: " + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _sync.Content = "Sync rosters";
        }
    }

    void Show(SyncRunResult r)
    {
        foreach (var note in r.Notes.Take(40)) _results.Children.Add(Text("• " + note));
        foreach (var d in r.Dbs)
        {
            var counts = string.Join(", ", d.Report.Counts.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"));
            var header = $"{(d.Ok ? "✓" : "✗")} {d.Db.Name} — {counts}; layout: {d.Guard}; read back: {(d.Problems.Count == 0 ? "OK" : d.Problems.Count + " problem(s)")}";
            var lines = d.Problems.Select(p => "problem\t" + p)
                .Concat(d.Report.Warnings.Select(w => "warning\t" + w))
                .Concat(d.Report.Changes.OrderBy(c => c, StringComparer.Ordinal))
                .ToList();
            _results.Children.Add(new Expander
            {
                Header = header, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 0),
                Content = new TextBox
                {
                    Text = string.Join("\n", lines), IsReadOnly = true, FontFamily = new FontFamily("Consolas"), MaxHeight = 360,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
            });
        }
        _results.Children.Add(Text(r.Disagreements.Count == 0
            ? "Cross-database check: every NHL player has the same team, number and position in every database that has him."
            : $"Cross-database check: {r.Disagreements.Count} player(s) disagree:\n" + string.Join("\n", r.Disagreements.Take(30))));
    }

    /// <summary>Roster saves in the CIMS game copy only (ApplyTargetDir's user folder), never the original game's.</summary>
    List<SavedRoster> FindSaves() =>
        string.IsNullOrEmpty(_settings.ApplyTargetDir) ? [] : SavedRoster.SavedataRoots(_settings.ApplyTargetDir).SelectMany(SavedRoster.Find).ToList();

    void WriteSaves()
    {
        if (_last is null) return;
        try
        {
            var done = RosterSyncRun.WriteSaves(_last);
            _status.Text = $"Wrote {done.Count} saved roster(s): {string.Join(", ", done)}. Backups: {Path.Combine(SuitePaths.Local, "save-backups")}.";
            _saves.IsEnabled = false;
        }
        catch (Exception ex) { _status.Text = "Saved roster not written: " + ex.Message; }
    }

    ModFile? _mod;

    /// <summary>Writes the synced databases into the CIMS game copy with the suite's own writer (backup + read-back).</summary>
    void ApplyRosters()
    {
        if (_mod?.Legacy is not { Count: > 0 } legacy) return;
        _apply.IsEnabled = false;
        _status.Text = "Applying to " + _settings.ApplyTargetDir + "...";
        Task.Run(() => Suite.Core.Frostbite.LegacyApply.Apply(_settings, legacy, s => Dispatcher.BeginInvoke(() => _status.Text = s)))
            .ContinueWith(t => Dispatcher.BeginInvoke(async () =>
            {
                var (ok, message, _) = t.IsFaulted ? (false, t.Exception!.GetBaseException().Message, null) : t.Result;
                _apply.IsEnabled = !ok;
                if (!ok) { _status.Text = "Not applied. " + message; return; }
                // The new databases came from NHL 24's own: put the in-place fixes back on top of them.
                var fixes = await ReapplyDatabaseFixesAsync();
                _status.Text = "Applied. " + message + "\n" + fixes;
            }));
    }

    void AddToMods()
    {
        if (_last is null) return;
        try
        {
            var mod = RosterSyncRun.ToModFile(_last);
            var path = mod.Save();
            _status.Text = $"Added \"{mod.Name}\" ({mod.Legacy!.Count} databases) to the mod list: {path}. Apply to CIMS copy writes it into the game (backup first)." +
                (_last.Dbs.Any(d => d.Save is not null) ? " Write saved roster updates your saved roster." : " No saved roster in the CIMS copy yet.");
            _add.IsEnabled = false;
            _apply.IsEnabled = true;
            _mod = mod;
        }
        catch (Exception ex) { _status.Text = "Not added: " + ex.Message; }
    }

    static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 14, 0, 0) };

    static TextBlock Text(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xb9, 0xc8, 0xd8)), LineHeight = 20, Margin = new Thickness(0, 6, 0, 0),
    };

    static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources["Action"], Margin = new Thickness(8, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }
}
