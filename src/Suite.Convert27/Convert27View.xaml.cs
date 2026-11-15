using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>Convert 27 page: texture matcher on the left, live GPU uniform viewer on the right.</summary>
public partial class Convert27View : UserControl
{
    readonly Convert27ViewModel _vm;
    readonly Queue<string> _pending = new();
    bool _viewerReady;

    public Convert27View(SuiteSettings settings)
    {
        InitializeComponent();
        _vm = new Convert27ViewModel(settings);
        DataContext = _vm;
        _vm.ViewerMessage += Post;
        Loaded += async (_, _) =>
        {
            if (_vm.Catalog27 is not null) return;
            await StartViewerAsync();
            await _vm.LoadAsync();
        };
    }

    public Convert27ViewModel ViewModel => _vm;

    async Task StartViewerAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(SuitePaths.Local, "webview2"));
            await Viewer.EnsureCoreWebView2Async(env);
            var core = Viewer.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("viewer.cims", Path.Combine(AppContext.BaseDirectory, "viewer"), CoreWebView2HostResourceAccessKind.Allow);
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady))
                core.SetVirtualHostNameToFolderMapping(ViewerUrl.Host(d.Name[0]), d.RootDirectory.FullName, CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDevToolsEnabled = true;
            core.WebMessageReceived += (_, e) =>
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "ready")
                {
                    _viewerReady = true;
                    SuiteLog.Write("viewer ready: " + (doc.RootElement.TryGetProperty("gpu", out var g) ? g.GetString() : ""));
                    while (_pending.Count > 0) core.PostWebMessageAsJson(_pending.Dequeue());
                }
                else if (type == "meshes")
                {
                    // Confirms the GLB skater pieces actually loaded in three.js (not just that they were exported).
                    SuiteLog.Write("viewer " + e.WebMessageAsJson);
                }
            };
            core.Navigate("https://viewer.cims/index.html");
        }
        catch (Exception ex)
        {
            ViewerError.Text = "The 3D viewer needs the Microsoft Edge WebView2 runtime: " + ex.Message;
            ViewerError.Visibility = Visibility.Visible;
        }
    }

    void Post(object message)
    {
        var json = JsonSerializer.Serialize(message);
        Dispatcher.InvokeAsync(() =>
        {
            if (_viewerReady && Viewer.CoreWebView2 is not null) Viewer.CoreWebView2.PostWebMessageAsJson(json);
            else _pending.Enqueue(json);
        });
    }

    void Repropose_Click(object sender, RoutedEventArgs e) => _vm.Repropose(keepManual: true);

    async void Convert_Click(object sender, RoutedEventArgs e) => await _vm.ConvertCurrentAsync();

    /// <summary>
    /// Picking a folder from the list commits it straight away. The editable ComboBox otherwise waits for
    /// LostFocus, so the matcher looked like it ignored the new folder until you clicked somewhere else.
    /// </summary>
    void Folder24_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Folder24Box.SelectedItem is not string folder) return;
        if (_vm.Folder24 == folder) return;
        _vm.Folder24 = folder;
        Folder24Box.Text = folder; // keep the editable text in sync so a later LostFocus push is correct
    }

    /// <summary>
    /// Picking a proposal commits its folder path. WPF's editable ComboBox otherwise shows a complex
    /// item's ToString(), which would corrupt the Folder27 binding and stop the matcher from changing folder.
    /// </summary>
    void Folder27_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Folder27Box.SelectedItem is not FolderProposal p) return;
        if (_vm.Folder27 == p.Folder27) return;
        _vm.Folder27 = p.Folder27;
        Folder27Box.Text = p.Folder27; // keep the editable text in sync so a later LostFocus push is correct
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Recompute every row in this folder, including rows you edited?", "Reset folder", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            _vm.Repropose(keepManual: false);
    }

    async void ProposeTeam_Click(object sender, RoutedEventArgs e) => await _vm.ProposeWholeTeamAsync();

    async void ProposeAll_Click(object sender, RoutedEventArgs e) => await _vm.ProposeAllTeamsAsync();

    async void CheckUniforms_Click(object sender, RoutedEventArgs e) => await _vm.CheckUniformsAsync();

    void Save_Click(object sender, RoutedEventArgs e) => _vm.Save();

    void EditVariants_Click(object sender, RoutedEventArgs e)
    {
        VariantMap.Load();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(VariantMap.UserPath) { UseShellExecute = true });
        _vm.Status = "Editing " + VariantMap.UserPath + " — restart the page to reload it.";
    }

    async void AddFolder_Click(object sender, RoutedEventArgs e) => await AddToModList(currentOnly: true);
    async void AddAll_Click(object sender, RoutedEventArgs e) => await AddToModList(currentOnly: false);
    async void AddNew_Click(object sender, RoutedEventArgs e) => await AddAsNew(currentOnly: true);
    async void AddAllNew_Click(object sender, RoutedEventArgs e) => await AddAsNew(currentOnly: false);
    async void InstallAllNew_Click(object sender, RoutedEventArgs e) => await InstallAllNew();
    async void AddToGame_Click(object sender, RoutedEventArgs e) => await InstallAllNew(currentOnly: AllTeams.IsChecked != true);

    async void Repair_Click(object sender, RoutedEventArgs e)
    {
        bool all = AllTeams.IsChecked == true;
        if (MessageBox.Show($"{(all ? "Repair every team's uniform whose slot holds an incomplete earlier install?" : $"Rebuild {Convert27ViewModel.FriendlyFolder(_vm.Folder24)} in its slot? Its old copy (complete or not) is removed and the uniform is added fresh. (Tick All teams to repair only the uniforms the check marks needs repair.)")}\n\n"
                + "The leftovers are removed and the uniform is added fresh in the same slot, with a backup first. "
                + $"If anything fails the game is put back exactly as it was.\n\nGame: {_vm.Settings.ApplyTargetDir}\nClose the game first.",
                "Repair broken kits", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        IsEnabled = false;
        _vm.Busy = true;
        try
        {
            SuiteLog.Write($"repair: clicked ({(all ? "all teams" : _vm.Folder24)})");
            var (ok, message) = await NewUniformExport.RepairAsync(_vm, currentFolderOnly: !all);
            SuiteLog.Write($"repair: finished ok={ok}: {message}");
            _vm.Status = (ok ? "Repaired: " : "Repair not done: ") + message;
        }
        catch (Exception ex)
        {
            SuiteLog.Write("repair: FAILED " + ex);
            _vm.Status = "Repair failed: " + ex.Message;
        }
        finally { _vm.Busy = false; IsEnabled = true; }
    }

    async void RepairNumberKits_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Rebuild the kits whose numbers now come from NHL 27 (the list in font_colours.json)? "
                + "One backup first; every kit is read back and checked; if anything fails the game is put back exactly as it was.\n\n"
                + $"Game: {_vm.Settings.ApplyTargetDir}\nClose the game first.",
                "Repair number-fix kits", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        IsEnabled = false;
        _vm.Busy = true;
        try
        {
            SuiteLog.Write("repair-numbers: clicked");
            var (ok, message) = await NewUniformExport.RepairNumberKitsAsync(_vm);
            SuiteLog.Write($"repair-numbers: finished ok={ok}: {message}");
            _vm.Status = (ok ? "Repaired: " : "Repair not done: ") + message;
        }
        catch (Exception ex)
        {
            SuiteLog.Write("repair-numbers: FAILED " + ex);
            _vm.Status = "Repair failed: " + ex.Message;
        }
        finally { _vm.Busy = false; IsEnabled = true; }
    }

    async void RebuildAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Rebuild every team's Fanatics home and away kit? Each kit's old copy (complete or not) is removed "
                + "and the kit is added fresh in the same slot with the current rules. One backup first; every kit is read back and "
                + "checked; if anything fails the game is put back exactly as it was.\n\n"
                + $"Game: {_vm.Settings.ApplyTargetDir}\nClose the game first. This takes about 45 minutes.",
                "Rebuild all Fanatics kits", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        IsEnabled = false;
        _vm.Busy = true;
        try
        {
            SuiteLog.Write("rebuild-all: clicked");
            var (ok, message) = await NewUniformExport.RebuildAllAsync(_vm);
            SuiteLog.Write($"rebuild-all: finished ok={ok}: {message}");
            _vm.Status = (ok ? "Rebuilt: " : "Rebuild not done: ") + message;
        }
        catch (Exception ex)
        {
            SuiteLog.Write("rebuild-all: FAILED " + ex);
            _vm.Status = "Rebuild failed: " + ex.Message;
        }
        finally { _vm.Busy = false; IsEnabled = true; }
    }

    async void UndoLast_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Restore the game to the backup taken just before the last write?", "Undo last add",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsEnabled = false;
        _vm.Busy = true;
        try
        {
            var (ok, message) = await ModApply.RestoreAsync(_vm.Settings, s => Dispatcher.Invoke(() => _vm.Status = s));
            _vm.Status = (ok ? "Restored: " : "Could not undo: ") + message;
        }
        finally { _vm.Busy = false; IsEnabled = true; }
    }
    async void ApplyNew_Click(object sender, RoutedEventArgs e) => await ApplyNewUniforms();

    /// <summary>One confirmation and one preparation: preflight, save, then install the same completed plan.</summary>
    async Task InstallAllNew(bool currentOnly = false)
    {
        IsEnabled = false;
        _vm.Busy = true;
        SuiteLog.Write(currentOnly ? $"install: Add to game clicked ({_vm.Folder24})" : "install: Add to game clicked (all teams)");
        try
        {
            var targetProblems = ModApply.Problems(_vm.Settings);
            if (targetProblems.Count > 0)
                throw new InvalidDataException("The selected build cannot be written:\n" + string.Join("\n", targetProblems));
            string name = ModName.Text.Trim();
            if (name.Length == 0 || name == "NHL 27 uniforms") name = currentOnly ? $"NHL 27 {_vm.Team} uniform" : "NHL 27 Fanatics new uniforms";
            var draft = await NewUniformExport.PlanAsync(_vm, name, currentFolderOnly: currentOnly);
            int count = draft.Prepared.AdditionPlan.Additions.Count;
            SuiteLog.Write($"install: plan ready for {count} uniforms; waiting for confirmation");
            if (MessageBox.Show(
                    $"{draft.Summary}\n\nSave and install these {count} independent uniforms to:\n{_vm.Settings.ApplyTargetDir}\n\n" +
                    "The completed plan will be used directly; preparation will not run twice. Assets and both databases share one backup and rollback. Existing uniforms are never replaced.\n\nContinue?",
                    "Add to game", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            string path = draft.Save();
            _vm.Settings.Save(); // keeps the jersey name typed on the page
            SuiteLog.Write("install: mod saved to " + path + "; starting coordinated apply");
            var (ok, message) = await NewUniformApply.ApplyPreparedAsync(_vm.Settings, draft.Prepared,
                s => Dispatcher.Invoke(() => _vm.Status = s));
            SuiteLog.Write($"install: apply finished ok={ok}: {message}");
            _vm.Status = (ok ? "Installed: " : "Install failed: ") + message + $" Saved mod: {path}";
        }
        catch (Exception ex)
        {
            SuiteLog.Write("install: FAILED " + ex);
            _vm.Status = "Could not install the new uniforms: " + ex.Message;
        }
        finally
        {
            _vm.Busy = false;
            IsEnabled = true;
        }
    }

    async Task AddAsNew(bool currentOnly)
    {
        _vm.Busy = true;
        try
        {
            string name = ModName.Text.Trim();
            if (name.Length == 0) name = currentOnly ? "NHL 27 new uniform" : "NHL 27 Fanatics 64 new uniforms";
            var draft = await NewUniformExport.PlanAsync(_vm, name, currentOnly);
            string details = draft.Details;
            var lines = details.Split('\n');
            if (lines.Length > 22) details = string.Join("\n", lines.Take(22)) + $"\n… and {lines.Length - 22} more lines";
            if (MessageBox.Show(
                    $"{draft.Summary}\n\n{details}\n\nSave this addition mod? This does not write to the game.",
                    "Add as new uniform — preflight passed", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            string path = draft.Save();
            _vm.Status = $"Saved new-uniform mod '{name}'. No game files were changed. {path}";
        }
        catch (Exception ex)
        {
            _vm.Status = "Could not add the new uniform(s): " + ex.Message;
        }
        finally { _vm.Busy = false; }
    }

    async Task ApplyNewUniforms()
    {
        var additionMods = ModApply.LoadMods().Where(m => m.Kind == NewUniformAddition.ModKind && m.Additions is { Count: > 0 }).ToList();
        int count = additionMods.Sum(m => m.Additions!.Count);
        if (count == 0) { _vm.Status = "No saved new-uniform mods. Use “Add as new uniform” first."; return; }
        var combined = new ModFile
        {
            Name = "New uniform apply",
            Kind = NewUniformAddition.ModKind,
            Additions = additionMods.SelectMany(m => m.Additions!).ToList(),
        };
        if (MessageBox.Show(
                $"Add {count} independent uniform(s) and their new selectable database rows to:\n{_vm.Settings.ApplyTargetDir}\n\n" +
                "Every source slot, target asset, semantic logo layout, artwork layer and spare row is checked before any write. " +
                "Assets and both databases share one backup and rollback. Existing uniforms are never replaced.\n\nContinue?",
                "Apply new uniforms", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _vm.Busy = true;
        try
        {
            var (ok, message) = await NewUniformApply.ApplyAsync(_vm.Settings, combined,
                s => Dispatcher.Invoke(() => _vm.Status = s));
            _vm.Status = (ok ? "Applied: " : "Apply failed: ") + message;
        }
        finally { _vm.Busy = false; }
    }

    /// <summary>Shows every saved mod with its replacements, and the apply/restore buttons.</summary>
    void ModList_Click(object sender, RoutedEventArgs e) => new ModListWindow(_vm.Settings).ShowDialog();


    void ReplacementBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Replacement texture folder (DDS / PNG)" };
        if (dlg.ShowDialog() != true) return;
        _vm.Settings.ReplacementDir = dlg.FolderName;
        _vm.Settings.Save();
        ReplacementFolder.Text = dlg.FolderName;
    }

    async void MatchFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = ReplacementFolder.Text.Trim();
        if (folder.Length == 0)
        {
            _vm.Status = "Choose a replacement folder first (Browse…), then Match from folder.";
            return;
        }
        _vm.Busy = true;
        try
        {
            await _vm.MatchFolderAsync(folder);
        }
        finally
        {
            _vm.Busy = false;
        }
    }

    /// <summary>Paints a logo onto the selected colour slot and bakes it into that slot's exported texture.</summary>
    void Stamp_Click(object sender, RoutedEventArgs e)
    {
        var row = (Grid.SelectedItem as SlotMatch)
            ?? _vm.Rows.FirstOrDefault(r => !r.IsArray && r.Garment == "jersey" && r.Role == "color" && !r.Name.IsBase)
            ?? _vm.Rows.FirstOrDefault(r => !r.IsArray && r.Role == "color");
        if (row is null) { _vm.Status = "Select a colour slot first (jersey, pant or sock colour)."; return; }
        var basePath = row.Kind == SourceKind.Nhl27 && row.Source27 is not null
            ? TextureImages.Nhl27Png(row.Source27)
            : _vm.Stock24.GetValueOrDefault(row.Slot);
        if (basePath is null || !File.Exists(basePath)) { _vm.Status = "That slot has no colour art to stamp on yet."; return; }
        try
        {
            var existing = _vm.Project.Stamps.FirstOrDefault(s => s.Slot == row.Slot);
            var w = new StampWindow(row.Slot, basePath, existing) { Owner = Window.GetWindow(this) };
            if (w.ShowDialog() == true && w.Result is { } stamp) _vm.ApplyStamp(stamp);
        }
        catch (Exception ex) { _vm.Status = "Could not open the stamp window: " + ex.Message; }
    }

    /// <summary>Writes the whole mod list into the game through the NHL24Kit worker.</summary>
    async void SlotMap_Click(object sender, RoutedEventArgs e)
    {
        Func<SlotMapEntry, IReadOnlyList<string>?> names = _ => null;
        try { names = await _vm.PhysicalSlotNamesAsync(); }
        catch (Exception ex) { _vm.Status = "Slot names fall back to the picture labels: " + ex.Message; }
        var w = new SlotMapWindow(_vm.SlotMaps, _vm.SlotMapItems(), names) { Owner = Window.GetWindow(this) };
        w.ShowDialog();
        if (w.Changed) _vm.Repropose(keepManual: true);
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var problems = ModApply.Problems(_vm.Settings);
        if (problems.Count > 0)
        {
            MessageBox.Show("Apply is not possible yet:\n\n• " + string.Join("\n• ", problems), "Apply to game", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var mods = ModApply.LoadMods().Where(m => m.Replacements.Count > 0).ToList();
        var total = ModApply.TotalReplacements(mods);
        if (total == 0)
        {
            _vm.Status = "The mod list is empty — use “Add folder to mod list” or “Add all matched folders” first.";
            return;
        }
        int overlaps = ModApply.OverlappingEntries(mods);
        if (MessageBox.Show(
                $"Apply {total:N0} unique replacement(s) from {mods.Count} mod(s) to the game at:\n{_vm.Settings.ApplyTargetDir}\n\n" +
                (overlaps > 0 ? $"{overlaps:N0} overlapping entries will be skipped; later listed mods win.\n" : "") +
                "Legacy baked jersey bases are repaired automatically. TOCs and CAS lengths are backed up first. Everything applied can be undone with “Restore stock game…” in the mod list window.\n\nContinue?",
                "Apply to game", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _vm.Busy = true;
        try
        {
            _vm.Status = "Applying the mod list…";
            var (ok, message) = await ModApply.ApplyAsync(_vm.Settings, s => Dispatcher.Invoke(() => _vm.Status = s));
            _vm.Status = (ok ? "Applied: " : "Apply failed: ") + message;
        }
        finally
        {
            _vm.Busy = false;
        }
    }

    async Task AddToModList(bool currentOnly)
    {
        try
        {
            _vm.Busy = true;
            await _vm.AddToModListAsync(ModName.Text.Trim().Length == 0 ? "NHL 27 uniforms" : ModName.Text.Trim(), currentOnly);
        }
        catch (Exception ex)
        {
            _vm.Status = "Could not save the mod: " + ex.Message;
        }
        finally
        {
            _vm.Busy = false;
        }
    }

    void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is SlotMatch row)
            foreach (var l in row.Layers)
            {
                l.PropertyChanged -= Layer_PropertyChanged;
                l.PropertyChanged += Layer_PropertyChanged;
            }
    }

    void Layer_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Grid.SelectedItem is SlotMatch row && row.Layers.Contains(sender))
        {
            row.Manual = true;
            row.Reason = "layers edited by hand";
            _vm.RefreshViewer(row);
        }
    }

    static string? DroppedImage(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(ImageIO.CanLoad)
            : null;

    void Grid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedImage(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Grid_Drop(object sender, DragEventArgs e)
    {
        var file = DroppedImage(e);
        if (file is null) return;
        var row = (e.OriginalSource as FrameworkElement)?.DataContext as SlotMatch ?? Grid.SelectedItem as SlotMatch;
        if (row is null) { _vm.Status = "Drop the image onto a slot row."; return; }
        if (row.IsArray && row.Layers.Count > 0)
        {
            _vm.Status = "This slot is a texture array: select it and drop the image on one of its layers.";
            Grid.SelectedItem = row;
            return;
        }
        _vm.SetFile(row, file);
        Grid.SelectedItem = row;
        e.Handled = true;
    }

    void Layer_Drop(object sender, DragEventArgs e)
    {
        var file = DroppedImage(e);
        if (file is null || sender is not FrameworkElement { Tag: LayerMatch layer } || Grid.SelectedItem is not SlotMatch row) return;
        _vm.SetLayerFile(row, layer, file);
        e.Handled = true;
    }
}

public sealed class PathToImageConverter : IValueConverter
{
    static readonly Dictionary<string, ImageSource> Cache = [];

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        // The parameter is the decode width in device pixels; the grid asks for 224 so its 108px thumbnails stay sharp.
        int decode = int.TryParse(parameter as string, out var n) && n > 0 ? n : 320;
        var key = $"{path}|{decode}|{File.GetLastWriteTimeUtc(path).Ticks}";
        if (Cache.TryGetValue(key, out var img)) return img;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = decode;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            if (Cache.Count > 4000) Cache.Clear();
            Cache[key] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ConfidenceToBrushConverter : IValueConverter
{
    static readonly Brush High = Frozen(0x5f, 0xc9, 0x7a), Mid = Frozen(0xf0, 0xc0, 0x4c), Low = Frozen(0xe0, 0x6c, 0x6c), None = Frozen(0x5a, 0x63, 0x72);

    static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? d <= 0 ? None : d >= 0.8 ? High : d >= 0.5 ? Mid : Low : None;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

public sealed class FolderDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Convert27ViewModel.FriendlyFolder(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class TitleCaseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } text ? char.ToUpperInvariant(text[0]) + text[1..] : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class PreflightSeverityToBrushConverter : IValueConverter
{
    static readonly Brush Ready = Frozen(0x27, 0x78, 0x50), Repaired = Frozen(0x35, 0x68, 0xa4),
        Warning = Frozen(0x9a, 0x6a, 0x24), Error = Frozen(0xa4, 0x42, 0x4b);

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        PreflightSeverity.Ready => Ready,
        PreflightSeverity.Repaired => Repaired,
        PreflightSeverity.Warning => Warning,
        _ => Error,
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
