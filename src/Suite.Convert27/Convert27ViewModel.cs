using System.Collections.ObjectModel;
using System.ComponentModel;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// State of the Convert 27 page: the matcher (left) and what the viewer (right) shows.
/// Every change to a row's source is pushed to the viewer straight away through <see cref="ViewerMessage"/>.
/// </summary>
public sealed class Convert27ViewModel : Observable
{
    public SuiteSettings Settings { get; }
    public TextureImages Images { get; }
    public MatchProject Project { get; private set; } = new();
    public Nhl27Catalog? Catalog27 { get; private set; }
    public Nhl24Catalog? Catalog24 { get; private set; }
    public VariantMap Variants { get; private set; } = new();
    string _variantInfo = "";
    /// <summary>The team's default home/away variants in both games, shown above the matcher.</summary>
    public string VariantInfo { get => _variantInfo; set => Set(ref _variantInfo, value); }

    public ObservableCollection<string> Teams { get; } = [];
    public ObservableCollection<string> Folders24 { get; } = [];
    public ObservableCollection<FolderProposal> Proposals { get; } = [];
    public ObservableCollection<string> Sources27 { get; } = [];
    public ObservableCollection<SlotMatch> Rows { get; } = [];
    public Dictionary<string, string> Stock24 { get; private set; } = [];

    /// <summary>Raised with a JSON-serialisable message for the viewer page.</summary>
    public event Action<object>? ViewerMessage;

    string _status = "";
    string? _team;
    string? _folder24;
    string? _folder27;
    bool _busy;
    FolderMatch? _current;

    public Convert27ViewModel(SuiteSettings settings)
    {
        Settings = settings;
        Images = new TextureImages(settings);
    }

