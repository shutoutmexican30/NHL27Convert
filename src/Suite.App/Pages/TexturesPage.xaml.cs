using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Suite.Convert27;
using Suite.Core;

namespace Suite.App.Pages;

/// <summary>
/// Texture browser: every folder of either game, its textures with previews, and a replacement picker that
/// writes into the mod library. Nothing here touches the game — it reads the index and the cached decodes only.
/// </summary>
public partial class TexturesPage : UserControl
{
    readonly SuiteSettings _settings;
    readonly TextureImages _images;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly Dictionary<string, string> _replacements = [];
    List<string> _folders24 = [];
    List<string> _folders27 = [];
    ObservableCollection<TextureRow> _rows = [];
    TextureRow? _selected;
    AssetIndex? _index24, _index27;
    bool _ready, _suppressFolder;
    int _loadToken;

    public TexturesPage(SuiteSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _images = new TextureImages(settings);
        _debounce.Tick += (_, _) => { _debounce.Stop(); RefillFolders(); };
        Loaded += async (_, _) => await LoadAsync();
    }

    bool Game24 => Game.SelectedIndex != 1;

    async Task LoadAsync()
    {
        if (_ready) return;
        Busy(true);
        try
        {
            Status.Text = "Reading the NHL 24 asset index...";
            _index24 = await IndexStore.GetAsync(IndexStore.Nhl24, _settings, progress: s => Status.Text = s);
            Status.Text = "Reading the NHL 27 asset index...";
            _index27 = await IndexStore.GetAsync(IndexStore.Nhl27, _settings, progress: s => Status.Text = s);
            _folders24 = _index24.Folders.Order(StringComparer.Ordinal).ToList();
            _folders27 = _index27.Folders.Order(StringComparer.Ordinal).ToList();
            _ready = true;
            Status.Text = $"{_index24.Entries.Count:N0} NHL 24 textures in {_folders24.Count:N0} folders · " +
                          $"{_index27.Entries.Count:N0} NHL 27 textures in {_folders27.Count:N0} folders";
            RefillFolders();
        }
        catch (Exception ex)
        {
            Status.Text = "Could not read the game indexes: " + ex.Message;
            SuiteLog.Write("textures page: " + ex);
        }
        finally
        {
            Busy(false);
        }
    }

