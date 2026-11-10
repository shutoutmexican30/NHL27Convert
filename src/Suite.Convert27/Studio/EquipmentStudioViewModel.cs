using System.Collections.ObjectModel;
using System.Globalization;
using Suite.Core;
using Suite.Rosters;

namespace Suite.Convert27.Studio;

public sealed record EquipmentTeamChoice(int? Id, string Name);
public sealed record EquipmentFilterChoice(string Name, int? Value);

public sealed class EquipmentGridRow
{
    public required EquipmentEditKey Key { get; init; }
    public required string TeamName { get; init; }
    public required IReadOnlyDictionary<string, long> Zones { get; init; }
    public required bool Paired { get; init; }
    public bool Editable => Key.IsReserved && Paired;
    public string VariantName => Key.Variant == 25 ? "Fanatics 26-27 · home" : Key.Variant == 24 ? "Fanatics 26-27 · away"
        : Key.Variant == 0 ? "Stock · home" : Key.Variant == 1 ? "Stock · away" : $"Stock · variant {Key.Variant}";
    public string WriteState => Editable ? "Editable" : Key.IsReserved ? "Unpaired · read only" : "Read only";
    public string Palette => string.Join("  ", Zones.Take(3).Select(z => $"#{z.Value:X6}"));
}

public sealed class EquipmentZoneRow : Observable
{
    readonly Action _changed;
    string _hex;
    public EquipmentZoneRow(string name, long value, Action changed) { Name = name; _hex = $"{value:X6}"; _changed = changed; }
    public EquipmentZoneRow(string name, string value, Action changed) { Name = name; _hex = value; _changed = changed; }
    public string Name { get; }
    public string Label => "Zone " + Name[4..].PadLeft(2, '0');
    public string Hex { get => _hex; set { if (Set(ref _hex, (value ?? "").Trim().TrimStart('#').ToUpperInvariant())) { Raise(nameof(Swatch)); _changed(); } } }
    public string Swatch => Hex.Length == 6 && Hex.All(Uri.IsHexDigit) ? "#" + Hex : "#596273";
    public long Value => long.Parse(Hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}

/// <summary>Validated equipment and ordered uniform-slot editing inside Uniform Studio. Planning never writes to a game.</summary>
public sealed class EquipmentStudioViewModel : Observable
{
    readonly SuiteSettings _settings;
    readonly Dictionary<EquipmentEditKey, Dictionary<string, string>> _staged = [];
    readonly Dictionary<int, string> _teamNames = [];
    InstalledEquipmentEditor.Session? _session;
    GameBuildProfile? _selectedBuild;
    ModFile? _selectedMod;
    EquipmentTeamChoice? _selectedTeam;
    EquipmentFilterChoice? _selectedVariant, _selectedType;
    EquipmentGridRow? _selectedRow;
    string _status = "Choose a build and load its equipment.", _preflight = "", _slotSummary = "";
    int _targetVariant = 25, _loadToken, _slotToken;
    bool _busy;

    public EquipmentStudioViewModel(SuiteSettings settings)
    {
        _settings = settings;
        RefreshBuilds(); RefreshMods();
        SelectedVariant = Variants[0]; SelectedType = Types[0];
    }

    public Action<Action> Post { get; set; } = a => a();
    public ObservableCollection<GameBuildProfile> Builds { get; } = [];
    public ObservableCollection<ModFile> Mods { get; } = [];
    public ObservableCollection<EquipmentTeamChoice> Teams { get; } = [];
    public ObservableCollection<EquipmentGridRow> Rows { get; } = [];
    public ObservableCollection<EquipmentZoneRow> Zones { get; } = [];
    public ObservableCollection<UniformSlotInspection.Row> SlotRows { get; } = [];
    public ObservableCollection<string> SlotErrors { get; } = [];
    public int[] TargetVariants { get; } = [24, 25];
    public EquipmentFilterChoice[] Variants { get; } =
    [
        new("Fanatics home · 25", 25), new("Fanatics away · 24", 24),
        new("Stock home · 0 (read only)", 0), new("Stock away · 1 (read only)", 1), new("All variants", null),
    ];
    public EquipmentFilterChoice[] Types { get; } = [new("Helmets and gloves", null), new("Helmets · type 4", 4), new("Gloves · type 7", 7)];

