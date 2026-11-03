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
    void SlotMap_Click(object sender, RoutedEventArgs e)
    {
        var w = new SlotMapWindow(_vm.SlotMaps, _vm.SlotMapItems()) { Owner = Window.GetWindow(this) };
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
        if (MessageBox.Show(
                $"Apply {total:N0} replacement(s) from {mods.Count} mod(s) to the game at:\n{_vm.Settings.ApplyTargetDir}\n\n" +
                "TOCs and CAS lengths are backed up first. Everything applied can be undone with “Restore stock game…” in the mod list window.\n\nContinue?",
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
