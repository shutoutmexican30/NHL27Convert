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
                if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "ready")
                {
                    _viewerReady = true;
                    SuiteLog.Write("viewer ready: " + (doc.RootElement.TryGetProperty("gpu", out var g) ? g.GetString() : ""));
                    while (_pending.Count > 0) core.PostWebMessageAsJson(_pending.Dequeue());
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

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Recompute every row in this folder, including rows you edited?", "Reset folder", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            _vm.Repropose(keepManual: false);
    }

    async void ProposeTeam_Click(object sender, RoutedEventArgs e) => await _vm.ProposeWholeTeamAsync();

    void Save_Click(object sender, RoutedEventArgs e) => _vm.Save();

    async void AddFolder_Click(object sender, RoutedEventArgs e) => await AddToModList(currentOnly: true);
    async void AddAll_Click(object sender, RoutedEventArgs e) => await AddToModList(currentOnly: false);

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
            _vm.UpdateThumbs(row);
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
        var key = path + File.GetLastWriteTimeUtc(path).Ticks;
        if (Cache.TryGetValue(key, out var img)) return img;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 96;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
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