    public string Status { get => _status; set => Set(ref _status, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }

    public string? Team
    {
        get => _team;
        set
        {
            if (!Set(ref _team, value)) return;
            Folders24.Clear();
            var vt = value is null ? null : Variants.ByNhl24Folder(value);
            VariantInfo = vt is null
                ? "No variant map entry for this team (Edit variant map to add one)."
                : $"Default variants — NHL 24 {value}: home = {vt.Nhl24.Home} ({vt.Nhl24.HomeVariant}), away = {vt.Nhl24.Away} ({vt.Nhl24.AwayVariant})" +
                  $"   ↔   NHL 27 {vt.Nhl27.Folder}: home = {vt.Nhl27.Home} ({vt.Nhl27.HomeVariant}), away = {vt.Nhl27.Away} ({vt.Nhl27.AwayVariant})";
            if (value is null || Catalog24 is null) return;
            var prefix = $"{TextureName.SkaterPrefix}nhl/{value}/";
            foreach (var f in Catalog24.ByFolder.Keys.Where(f => f.StartsWith(prefix, StringComparison.Ordinal))
                         .OrderBy(f => f.Split('/')[^2]).ThenBy(f => TextureName.SetOrder(f.Split('/')[^1])))
                Folders24.Add(f);
            Folder24 = Folders24.FirstOrDefault(f => f.EndsWith("/home", StringComparison.Ordinal)) ?? Folders24.FirstOrDefault();
        }
    }

    /// <summary>The NHL 24 folder being matched. Any folder can be typed, not only uniforms.</summary>
    public string? Folder24
    {
        get => _folder24;
        set { if (Set(ref _folder24, value?.Trim().TrimEnd('/'))) _ = OpenFolderAsync(); }
    }

    /// <summary>The NHL 27 folder that supplies art. Editable; changing it re-proposes non-manual rows.</summary>
    public string? Folder27
    {
        get => _folder27;
        set
        {
            if (!Set(ref _folder27, value?.Trim().TrimEnd('/'))) return;
            Sources27.Clear();
            if (_folder27 is not null && Catalog27?.ByFolder.TryGetValue(_folder27, out var list) == true)
                foreach (var t in list.OrderBy(t => t.Name, StringComparer.Ordinal)) Sources27.Add(t.Name);
            if (_current is not null && _current.Folder27 != _folder27)
            {
                var p = Proposals.FirstOrDefault(x => x.Folder27 == _folder27);
                _current.Folder27 = _folder27;
                _current.Confidence = p?.Confidence ?? 0.8;
                _current.Reason = p?.Reason ?? "chosen by hand";
                _current.Manual = p is null || p != Proposals.FirstOrDefault();
                Repropose(keepManual: true);
            }
            _ = PushLeftAsync();
        }
    }

    public async Task LoadAsync()
    {
        Busy = true;
        try
        {
            Project = MatchProject.Load();
            Variants = VariantMap.Load();
            Status = "Loading the NHL 27 asset index...";
            var i27 = IndexStore.GetAsync(IndexStore.Nhl27, Settings, progress: s => Status = s);
            var i24 = IndexStore.GetAsync(IndexStore.Nhl24, Settings, progress: s => Status = s);
            var ix27 = await i27;
            Catalog27 = await Task.Run(() => new Nhl27Catalog(ix27));
            Status = "Loading the NHL 24 asset index (the first build lists every texture, a few minutes)...";
            var ix24 = await i24;
            Catalog24 = await Task.Run(() => new Nhl24Catalog(ix24));
            Teams.Clear();
            foreach (var t in Catalog24.ByFolder.Keys.Where(f => f.StartsWith(TextureName.SkaterPrefix + "nhl/", StringComparison.Ordinal))
                         .Select(f => f.Split('/')[5]).Distinct().Order())
                Teams.Add(t);
            Status = $"NHL 27: {Catalog27.ByName.Count:N0} textures · NHL 24: {Catalog24.ByName.Count:N0} slots · {Project.Folders.Count} folders matched so far";
            Team = Teams.Contains("boston") ? "boston" : Teams.FirstOrDefault();
            _ = PushMeshesAsync();
        }
        catch (Exception ex)
        {
            Status = "Load failed: " + ex.Message;
            SuiteLog.Write(ex.ToString());
        }
        finally
        {
            Busy = false;
        }
    }

    async Task OpenFolderAsync()
    {
        if (Catalog24 is null || Catalog27 is null || _folder24 is null) return;
        Proposals.Clear();
        foreach (var p in Matcher.ProposeFolders(_folder24, Catalog27, Project.TeamMap, Variants)) Proposals.Add(p);

        _current = Project.Find(_folder24);
        if (_current is null)
        {
            var best = Proposals.FirstOrDefault();
            _current = new FolderMatch { Folder24 = _folder24, Folder27 = best?.Folder27, Confidence = best?.Confidence ?? 0, Reason = best?.Reason ?? "no NHL 27 folder found" };
            Project.Folders.Add(_current);
            _folder27 = null;
            Folder27 = _current.Folder27;
            Repropose(false);
        }
        else if (!_current.Manual && Proposals.FirstOrDefault() is { } best && best.Folder27 != _current.Folder27)
        {
            // The folder pairing was automatic and the rules now prefer another NHL 27 set: follow them, keep edited rows.
            _current.Folder27 = best.Folder27;
            _current.Confidence = best.Confidence;
            _current.Reason = best.Reason;
            _folder27 = null;
            Folder27 = _current.Folder27;
            Repropose(keepManual: true);
        }
        else
        {
            _folder27 = null;
            Folder27 = _current.Folder27;
            ShowRows();
        }
        await LoadStock24Async();
    }

    /// <summary>Re-runs the proposal for the current folder. Rows the user edited stay as they are unless keepManual is false.</summary>
    public void Repropose(bool keepManual)
    {
        if (_current is null || Catalog24 is null || Catalog27 is null) return;
        var slots = Catalog24.ByFolder.TryGetValue(_current.Folder24, out var s) ? s : [];
        var fresh = _current.Folder27 is null
            ? slots.Select(x => new SlotMatch { Slot = x.Name, IsArray = x.Array, Reason = "no NHL 27 folder chosen" }).ToList()
            : Matcher.Propose(slots, _current.Folder27, _current.Confidence, Catalog27);
        if (keepManual)
        {
            var manual = _current.Rows.Where(r => r.Manual).ToDictionary(r => r.Slot);
            fresh = fresh.Select(r => manual.TryGetValue(r.Slot, out var m) ? m : r).ToList();
        }
        _current.Rows = fresh;
        ShowRows();
    }

    public Task ProposeWholeTeamAsync() => MatchFoldersAsync(Folders24.ToList(), $"team {Team}");

    /// <summary>Matches every uniform folder of every NHL team in the NHL 24 index (folders set by hand are kept).</summary>
    public Task ProposeAllTeamsAsync()
    {
        if (Catalog24 is null) return Task.CompletedTask;
        var all = Catalog24.ByFolder.Keys.Where(f => f.StartsWith(TextureName.SkaterPrefix + "nhl/", StringComparison.Ordinal)).Order().ToList();
        return MatchFoldersAsync(all, "all teams");
    }

    async Task MatchFoldersAsync(List<string> folders, string what)
    {
        if (Catalog24 is null || Catalog27 is null) return;
        Busy = true;
        var keep = _folder24;
        var c24 = Catalog24;
        var c27 = Catalog27;
        var existing = Project.Folders.ToDictionary(f => f.Folder24);
        // Proposals are independent per folder, so they run on all cores.
        var results = await Task.Run(() => folders.AsParallel().Select(f =>
        {
            existing.TryGetValue(f, out var fm);
            if (fm is not null && fm.Manual) return (f, (FolderMatch?)null);
            var best = Matcher.ProposeFolders(f, c27, Project.TeamMap, Variants).FirstOrDefault();
            var next = new FolderMatch
            {
                Folder24 = f, Folder27 = best?.Folder27, Confidence = best?.Confidence ?? 0,
                Reason = best?.Reason ?? "no NHL 27 folder found",
            };
            var slots = c24.ByFolder[f];
            var manual = (fm?.Rows ?? []).Where(r => r.Manual).ToDictionary(r => r.Slot);
            next.Rows = (next.Folder27 is null ? slots.Select(x => new SlotMatch { Slot = x.Name, IsArray = x.Array, Reason = "no NHL 27 folder found" }).ToList()
                    : Matcher.Propose(slots, next.Folder27, next.Confidence, c27))
                .Select(r => manual.TryGetValue(r.Slot, out var m) ? m : r).ToList();
            return (f, (FolderMatch?)next);
        }).ToList());
        int n = 0;
        foreach (var (f, next) in results)
        {
            if (next is null) continue;
            Project.Folders.RemoveAll(x => x.Folder24 == f);
            Project.Folders.Add(next);
            n++;
        }
        var rows = results.Where(r => r.Item2 is not null).SelectMany(r => r.Item2!.Rows).ToList();
        Status = $"Matched {n} folders ({what}): {rows.Count(r => r.Kind != SourceKind.Stock || r.Layers.Any(l => l.Source27 != null)):N0} of {rows.Count:N0} slots have an NHL 27 source. Folders you set by hand were kept. Nothing is written to the game until Apply.";
        Busy = false;
        _folder24 = null;
        Folder24 = keep;
    }
    void ShowRows()
    {
        foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        if (_current is null) return;
        foreach (var r in _current.Rows.OrderBy(r => r.Garment).ThenBy(r => r.Leaf, StringComparer.Ordinal))
        {
            r.PropertyChanged += OnRowChanged;
            r.SourceTextChanged = ResolveSourceText;
            Rows.Add(r);
        }
        _ = DecodeSourcesAsync(Rows.ToList());
        _ = PushRightAsync();
    }

    async Task LoadStock24Async()
    {
        if (_folder24 is null) return;
        var folder = _folder24;
        Stock24 = await Images.Nhl24FolderAsync(folder, s => Status = s);
        if (folder != _folder24) return;
        foreach (var r in Rows) UpdateThumbs(r);
        Status = $"{folder}: {Rows.Count} slots, {Rows.Count(r => r.Kind != SourceKind.Stock || r.Layers.Any(l => l.Source27 != null))} with a source. {Stock24.Count} stock images.";
        await PushRightAsync();
    }

    async Task DecodeSourcesAsync(IReadOnlyList<SlotMatch> rows)
    {
        if (Catalog27 is null) return;
        var wanted = rows.SelectMany(r => r.Layers.Select(l => l.Source27).Append(r.Source27))
            .Where(n => n is not null && Catalog27.ByName.ContainsKey(n)).Select(n => Catalog27.ByName[n!]).ToList();
        if (_folder27 is not null && Catalog27.ByFolder.TryGetValue(_folder27, out var all)) wanted.AddRange(all);
        await Images.Nhl27Async(wanted, s => Status = s);
        foreach (var r in rows) UpdateThumbs(r);
    }

    void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SlotMatch row) return;
        if (e.PropertyName is nameof(SlotMatch.Kind) or nameof(SlotMatch.Source27) or nameof(SlotMatch.FilePath))
        {
            if (row.Kind == SourceKind.Nhl27 && row.Source27 is not null && Catalog27?.ByName.TryGetValue(row.Source27, out var t) == true)
                _ = Images.Nhl27Async([t]).ContinueWith(_ => { UpdateThumbs(row); return PushRightAsync(row); });
            else
                _ = PushRightAsync(row);
        }
    }

    /// <summary>Typed or picked text in the Source column: an NHL 27 path, an image file, or empty for stock.</summary>
    void ResolveSourceText(SlotMatch row, string? text)
    {
        text = text?.Trim().Trim('"');
        row.Manual = true;
        if (string.IsNullOrEmpty(text) || text.StartsWith("(keep", StringComparison.Ordinal))
            row.ClearSource();
        else if (Catalog27?.ByName.ContainsKey(text) == true)
        {
            row.FilePath = null; row.Source27 = text; row.Kind = SourceKind.Nhl27;
        }
        else if (_folder27 is not null && Catalog27?.ByName.ContainsKey(_folder27 + "/" + text) == true)
        {
            row.FilePath = null; row.Source27 = _folder27 + "/" + text; row.Kind = SourceKind.Nhl27;
        }
        else if (File.Exists(text) && ImageIO.CanLoad(text))
            SetFile(row, text);
        else
        {
            Status = $"'{text}' is not an NHL 27 texture or an image file.";
            return;
        }
        row.Confidence = 1;
        row.Reason = "set by hand";
    }

    public void SetFile(SlotMatch row, string path)
    {
        row.Manual = true;
        row.Source27 = null;
        row.FilePath = path;
        row.Kind = SourceKind.File;
        row.Confidence = 1;
        row.Reason = "image dropped by hand";
    }

    public void SetLayerFile(SlotMatch row, LayerMatch layer, string path)
    {
        row.Manual = true;
        layer.Source27 = null;
        layer.FilePath = path;
        row.Reason = "layer set by hand";
        UpdateThumbs(row);
    }

    public void Save()
    {
        Project.Save();
        Status = $"Saved {Project.Folders.Count} folder matches to {MatchProject.DefaultPath}";
    }

    public async Task<string> AddToModListAsync(string name, bool currentFolderOnly)
    {
        if (Catalog27 is null) throw new InvalidOperationException("Not loaded.");
        var folders = currentFolderOnly && _current is not null ? [_current] : Project.Folders;
        var sources = folders.SelectMany(f => f.Rows).SelectMany(r => r.Layers.Select(l => l.Source27).Append(r.Kind == SourceKind.Nhl27 ? r.Source27 : null))
            .Where(n => n is not null && Catalog27.ByName.ContainsKey(n)).Select(n => Catalog27.ByName[n!]).ToList();
        await Images.Nhl27Async(sources, s => Status = s);
        Project.Save();
        var mod = Project.ToModFile(name, folders);
        var path = mod.Save();
        Status = $"Saved mod '{name}' with {mod.Replacements.Count} replacements: {path}";
        return path;
    }

    // ---------------------------------------------------------------- viewer

    static readonly string[] Parts = ["jersey", "pant", "sock"];
    static readonly string[] Roles = ["color", "normal"];

    /// <summary>Sends the real NHL 24 skater meshes to both viewer sides (extracting them first if needed).</summary>
    async Task PushMeshesAsync()
    {
        var problem = await Nhl24Meshes.EnsureAsync(Settings, s => Status = s);
        var items = Nhl24Meshes.Available().Select(m => new { url = ViewerUrl.For(m.File), part = m.Part }).ToList();
        if (problem is not null) SuiteLog.Write("meshes: " + problem);
        if (items.Count > 0) ViewerMessage?.Invoke(new { type = "meshes", items });
        else Status = "NHL 24 skater meshes unavailable: " + problem;
    }

    async Task PushLeftAsync()
    {
        ViewerMessage?.Invoke(new { type = "labels", left = "NHL 27 · " + Short(_folder27), right = "NHL 24 result · " + Short(_folder24) });
        if (Catalog27 is null) return;
        var textures = _folder27 is not null && Catalog27.ByFolder.TryGetValue(_folder27, out var l) ? l : [];
        var picks = new List<(string part, string role, Nhl27Texture? tex)>();
        foreach (var part in Parts)
        foreach (var role in Roles)
            picks.Add((part, role, Pick(textures.Select(t => (TextureName.Parse(t.Name), t)), part, role)));
        var pngs = await Images.Nhl27Async(picks.Where(p => p.tex is not null).Select(p => p.tex!));
        foreach (var (part, role, tex) in picks)
            ViewerMessage?.Invoke(new { type = "tex", side = "l", part, role, url = tex is not null && pngs.TryGetValue(tex.Name, out var png) ? ViewerUrl.For(png) : null });
    }

    async Task PushRightAsync(SlotMatch? changed = null)
    {
        await Task.Yield();
        foreach (var part in Parts)
        foreach (var role in Roles)
        {
            var row = Pick(Rows.Select(r => (r.Name, r)), part, role);
            if (row is null || (changed is not null && row != changed)) continue;
            ViewerMessage?.Invoke(new { type = "tex", side = "r", part, role, url = ImageFor(row) is { } p ? ViewerUrl.For(p) : null });
        }
    }

    /// <summary>The picture a row currently produces: its NHL 27 source, its dropped file, or the stock NHL 24 texture.</summary>
    public string? ImageFor(SlotMatch row)
    {
        try
        {
            return row.Kind switch
            {
                SourceKind.Nhl27 when row.Source27 is not null && File.Exists(TextureImages.Nhl27Png(row.Source27)) => TextureImages.Nhl27Png(row.Source27),
                SourceKind.File when row.FilePath is not null && File.Exists(row.FilePath) => TextureImages.PreviewOf(row.FilePath),
                _ => Stock24.GetValueOrDefault(row.Slot),
            };
        }
        catch (Exception ex)
        {
            Status = "Cannot read image: " + ex.Message;
            return null;
        }
    }

    public string? StockImage(SlotMatch row) => Stock24.GetValueOrDefault(row.Slot) ?? Stock24.GetValueOrDefault(row.Slot + "#0");

    public void UpdateThumbs(SlotMatch row)
    {
        row.Thumb24 = StockImage(row);
        if (row.IsArray)
        {
            var first = row.Layers.FirstOrDefault(l => l.FilePath is not null || l.Source27 is not null);
            row.ThumbSource = first?.FilePath ?? (first?.Source27 is { } s && File.Exists(TextureImages.Nhl27Png(s)) ? TextureImages.Nhl27Png(s) : null);
        }
        else row.ThumbSource = row.Kind == SourceKind.Stock ? null : ImageFor(row);
    }

    /// <summary>Skaters draw the _base_ textures (rule 1), so those win; plain ones are the fallback.</summary>
    static T? Pick<T>(IEnumerable<(TextureName name, T item)> items, string part, string role) where T : class
    {
        var list = items.Where(x => x.name.Garment == part && x.name.Role == role && x.name.Layer is null && !x.name.IsArrayName).ToList();
        return list.FirstOrDefault(x => x.name.IsBase).item ?? list.FirstOrDefault().item;
    }

    static string Short(string? folder) => folder is null ? "(none)" : folder.StartsWith(TextureName.SkaterPrefix) ? folder[TextureName.SkaterPrefix.Length..] : folder;
}

/// <summary>Maps local files to URLs the viewer can load (WebView2 virtual hosts per drive).</summary>
public static class ViewerUrl
{
    public static string Host(char drive) => $"drive-{char.ToLowerInvariant(drive)}.cims";

    public static string For(string path)
    {
        var full = Path.GetFullPath(path);
        var rel = full[3..].Split('\\').Select(Uri.EscapeDataString);
        var stamp = File.Exists(full) ? File.GetLastWriteTimeUtc(full).Ticks : 0;
        return $"https://{Host(full[0])}/{string.Join('/', rel)}?v={stamp}";
    }
}