    public GameBuildProfile? SelectedBuild
    {
        get => _selectedBuild;
        set { if (!Set(ref _selectedBuild, value) || value is null) return; _settings.SelectGameBuild(value.Id); _settings.Save(); _ = LoadAsync(); }
    }
    public string GameDir
    {
        get => _settings.ApplyTargetDir;
        set { _settings.UseGameBuild(value, value); _settings.Save(); RefreshBuilds(); _ = LoadAsync(); }
    }
    public ModFile? SelectedMod
    {
        get => _selectedMod;
        set { if (!Set(ref _selectedMod, value)) return; UpdateTeams(); _ = LoadSlotsAsync(); }
    }
    public EquipmentTeamChoice? SelectedTeam { get => _selectedTeam; set { if (Set(ref _selectedTeam, value)) RefreshRows(); } }
    public EquipmentFilterChoice? SelectedVariant { get => _selectedVariant; set { if (Set(ref _selectedVariant, value)) { if (value?.Value is 24 or 25) TargetVariant = value.Value.Value; RefreshRows(); } } }
    public EquipmentFilterChoice? SelectedType { get => _selectedType; set { if (Set(ref _selectedType, value)) RefreshRows(); } }
    public EquipmentGridRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value)) return;
            Zones.Clear();
            if (value is not null)
            {
                var values = _staged.TryGetValue(value.Key, out var staged) ? staged : value.Zones.ToDictionary(kv => kv.Key, kv => $"{kv.Value:X6}");
                foreach (string zone in NewUniformEquipmentPlan.Zones)
                    Zones.Add(new EquipmentZoneRow(zone, values[zone], StageCurrent));
            }
            Raise(nameof(CanEditSelected)); Raise(nameof(CanDuplicate)); Raise(nameof(SelectionDetails));
        }
    }
    public int TargetVariant { get => _targetVariant; set => Set(ref _targetVariant, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Preflight { get => _preflight; set => Set(ref _preflight, value); }
    public string SlotSummary { get => _slotSummary; set => Set(ref _slotSummary, value); }
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) { Raise(nameof(CanEditSelected)); Raise(nameof(CanDuplicate)); Raise(nameof(CanEdit)); } } }
    public bool CanEdit => !Busy && _session is not null;
    public bool CanEditSelected => CanEdit && SelectedRow?.Editable == true;
    public bool CanDuplicate => CanEdit && SelectedRow is not null;
    public string SelectionDetails => SelectedRow is null ? "Select a helmet or glove row." : SelectedRow.Key.Summary
        + (SelectedRow.Editable ? " · all 15 zones editable" : SelectedRow.Key.IsReserved
            ? " · unpaired in the two databases: read only" : " · stock row: read only, or use it as an explicit donor");

    public async Task LoadAsync()
    {
        int token = ++_loadToken;
        if (!SuiteSettings.IsNhl24Dir(_settings.ApplyTargetDir)) { Status = "Choose a complete NHL 24 build first."; return; }
        Busy = true; Status = "Reading both equipment databases…";
        try
        {
            string game = _settings.ApplyTargetDir;
            var session = await Task.Run(() => InstalledEquipmentEditor.Load(game, _settings));
            if (token != _loadToken) return;
            _session = session; _staged.Clear(); UpdateTeams(); RefreshRows();
            var plan = session.Document.BuildPlan();
            Status = $"Equipment loaded · {plan.Databases.Count} databases · stock and goalie equipment protected.";
            Preflight = "No pending changes. Only Fanatics variants 24 and 25 can be recolored or receive an explicit donor copy.";
            Raise(nameof(CanEdit)); Raise(nameof(CanDuplicate));
            await LoadSlotsAsync();
        }
        catch (Exception ex) { Status = "Equipment could not be loaded: " + ex.Message; }
        finally { if (token == _loadToken) Busy = false; }
    }

    void RefreshBuilds()
    {
        Builds.Clear(); foreach (var build in _settings.GameBuilds) Builds.Add(build);
        _selectedBuild = Builds.FirstOrDefault(b => b.Id == _settings.ActiveGameBuildId); Raise(nameof(SelectedBuild)); Raise(nameof(GameDir));
    }

    public void RefreshMods()
    {
        Mods.Clear();
        foreach (var file in Directory.EnumerateFiles(SuitePaths.Mods, "*.json").OrderByDescending(File.GetLastWriteTime))
        {
            try { if (ModFile.Load(file) is { Kind: NewUniformAddition.ModKind, Additions.Count: > 0 } mod) Mods.Add(mod); }
            catch (Exception ex) { SuiteLog.Write("equipment editor mod list: " + ex.Message); }
        }
        SelectedMod = Mods.OrderByDescending(m => m.Additions is { Count: 64 } additions
                && additions.Select(a => a.TeamId).Distinct().Count() == 32)
            .ThenByDescending(m => m.Saved).FirstOrDefault();
        if (SelectedMod is null) SlotSummary = "Save an installed new-uniform mod in Convert 27 to inspect its ordered logo slots.";
    }

    void UpdateTeams()
    {
        _teamNames.Clear();
        var map = VariantMap.Load();
        foreach (var team in map.Teams.Where(t => t.Index is >= 0 and <= 31)) _teamNames[team.Index] = team.Index == 31 ? "Utah" : team.Name27;
        foreach (var addition in SelectedMod?.Additions ?? [])
            _teamNames[addition.TeamId] = addition.TeamId == 31 ? "Utah" : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(addition.Team.Replace('_', ' '));
        var ids = SelectedMod?.Additions?.Select(a => a.TeamId).Distinct().Order().ToList()
            ?? _session?.Document.Rows().Select(r => (int)r.Key.Team).Distinct().Order().ToList() ?? _teamNames.Keys.Order().ToList();
        Teams.Clear(); Teams.Add(new EquipmentTeamChoice(null, SelectedMod is null ? "All teams" : "All teams in this mod"));
        foreach (int id in ids) Teams.Add(new EquipmentTeamChoice(id, TeamName(id)));
        _selectedTeam = Teams[0]; Raise(nameof(SelectedTeam)); RefreshRows();
    }

    string TeamName(int id) => _teamNames.GetValueOrDefault(id, $"Team {id}");

    void RefreshRows()
    {
        if (_session is null) return;
        var rows = _session.Document.Rows(SelectedTeam?.Id, SelectedType?.Value, SelectedVariant?.Value);
        Rows.Clear();
        foreach (var row in rows) Rows.Add(new EquipmentGridRow { Key = row.Key, TeamName = TeamName((int)row.Key.Team), Zones = row.Zones, Paired = _session.Document.IsPaired(row.Key) });
        SelectedRow = Rows.FirstOrDefault();
    }

    void StageCurrent()
    {
        if (SelectedRow is null) return;
        _staged[SelectedRow.Key] = Zones.ToDictionary(z => z.Name, z => z.Hex, StringComparer.Ordinal);
        Preflight = "Colors staged in memory. Choose Review changes before anything can be applied.";
    }

    EquipmentEditPlan Prepare()
    {
        var session = _session ?? throw new InvalidOperationException("Load a game build first.");
        foreach (var (key, zones) in _staged)
        {
            foreach (string zone in NewUniformEquipmentPlan.Zones)
                if (!zones.TryGetValue(zone, out string? hex) || hex.Length != 6 || !hex.All(Uri.IsHexDigit))
                    throw new InvalidDataException($"{key.Summary}, {zone}: enter a six-digit RGB color.");
            session.Document.SetZones(key, NewUniformEquipmentPlan.Zones.ToDictionary(z => z,
                z => long.Parse(zones[z], NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        }
        var plan = session.Document.BuildPlan();
        Preflight = $"Validation passed\n\n{plan.ChangedRows} recolored rows · {plan.Inserted.Count} duplicated rows across both databases\n"
            + "Only stockteamequipmenttable changes. Stock equipment, goalie gear, jerseys, textures and unrelated tables are preserved.\n"
            + "Apply makes a new current-state backup, checks both logical databases byte for byte, and rolls back on failure.";
        Status = plan.HasChanges ? "Review passed. Ready to apply to " + session.Game : "All requested changes are already installed.";
        return plan;
    }

    public void Review()
    {
        try { _ = Prepare(); }
        catch (Exception ex) { Status = "Review blocked: " + ex.Message; Preflight = ex.Message; }
    }

    public void DuplicateSelected()
    {
        try
        {
            _ = Prepare();
            var donor = SelectedRow?.Key ?? throw new InvalidOperationException("Select a donor row first.");
            var key = _session!.Document.Duplicate(donor, TargetVariant);
            SelectedVariant = Variants.First(v => v.Value == TargetVariant);
            RefreshRows(); SelectedRow = Rows.FirstOrDefault(r => r.Key == key);
            Status = $"Duplicated {donor.Type} into {key.Summary}. The installed target jersey art was verified.";
            Preflight = "Donor copied in memory with all 15 zones. Review changes, then apply to write both databases.";
        }
        catch (Exception ex) { Status = "Duplicate blocked: " + ex.Message; Preflight = ex.Message; }
    }

    public async Task ResetNhl27ColorsAsync()
    {
        if (_session is null || SelectedMod?.Additions is not { Count: > 0 } additions) { Status = "Choose a saved new-uniform mod first."; return; }
        Busy = true;
        try
        {
            var current = Prepare();
            string source = Path.Combine(AppContext.BaseDirectory, "data", "nhl27-equipment");
            var corrected = await Task.Run(() => NewUniformEquipmentPlanner.Build(current.Databases, _session.Document.MetaXml, source, additions));
            _session = _session with { Document = EquipmentEditorDocument.Load(corrected.Databases, _session.Document.MetaXml) };
            _staged.Clear(); RefreshRows();
            Status = "Verified NHL 27 colors are staged for every helmet and glove in this mod. Review and apply to write them.";
            Preflight = "The reset used the numbered home/away source kits, not the NHL 24 structural donor. All 15 zones were validated.";
        }
        catch (Exception ex) { Status = "NHL 27 color reset blocked: " + ex.Message; }
        finally { Busy = false; }
    }

    public async Task ApplyAsync()
    {
        if (_session is null) return;
        Busy = true;
        try
        {
            var session = _session;
            var plan = Prepare();
            string? backup = await Task.Run(() => InstalledEquipmentEditor.Apply(_settings, session, plan,
                line => Post(() => Status = line)));
            Status = backup is null ? "Everything was already current; no game files changed." : "Applied and verified. Backup: " + backup;
            await LoadAsync();
        }
        catch (Exception ex) { Status = "Apply failed: " + ex.Message; Preflight = ex.Message; }
        finally { Busy = false; }
    }

    public async Task LoadSlotsAsync()
    {
        int token = ++_slotToken;
        SlotRows.Clear(); SlotErrors.Clear();
        if (_session is null || SelectedMod?.Additions is not { Count: > 0 } additions) return;
        SlotSummary = "Reading installed artwork and every ordered logo slot…";
        try
        {
            string game = _session.Game;
            var result = await Task.Run(() =>
            {
                var index = Suite.Core.Frostbite.BundleIndex.Build(game,
                    line => Post(() => SlotSummary = "Reading installed uniforms: " + line));
                return UniformSlotInspection.Build(game, index, additions,
                    path => InstalledBrandingAudit.ResolveArtwork(path, SuitePaths.Nhl27Images), _settings.Workers,
                    line => Post(() => SlotSummary = line));
            });
            if (token != _slotToken) return;
            foreach (var row in result.Rows) SlotRows.Add(row);
            foreach (string error in result.Errors.Take(100)) SlotErrors.Add(error);
            SlotSummary = result.IsValid
                ? $"{result.Rows.Count} installed color/normal copies passed pixel and placed-role verification; {result.IntendedSponsors} front sponsors. Final in-game review is still required."
                : $"{result.Errors.Count} artwork/role problems found. No game files were changed by this audit; review the affected installed copies before applying any correction.";
        }
        catch (Exception ex) { SlotSummary = "Slot inspection unavailable: " + ex.Message; }
    }
}
