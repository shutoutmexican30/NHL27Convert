using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace Suite.Convert27.Studio;

/// <summary>
/// Uniform Studio: pick a team and uniform discovered from the game, see every texture of it as a card with its
/// ORIGINAL (from the game) and NEW (replacement) image, match replacements by name and role, override by hand,
/// and save the selection to the mod library. Nothing is written to the game here.
/// </summary>
public sealed class UniformStudioViewModel : Observable
{
    readonly SuiteSettings _settings;
    readonly TextureImages _images;
    AssetIndex? _index24;
    Nhl27Catalog? _catalog27;
    HashSet<string> _teamWords = [];
    List<ReplacementFile> _files = [];
    int _loadToken;

    public ObservableCollection<string> Teams { get; } = [];
    public ObservableCollection<string> Uniforms { get; } = [];
    public ObservableCollection<StudioCard> Cards { get; } = [];
    public ObservableCollection<ReplacementFile> UnmatchedFiles { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    string? _team, _uniform, _status = "", _summary = "";
    bool _busy, _offer27 = true;
    int _errors;

    public UniformStudioViewModel(SuiteSettings settings)
    {
        _settings = settings;
        _images = new TextureImages(settings);
        SuiteLog.Written += line => Post(() => { Log.Add(line); if (Log.Count > 2000) Log.RemoveAt(0); });
    }

    /// <summary>Marshals to the UI thread (set by the view).</summary>
    public Action<Action> Post { get; set; } = a => a();

    public string GameDir
    {
        get => _settings.Nhl24GameDir;
        set
        {
            if (value == _settings.Nhl24GameDir) return;
            _settings.Nhl24GameDir = value;
            _settings.Save();
            Raise();
            _ = LoadAsync();
        }
    }

    public string ReplacementDir
    {
        get => _settings.ReplacementDir;
        set
        {
            if (value == _settings.ReplacementDir) return;
            _settings.ReplacementDir = value;
            _settings.Save();
            Raise();
            _ = ScanReplacementsAsync();
        }
    }

    /// <summary>Also offer the matched NHL 27 uniform's textures as replacement candidates.</summary>
    public bool OfferNhl27 { get => _offer27; set { if (Set(ref _offer27, value)) _ = ScanReplacementsAsync(); } }

    public string? Team
    {
        get => _team;
        set
        {
            if (!Set(ref _team, value)) return;
            Uniforms.Clear();
            if (value is null || _index24 is null) return;
            var prefix = TeamPrefix(value);
            foreach (var u in _index24.Folders.Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && f.Count(c => c == '/') == prefix.Count(c => c == '/') + 1)
                         .Select(f => f[prefix.Length..])
                         .OrderBy(f => f.Split('/')[0]).ThenBy(f => TextureName.SetOrder(f.Split('/')[^1])))
                Uniforms.Add(u);
            Uniform = Uniforms.FirstOrDefault(u => u.EndsWith("/home", StringComparison.Ordinal)) ?? Uniforms.FirstOrDefault();
        }
    }