    void Busy(bool on)
    {
        Progress.IsIndeterminate = on;
        Progress.Visibility = on ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>Progress callbacks come from the decode's worker threads, so marshal them onto the UI thread.</summary>
    void SetStatus(string text) => Dispatcher.Invoke(() => Status.Text = text);

    void Game_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) RefillFolders();
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _debounce.Stop();
        _debounce.Start();
    }

    void RefillFolders()
    {
        var all = Game24 ? _folders24 : _folders27;
        var q = Search.Text.Trim();
        var matches = q.Length == 0 ? all : all.Where(f => f.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _suppressFolder = true;
        try
        {
            Folder.ItemsSource = matches.Take(800).ToList();
            Folder.SelectedIndex = matches.Count > 0 ? 0 : -1;
        }
        finally
        {
            _suppressFolder = false;
        }
        if (matches.Count == 0)
        {
            _rows = [];
            CardList.ItemsSource = _rows;
            Status.Text = $"No folder matches “{q}”.";
            return;
        }
        Status.Text = $"{matches.Count:N0} folders{(q.Length == 0 ? "" : $" matching “{q}”")}" +
                      (matches.Count > 800 ? " (showing the first 800 — narrow the filter)" : "");
        _ = LoadFolderAsync(matches[0]);
    }

    async void Folder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFolder) return;
        if (Folder.SelectedItem is string folder) await LoadFolderAsync(folder);
    }

    async Task LoadFolderAsync(string folder)
    {
        if (_index24 is null || _index27 is null) return;
        var token = ++_loadToken;
        Busy(true);
        try
        {
            var rows = new ObservableCollection<TextureRow>();
            if (Game24)
            {
                foreach (var entry in _index24.InFolder(folder).OrderBy(e => e.Path, StringComparer.Ordinal))
                    rows.Add(new TextureRow { Slot = entry.Path, Sub = Describe(entry) });
                Status.Text = $"{folder}: {rows.Count} textures — decoding…";
                var pngs = await _images.Nhl24FolderAsync(folder, SetStatus);
                if (token != _loadToken) return;
                foreach (var r in rows)
                    if (pngs.TryGetValue(r.Slot, out var png) || pngs.TryGetValue(r.Slot + "#0", out png)) r.Preview = png;
            }
            else
            {
                var entries = _index27.InFolder(folder).OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
                foreach (var entry in entries) rows.Add(new TextureRow { Slot = entry.Path, Sub = Describe(entry) });
                Status.Text = $"{folder}: {rows.Count} textures — decoding…";
                var pngs = await _images.Nhl27Async(entries.Select(e => new Nhl27Texture(e)), SetStatus);
                if (token != _loadToken) return;
                foreach (var r in rows) if (pngs.TryGetValue(r.Slot, out var png)) r.Preview = png;
            }
            foreach (var r in rows) // put back anything already chosen for these slots this session
                if (_replacements.TryGetValue(r.Slot, out var existing)) r.Replacement = existing;
            _rows = rows;
            CardList.ItemsSource = _rows;
            _selected = null;
            Selection.Text = "(none)";
            Status.Text = $"{folder}: {rows.Count} textures";
        }
        catch (Exception ex)
        {
            Status.Text = $"Could not open {folder}: {ex.Message}";
            SuiteLog.Write($"textures page: {ex}");
        }
        finally
        {
            if (token == _loadToken) Busy(false);
        }
    }

    static string Describe(AssetEntry e) =>
        e.Width > 0 ? $"{e.Width}×{e.Height} · {e.Format}" : (e.Kind == AssetKind.TextureArray ? "texture array" : "texture");

    void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TextureRow row }) return;
        _selected = row;
        Selection.Text = row.Slot;
        Status.Text = $"Selected {row.Leaf} — “Choose replacement…” picks a file for this slot.";
    }

    void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            Status.Text = "Click a texture in the grid first, then choose its replacement.";
            return;
        }
        var dlg = new OpenFileDialog
        {
            Title = $"Replacement for {_selected.Leaf}",
            Filter = "Textures (*.dds;*.png;*.tga;*.jpg;*.bmp)|*.dds;*.png;*.tga;*.jpg;*.bmp|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        _selected.Replacement = dlg.FileName;
        _replacements[_selected.Slot] = dlg.FileName;
        Status.Text = $"{_selected.Leaf}  →  {Path.GetFileName(dlg.FileName)}";
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) { Status.Text = "Click a texture first."; return; }
        _selected.Replacement = null;
        _replacements.Remove(_selected.Slot);
        Status.Text = $"{_selected.Leaf}: replacement cleared.";
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.Replacement is not null).ToList();
        if (chosen.Count == 0)
        {
            Status.Text = "Nothing to save yet — pick a texture, then Choose replacement…";
            return;
        }
        try
        {
            var name = ModName.Text.Trim();
            var mod = new ModFile
            {
                Name = name.Length == 0 ? "Texture replacements" : name,
                Kind = "textures",
                Comment = "Made by the texture browser.",
            };
            foreach (var r in chosen) mod.Replacements.Add(new TextureReplacement(r.Slot, r.Replacement!));
            var path = mod.Save();
            Status.Text = $"Saved {chosen.Count} replacement(s) to {path}. Nothing is written to the game.";
        }
        catch (Exception ex)
        {
            Status.Text = "Could not save the mod: " + ex.Message;
        }
    }

    void ShowReplacements_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.Replacement is not null).ToList();
        if (chosen.Count == 0)
        {
            Status.Text = "No replacements chosen in this folder.";
            return;
        }
        var text = string.Join(Environment.NewLine, chosen.Take(25).Select(r => $"{r.Leaf}  →  {Path.GetFileName(r.Replacement!)}"));
        if (chosen.Count > 25) text += $"{Environment.NewLine}… and {chosen.Count - 25} more";
        MessageBox.Show(text, $"{chosen.Count} replacement(s) in this folder", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

/// <summary>One texture card in the browser.</summary>
public sealed class TextureRow : Observable
{
    public required string Slot { get; init; }
    public string Leaf => Slot[(Slot.LastIndexOf('/') + 1)..];

    string _sub = "";
    public string Sub { get => _sub; set => Set(ref _sub, value); }

    string? _preview;
    public string? Preview { get => _preview; set => Set(ref _preview, value); }

    string? _replacement;
    public string? Replacement
    {
        get => _replacement;
        set { if (Set(ref _replacement, value)) Raise(nameof(ReplaceText)); }
    }

    public string ReplaceText => _replacement is null ? "" : "✓ " + Path.GetFileName(_replacement);
}
