using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// The saved mod library: what is in each mod, and the buttons that write it into the game (apply) or put the
/// game back to stock (restore). Both go through <see cref="ModApply"/> and the NHL24Kit worker.
/// </summary>
public sealed class ModListWindow : Window
{
    readonly SuiteSettings _settings;
    readonly ListBox _list = new();
    readonly TextBox _details = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1a, 0x22)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xcf, 0xd8, 0xe4)),
        BorderThickness = new Thickness(0), FontFamily = new FontFamily("Consolas"), FontSize = 12,
    };
    readonly TextBlock _summary = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xa9, 0xc7, 0xf0)), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xa9, 0xc7, 0xf0)), TextWrapping = TextWrapping.Wrap };
    List<ModFile> _mods = [];

    public ModListWindow(SuiteSettings settings)
    {
        _settings = settings;
        Title = "Mod list — what will be applied";
        Width = 1060;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1a, 0x22));
        Foreground = new SolidColorBrush(Color.FromRgb(0xe6, 0xeb, 0xf2));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        Build();
        Reload();
    }

    static Button Btn(string text, RoutedEventHandler click)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x30, 0x42)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xe6, 0xeb, 0xf2)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x46, 0x5c)),
        };
        b.Click += click;
        return b;
    }

    void Build()
    {
        var root = new DockPanel { Margin = new Thickness(14) };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var refresh = Btn("Refresh", (_, _) => Reload());
        DockPanel.SetDock(refresh, Dock.Right);
        top.Children.Add(refresh);
        top.Children.Add(_summary);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in new[] { Btn("Apply replacements…", Apply_Click), Btn("Apply new uniforms…", ApplyNew_Click), Btn("Undo last game apply…", Restore_Click) })
        {
            var problems = ModApply.Problems(_settings);
            b.IsEnabled = problems.Count == 0;
            ToolTipService.SetShowOnDisabled(b, true);
            b.ToolTip = problems.Count == 0 ? $"Writes to {_settings.ApplyTargetDir} (backed up first)." : string.Join("\n", problems);
            buttons.Children.Add(b);
        }
        buttons.Children.Add(Btn("Delete selected mod", Delete_Click));
        buttons.Children.Add(Btn("Close", (_, _) => Close()));
        bottom.Children.Add(_status);
        bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _list.Background = new SolidColorBrush(Color.FromRgb(0x1b, 0x22, 0x30));
        _list.Foreground = new SolidColorBrush(Color.FromRgb(0xe6, 0xeb, 0xf2));
        _list.BorderThickness = new Thickness(0);
        _list.SelectionChanged += (_, _) => ShowDetails();
        body.Children.Add(_list);

        var detailBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1a, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x27, 0x32, 0x46)), BorderThickness = new Thickness(1),
            Child = _details,
        };
        Grid.SetColumn(detailBorder, 2);
        body.Children.Add(detailBorder);
        root.Children.Add(body);

        Content = root;
    }

    void Reload()
    {
        _mods = ModApply.LoadMods();
        _list.ItemsSource = _mods.Select(m => m.Kind == NewUniformAddition.ModKind
            ? $"{m.Name}  —  {m.Additions?.Count ?? 0} new uniform(s)"
            : $"{m.Name}  —  {m.Replacements.Count} replacement{(m.Replacements.Count == 1 ? "" : "s")}").ToList();
        int overlaps = ModApply.OverlappingEntries(_mods);
        int additions = _mods.Sum(m => m.Additions?.Count ?? 0);
        _summary.Text = _mods.Count == 0
            ? $"Mod list is empty.  ({SuitePaths.Mods})"
            : $"{_mods.Count} mod(s), {ModApply.TotalReplacements(_mods):N0} unique replacement(s), {additions} new uniform(s)" +
              (overlaps > 0 ? $"; {overlaps:N0} overlapping entr{(overlaps == 1 ? "y" : "ies")} — later listed mods win." : ".") + $"   {SuitePaths.Mods}";
        if (_mods.Count > 0)
        {
            _list.SelectedIndex = 0;
            _status.Text = "Replacements and new uniforms have separate Apply actions. New uniforms use independent assets and spare rows; neither action is performed until you confirm it.";
        }
        else
        {
            _details.Text = "";
            _status.Text = "Nothing in the mod list. Use Convert 27 to save replacements or independent new uniforms.";
        }
    }

    void ShowDetails()
    {
        var i = _list.SelectedIndex;
        if (i < 0 || i >= _mods.Count) { _details.Text = ""; return; }
        var m = _mods[i];
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Name:     {m.Name}");
        sb.AppendLine($"Kind:     {m.Kind}");
        if (!string.IsNullOrEmpty(m.Comment)) sb.AppendLine($"Comment:  {m.Comment}");
        sb.AppendLine($"Saved:    {m.Saved:g}");
        sb.AppendLine(m.Kind == NewUniformAddition.ModKind ? $"New uniforms: {m.Additions?.Count ?? 0}" : $"Entries:  {m.Replacements.Count}");
        sb.AppendLine();
        foreach (var addition in m.Additions ?? [])
        {
            sb.AppendLine($"{addition.Team} · {addition.SourceKit} → NEW variant {addition.TargetVariant} ({addition.TargetPrefix})");
            sb.AppendLine($"    {addition.Textures.Count} source → target texture mappings; no existing row or asset is replaced");
            foreach (var texture in addition.Textures)
                sb.AppendLine($"    {texture.SourceSlot}\n      → {texture.TargetSlot}");
        }
        foreach (var r in m.Replacements)
        {
            var source = r.Source.Length == 0 ? (r.Layers is null ? "(none)" : $"{r.Layers.Count} layers") : Path.GetFileName(r.Source);
            sb.AppendLine($"{r.Slot}");
            sb.AppendLine($"    → {source}{(r.Alpha is null ? "" : $"   [alpha {r.Alpha}]")}");
        }
        _details.Text = sb.ToString();
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var mods = _mods.Where(m => m.Replacements.Count > 0).ToList();
        var total = ModApply.TotalReplacements(mods);
        if (total == 0) { _status.Text = "The mod list is empty."; return; }
        var confirm = MessageBox.Show(
            $"Apply {total:N0} replacement(s) from {mods.Count} mod(s) to the game at:\n{_settings.ApplyTargetDir}\n\n" +
            "This appends new data to the game's CAS archives and repoints the TOCs. Close the game first.\n" +
            "It writes on top of everything already in the game: rosters, goalie gear, portraits and center ice stay.\n\nContinue?",
            "Apply to game", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes;
        if (confirm) return;

        IsEnabled = false;
        _status.Text = "Applying… (this can take a while; do not close the window)";
        try
        {
            var (ok, message) = await ModApply.ApplyAsync(_settings, line => Dispatcher.Invoke(() => _status.Text = line));
            _status.Text = (ok ? "Applied: " : "Apply failed: ") + message;
        }
        finally
        {
            IsEnabled = true;
            Reload();
        }
    }

    async void ApplyNew_Click(object sender, RoutedEventArgs e)
    {
        var saved = _mods.Where(m => m.Kind == NewUniformAddition.ModKind && m.Additions is { Count: > 0 }).ToList();
        int count = saved.Sum(m => m.Additions!.Count);
        if (count == 0) { _status.Text = "There are no saved new uniforms to apply."; return; }
        if (MessageBox.Show(
                $"Add {count} independent uniform(s) and their new selectable rows to:\n{_settings.ApplyTargetDir}\n\n" +
                "Every asset, ordered logo role, artwork layer and spare row is preflighted. Assets and databases share one backup and rollback. Existing uniforms are not replaced.\n\nContinue?",
                "Apply new uniforms", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var combined = new ModFile
        {
            Name = "New uniform apply", Kind = NewUniformAddition.ModKind,
            Additions = saved.SelectMany(m => m.Additions!).ToList(),
        };
        IsEnabled = false;
        _status.Text = "Preflighting new uniforms…";
        try
        {
            var (ok, message) = await NewUniformApply.ApplyAsync(_settings, combined,
                line => Dispatcher.Invoke(() => _status.Text = line));
            _status.Text = (ok ? "Applied: " : "Apply failed: ") + message;
        }
        finally
        {
            IsEnabled = true;
            Reload();
        }
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Undo the last game apply? The game goes back to the backup taken right before it. This is refused when " +
                "anything was written to the game after that apply, because that would be lost too.\n\nContinue?",
                "Undo last texture apply", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        IsEnabled = false;
        _status.Text = "Undoing the last texture apply…";
        try
        {
            var (ok, message) = await ModApply.RestoreAsync(_settings, line => Dispatcher.Invoke(() => _status.Text = line));
            _status.Text = (ok ? "Restored: " : "Restore failed: ") + message;
        }
        catch (Exception ex)
        {
            _status.Text = "Restore failed without closing the mod list: " + ex.Message;
        }
        finally
        {
            IsEnabled = true;
            Reload();
        }
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var i = _list.SelectedIndex;
        if (i < 0 || i >= _mods.Count) { _status.Text = "Select a mod first."; return; }
        var m = _mods[i];
        if (MessageBox.Show($"Delete the mod “{m.Name}”? Its file will be removed from the mod library.", "Delete mod",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var path = ModFile.PathFor(m.Name);
            if (File.Exists(path)) File.Delete(path);
            _status.Text = $"Deleted “{m.Name}”.";
            Reload();
        }
        catch (Exception ex)
        {
            _status.Text = "Could not delete: " + ex.Message;
        }
    }
}
