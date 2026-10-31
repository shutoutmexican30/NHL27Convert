using System.Collections.ObjectModel;
using System.ComponentModel;
using Suite.Convert27.Studio;
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
            PushColors();
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
        var folder = _folder24;
        try
        {
            Proposals.Clear();
            foreach (var p in Matcher.ProposeFolders(folder, Catalog27, Project.TeamMap, Variants)) Proposals.Add(p);

            _current = Project.Find(folder);
            if (_current is null)
            {
                var best = Proposals.FirstOrDefault();
                _current = new FolderMatch { Folder24 = folder, Folder27 = best?.Folder27, Confidence = best?.Confidence ?? 0, Reason = best?.Reason ?? "no NHL 27 folder found" };
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
        catch (Exception ex)
        {
            Status = $"Could not open {folder}: {ex.Message}";
            SuiteLog.Write("open folder: " + ex);
        }
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
        MatchLogoShapes();
        CompositeBaseColours();
        Status = $"{folder}: {Rows.Count} slots, {Rows.Count(r => r.Kind != SourceKind.Stock || r.Layers.Any(l => l.Source27 != null))} with a source. {Stock24.Count} stock images.";
        await PushRightAsync();
    }

    /// <summary>
    /// Fills the numbered logo layers that the index-based matcher leaves stock. First by alpha silhouette (the same
    /// logo has the same shape, recolours do not matter), then — for layers with no shape match, such as the
    /// adidas → Fanatics manufacturer swap — by the same layer index when that NHL 27 layer was not already claimed
    /// by a shape match. Rows the user edited are never touched.
    /// </summary>
    void MatchLogoShapes()
    {
        if (_current is null || _folder27 is null || Catalog27 is null || Stock24.Count == 0) return;
        if (!Catalog27.ByFolder.TryGetValue(_folder27, out var textures)) return;

        var candidates = new Dictionary<string, List<(string Name, string Png, int? Layer)>>(StringComparer.Ordinal);
        foreach (var t in textures)
        {
            var n = TextureName.Parse(t.Name);
            if (n.Role != "color" || n.IsArrayName || !IsLogoGarment(n.Garment)) continue;
            var png = TextureImages.Nhl27Png(t.Name);
            if (!File.Exists(png)) continue;
            if (!candidates.TryGetValue(n.Garment, out var list)) candidates[n.Garment] = list = [];
            list.Add((t.Name, png, n.Layer));
        }
        if (candidates.Count == 0) return;

        var silhouettes = new Dictionary<string, double[]?>(StringComparer.Ordinal);
        double[]? Sil(string png)
        {
            if (!silhouettes.TryGetValue(png, out var s)) silhouettes[png] = s = LogoMatcher.Silhouette(png);
            return s;
        }

        var claimed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string> ClaimedFor(string garment)
        {
            if (!claimed.TryGetValue(garment, out var set)) claimed[garment] = set = new HashSet<string>(StringComparer.Ordinal);
            return set;
        }
        var arrayLayerSource = new Dictionary<(string Garment, int Layer), string>();

        int filled = 0, swapped = 0;

        // Pass 1: the arrays are what the game actually uses, so they are matched first and claim their layers.
        foreach (var row in Rows)
        {
            if (!row.IsArray || row.Manual || row.Kind != SourceKind.Stock) continue;
            var name = row.Name;
            if (name.Role != "color" || !IsLogoGarment(name.Garment)) continue;
            if (!candidates.TryGetValue(name.Garment, out var cands)) continue;
            var items = new List<(string Name, int? Layer, double[] Sil)>();
            foreach (var (cname, cpng, clay) in cands)
                if (Sil(cpng) is { } cs) items.Add((cname, clay, cs));
            if (items.Count == 0) continue;
            var used = ClaimedFor(name.Garment);

            int byShape = 0, byIndex = 0;
            foreach (var layer in row.Layers)
            {
                if (layer.Source27 is not null || layer.FilePath is not null) continue;
                var stockPng = Stock24.GetValueOrDefault($"{row.Slot}#{layer.Layer}");
                if (stockPng is null || Sil(stockPng) is not { } stock) continue;
                string? pick = null;
                double best = double.MaxValue;
                foreach (var i in items.Where(i => !used.Contains(i.Name)))
                {
                    var score = LogoMatcher.Difference(stock, i.Sil);
                    if (score <= LogoMatcher.AcceptMad && score < best) { best = score; pick = i.Name; }
                }
                if (pick is null) continue;
                layer.Source27 = pick;
                used.Add(pick);
                arrayLayerSource[(name.Garment, layer.Layer)] = pick;
                byShape++;
            }

            // Manufacturer/logo swap (adidas → Fanatics): only when exactly one layer is left over, so there is
            // no ambiguity about which layer it maps to.
            var unmatched = row.Layers.Where(l => l.Source27 is null && l.FilePath is null).ToList();
            var unclaimed = items.Where(i => !used.Contains(i.Name)).ToList();
            if (unmatched.Count == 1 && unclaimed.Count > 0)
            {
                var same = unclaimed.FirstOrDefault(i => i.Layer == unmatched[0].Layer);
                var pick = same.Name ?? (unclaimed.Count == 1 ? unclaimed[0].Name : null);
                if (pick is not null)
                {
                    unmatched[0].Source27 = pick;
                    used.Add(pick);
                    arrayLayerSource[(name.Garment, unmatched[0].Layer)] = pick;
                    byIndex++;
                }
            }

            if (byShape + byIndex > 0)
            {
                row.Confidence = byIndex == 0 ? 0.9 : 0.8;
                row.Reason = $"{byShape} layer(s) by logo shape, {byIndex} by layer index (manufacturer/logo swap) of {row.Layers.Count}";
                filled += byShape + byIndex;
                swapped += byIndex;
            }
        }

        // Pass 2: the individual `_N_color` slots mirror the array layers, so they follow the array's answer.
        foreach (var row in Rows)
        {
            if (row.IsArray || row.Manual || row.Kind != SourceKind.Stock) continue;
            var name = row.Name;
            if (name.Role != "color" || !IsLogoGarment(name.Garment) || name.Layer is not { } layerIndex) continue;
            if (!arrayLayerSource.TryGetValue((name.Garment, layerIndex), out var source)) continue;
            row.Kind = SourceKind.Nhl27;
            row.Source27 = source;
            row.Confidence = 0.9;
            row.Reason = "same as the branding array layer (the game reads logos from the array)";
            filled++;
        }

        if (filled > 0)
        {
            foreach (var r in Rows) UpdateThumbs(r);
            SuiteLog.Write($"logo shapes: matched {filled} layer(s), {swapped} by layer index, in {Short(_current.Folder24)}");
        }
    }

    static bool IsLogoGarment(string garment) =>
        garment.StartsWith("branding", StringComparison.Ordinal) || garment == "helmetlogo";

    async Task DecodeSourcesAsync(IReadOnlyList<SlotMatch> rows)
    {
        if (Catalog27 is null) return;
        var wanted = rows.SelectMany(r => r.Layers.Select(l => l.Source27).Append(r.Source27))
            .Where(n => n is not null && Catalog27.ByName.ContainsKey(n)).Select(n => Catalog27.ByName[n!]).ToList();
        if (_folder27 is not null && Catalog27.ByFolder.TryGetValue(_folder27, out var all)) wanted.AddRange(all);
        await Images.Nhl27Async(wanted, s => Status = s);
        foreach (var r in rows) UpdateThumbs(r);
        MatchLogoShapes();
        CompositeBaseColours();
        // The NHL 27 sources are decoded now: push the right side so the viewer shows the new art
        // instead of the stock NHL 24 textures it grabbed before the decode finished.
        await PushRightAsync();
    }

    void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SlotMatch row) return;
        if (e.PropertyName is not (nameof(SlotMatch.Kind) or nameof(SlotMatch.Source27) or nameof(SlotMatch.FilePath))) return;
        if (row.Kind == SourceKind.Nhl27 && row.Source27 is not null && Catalog27?.ByName.TryGetValue(row.Source27, out var t) == true)
        {
            _ = Images.Nhl27Async([t]).ContinueWith(_ =>
            {
                UpdateThumbs(row);
                _ = PushRightAsync(row);
            });
        }
        else
        {
            UpdateThumbs(row);
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

    // ---------------------------------------------------------------- replacement folder

    /// <summary>
    /// Matches image files from a folder onto the rows that the user has not edited, using the same name/role
    /// rules as the old Uniform Studio (exact asset name 100%, same material role 90%, same garment+role 70%).
    /// Numbered logo layers only take an exact name, because layer N is a different logo in each game.
    /// </summary>
    public async Task<int> MatchFolderAsync(string folder)
    {
        if (Catalog24 is null) return 0;
        if (!Directory.Exists(folder)) { Status = "That replacement folder does not exist."; return 0; }
        var teamWords = Catalog24.ByFolder.Keys
            .Select(f => TextureName.Parse(f + "/x").Team).Where(t => t is not null).Select(t => t!)
            .Append("utah").ToHashSet(StringComparer.Ordinal);
        var paths = Directory.EnumerateFiles(folder, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(ImageIO.CanLoad).ToList();
        if (paths.Count == 0) { Status = $"No DDS/PNG files in {folder}."; return 0; }
        Status = $"Scanning {paths.Count} replacement files…";
        var files = await Task.Run(() => paths
            .Select(p => new ReplacementFile { Path = p, Source = "folder", Key = MaterialKey.Of(Path.GetFileNameWithoutExtension(p), teamWords) })
            .ToList());
        int matched = 0;
        foreach (var row in Rows)
        {
            if (row.Manual || row.IsArray || row.Name.Role == "coeff") continue;
            var card = new StudioCard { Slot = row.Slot, Key = MaterialKey.Of(row.Name.Leaf, teamWords) };
            var best = StudioMatcher.Rank(card, files)
                .FirstOrDefault(c => c.Score >= (card.Key.Layer is not null ? 0.99 : 0.7));
            if (best is null) continue;
            row.FilePath = best.File.Path;
            row.Source27 = null;
            row.Kind = SourceKind.File;
            row.Confidence = best.Score;
            row.Reason = best.Reason + " (replacement folder)";
            matched++;
        }
        foreach (var row in Rows) UpdateThumbs(row);
        await PushRightAsync();
        Status = matched == 0
            ? "No replacement file matched this folder's slots."
            : $"Matched {matched} row(s) from {folder}. Array layers and coeffs are left alone; edit any row by hand.";
        return matched;
    }

    // ---------------------------------------------------------------- viewer

    /// <summary>Sends the team's DB colours to the viewer so the helmet and gloves use the right colours.</summary>
    void PushColors()
    {
        var index = Team is not null ? Variants.ByNhl24Folder(Team)?.Index : null;
        ViewerMessage?.Invoke(new
        {
            type = "colors",
            primary = TeamColors.Primary(index),
            secondary = TeamColors.Secondary(index),
        });
    }

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
        var named = textures.Select(t => (TextureName.Parse(t.Name), t)).ToList();
        var picks = new List<(string part, string role, Nhl27Texture? tex)>();
        foreach (var part in Parts)
        foreach (var role in Roles)
            picks.Add((part, role, PickArt(named, part, role)));
        var regions = Parts.Select(part => (part, tex: PickBase(named, part))).ToList();
        var font = PickArt(named, "font", "color");
        var helmetLogo = PickHelmetLogo(named);
        var all = picks.Where(p => p.tex is not null).Select(p => p.tex!)
            .Concat(regions.Where(r => r.tex is not null).Select(r => r.tex!))
            .Concat(font is null ? [] : [font])
            .Concat(helmetLogo is null ? [] : [helmetLogo])
            .DistinctBy(t => t.Name).ToList();
        var pngs = await Images.Nhl27Async(all);
        foreach (var (part, role, tex) in picks)
            ViewerMessage?.Invoke(new { type = "tex", side = "l", part, role, url = tex is not null && pngs.TryGetValue(tex.Name, out var png) ? ViewerUrl.For(png) : null });
        foreach (var (part, tex) in regions)
            ViewerMessage?.Invoke(new { type = "region", side = "l", part, url = tex is not null && pngs.TryGetValue(tex.Name, out var png) ? ViewerUrl.For(png) : null });
        ViewerMessage?.Invoke(new { type = "font", side = "l", url = font is not null && pngs.TryGetValue(font.Name, out var fontPng) ? ViewerUrl.For(fontPng) : null });
        ViewerMessage?.Invoke(new { type = "helmetlogo", side = "l", url = helmetLogo is not null && pngs.TryGetValue(helmetLogo.Name, out var hlPng) ? ViewerUrl.For(hlPng) : null });
    }

    async Task PushRightAsync(SlotMatch? changed = null)
    {
        await Task.Yield();
        var rows = Rows.Select(r => (r.Name, r)).ToList();
        foreach (var part in Parts)
        {
            foreach (var role in Roles)
            {
                var row = PickArt(rows, part, role);
                if (row is null || (changed is not null && row != changed)) continue;
                var image = role == "color" ? ArtImageFor(row) : ImageFor(row);
                ViewerMessage?.Invoke(new { type = "tex", side = "r", part, role, url = image is { } p ? ViewerUrl.For(p) : null });
            }
            var region = PickBase(rows, part);
            if (region is not null && (changed is null || region == changed))
                ViewerMessage?.Invoke(new { type = "region", side = "r", part, url = ImageFor(region) is { } p ? ViewerUrl.For(p) : null });
        }
        var font = PickArt(rows, "font", "color");
        if (font is not null && (changed is null || font == changed))
            ViewerMessage?.Invoke(new { type = "font", side = "r", url = ImageFor(font) is { } p ? ViewerUrl.For(p) : null });
        var helmetLogo = PickHelmetLogo(rows);
        if (helmetLogo is not null && (changed is null || helmetLogo == changed))
            ViewerMessage?.Invoke(new { type = "helmetlogo", side = "r", url = ImageFor(helmetLogo) is { } p ? ViewerUrl.For(p) : null });
    }

    /// <summary>
    /// The picture the viewer should use for a colour row. The plain `_color` map carries the sponsor/brand art;
    /// the `_base_` map is only the fabric layout, so when the row is a base slot we show the matched NHL 27
    /// plain map instead — that keeps a sponsor that has no logo-array slot visible in the viewer.
    /// </summary>
    public string? ArtImageFor(SlotMatch row)
    {
        if (row.Name.IsBase && Catalog27 is not null && _folder27 is not null
            && Catalog27.ByFolder.TryGetValue(_folder27, out var textures))
        {
            var plainCanonical = row.Name.Canonical.Replace("_base_", "_");
            var plain = textures.FirstOrDefault(t => TextureName.Parse(t.Name).Canonical == plainCanonical);
            if (plain is not null)
            {
                var png = TextureImages.Nhl27Png(plain.Name);
                if (File.Exists(png)) return png;
            }
        }
        return ImageFor(row);
    }

    /// <summary>
    /// Base colour slots get the plain art with the base map's fabric alpha. Some uniforms carry a sponsor only in the
    /// plain colour map (no logo-array slot for it), so writing the plain base alone would lose it; this keeps the
    /// sponsor and the region alpha in one texture.
    /// </summary>
    void CompositeBaseColours()
    {
        if (Catalog27 is null || _folder27 is null) return;
        if (!Catalog27.ByFolder.TryGetValue(_folder27, out var textures)) return;
        int made = 0;
        foreach (var row in Rows)
        {
            if (row.Manual || row.IsArray) continue;
            if (!row.Name.IsBase || row.Name.Role != "color") continue;
            if (row.Kind != SourceKind.Nhl27 || row.Source27 is null) continue;
            var basePng = TextureImages.Nhl27Png(row.Source27);
            if (!File.Exists(basePng)) continue;
            var plainCanonical = row.Name.Canonical.Replace("_base_", "_");
            var plain = textures.FirstOrDefault(t => TextureName.Parse(t.Name).Canonical == plainCanonical);
            if (plain is null) continue;
            var artPng = TextureImages.Nhl27Png(plain.Name);
            if (!File.Exists(artPng)) continue;
            var composite = TextureImages.CompositeArtWithBaseAlpha(artPng, basePng, row.Slot + "|" + plain.Name);
            if (composite is null) continue;
            row.FilePath = composite;
            row.Source27 = null;
            row.Kind = SourceKind.File;
            row.Alpha = null;
            row.Reason = "plain art + _base_ fabric alpha (keeps a sponsor that has no logo slot)";
            made++;
        }
        if (made > 0)
        {
            foreach (var r in Rows) UpdateThumbs(r);
            SuiteLog.Write($"composite base colour: {made} row(s) in {Short(_current?.Folder24)}");
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

    /// <summary>
    /// The colour map the viewer samples for a part: the plain (non-base) texture carries the crest / numbers /
    /// manufacturer art, while the _base_ map is the same layout with only the fabric-region alpha, so plain wins.
    /// </summary>
    static T? PickArt<T>(IEnumerable<(TextureName name, T item)> items, string part, string role) where T : class
    {
        var list = items.Where(x => x.name.Garment == part && x.name.Role == role && x.name.Layer is null && !x.name.IsArrayName).ToList();
        return list.FirstOrDefault(x => !x.name.IsBase).item ?? list.FirstOrDefault().item;
    }

    /// <summary>The _base_ colour map (alpha = fabric regions, rule 11) for the region view.</summary>
    static T? PickBase<T>(IEnumerable<(TextureName name, T item)> items, string part) where T : class
    {
        var list = items.Where(x => x.name.Garment == part && x.name.Role == "color" && x.name.Layer is null && !x.name.IsArrayName).ToList();
        return list.FirstOrDefault(x => x.name.IsBase).item ?? list.FirstOrDefault().item;
    }

    /// <summary>The helmet logo decal (a numbered layer, so it is not picked by <see cref="PickArt"/>).</summary>
    static T? PickHelmetLogo<T>(IEnumerable<(TextureName name, T item)> items) where T : class
    {
        var list = items.Where(x => x.name.Garment == "helmetlogo" && x.name.Role == "color" && !x.name.IsArrayName).ToList();
        return list.FirstOrDefault().item;
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
