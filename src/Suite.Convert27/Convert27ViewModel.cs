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
    /// <summary>Role-based branding layer maps (%APPDATA%\CenterIceModdingSuite\data\slot_map.json).</summary>
    public SlotMapStore SlotMaps { get; private set; } = new();
    string _variantInfo = "";
    /// <summary>The team's default home/away variants in both games, shown above the matcher.</summary>
    public string VariantInfo { get => _variantInfo; set => Set(ref _variantInfo, value); }

    public ObservableCollection<string> Teams { get; } = [];
    public ObservableCollection<string> Folders24 { get; } = [];
    public ObservableCollection<FolderProposal> Proposals { get; } = [];
    public ObservableCollection<string> Sources27 { get; } = [];
    public ObservableCollection<SlotMatch> Rows { get; } = [];
    public ObservableCollection<PreflightItem> PreflightItems { get; } = [];
    public Dictionary<string, string> Stock24 { get; private set; } = [];
    /// <summary>The folder <see cref="Stock24"/> was dumped from; array-slice checks run only while it is current.</summary>
    string? _stockFolder;
    bool _advancedMode;
    string _preflightSummary = "Choose a uniform, then select Check & convert.";

    /// <summary>Standard hides texture plumbing by default; Advanced reveals the complete matcher and override tools.</summary>
    public bool AdvancedMode { get => _advancedMode; set => Set(ref _advancedMode, value); }
    public string PreflightSummary { get => _preflightSummary; set => Set(ref _preflightSummary, value); }
    public string SourceSummary => Folder27 is null
        ? "The best NHL 27 source will be selected automatically."
        : $"{FriendlyFolder(Folder27)} selected automatically · logos are matched by role";

    /// <summary>Raised with a JSON-serialisable message for the viewer page.</summary>
    public event Action<object>? ViewerMessage;

    string _status = "";
    string? _team;
    string? _folder24;
    string? _folder27;
    bool _busy;
    FolderMatch? _current;
    /// <summary>Per garment: the Fanatics colour/normal taken from another NHL 27 set when the paired one has none.</summary>
    readonly Dictionary<string, (Nhl27Texture Color, Nhl27Texture Normal)?> _fanaticsCache = new(StringComparer.Ordinal);

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
                  $"   ↔   NHL 27 {vt.Nhl27.Folder}: home = {vt.Nhl27.Home} ({vt.Nhl27.HomeVariant}), away = {vt.Nhl27.Away} ({vt.Nhl27.AwayVariant})" +
                  (vt.ExtraKits.Count == 0 ? "" : "   ·   extra NHL 27 kits: " +
                      string.Join(", ", vt.ExtraKits.Select(k => $"{k.Nhl27} → replaces {k.Replaces24 ?? "(none)"}")));
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
        set
        {
            if (!Set(ref _folder24, value?.Trim().TrimEnd('/'))) return;
            Raise(nameof(ShoulderOption));
            PreflightItems.Clear();
            PreflightSummary = "Uniform changed. Select Check & convert to validate it.";
            _ = OpenFolderAsync();
        }
    }

    /// <summary>The NHL 27 folder that supplies art. Editable; changing it re-proposes non-manual rows.</summary>
    public string? Folder27
    {
        get => _folder27;
        set
        {
            if (!Set(ref _folder27, value?.Trim().TrimEnd('/'))) return;
            Raise(nameof(SourceSummary));
            Raise(nameof(ShoulderOption));
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

    /// <summary>A jersey path in the language of the Standard workflow rather than its internal set name.</summary>
    public static string FriendlyFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "No uniform selected";
        var clean = path.Trim().TrimEnd('/');
        if (clean.Length == 0) return "No uniform selected";
        var parts = clean.Split('/');
        var set = parts[^1];
        var team = parts.Length > 2 ? parts[^3] : "";
        var variant = set.ToLowerInvariant() switch
        {
            "home" or "ten" => "Home",
            "away" or "eleven" => "Away",
            "twelve" => "Alternate 1",
            "thirteen" => "Alternate 2",
            _ => char.ToUpperInvariant(set[0]) + set[1..],
        };
        return team.Length == 0 ? variant : $"{char.ToUpperInvariant(team[0]) + team[1..]} · {variant}";
    }

    public async Task LoadAsync()
    {
        Busy = true;
        try
        {
            Project = MatchProject.Load();
            Variants = VariantMap.Load();
            SlotMaps = SlotMapStore.Load();
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
        _ = PushReferenceAsync(folder);
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

    /// <summary>
    /// Checks every team's NHL 27 kit picks (variant_map.json home/away/extraKits) against nhluniforms.com's drawings of
    /// this season and shows the report. Read only; the drawings stay a local personal reference.
    /// </summary>
    public async Task CheckUniformsAsync()
    {
        if (Catalog27 is null) { Status = "The NHL 27 index is still loading."; return; }
        Busy = true;
        try
        {
            string season = UniformReference.CurrentSeason();
            var (report, summary) = await UniformReference.CheckAsync(season, Settings, Catalog27, Variants, s => Status = s);
            string contact = Path.Combine(UniformReference.Dir(season), "contact-sheet.html");
            Status = summary + " Visual review: " + contact;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(contact) { UseShellExecute = true });
        }
        catch (Exception ex) { Status = "Uniform check failed: " + ex.Message; }
        finally { Busy = false; }
    }

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
    /// <summary>Re-sends everything one row contributes, so an edit shows immediately.</summary>
    public void RefreshViewer(SlotMatch row)
    {
        UpdateThumbs(row);
        _ = PushRightAsync(row);
    }

    /// <summary>Names every slot for the selected team: what the game draws there, for this garment and set.</summary>
    void LabelSlots()
    {
        if (_current is null) return;
        var entries = SlotMaps.Entries.Where(e => e.Folder24 == _current.Folder24 && e.Folder27 == _folder27).ToList();
        foreach (var row in Rows)
        {
            var name = row.Name;
            LayerLabel? label = null;
            if (name.Layer is { } layer && entries.FirstOrDefault(e => e.Garment == name.Garment) is { } entry)
            {
                label = entry.Layers24.FirstOrDefault(l => l.Layer == layer);
                // A numbered row is labelled by the array layer showing the same picture, never by its own number
                // (Detroit: numbered 0 is the club crest while array layer 0 is the shield), so the label matches
                // the stock logo shown next to it.
                if (!row.IsArray && ArrayRow(name.Garment) is { } arrayRow
                    && NumberedPairsFor(arrayRow.Slot).FirstOrDefault(kv => kv.Value == ColorSibling(row)) is { Value: not null } pair
                    && entry.Layers24.FirstOrDefault(l => l.Layer == pair.Key) is { } byPicture)
                    label = byPicture;
            }
            var what = Describe(name, label);
            // A layer with no slice in the NHL 24 array cannot be written, however it was labelled.
            if (name.Layer is { } slice && name.Garment.StartsWith("branding", StringComparison.Ordinal))
            {
                var array = Rows.FirstOrDefault(r => r.IsArray && r.Garment == name.Garment && r.Role == "color");
                if (array is not null && !Stock24.ContainsKey($"{array.Slot}#{slice}"))
                    what = $"No slice {slice} in NHL 24 (not written)";
            }
            row.What = what;
        }
    }

    /// <summary>
    /// Hides the numbered branding rows NHL 24 does not have: their own layer number has no slice in the array and
    /// no array slice shows the same picture, so there is nothing to write to. Rows with a source stay visible (they
    /// are pruned at export instead). Only the current folder's stock images decide; before they load nothing is hidden.
    /// </summary>
    void HideMissingSlots()
    {
        if (_current is null || _stockFolder != _current.Folder24) return;
        foreach (var row in _current.Rows)
        {
            var name = row.Name;
            row.Hidden = !row.IsArray && name.Layer is { } slice
                && name.Garment.StartsWith("branding", StringComparison.Ordinal)
                && row.Kind == SourceKind.Stock
                && ArrayRow(name.Garment) is { } arrayRow
                && !Stock24.ContainsKey($"{arrayRow.Slot}#{slice}")
                && !NumberedPairsFor(arrayRow.Slot).Any(kv => kv.Value == ColorSibling(row));
        }
        foreach (var row in _current.Rows.Where(r => r.Hidden).ToList())
            Rows.Remove(row);
    }

    /// <summary>One slot in words. Branding layers follow the slot map; everything else follows the texture name.</summary>
    static string Describe(TextureName n, LayerLabel? label)
    {
        if (label is not null)
            return label.Role switch
            {
                "crest" => n.Garment == "branding_pant" ? "Club mark (pants)" : "Team crest",
                "shield" => "NHL shield",
                "brand" => label.Detail?.StartsWith("adidas", StringComparison.OrdinalIgnoreCase) == true
                    ? "Adidas → Fanatics (brand)" : "Fanatics (brand)",
                "epw_tag" => "End Plastic Waste · back-hem tag",
                "sponsor" => label.Detail is { } detail ? detail + " · sponsor" : "Sponsor",
                "shoulder" => "Shoulder logo",
                "blank" => "Blank in NHL 24",
                _ => label.Role,
            };
        return n.Garment switch
        {
            "jersey" or "pant" or "sock" => (n.IsBase, n.Role) switch
            {
                (true, "color") => Cap(n.Garment) + " base colour (fabric regions)",
                (true, "coeff") => Cap(n.Garment) + " material coeff (kept stock)",
                (true, _) => Cap(n.Garment) + " base normal",
                (false, "color") => Cap(n.Garment) + " colour art",
                (false, "coeff") => Cap(n.Garment) + " material coeff",
                _ => Cap(n.Garment) + " normal map",
            },
            "font" => n.Role == "color" ? "Numbers font" : "Font " + n.Role,
            "helmetlogo" => "Helmet sponsor",
            _ => n.Garment + " " + n.Role,
        };
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    void ShowRows()
    {
        foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        if (_current is null) return;
        foreach (var r in _current.Rows.Where(r => !r.Hidden).OrderBy(r => r.Garment).ThenBy(r => r.Leaf, StringComparer.Ordinal))
        {
            r.PropertyChanged += OnRowChanged;
            r.SourceTextChanged = ResolveSourceText;
            Rows.Add(r);
        }
        LabelSlots();
        HideMissingSlots();
        _ = DecodeSourcesAsync(Rows.ToList());
        _ = PushRightAsync();
    }

    async Task LoadStock24Async()
    {
        if (_folder24 is null) return;
        var folder = _folder24;
        Stock24 = await Images.Nhl24FolderAsync(folder, s => Status = s, expected: ExpectedFor(folder));
        _stockFolder = folder;
        _numbered.Clear();
        if (folder != _folder24) return;
        foreach (var r in Rows) UpdateThumbs(r);
        ClampArrayLayers();
        HideMissingSlots();
        MatchLogoShapes();
        RepairBaseColours();
        LabelSlots();
        BakeStamps();
        Status = $"{folder}: {Rows.Count} slots, {Rows.Count(r => r.Kind != SourceKind.Stock || r.Layers.Any(l => l.Source27 != null))} with a source. {Stock24.Count} stock images.";
        await PushRightAsync();
    }

    /// <summary>The texture names the index says this folder has, used to reject a partial stock dump.</summary>
    IReadOnlyList<string>? ExpectedFor(string folder) =>
        Catalog24 is not null && Catalog24.ByFolder.TryGetValue(folder, out var slots)
            ? slots.Select(s => s.Name).ToList()
            : null;

    /// <summary>
    /// Hides the array layer rows the destination array cannot hold. The row count came from the numbered `_N_color`
    /// textures, but the array usually has fewer slices (Detroit jersey 4 of 6, pant 2 of 3; Florida jersey 6 of 7), which
    /// is what put a "free sponsor slot" in the grid that could never be written. Filled rows are kept and labelled.
    /// </summary>
    void ClampArrayLayers()
    {
        int removed = 0;
        foreach (var row in Rows.Where(r => r.IsArray && r.Name.Role == "color" && IsLogoGarment(r.Name.Garment)).ToList())
        {
            int slices = Matcher.SliceCount(Stock24, row.Slot);
            if (slices == 0) continue;
            int hidden = Matcher.ClampToSlices(row, slices);
            if (hidden == 0) continue;
            removed += hidden;
            row.Reason = $"{row.Layers.Count} layer(s) in the NHL 24 array; {hidden} more had no slice and were hidden";
        }
        if (removed > 0) SuiteLog.Write($"array layers: hid {removed} layer row(s) with no slice in {Short(_current?.Folder24)}");
    }

    /// <summary>
    /// The Fanatics mark for one garment. The paired NHL 27 set is preferred, otherwise the nearest other set that has
    /// one is used: the mark is the same image league-wide, so the brand layer is never left stock as Adidas. Null when
    /// no decoded NHL 27 set supplies both its colour and its normal map.
    /// </summary>
    (Nhl27Texture Color, Nhl27Texture Normal)? FanaticsFromAnyNhl27Set(string garment, string? preferTeam)
    {
        if (_fanaticsCache.TryGetValue(garment, out var cached)) return cached;
        (Nhl27Texture, Nhl27Texture)? hit = null;
        if (Catalog27 is not null)
        {
            foreach (var folder in Catalog27.ByFolder.Keys
                         .OrderBy(f => TextureName.Parse(f + "/x").Team == preferTeam ? 0 : 1)
                         .ThenBy(f => f, StringComparer.Ordinal))
            {
                var list = Catalog27.ByFolder[folder];
                foreach (var tex in list)
                {
                    if (!tex.Name.Contains(garment + "_", StringComparison.Ordinal)) continue;
                    var n = TextureName.Parse(tex.Name);
                    if (n.Garment != garment || n.Role != "color" || n.Layer is not { } layer || n.IsArrayName) continue;
                    var png = TextureImages.Nhl27Png(tex.Name);
                    if (!File.Exists(png) || SlotRoles.KnownLogo(png) != "brand_fanatics") continue;
                    var normal = list.FirstOrDefault(x => TextureName.Parse(x.Name) is { Role: "normal" } m
                        && m.Garment == garment && m.Layer == layer);
                    if (normal is null || !File.Exists(TextureImages.Nhl27Png(normal.Name))) continue;
                    hit = (tex, normal);
                    break;
                }
                if (hit is not null) break;
            }
        }
        _fanaticsCache[garment] = hit;
        return hit;
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

        // Pass 0: the role-based slot map (crest→crest, brand adidas→Fanatics, shield→shield, ...), exact at 100%.
        var store = SlotMaps;
        foreach (var row in Rows)
        {
            // A saved automatic row can already be marked Nhl27 by an earlier branding pass. It still needs
            // reconciliation: the role map, not that stale source marker, owns every non-manual array layer.
            if (!row.IsArray || row.Manual) continue;
            var name = row.Name;
            if (name.Role != "color" || !name.Garment.StartsWith("branding", StringComparison.Ordinal)) continue;
            if (!candidates.TryGetValue(name.Garment, out var cands)) continue;
            var images24 = row.Layers.Select(l => (l.Layer, (string?)Stock24.GetValueOrDefault($"{row.Slot}#{l.Layer}"))).ToList();
            var images27 = cands.Where(c => c.Layer is not null).Select(c => (c.Layer!.Value, (string?)c.Png)).ToList();
            if (images24.All(i => i.Item2 is null) || images27.Count == 0) continue;
            var entry = store.Resolve(_current.Folder24, _folder27, name.Garment, images24, images27);
            var used = ClaimedFor(name.Garment);
            var sources = cands.Where(c => c.Layer is not null).ToDictionary(c => c.Layer!.Value, c => c.Name);
            int linked = SlotRoles.ReconcileColourArray(row, entry, sources);
            foreach (var link in entry.Links.Where(l => l.Nhl27 is not null))
            {
                if (!sources.TryGetValue(link.Nhl27!.Value, out var source)) continue;
                var layer = row.Layers.FirstOrDefault(l => l.Layer == link.Nhl24);
                if (layer?.Source27 != source) continue;
                used.Add(source);
                arrayLayerSource[(name.Garment, layer.Layer)] = source;
            }
            if (linked == 0) continue;
            row.Confidence = 1.0;
            var roles = string.Join(", ", entry.Links.Select(l => $"{l.Nhl24}:{l.Role}" + (l.Nhl27 is null ? "→stock" : $"→{l.Nhl27}")));
            row.Reason = $"slot map ({roles})" + (entry.NoSlot27.Count > 0 ? $"; NHL 27 layer(s) {string.Join(",", entry.NoSlot27)} have no NHL 24 slot" : "");
            filled += linked;
        }
        store.Save();

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
                // A league-wide logo (NHL shield, maker mark, patch) only pairs with that same logo, and anything else
                // never with one: shape alone paired sponsor patches and shield-shaped crests with the NHL shield.
                var league = SlotRoles.KnownLogo(stock);
                foreach (var i in items.Where(i => !used.Contains(i.Name) && SlotRoles.KnownLogo(i.Sil) == league))
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

            // Never guess a logo from its layer number. Roles own manufacturer/team marks.

            if (byShape + byIndex > 0)
            {
                row.Confidence = byIndex == 0 ? 0.9 : 0.8;
                row.Reason = $"{byShape} layer(s) by logo shape, {byIndex} by layer index (manufacturer/logo swap) of {row.Layers.Count}";
                filled += byShape + byIndex;
                swapped += byIndex;
            }
        }

        // Pass 2: each individual `_N_color` slot follows the array layer that shows the same picture - found by picture,
        // never by number (NHL 24 Detroit pants: array 0 = shield, 1 = club; numbered 0 = club, 1 = empty, 2 = shield,
        // so "by number" put the Red Wings on the empty layer and sponsor patches turned into crests).  No picture match
        // = it stays stock.
        foreach (var array in Rows.Where(r => r.IsArray && r.Role == "color" && IsLogoGarment(r.Garment)).ToList())
            foreach (var layer in array.Layers)
            {
                if (!arrayLayerSource.TryGetValue((array.Garment, layer.Layer), out var source)) continue;
                if (NumberedFor(array.Slot, layer.Layer) is not { } slot) continue;
                var row = Rows.FirstOrDefault(r => r.Slot == slot);
                if (row is null || row.Manual || row.Kind != SourceKind.Stock) continue;
                row.Kind = SourceKind.Nhl27;
                row.Source27 = source;
                row.Confidence = 0.9;
                row.Reason = $"same picture as branding array layer {layer.Layer} (the game reads logos from the array)";
                filled++;
            }

        // Required branding rules also repair old saved/manual picks. Normal maps follow
        // the selected logo's source layer, never the destination's numeric index.
        foreach (var array in Rows.Where(r => r.IsArray && r.Role == "color" && IsLogoGarment(r.Garment)))
        {
            if (!candidates.TryGetValue(array.Garment, out var cands)) continue;
            var entry = store.Resolve(_current.Folder24, _folder27, array.Garment,
                array.Layers.Select(l => (l.Layer, (string?)Stock24.GetValueOrDefault($"{array.Slot}#{l.Layer}"))).ToList(),
                cands.Where(c => c.Layer is not null).Select(c => (c.Layer!.Value, (string?)c.Png)).ToList());
            foreach (var label in entry.Layers24.Where(l => ManufacturerBranding.IsAdidas(l)
                         || (array.Garment == "branding_pant" && l.Role == "crest")))
            {
                var link = entry.Links.FirstOrDefault(l => l.Nhl24 == label.Layer);
                var color = array.Layers.FirstOrDefault(l => l.Layer == label.Layer);
                var candidate = cands.FirstOrDefault(c => c.Layer == link?.Nhl27);
                if (color is null) continue;
                if (link?.Nhl27 is null || candidate.Name is null)
                {
                    // Adidas may never survive a conversion, and the Fanatics mark is the same image league-wide, so when
                    // the paired set has no brand layer the colour and its normal are taken from the nearest set that has one.
                    var alt = ManufacturerBranding.IsAdidas(label)
                        ? FanaticsFromAnyNhl27Set(array.Garment, TextureName.Parse(_current.Folder24 + "/x").Team)
                        : null;
                    if (alt is not null)
                    {
                        ManufacturerBranding.ApplyToRows(Rows, new BrandingLayerSet(array.Slot, label.Layer,
                            TextureImages.Nhl27Png(alt.Value.Color.Name), TextureImages.Nhl27Png(alt.Value.Normal.Name)),
                            l => NumberedFor(array.Slot, l),
                            "Adidas → Fanatics logo + normal from another NHL 27 set (the paired set has no brand layer)");
                        continue;
                    }
                    color.Source27 = null; color.FilePath = null;
                    var oldNormal = Rows.FirstOrDefault(r => r.IsArray && r.Garment == array.Garment && r.Role == "normal")?
                        .Layers.FirstOrDefault(l => l.Layer == label.Layer);
                    if (oldNormal is not null) { oldNormal.Source27 = null; oldNormal.FilePath = null; }
                    array.Reason = ManufacturerBranding.IsAdidas(label) ? "REQUIRED: Fanatics artwork/normal missing; cannot export Adidas"
                        : "Pants team logo has no club-mark match; kept stock (not the NHL shield)";
                    continue;
                }
                color.Source27 = candidate.Name; color.FilePath = null;
                var normal = textures.FirstOrDefault(t => TextureName.Parse(t.Name) is { Role: "normal" } n
                    && n.Garment == array.Garment && n.Layer == link.Nhl27);
                if (normal is not null && File.Exists(TextureImages.Nhl27Png(normal.Name)))
                {
                    ManufacturerBranding.ApplyToRows(Rows, new BrandingLayerSet(array.Slot, label.Layer,
                        candidate.Png, TextureImages.Nhl27Png(normal.Name)), l => NumberedFor(array.Slot, l), ManufacturerBranding.IsAdidas(label) ? null
                            : "Pants club logo + matching normal; NHL shield is not a fallback");
                }
                else array.Reason = "Required matching logo normal map is missing; check before exporting";
            }
        }
        int shields = KeepShieldOffPants();
        store.Save();
        // Every colour layer with an NHL 27 source also brings its normal sibling, so the new logo never
        // sits on the old logo's relief (this runs after the pants rule, so reset layers get nothing).
        int normals = ManufacturerBranding.PropagateNormals(Rows, n => File.Exists(TextureImages.Nhl27Png(n)));
        if (filled > 0 || shields > 0 || normals > 0)
        {
            foreach (var r in Rows) UpdateThumbs(r);
            SuiteLog.Write($"logo shapes: matched {filled} layer(s), {swapped} by layer index, {normals} normal(s) followed, in {Short(_current.Folder24)}");
        }
    }

    /// <summary>
    /// Last word on pants (user rule): every NHL 24 pants set already has the NHL shield on the back, so a pants layer
    /// never ends up with the shield - not from the slot map, a shape match, a saved pick or a dropped file. Layers whose
    /// stock image is the shield stay stock too. The normal map of a reset layer goes back to stock with it.
    /// Returns the number of layers reset.
    /// </summary>
    int KeepShieldOffPants()
    {
        int reset = 0;
        string? Png(string? source27, string? file) => file ?? (source27 is null ? null : TextureImages.Nhl27Png(source27));
        foreach (var array in Rows.Where(r => r.IsArray && r.Garment == "branding_pant" && r.Role == "color"))
        foreach (var layer in array.Layers.Where(l => l.Source27 is not null || l.FilePath is not null))
        {
            bool stockShield = SlotRoles.LooksLikeShield(Stock24.GetValueOrDefault($"{array.Slot}#{layer.Layer}"));
            if (!stockShield && !SlotRoles.LooksLikeShield(Png(layer.Source27, layer.FilePath))) continue;
            layer.Source27 = null; layer.FilePath = null;
            var normal = Rows.FirstOrDefault(r => r.IsArray && r.Garment == "branding_pant" && r.Role == "normal")?
                .Layers.FirstOrDefault(l => l.Layer == layer.Layer);
            if (normal is not null) { normal.Source27 = null; normal.FilePath = null; }
            var single = Rows.FirstOrDefault(r => !r.IsArray && r.Garment == "branding_pant" && r.Role == "color" && r.Name.Layer == layer.Layer);
            if (single is not null) single.ClearSource();
            array.Reason = $"pants layer {layer.Layer} kept stock: " + (stockShield ? "it is the NHL shield (already on the back of every pants set)"
                : "the NHL shield never replaces a pants team logo");
            SuiteLog.Write($"pants rule: layer {layer.Layer} of {array.Slot} kept stock ({(stockShield ? "stock shield" : "NHL 27 shield refused")})");
            reset++;
        }
        foreach (var row in Rows.Where(r => !r.IsArray && r.Garment == "branding_pant" && r.Role == "color" && r.Kind != SourceKind.Stock))
        {
            if (!SlotRoles.LooksLikeShield(Png(row.Kind == SourceKind.Nhl27 ? row.Source27 : null, row.Kind == SourceKind.File ? row.FilePath : null))) continue;
            row.ClearSource();
            row.Reason = "kept stock: the NHL shield never replaces a pants team logo";
            reset++;
        }
        return reset;
    }

    public IReadOnlyList<string> ShoulderOptions { get; } =
        [ShoulderPatches.Label(null), .. ShoulderPatches.Choices.Select(c => ShoulderPatches.Label(c))];

    /// <summary>The selected uniform's Shoulder patches choice, saved in its jersey slot-map entry.</summary>
    public string ShoulderOption
    {
        get => ShoulderPatches.Label(JerseyEntry(create: false)?.Shoulders);
        set
        {
            if (_folder24 is null || _folder27 is null) return;
            string? choice = ShoulderPatches.Choices.FirstOrDefault(c => ShoulderPatches.Label(c) == value);
            var entry = JerseyEntry(create: true)!;
            if (entry.Shoulders == choice) return;
            entry.Shoulders = choice;
            SlotMaps.Save();
            Raise();
            PreflightItems.Clear();
            PreflightSummary = "Shoulder patches changed. Select Check to validate it.";
        }
    }

    SlotMapEntry? JerseyEntry(bool create)
    {
        if (_folder24 is null || _folder27 is null) return null;
        var entry = SlotMaps.Find(_folder24, _folder27, "branding_jersey");
        if (entry is null && create)
            SlotMaps.Entries.Add(entry = new SlotMapEntry { Folder24 = _folder24, Folder27 = _folder27, Garment = "branding_jersey" });
        return entry;
    }

    static Suite.Core.Frostbite.BundleIndex? _gameLayouts;
    static string? _gameLayoutsDir;

    /// <summary>Plain names of the stock slots of one slot-map entry's array, read from the game's jersey stamp.</summary>
    public async Task<Func<SlotMapEntry, IReadOnlyList<string>?>> PhysicalSlotNamesAsync()
    {
        string game = Settings.Nhl24GameDir;
        if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game)) return _ => null;
        if (_gameLayouts is null || _gameLayoutsDir != game)
        {
            Status = "Reading the game's jersey layouts…";
            _gameLayouts = await Task.Run(() => Suite.Core.Frostbite.BundleIndex.Shared(game));
            _gameLayoutsDir = game;
            Status = "Slot map ready";
        }
        var index = _gameLayouts;
        return entry => Rows.FirstOrDefault(r => r.IsArray && r.Name.Garment == entry.Garment && r.Name.Role == "color") is { } array
            ? SlotNames.Physical(index, array.Slot) : null;
    }

    /// <summary>The slot-map entries of the current folder pair, with image lookups for both games' layers.</summary>
    public List<(SlotMapEntry Entry, Func<int, string?> Png24, Func<int, string?> Png27)> SlotMapItems()
    {
        var items = new List<(SlotMapEntry, Func<int, string?>, Func<int, string?>)>();
        if (_current is null || _folder27 is null || Catalog27 is null) return items;
        var textures27 = Catalog27.ByFolder.TryGetValue(_folder27, out var t) ? t : [];
        foreach (var entry in SlotMaps.Entries.Where(e => e.Folder24 == _current.Folder24 && e.Folder27 == _folder27).OrderBy(e => e.Garment))
        {
            var array = Rows.FirstOrDefault(r => r.IsArray && r.Name.Garment == entry.Garment && r.Name.Role == "color");
            if (array is null) continue;
            var garment = entry.Garment;
            items.Add((entry,
                layer => Stock24.GetValueOrDefault($"{array.Slot}#{layer}"),
                layer => textures27.Select(x => (x, n: TextureName.Parse(x.Name)))
                    .Where(x => x.n.Garment == garment && x.n.Role == "color" && x.n.Layer == layer && !x.n.IsArrayName)
                    .Select(x => TextureImages.Nhl27Png(x.x.Name)).FirstOrDefault(File.Exists)));
        }
        return items;
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
        RepairBaseColours();
        LabelSlots();
        BakeStamps();
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
        RefreshViewer(row);
    }

    public void Save()
    {
        Project.Save();
        Status = $"Saved {Project.Folders.Count} folder matches to {MatchProject.DefaultPath}";
    }

    /// <summary>The single Standard-mode action: reconcile the selected uniform, validate it, and refresh the preview.</summary>
    public async Task ConvertCurrentAsync()
    {
        if (Catalog27 is null || _current is null)
        {
            Status = "Choose a uniform first.";
            return;
        }
        Busy = true;
        try
        {
            await LoadStock24Async();
            var report = await RunPreflightAsync(currentFolderOnly: true);
            Status = report.Summary;
            await PushRightAsync();
        }
        catch (Exception ex)
        {
            PreflightSummary = "The conversion check could not finish";
            PreflightItems.Clear();
            PreflightItems.Add(new PreflightItem(PreflightSeverity.Error, "Conversion check failed", ex.Message));
            Status = PreflightSummary + ": " + ex.Message;
        }
        finally { Busy = false; }
    }

    /// <summary>Runs the same automatic repair and validation path used immediately before every export.</summary>
    public async Task<PreflightReport> RunPreflightAsync(bool currentFolderOnly, IEnumerable<FolderMatch>? canonicalFolders = null)
    {
        if (Catalog27 is null) throw new InvalidOperationException("The texture indexes are not loaded yet.");
        List<FolderMatch> folders = canonicalFolders?.ToList()
            ?? (currentFolderOnly && _current is not null ? [_current] : Project.Folders.ToList());
        var sources = folders.SelectMany(f => f.Rows).SelectMany(r => r.Layers.Select(l => l.Source27).Append(r.Kind == SourceKind.Nhl27 ? r.Source27 : null))
            .Where(n => n is not null && Catalog27.ByName.ContainsKey(n)).Select(n => Catalog27.ByName[n!]).ToList();
        // Manufacturer recognition needs every candidate's real alpha, not just an old saved layer pick.
        foreach (var folder in folders)
            if (folder.Folder27 is not null && Catalog27.ByFolder.TryGetValue(folder.Folder27, out var textures))
                sources.AddRange(textures.Where(t => TextureName.Parse(t.Name) is { Layer: not null } n
                    && n.Role is "color" or "normal" && IsLogoGarment(n.Garment)));
        await Images.Nhl27Async(sources, s => Status = s);

        var items = new List<PreflightItem>();
        foreach (var folder in folders)
        {
            if (folder.Folder27 is null || !Catalog27.ByFolder.TryGetValue(folder.Folder27, out var textures))
            {
                items.Add(new PreflightItem(PreflightSeverity.Error, "No NHL 27 source is selected",
                    $"{FriendlyFolder(folder.Folder24)}: choose a source in Advanced mode before converting."));
                continue;
            }
            int baseChanges = BaseColourRules.RepairAutomaticBaseColours(folder.Rows, textures);
            int logoChanges = 0;
            var entries = new List<SlotMapEntry>();
            var arrays = folder.Rows.Where(r => r.IsArray && !r.Manual && r.Role == "color"
                && r.Name.Garment.StartsWith("branding", StringComparison.Ordinal)).ToList();
            if (arrays.Count > 0)
            {
                var stock = await Images.Nhl24FolderAsync(folder.Folder24);
                foreach (var row in arrays)
                {
                    var candidates = textures.Where(t => TextureName.Parse(t.Name) is { Role: "color", Layer: not null } n
                            && !n.IsArrayName && n.Garment == row.Garment)
                        .Select(t => (Name: t.Name, Layer: TextureName.Parse(t.Name).Layer!.Value)).ToList();
                    var images24 = row.Layers.Select(l => (l.Layer, (string?)stock.GetValueOrDefault($"{row.Slot}#{l.Layer}"))).ToList();
                    var images27 = candidates.Select(c => (c.Layer, (string?)TextureImages.Nhl27Png(c.Name))).ToList();
                    if (images24.All(i => i.Item2 is null) || images27.Count == 0) continue;
                    var entry = SlotMaps.Resolve(folder.Folder24, folder.Folder27, row.Garment, images24, images27);
                    entries.Add(entry);
                    logoChanges += SlotRoles.ReconcileColourArray(row, entry, candidates.ToDictionary(c => c.Layer, c => c.Name));
                }
            }
            int normalChanges = ManufacturerBranding.PropagateNormals(folder.Rows, n => File.Exists(TextureImages.Nhl27Png(n)));
            if (baseChanges + logoChanges + normalChanges > 0)
            {
                var repairs = new[] { (Count: baseChanges, Name: "base texture"), (Count: logoChanges, Name: "logo layer"),
                    (Count: normalChanges, Name: "normal map") }.Where(r => r.Count > 0)
                    .Select(r => $"{r.Count} {r.Name}{(r.Count == 1 ? "" : "s")}");
                items.Add(new PreflightItem(PreflightSeverity.Repaired, $"Repaired {FriendlyFolder(folder.Folder24)}",
                    "Updated " + string.Join(", ", repairs) + " automatically. Manual overrides were preserved."));
            }
            items.AddRange(ConversionPreflight.InspectRows(folder.Rows,
                n => File.Exists(TextureImages.Nhl27Png(n)), File.Exists));
            foreach (var entry in entries) items.AddRange(ConversionPreflight.InspectUnplacedLogos(entry));
            if (entries.FirstOrDefault(e => e.Garment == "branding_jersey") is { } jersey) items.Add(ConversionPreflight.InspectShoulders(jersey));
        }
        var report = new PreflightReport(items);
        if (report.CanExport && report.WarningCount == 0)
            items.Insert(0, new PreflightItem(PreflightSeverity.Ready, "This uniform is ready",
                "Base artwork, logo slots, normal maps, and image sources passed the export checks."));
        report = new PreflightReport(items);
        PreflightItems.Clear();
        foreach (var item in items) PreflightItems.Add(item);
        PreflightSummary = report.Summary;
        return report;
    }

    public async Task<string> AddToModListAsync(string name, bool currentFolderOnly)
    {
        if (Catalog27 is null) throw new InvalidOperationException("Not loaded.");
        var preflight = await RunPreflightAsync(currentFolderOnly);
        if (!preflight.CanExport)
            throw new InvalidOperationException(preflight.Summary + ". Review the conversion report before exporting.");
        List<FolderMatch> folders = currentFolderOnly && _current is not null ? [_current] : Project.Folders.ToList();
        SlotMaps.Save();
        Project.Save();
        var mod = Project.ToModFile(name, folders);
        var manufacturerLayers = new List<BrandingLayerSet>();
        foreach (var folder in folders)
        {
            var arrays = folder.Rows.Where(r => r.IsArray && r.Role == "color" && IsLogoGarment(r.Garment)).ToList();
            if (arrays.Count == 0) continue;
            if (!SlotRoles.HasManufacturerReferences) throw new InvalidOperationException("Manufacturer logo references are missing; cannot verify Adidas → Fanatics branding.");
            Status = "Checking Adidas → Fanatics branding: " + folder.Folder24;
            var stock = await Images.Nhl24FolderAsync(folder.Folder24);
            var textures = folder.Folder27 is not null && Catalog27.ByFolder.TryGetValue(folder.Folder27, out var ts) ? ts : [];
            foreach (var array in arrays)
            {
                // A layer with no slice in the NHL 24 array can never be written there: drop it instead of refusing the
                // whole set (the NHL 24 array usually holds fewer slices than the numbered logo textures).
                var present = array.Layers.Where(l => stock.TryGetValue($"{array.Slot}#{l.Layer}", out var image) && File.Exists(image)).ToList();
                var absent = array.Layers.Where(l => !present.Contains(l)).Select(l => l.Layer).ToList();
                if (absent.Count > 0)
                {
                    SuiteLog.Write($"{array.Slot}: layer(s) {string.Join(",", absent)} have no slice in NHL 24 and are not written");
                    ManufacturerBranding.PruneLayers(mod, array.Slot[..^"_color_array".Length], absent);
                }
                if (present.Count == 0)
                    throw new InvalidOperationException($"No stock branding images for {array.Slot}; cannot verify manufacturer/team-logo roles.");
                var labels24 = SlotRoles.Label(present.Select(l => (l.Layer, (string?)stock.GetValueOrDefault($"{array.Slot}#{l.Layer}"))).ToList(), array.Garment);
                var adidas = labels24.Where(ManufacturerBranding.IsAdidas).ToList();
                var candidates = textures.Where(t => TextureName.Parse(t.Name) is { Role: "color", Layer: not null } n
                    && n.Garment == array.Garment).Select(t => (Layer: TextureName.Parse(t.Name).Layer!.Value, Png: TextureImages.Nhl27Png(t.Name))).ToList();
                var labels27 = SlotRoles.Label(candidates.Select(c => (c.Layer, (string?)c.Png)).ToList(), array.Garment);
                if (array.Garment == "branding_pant")
                {
                    var crest = labels27.FirstOrDefault(l => l.Role == "crest");
                    foreach (var target in labels24.Where(l => l.Role == "crest"))
                    {
                        var crestNormal = crest is null ? null : textures.FirstOrDefault(t => TextureName.Parse(t.Name) is { Role: "normal" } n
                            && n.Garment == array.Garment && n.Layer == crest.Layer);
                        if (crest is null || crestNormal is null || !File.Exists(TextureImages.Nhl27Png(crestNormal.Name)))
                        {
                            ManufacturerBranding.KeepStockLayer(mod, array.Slot, target.Layer, SlotRoles.PairNumbered(array.Slot, stock).GetValueOrDefault(target.Layer));
                            continue;
                        }
                        manufacturerLayers.Add(new BrandingLayerSet(array.Slot, target.Layer,
                            candidates.First(c => c.Layer == crest.Layer).Png, TextureImages.Nhl27Png(crestNormal.Name)));
                    }
                }
                if (adidas.Count == 0) continue;
                var fanatics = labels27.Where(ManufacturerBranding.IsFanatics).ToList();
                string? png = null, normalPng = null;
                if (fanatics.Count > 0)
                {
                    var fanLayer = fanatics[0].Layer;
                    png = candidates.First(c => c.Layer == fanLayer).Png;
                    var normal = textures.FirstOrDefault(t => TextureName.Parse(t.Name) is { Role: "normal" } n
                        && n.Garment == array.Garment && n.Layer == fanLayer);
                    normalPng = normal is null ? null : TextureImages.Nhl27Png(normal.Name);
                }
                if (png is null || normalPng is null || !File.Exists(normalPng))
                {
                    // Same rule as the matcher: the mark and its normal come from another NHL 27 set rather than failing.
                    var alt = FanaticsFromAnyNhl27Set(array.Garment, TextureName.Parse(folder.Folder24 + "/x").Team);
                    if (alt is not null)
                    {
                        png = TextureImages.Nhl27Png(alt.Value.Color.Name);
                        normalPng = TextureImages.Nhl27Png(alt.Value.Normal.Name);
                    }
                }
                if (png is null || normalPng is null || !File.Exists(png) || !File.Exists(normalPng))
                    throw new InvalidOperationException($"Fanatics logo and normal map missing for {folder.Folder24}/{array.Garment}, and no NHL 27 set supplies them. Adidas branding cannot be kept in a converted kit.");
                foreach (var label in adidas) manufacturerLayers.Add(new BrandingLayerSet(array.Slot, label.Layer, png, normalPng));
            }
        }
        ManufacturerBranding.ApplyToMod(mod, manufacturerLayers);
        if (ModFile.Load(ModFile.PathFor(name)) is { } existing)
        {
            // Adding to a mod never wipes it: slots this batch touches are replaced, everything else stays.
            // "Add all matched folders" still rebuilds every matched slot, because the batch covers them all;
            // entries whose slots are not in the project at all are kept too.
            var slots = folders.SelectMany(f => f.Rows.Select(r => r.Slot)).ToHashSet(StringComparer.Ordinal);
            var before = existing.Replacements.Count;
            existing.Replacements.RemoveAll(r => slots.Contains(r.Slot));
            existing.Replacements.AddRange(mod.Replacements);
            existing.Comment = mod.Comment;
            existing.Legacy ??= mod.Legacy;
            mod = existing;
            if (mod.Replacements.Count < before / 2)
                SuiteLog.Write($"mod '{name}' shrank from {before} to {mod.Replacements.Count} replacements on save");
        }
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

    /// <summary>Loads the matching season home/away band for the viewer's Reference button.</summary>
    async Task PushReferenceAsync(string folder)
    {
        string season = UniformReference.CurrentSeason();
        bool homeOrAway = folder.EndsWith("/home", StringComparison.Ordinal) || folder.EndsWith("/away", StringComparison.Ordinal);
        ViewerMessage?.Invoke(new { type = "reference", available = false, loading = homeOrAway });
        try
        {
            UniformViewerReference.Selection? selected = null;
            if (homeOrAway)
            {
                selected = UniformViewerReference.FindLocal(folder, season);
                if (selected is null)
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    selected = await UniformViewerReference.FetchAsync(folder, season, http);
                }
            }
            if (_folder24 != folder) return; // a slower previous download must not replace the newly selected kit
            if (selected is null)
                ViewerMessage?.Invoke(new { type = "reference", available = false, loading = false });
            else
                ViewerMessage?.Invoke(new
                {
                    type = "reference", available = true, loading = false, season,
                    team = selected.TeamKey, role = selected.Role, kit = selected.Kit, kits = selected.Kits,
                    url = ViewerUrl.For(selected.Png),
                });
        }
        catch (Exception ex)
        {
            if (_folder24 != folder) return;
            SuiteLog.Write("viewer reference: " + ex);
            ViewerMessage?.Invoke(new { type = "reference", available = false, loading = false });
        }
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
        if (row.Kind == SourceKind.File && row.FilePath is not null) return ImageFor(row);
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
    /// Base colour uses the NHL 27 base art, never the plain preview with crests and sponsors already baked in.
    /// The game adds those logos from the branding arrays; using both sources draws them twice. Also migrates
    /// automatic composite rows saved by older versions. Explicit stamps and other manual choices stay untouched.
    /// </summary>
    void RepairBaseColours()
    {
        if (Catalog27 is null || _folder27 is null) return;
        if (!Catalog27.ByFolder.TryGetValue(_folder27, out var textures)) return;
        int repaired = BaseColourRules.RepairAutomaticBaseColours(Rows, textures);
        if (repaired == 0) return;
        foreach (var r in Rows) UpdateThumbs(r);
        SuiteLog.Write($"base colour: repaired {repaired} row(s) to use unbaked base art in {Short(_current?.Folder24)}");
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

    readonly Dictionary<string, Dictionary<int, string>> _numbered = new(StringComparer.Ordinal);
    readonly Dictionary<string, double[]?> _stockSil = new(StringComparer.Ordinal);

    /// <summary>All array layer ↔ numbered texture pairs of one array (<see cref="SlotRoles.PairNumbered"/>).</summary>
    IReadOnlyDictionary<int, string> NumberedPairsFor(string colorArraySlot)
    {
        if (!_numbered.TryGetValue(colorArraySlot, out var pairs))
            _numbered[colorArraySlot] = pairs = SlotRoles.PairNumbered(colorArraySlot, Stock24, _stockSil);
        return pairs;
    }

    /// <summary>The numbered texture showing the same picture as an array layer (<see cref="SlotRoles.PairNumbered"/>), or null.</summary>
    string? NumberedFor(string colorArraySlot, int layer) => NumberedPairsFor(colorArraySlot).GetValueOrDefault(layer);

    /// <summary>The colour-array row of a branding garment — the one the numbered textures mirror.</summary>
    SlotMatch? ArrayRow(string garment) =>
        _current?.Rows.FirstOrDefault(r => r.IsArray && r.Role == "color" && r.Garment == garment);

    /// <summary>The numbered colour slot a numbered normal/coeff row mirrors (…_N_normal → …_N_color).</summary>
    static string ColorSibling(SlotMatch row) => row.Role == "color" ? row.Slot
        : row.Slot.EndsWith("_" + row.Role, StringComparison.Ordinal) ? row.Slot[..^(row.Role.Length + 1)] + "_color" : row.Slot;

    public string? StockImage(SlotMatch row) => Stock24.GetValueOrDefault(row.Slot) ?? Stock24.GetValueOrDefault(row.Slot + "#0");

    /// <summary>
    /// Bakes every saved stamp into its slot's colour art and points the row at the result. Stamps live in the project,
    /// so they survive re-matching; the row then exports like any other file source.
    /// </summary>
    public void BakeStamps()
    {
        if (Project.Stamps.Count == 0) return;
        int baked = 0;
        foreach (var stamp in Project.Stamps.Where(s => s.Enabled))
        {
            var row = Rows.FirstOrDefault(r => r.Slot == stamp.Slot);
            if (row is null || row.IsArray) continue;
            var basePath = File.Exists(stamp.Base) ? stamp.Base : Stock24.GetValueOrDefault(row.Slot);
            if (basePath is null || !File.Exists(basePath)) { Status = "Stamp skipped: no colour art for " + row.Leaf; continue; }
            if (!File.Exists(stamp.Image)) { Status = "Stamp skipped: the logo file is gone (" + stamp.Image + ")"; continue; }
            var png = TextureImages.Stamp(basePath, stamp.Image, stamp.X, stamp.Y, stamp.Size, stamp.Slot + "|" + stamp.Image);
            if (png is null) continue;
            row.Kind = SourceKind.File;
            row.FilePath = png;
            row.Confidence = 1;
            row.Manual = true;
            row.Reason = $"logo stamped at {stamp.X:P0}/{stamp.Y:P0}, {stamp.Size:P0} of the map";
            baked++;
        }
        if (baked > 0) foreach (var r in Rows) UpdateThumbs(r);
    }

    /// <summary>Adds or replaces the stamp for its slot, bakes it and saves the project.</summary>
    public void ApplyStamp(StampEntry stamp)
    {
        Project.Stamps.RemoveAll(s => s.Slot == stamp.Slot);
        Project.Stamps.Add(stamp);
        BakeStamps();
        Project.Save();
        Status = $"Stamped {Path.GetFileName(stamp.Image)} onto {stamp.Slot.Split('/')[^1]}: baked into the colour map and saved with the project";
    }

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
