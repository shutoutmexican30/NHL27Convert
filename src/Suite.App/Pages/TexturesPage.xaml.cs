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
/// The texture suite: every texture of the game in one place, organized like FMT. Left: the game's folder
/// tree (lazy, with texture counts). Right: the selected folder's textures as preview cards, or — while the
/// search box has text — every matching texture of the game. Replacement picks write into the mod library;
/// Apply writes the mod list into the game. Nothing here touches the game on browse — reads use the index
/// and the cached decodes, export reads the game read-only.
/// </summary>
public partial class TexturesPage : UserControl
{
    readonly SuiteSettings _settings;
    readonly TextureImages _images;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly Dictionary<string, string> _replacements = [];
    List<string> _folders24 = [];
    List<string> _folders27 = [];
    ObservableCollection<TextureRow> _rows = [];
    TextureRow? _selected;
    AssetIndex? _index24, _index27;
    FolderNode? _tree24, _tree27;
    string _currentFolder = "";
    bool _ready, _suppressTree, _searchMode;
    int _loadToken;

    public TexturesPage(SuiteSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _images = new TextureImages(settings);
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(); };
        Loaded += async (_, _) => await LoadAsync();
    }

    bool Game24 => Game.SelectedIndex != 1;

    AssetIndex? CurIndex => Game24 ? _index24 : _index27;

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
            _tree24 = BuildTree(_index24, _folders24);
            _tree27 = BuildTree(_index27, _folders27);
            _ready = true;
            Status.Text = $"{_index24.Entries.Count:N0} NHL 24 textures in {_folders24.Count:N0} folders · " +
                          $"{_index27.Entries.Count:N0} NHL 27 textures in {_folders27.Count:N0} folders — pick a folder, or search.";
            FillTree();
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

    // ---------------------------------------------------------------- folder tree

    sealed class FolderNode(string name, string path)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public int Count { get; set; }                 // textures in this exact folder
        public int Total { get; set; }                 // textures in this subtree
        public Dictionary<string, FolderNode> Children { get; } = new(StringComparer.Ordinal);
    }

    static FolderNode BuildTree(AssetIndex index, List<string> folders)
    {
        var root = new FolderNode("", "");
        foreach (var folder in folders)
        {
            var count = index.InFolder(folder).Count;
            var node = root;
            node.Total += count;
            var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var path = "";
            for (int i = 0; i < parts.Length; i++)
            {
                path = path.Length == 0 ? parts[i] : path + "/" + parts[i];
                if (!node.Children.TryGetValue(parts[i], out var child))
                    node.Children[parts[i]] = child = new FolderNode(parts[i], path);
                child.Total += count;
                node = child;
            }
            node.Count = count;
        }
        return root;
    }

    void FillTree()
    {
        _suppressTree = true;
        TreeViewItem? firstItem = null;
        try
        {
            Tree.Items.Clear();
            var root = Game24 ? _tree24 : _tree27;
            if (root is null) return;
            foreach (var child in root.Children.Values.OrderByDescending(c => c.Total).ThenBy(c => c.Name, StringComparer.Ordinal))
                Tree.Items.Add(ItemFor(child));
            if (Tree.Items.Count == 0) return;
            // Open the first folder that actually holds textures, expanding the way down.
            TreeViewItem item = (TreeViewItem)Tree.Items[0];
            while (item.Tag is FolderNode { Count: 0, Children.Count: > 0 })
            {
                item.IsExpanded = true;   // populates the child items
                item = (TreeViewItem)item.Items[0];
            }
            firstItem = item;
            firstItem.IsSelected = true;
        }
        finally { _suppressTree = false; }
        if (firstItem?.Tag is FolderNode first)
            _ = LoadFolderAsync(first.Path);
    }

    static TreeViewItem ItemFor(FolderNode node)
    {
        var item = new TreeViewItem { Header = $"{node.Name}  ({node.Total:N0})", Tag = node };
        if (node.Children.Count > 0) item.Items.Add("…");   // children are populated on first expand
        return item;
    }

    void Tree_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem { Tag: FolderNode node } item) return;
        if (item.Items.Count == 1 && item.Items[0] is string) item.Items.Clear();
        if (item.Items.Count > 0) return;
        foreach (var child in node.Children.Values.OrderByDescending(c => c.Total).ThenBy(c => c.Name, StringComparer.Ordinal))
            item.Items.Add(ItemFor(child));
    }

    async void Tree_Changed(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTree || e.NewValue is not TreeViewItem { Tag: FolderNode node }) return;
        _searchMode = false;
        Mode.Text = "";
        await LoadFolderAsync(node.Path);
    }

    FolderNode? FindNode(string path)
    {
        var root = Game24 ? _tree24 : _tree27;
        if (root is null) return null;
        var node = root;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (!node.Children.TryGetValue(part, out var child)) return null;
            else node = child;
        return node;
    }

    // ---------------------------------------------------------------- folder view & search

    async Task LoadFolderAsync(string folder)
    {
        if (CurIndex is null) return;
        _currentFolder = folder;
        var token = ++_loadToken;
        Busy(true);
        try
        {
            var rows = new ObservableCollection<TextureRow>();
            foreach (var entry in CurIndex.InFolder(folder).OrderBy(e => e.Path, StringComparer.Ordinal))
                rows.Add(new TextureRow { Slot = entry.Path, Sub = Describe(entry) });
            await FillPreviewsAsync(rows, token, $"{folder}: {rows.Count} textures");
            if (token != _loadToken) return;
            MarkLegacySingles(rows);
            foreach (var r in rows) // put back anything already chosen for these slots this session
                if (_replacements.TryGetValue(r.Slot, out var existing)) r.Replacement = existing;
            _rows = rows;
            CardList.ItemsSource = _rows;
            _selected = null;
            Selection.Text = "(none)";
            var node = FindNode(folder);
            Status.Text = rows.Count > 0
                ? $"{folder}: {rows.Count} textures"
                : $"{folder}: no textures directly here — expand it for {node?.Total ?? 0:N0} in its subfolders.";
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

    /// <summary>Decode previews for the rows (from the on-disk PNG cache, decoding missing folders as needed).</summary>
    async Task FillPreviewsAsync(ObservableCollection<TextureRow> rows, int token, string what)
    {
        if (rows.Count == 0) return;
        if (Game24)
        {
            Status.Text = $"{what} — decoding…";
            var pngs = await _images.Nhl24NamesAsync(rows.Select(r => r.Slot), SetStatus);
            if (token != _loadToken) return;
            foreach (var r in rows)
                if (pngs.TryGetValue(r.Slot + TextureImages.SheetSuffix, out var png) || pngs.TryGetValue(r.Slot, out png) || pngs.TryGetValue(r.Slot + "#0", out png))
                    r.Preview = png;
        }
        else
        {
            var entries = rows.Select(r => CurIndex!.Get(r.Slot)).Where(e => e is not null).Cast<AssetEntry>().ToList();
            Status.Text = $"{what} — decoding…";
            var pngs = await _images.Nhl27Async(entries.Select(e => new Nhl27Texture(e)), SetStatus);
            if (token != _loadToken) return;
            foreach (var r in rows) if (pngs.TryGetValue(r.Slot, out var png)) r.Preview = png;
        }
    }

    void Game_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Search.Text = "";
        _searchMode = false;
        Mode.Text = "";
        FillTree();
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _debounce.Stop();
        _debounce.Start();
    }

    async void RunSearch()
    {
        var q = Search.Text.Trim();
        if (q.Length == 0)
        {
            if (_searchMode) { _searchMode = false; Mode.Text = ""; await LoadFolderAsync(_currentFolder); }
            return;
        }
        if (CurIndex is null) return;
        var token = ++_loadToken;
        Busy(true);
        try
        {
            var entries = CurIndex.Query(q).Take(400).ToList();
            var rows = new ObservableCollection<TextureRow>(entries.Select(e => new TextureRow
            {
                Slot = e.Path,
                Sub = $"{e.Name.Folder} · {Describe(e)}",
            }));
            _searchMode = true;
            Mode.Text = $"search: {entries.Count:N0} textures{(entries.Count == 400 ? " (first 400 — narrow the search)" : "")}";
            await FillPreviewsAsync(rows, token, Mode.Text);
            if (token != _loadToken) return;
            MarkLegacySingles(rows);
            foreach (var r in rows)
                if (_replacements.TryGetValue(r.Slot, out var existing)) r.Replacement = existing;
            _rows = rows;
            CardList.ItemsSource = _rows;
            _selected = null;
            Selection.Text = "(none)";
            Status.Text = Mode.Text;
        }
        catch (Exception ex)
        {
            Status.Text = "Search failed: " + ex.Message;
        }
        finally
        {
            if (token == _loadToken) Busy(false);
        }
    }

    /// <summary>Jersey branding families ship legacy per-index singles (…_0_color, _1_color…) alongside the live
    /// array texture (…_color_array) the game actually reads. In the stock data every single's chunk points at a
    /// shared default (Anaheim — alphabetically first), so their previews are identical and wrong for every team.
    /// Blank the misleading preview and say where the live art lives.</summary>
    static void MarkLegacySingles(ObservableCollection<TextureRow> rows)
    {
        var live = rows.Where(r => r.Slot.EndsWith("_array", StringComparison.Ordinal)).Select(r => r.Slot).ToHashSet(StringComparer.Ordinal);
        if (live.Count == 0) return;
        foreach (var r in rows)
        {
            var m = System.Text.RegularExpressions.Regex.Match(r.Slot, @"^(.*)_\d+_(color|coeff|normal)$");
            if (!m.Success) continue;
            if (!live.Contains(m.Groups[1].Value + "_color_array") && !live.Contains(m.Groups[1].Value + "_coeff_array")) continue;
            r.Sub += "  · legacy single (stock points it at a shared default; the game reads the *_array)";
        }
    }

    static string Describe(AssetEntry e) =>
        e.Width > 0 ? $"{e.Width}×{e.Height} · {e.Format}" : (e.Kind == AssetKind.TextureArray ? "texture array" : "texture");

    void Card_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { Tag: TextureRow row }) return;
        new TexturePreviewWindow(row.Slot, row.Preview, Game24) { Owner = Window.GetWindow(this) }.Show();
        e.Handled = true;
    }

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
        int? layer = int.TryParse(Layer.Text.Trim(), out var l) && l >= 0 ? l : null;
        _selected.Replacement = dlg.FileName;
        _selected.ReplaceLayer = layer;
        _replacements[_selected.Slot] = dlg.FileName;
        Status.Text = $"{_selected.Leaf}{(layer is null ? "" : $" layer {layer}")}  →  {Path.GetFileName(dlg.FileName)}";
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
            foreach (var r in chosen)
                mod.Replacements.Add(r.ReplaceLayer is { } layer
                    ? new TextureReplacement(r.Slot, "", new Dictionary<int, string> { [layer] = r.Replacement! })
                    : new TextureReplacement(r.Slot, r.Replacement!));
            var path = mod.Save();
            Status.Text = $"Saved {chosen.Count} replacement(s) to {path}. Nothing is written to the game.";
        }
        catch (Exception ex)
        {
            Status.Text = "Could not save the mod: " + ex.Message;
        }
    }

    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || !Game24) { Status.Text = "Pick an NHL 24 texture in the grid first."; return; }
        var game = _settings.ApplyTargetDir;
        if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game)) { Status.Text = "Set the apply target game in Settings first."; return; }
        var dlg = new OpenFolderDialog { Title = $"Folder for {_selected.Leaf} PNGs" };
        if (dlg.ShowDialog() != true) return;
        var slot = _selected.Slot;
        Busy(true);
        try
        {
            Status.Text = $"Reading {_selected.Leaf} from the game...";
            int n = await Task.Run(() =>
            {
                var index = Suite.Core.Frostbite.BundleIndex.Shared(game);
                var t = Suite.Core.Frostbite.TextureSlots.Read(game, index, slot);
                var layers = Suite.Core.Frostbite.Ps4Codec.DecodeSlices(t.Res, t.Stock);
                var leaf = slot[(slot.LastIndexOf('/') + 1)..];
                for (int i = 0; i < layers.Count; i++)
                    ImageIO.SavePng(layers[i], Path.Combine(dlg.FolderName, layers.Count == 1 ? leaf + ".png" : $"{leaf}_layer{i}.png"));
                return layers.Count;
            });
            Status.Text = $"Exported {n} PNG(s) to {dlg.FolderName}. Edit, then Choose replacement (with the Layer number for arrays).";
        }
        catch (Exception ex) { Status.Text = "Export failed: " + ex.Message; }
        finally { Busy(false); }
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var mods = ModApply.LoadMods();
        if (MessageBox.Show($"Write the mod list ({ModApply.TotalReplacements(mods)} texture(s)) into\n{_settings.ApplyTargetDir}?\n\nClose NHL 24 first. A backup is taken before writing.",
                "Apply mod list", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Busy(true);
        try
        {
            var (ok, message) = await ModApply.ApplyAsync(_settings, line => Dispatcher.Invoke(() => Status.Text = line));
            Status.Text = message;
            if (!ok) MessageBox.Show(message, "Apply", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Busy(false); }
    }

    async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Put the game back to the backup taken right before the last apply?\nAnything written after that apply is undone too.",
                "Undo last apply", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Busy(true);
        try
        {
            var (ok, message) = await ModApply.RestoreAsync(_settings, line => Dispatcher.Invoke(() => Status.Text = line));
            Status.Text = message;
            if (!ok) MessageBox.Show(message, "Undo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Busy(false); }
    }

    void ModList_Click(object sender, RoutedEventArgs e) => new ModListWindow(_settings) { Owner = Window.GetWindow(this) }.ShowDialog();

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

    public int? ReplaceLayer { get; set; }

    public string ReplaceText => _replacement is null ? "" : "✓ " + (ReplaceLayer is { } l ? $"layer {l}: " : "") + Path.GetFileName(_replacement);
}