    public string? Uniform { get => _uniform; set { if (Set(ref _uniform, value)) _ = OpenUniformAsync(); } }
    public string Status { get => _status!; set => Set(ref _status, value); }
    public string Summary { get => _summary!; set => Set(ref _summary, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public int Errors { get => _errors; set => Set(ref _errors, value); }

    string Folder => _team is null || _uniform is null ? "" : TeamPrefix(_team) + _uniform;
    static string TeamPrefix(string team) => team.Contains('/') ? $"{TextureName.SkaterPrefix}{team}/" : $"{TextureName.SkaterPrefix}nhl/{team}/";

    void Error(string message)
    {
        Errors++;
        SuiteLog.Write("ERROR " + message);
        Status = message;
    }

    // ------------------------------------------------------------------ loading

    public async Task LoadAsync()
    {
        Busy = true;
        try
        {
            if (!SuiteSettings.IsNhl24Dir(_settings.Nhl24GameDir))
            {
                Status = "Choose the NHL 24 game folder (an NHL24PC build or an extracted CUSA37933 folder).";
                return;
            }
            Status = "Reading the NHL 24 asset index...";
            _index24 = await IndexStore.GetAsync(IndexStore.Nhl24, _settings, progress: s => Status = s);
            if (Directory.Exists(Path.Combine(_settings.Nhl27GameRoot, "Data")) && Directory.Exists(_settings.Nhl27ViewerDir))
            {
                var ix27 = await IndexStore.GetAsync(IndexStore.Nhl27, _settings, progress: s => Status = s);
                _catalog27 = await Task.Run(() => new Nhl27Catalog(ix27));
            }
            // Teams and uniforms are discovered from the game's folders, not hard-coded.
            var teams = _index24.Folders.Where(f => f.StartsWith(TextureName.SkaterPrefix, StringComparison.Ordinal))
                .Select(f => f[TextureName.SkaterPrefix.Length..].Split('/'))
                .Where(p => p.Length >= 4)
                .Select(p => p[0] == "nhl" ? p[1] : $"{p[0]}/{p[1]}")
                .Distinct().OrderBy(t => t.Contains('/') ? 1 : 0).ThenBy(t => t).ToList();
            _teamWords = teams.Select(t => t.Split('/')[^1]).Append("utah").ToHashSet();
            Teams.Clear();
            foreach (var t in teams) Teams.Add(t);
            Status = $"{_index24.Entries.Count:N0} NHL 24 textures indexed · {teams.Count} teams found.";
            Team ??= Teams.Contains("boston") ? "boston" : Teams.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Error("Could not read the game: " + ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    async Task OpenUniformAsync()
    {
        if (_index24 is null || _uniform is null) return;
        var token = ++_loadToken;
        var folder = Folder;
        Busy = true;
        try
        {
            Status = $"Loading the game's textures for {folder}...";
            var stock = await _images.Nhl24FolderAsync(folder, s => Status = s);
            if (token != _loadToken) return;
            var dumps = Directory.Exists(Path.Combine(SuitePaths.Nhl24Refs, SuitePaths.Flatten(folder)))
                ? Path.Combine(SuitePaths.Nhl24Refs, SuitePaths.Flatten(folder))
                : Path.Combine(_settings.LegacyRefsDir, SuitePaths.Flatten(folder));

            var cards = new List<StudioCard>();
            foreach (var e in _index24.InFolder(folder).OrderBy(e => e.Name.Garment).ThenBy(e => e.Path, StringComparer.Ordinal))
            {
                var leaf = e.Name.Leaf;
                var layers = stock.Keys.Where(k => k.StartsWith(e.Path + "#", StringComparison.Ordinal))
                    .Select(k => int.Parse(k[(e.Path.Length + 1)..])).Order().ToList();
                if (e.Kind == AssetKind.TextureArray && layers.Count > 0)
                    foreach (var l in layers)
                        cards.Add(MakeCard(e.Path, l, leaf, stock.GetValueOrDefault($"{e.Path}#{l}"), Path.Combine(dumps, $"{leaf}#{l}.dds")));
                else
                    cards.Add(MakeCard(e.Path, null, leaf, stock.GetValueOrDefault(e.Path), Path.Combine(dumps, leaf + ".dds")));
            }
            Cards.Clear();
            foreach (var c in cards) Cards.Add(c);
            await ScanReplacementsAsync(restoreSaved: true);
        }
        catch (Exception ex)
        {
            Error($"Could not open {folder}: {ex.Message}");
        }
        finally
        {
            if (token == _loadToken) Busy = false;
        }
    }

    StudioCard MakeCard(string slot, int? layer, string leaf, string? png, string dds)
    {
        var details = Dds.Describe(dds) ?? "not dumped yet";
        var key = MaterialKey.Of(layer is null ? leaf : $"{leaf}#{layer}", _teamWords);
        if (key.IsBase && key.Role == "color") details += " · alpha = fabric regions (kept)";
        return new StudioCard { Slot = slot, Layer = layer, Key = key, OriginalPng = png, Details = details };
    }

    // ------------------------------------------------------------------ replacements

    public async Task ScanReplacementsAsync(bool restoreSaved = false)
    {
        var files = new ConcurrentBag<ReplacementFile>();
        var dir = _settings.ReplacementDir;
        if (Directory.Exists(dir))
        {
            Status = "Scanning replacement folder...";
            var paths = Directory.EnumerateFiles(dir, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(p => p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
            await Parallel.ForEachAsync(paths, new ParallelOptions { MaxDegreeOfParallelism = _settings.Workers }, (p, _) =>
            {
                var f = new ReplacementFile { Path = p, Source = "folder", Key = MaterialKey.Of(Path.GetFileNameWithoutExtension(p), _teamWords) };
                try { f.Preview = TextureImages.PreviewOf(p); }
                catch (Exception ex) { SuiteLog.Write($"ERROR cannot read {Path.GetFileName(p)}: {ex.Message}"); }
                files.Add(f);
                return ValueTask.CompletedTask;
            });
        }
        if (_offer27 && _catalog27 is not null && Folder.Length > 0)
        {
            var best = Matcher.ProposeFolders(Folder, _catalog27, Matcher.DefaultTeamMap, VariantMap.Load()).FirstOrDefault();
            if (best is not null && _catalog27.ByFolder.TryGetValue(best.Folder27, out var tex))
            {
                Status = $"Decoding NHL 27 {best.Folder27.Split('/')[^3]}/{best.Folder27.Split('/')[^1]} ({best.Reason})...";
                var pngs = await _images.Nhl27Async(tex, s => Status = s);
                foreach (var (name, png) in pngs)
                    files.Add(new ReplacementFile { Path = png, Source = "NHL 27 " + best.Folder27.Split('/')[^1], Key = MaterialKey.Of(name.Split('/')[^1], _teamWords), Preview = png });
            }
        }
        _files = files.OrderBy(f => f.Source).ThenBy(f => f.Name).ToList();
        if (restoreSaved) RestoreSaved();
        var unused = StudioMatcher.AutoMatch(Cards, _files);
        Refresh(unused);
    }

    /// <summary>Re-applies a saved mod-library selection for this uniform as manual choices.</summary>
    void RestoreSaved()
    {
        var mod = ModFile.Load(ModFile.PathFor(ModName));
        if (mod is null) return;
        int n = 0;
        foreach (var r in mod.Replacements)
        {
            IEnumerable<(int? layer, string path)> items = r.Layers is { Count: > 0 } ? r.Layers.Select(kv => ((int?)kv.Key, kv.Value)) : [(null, r.Source)];
            foreach (var (layer, path) in items)
            {
                var card = Cards.FirstOrDefault(c => c.Slot == r.Slot && c.Layer == layer);
                if (card is null || !File.Exists(path)) continue;
                var file = _files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))
                           ?? AddFile(path, "saved");
                card.New = file; card.Replace = true; card.Manual = true; card.Score = 1; card.Reason = "saved selection";
                n++;
            }
        }
        if (n > 0) SuiteLog.Write($"restored {n} saved choices from '{mod.Name}'");
    }

    ReplacementFile AddFile(string path, string source)
    {
        var f = new ReplacementFile { Path = path, Source = source, Key = MaterialKey.Of(Path.GetFileNameWithoutExtension(path), _teamWords) };
        try { f.Preview = TextureImages.PreviewOf(path); } catch (Exception ex) { Error($"cannot read {Path.GetFileName(path)}: {ex.Message}"); }
        _files.Add(f);
        return f;
    }

    public IReadOnlyList<Candidate> CandidatesFor(StudioCard card)
    {
        var ranked = StudioMatcher.Rank(card, _files).ToList();
        // Everything else stays choosable by hand, below the ranked matches.
        ranked.AddRange(_files.Where(f => ranked.All(c => c.File != f)).Select(f => new Candidate(f, 0, "no name/role match (manual)")));
        return ranked;
    }

    public void Choose(StudioCard card, ReplacementFile? file, double score = 1, string reason = "chosen by hand")
    {
        card.Manual = true;
        card.New = file;
        card.Replace = file is not null;
        card.Score = file is null ? 0 : score;
        card.Reason = file is null ? "kept stock (chosen by hand)" : reason;
        Refresh(StudioMatcher.Unused(Cards, _files));
    }

    public void Browse(StudioCard card, string path)
    {
        var file = _files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) ?? AddFile(path, "browsed");
        Choose(card, file, 1, "file chosen by hand");
    }

    public void ResetAuto()
    {
        foreach (var c in Cards) c.Manual = false;
        Refresh(StudioMatcher.AutoMatch(Cards, _files));
    }

    public void Refresh(List<ReplacementFile>? unused = null)
    {
        unused ??= StudioMatcher.Unused(Cards, _files);
        UnmatchedFiles.Clear();
        foreach (var f in unused.Where(f => f.Source == "folder" || f.Source == "browsed")) UnmatchedFiles.Add(f);
        int replaced = Cards.Count(c => c.State == "REPLACED");
        int coeff = Cards.Count(c => c.State == "COEFF KEPT STOCK");
        int unusedCards = Cards.Count(c => c.State == "UNUSED");
        Summary = $"{Cards.Count} textures · {replaced} replaced · {unusedCards} unused (stock) · {coeff} coeff kept stock · {UnmatchedFiles.Count} unmatched replacement files";
        Status = Summary;
    }

    // ------------------------------------------------------------------ mod library

    public string ModName => $"Uniform Studio - {(_team ?? "").Replace('/', ' ')} {(_uniform ?? "").Replace('/', ' ')}";

    public string Save()
    {
        var mod = StudioMatcher.ToMod(ModName, Cards);
        if (mod.Replacements.Count == 0) throw new InvalidOperationException("Nothing is selected to replace.");
        var path = mod.Save();
        SuiteLog.Write($"saved '{mod.Name}' ({mod.Replacements.Count} replacements) to {path}");
        Status = $"Saved to the mod library: {path}. Nothing was written to the game.";
        return path;
    }
}
