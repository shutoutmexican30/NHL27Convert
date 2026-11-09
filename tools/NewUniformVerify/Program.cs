using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Suite.Convert27;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: NewUniformVerify <workspace-root> [--roles-only] [--donors] [--preview] [--commit] [--full-v32-install] [--live-preflight] [--folders-only] [team ...]");
    return 2;
} 
string root = Path.GetFullPath(args[0]);
if (args.Length == 3 && args[1] == "--stage-restore")
{
    // Recovery for an interrupted staging run. This option is hard-wired to the isolated fixture, never a playable build.
    string backup = args.Length == 2 ? args[1] : args[2];
    string target = Path.GetFullPath(Path.Combine(root, "Debugging", "new-uniform-integration", "apply-target", "game"));
    var snapshot = ApplyTarget.LoadSnapshot(Path.GetFullPath(backup));
    if (!string.Equals(GameLayout.Normalize(snapshot.Game), GameLayout.Normalize(target), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("That backup does not belong to the isolated Detroit staging copy.");
    if (ApplyTarget.RunningGame() is { } running) { Console.Error.WriteLine("REFUSED: " + running); return 20; }
    ApplyTarget.RestoreFromBackup(target, backup);
    var differences = ApplyTarget.Compare(target, backup);
    if (differences.Count > 0) throw new InvalidDataException("Staging restore differences: " + string.Join("; ", differences));
    Console.WriteLine("INTERRUPTED STAGING RUN RESTORED AND VERIFIED: " + backup);
    return 0;
}
bool rolesOnly = args.Contains("--roles-only", StringComparer.Ordinal);
bool showDonors = args.Contains("--donors", StringComparer.Ordinal);
bool preview = args.Contains("--preview", StringComparer.Ordinal);
bool commit = args.Contains("--commit", StringComparer.Ordinal);
bool fullV32Install = args.Contains("--full-v32-install", StringComparer.Ordinal);
bool livePreflight = args.Contains("--live-preflight", StringComparer.Ordinal);
bool installPreflight = args.Contains("--install-preflight", StringComparer.Ordinal);
if (installPreflight)
{
    // Mirror InstallAllNew's early stages exactly, before any game write: target problems, catalog load,
    // then canonical folder building. This is the stage that fails without leaving any suite.log lines.
    var settings = SuiteSettings.Load();
    Console.WriteLine("ApplyTargetDir: " + settings.ApplyTargetDir);
    var problems = ModApply.Problems(settings);
    foreach (string p in problems) Console.WriteLine("TARGET PROBLEM: " + p);
    var vm = new Convert27ViewModel(settings);
    await vm.LoadAsync();
    Console.WriteLine($"Catalog24 null: {vm.Catalog24 is null}; Catalog27 null: {vm.Catalog27 is null}");
    if (vm.Catalog24 is null || vm.Catalog27 is null) return 8;
    try
    {
        var earlyFolders = NewUniformExport.BuildDefaultFolders(vm.Catalog24, vm.Catalog27, vm.Variants);
        Console.WriteLine($"Canonical folders: {earlyFolders.Count}");
    }
    catch (Exception ex)
    {
        Console.WriteLine("BUILD FOLDERS FAILED: " + ex.GetType().Name + ": " + ex.Message);
        return 9;
    }
    Console.WriteLine("Install-preflight early stages OK.");
    return problems.Count == 0 ? 0 : 7;
}
if (args.Contains("--backup-only", StringComparer.Ordinal) || args.Contains("--restore-backup", StringComparer.Ordinal))
{
    // --backup-only: safety snapshot of the configured target. --restore-backup <dir>: restore it (game closed).
    var settings = SuiteSettings.Load();
    string target = settings.ApplyTargetDir ?? throw new InvalidOperationException("No apply target configured.");
    if (ApplyTarget.RunningGame() is { } running) { Console.WriteLine("REFUSED: " + running + " is running."); return 20; }
    if (args.Contains("--backup-only", StringComparer.Ordinal))
    {
        Console.WriteLine("Safety backup: " + ApplyTarget.Backup(target));
        return 0;
    }
    int at = Array.IndexOf(args, "--restore-backup");
    if (at < 0 || at + 1 >= args.Length) { Console.Error.WriteLine("--restore-backup needs a backup directory."); return 21; }
    string backup = args[at + 1];
    Console.WriteLine($"Restoring {target} from {backup}");
    ApplyTarget.RestoreFromBackup(target, backup);
    Console.WriteLine("Restore verified.");
    return 0;
}
if (args.Contains("--detroit-live-reset", StringComparer.Ordinal) || args.Contains("--detroit-live-install", StringComparer.Ordinal))
    return DetroitLive.Run(root, install: args.Contains("--detroit-live-install", StringComparer.Ordinal),
        verifyCommitted: VerifyCommitted);
if (args.Contains("--detroit-rows", StringComparer.Ordinal))
{
    // Read-only: report the fields that can affect Detroit's jersey-menu labels and ordering.
    const string target = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
    var settings = new SuiteSettings { ApplyTargetDir = target, Nhl24GameDir = target };
    var files = new LegacyFiles(target, settings);
    byte[] dbBytes = files.Read("gamemodes/database/t3db/nhl_game.db");
    string meta = WorkspaceFile(root, "Debugging", "detroit-rows-nhl_game-meta.xml");
    File.WriteAllBytes(meta, files.Read("gamemodes/database/t3db/nhl_game-meta.xml"));
    var db = T3Db.Load(dbBytes, meta);
    foreach (string tableName in new[] { JerseyRowPlanner.TableName, "leagueteamjerseys", "customteamjerseys", "stockteamequipmenttable" })
    {
        var table = db.Tables[tableName];
        string[] fields = table.Fields.Keys.Order().ToArray();
        for (int row = 0; row < table.InUse; row++)
        {
            string teamField = table.Fields.ContainsKey("teamid") ? "teamid" : "team";
            if (db.GetInt(table, row, teamField) != 10) continue;
            if (table.Fields.ContainsKey("style") && db.GetInt(table, row, "style") != 2) continue;
            Console.WriteLine($"{tableName} ROW {row}: " + string.Join(", ",
                fields.Select(field => $"{field}={db.GetInt(table, row, field)}")));
        }
    }
    return 0;

    static string WorkspaceFile(string root, params string[] parts)
    {
        string path = Path.GetFullPath(Path.Combine(new[] { root }.Concat(parts).ToArray()));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Diagnostic output escaped the workspace.");
        return path;
    }
}
if (args.Contains("--centerice-verify", StringComparer.Ordinal))
{
    // The Center Ice page's "Verify all vs template", headless and read-only: NHL 27 art for every team slot,
    // converted as the app does, validated against Centerlogo_Template.psd and rendered over its guides.
    var settings = SuiteSettings.Load();
    string ciGame = settings.ApplyTargetDir ?? throw new InvalidOperationException("No apply target configured.");
    Console.WriteLine("Target (read-only): " + ciGame);
    var ciIndex = BundleIndex.Build(ciGame, s => Console.WriteLine(s));
    var slots = CenterIceTemplate.TeamSlots();
    var nhl27 = await IndexStore.GetAsync(IndexStore.Nhl27, settings);
    var wanted = new List<Nhl27Texture>();
    foreach (var slot in slots)
    {
        string name = CenterIceSlots.Name(CenterIceSlots.Nhl27Slot(slot));
        var entry = nhl27.Entries.FirstOrDefault(e => e.Path == name);
        if (entry is not null) wanted.Add(new Nhl27Texture(entry));
    }
    Console.WriteLine($"Decoding {wanted.Count} NHL 27 center-ice textures...");
    var pngs = await new TextureImages(settings).Nhl27Async(wanted, s => Console.WriteLine(s));
    string dir = Path.Combine(root, "Debugging", "centerice-verify");
    Directory.CreateDirectory(dir);
    var reportLines = new List<string>();
    int passed = 0;
    foreach (var slot in slots)
    {
        try
        {
            string name = CenterIceSlots.Name(CenterIceSlots.Nhl27Slot(slot));
            if (!pngs.TryGetValue(name, out var png)) { reportLines.Add($"slot {slot}: no NHL 27 art decoded"); continue; }
            var stock = CenterIceSlots.Read(ciGame, ciIndex, slot);
            var decal = CenterIceImage.CutRedLine(ImageIO.ResizeSmooth(ImageIO.Load(png), stock.Image.Width, stock.Image.Height));
            var ciReport = CenterIceTemplate.Validate(decal);
            ImageIO.SavePng(CenterIceTemplate.VerificationCell(decal), Path.Combine(dir, $"slot-{slot}.png"));
            if (ciReport.Ok) passed++;
            string line = $"slot {slot} (NHL 27 slot {CenterIceSlots.Nhl27Slot(slot)}): {ciReport.Summary}";
            reportLines.Add(line);
            Console.WriteLine(line);
        }
        catch (Exception ex)
        {
            reportLines.Add($"slot {slot}: ERROR {ex.Message}");
            Console.WriteLine($"slot {slot}: ERROR {ex.Message}");
        }
    }
    File.WriteAllLines(Path.Combine(dir, "report.txt"), reportLines);
    CenterIceTemplate.WriteSheet(dir, slots, reportLines);
    Console.WriteLine($"CENTER ICE TEMPLATE CHECK: {passed}/{slots.Length} passed. Sheet: {Path.Combine(dir, "sheet.html")}");
    return passed == slots.Length ? 0 : 13;
}
if (args.Contains("--live-write-test", StringComparer.Ordinal))
{
    // Short real-target diagnostic: plan one team (Anaheim by default), write assets without DB rows, read back,
    // then restore. The writer rolls back automatically on failure; success is restored explicitly as well.
    string team = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "anaheim";
    var settings = SuiteSettings.Load();
    string target = settings.ApplyTargetDir;
    if (ApplyTarget.RunningGame() is { } running) { Console.WriteLine("REFUSED: " + running); return 22; }
    string modPath = Path.Combine(SuitePaths.Mods, "NHL 27 Fanatics 64 new uniforms.json");
    var mod = ModFile.Load(modPath) ?? throw new InvalidDataException("Saved uniform mod not found: " + modPath);
    mod.Additions = (mod.Additions ?? []).Where(a => a.Team.Equals(team, StringComparison.OrdinalIgnoreCase)).ToList();
    if (mod.Additions.Count != 2) throw new InvalidDataException($"Expected {team} home and away in the saved mod.");
    string backup = Path.Combine(root, "Debugging", "live-write-test-backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    try
    {
        throw new InvalidOperationException("Retired: this donor-based write cannot produce a working kit. Use tools/KitInstall.");
#pragma warning disable CS0162
        var prepared = NewUniformApply.Prepare(settings, mod, planAssets: true,
            progress: s => { Console.WriteLine(s); SuiteLog.Write("live-write-test: " + s); });
        var clones = prepared.AssetPlans.SelectMany(a => a.Clones).ToList();
        Console.WriteLine($"Diagnostic writing {clones.Count} {team} asset bundle(s); no database rows will be changed.");
        BrandingCloneWriter.ApplyBatch(clones, target, backup, s => Console.WriteLine(s));
        Console.WriteLine("Diagnostic write read back successfully; restoring the test assets.");
        BrandingCloneWriter.Restore(target, backup);
        Console.WriteLine("LIVE WRITE TEST SUCCEEDED AND RESTORED: " + backup);
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine("LIVE WRITE TEST FAILED (automatic rollback path): " + ex);
        SuiteLog.Write("live-write-test failure: " + ex);
        return 23;
    }
}
if (args.Contains("--live-install", StringComparer.Ordinal))
{
    // The Install all 64 as new button, headless: plan, save the mod, then coordinated backup/write/verify
    // against the live target from settings. Every progress line goes to both console and suite.log.
    var settings = SuiteSettings.Load();
    var vm = new Convert27ViewModel(settings);
    await vm.LoadAsync();
    void Step(string s) { Console.WriteLine(s); SuiteLog.Write("live-install: " + s); }
    try
    {
        Step("planning started");
        var draft = await NewUniformExport.PlanAsync(vm, "NHL 27 Fanatics 64 new uniforms", currentFolderOnly: false);
        Step("plan ready: " + draft.Summary);
        string modPath = draft.Save();
        Step("mod saved: " + modPath);
        var (ok, message) = await NewUniformApply.ApplyPreparedAsync(settings, draft.Prepared, Step);
        Step($"apply finished ok={ok}: {message}");
        Console.WriteLine(ok ? "LIVE INSTALL SUCCEEDED" : "LIVE INSTALL FAILED: " + message);
        return ok ? 0 : 11;
    }
    catch (Exception ex)
    {
        Step("FAILED " + ex);
        Console.WriteLine("LIVE INSTALL EXCEPTION: " + ex);
        return 12;
    }
}
if (args.Contains("--full-plan", StringComparer.Ordinal))
{
    // Run the exact PlanAsync the Install button uses, headlessly, with full exception output.
    // This writes nothing to the game; it only plans and validates.
    var settings = SuiteSettings.Load();
    var vm = new Convert27ViewModel(settings);
    await vm.LoadAsync();
    try
    {
        var draft = await NewUniformExport.PlanAsync(vm, "NHL 27 Fanatics 64 new uniforms", currentFolderOnly: false);
        Console.WriteLine("PLAN OK: " + draft.Summary);
        Console.WriteLine(draft.Details);
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine("PLAN FAILED: " + ex);
        return 10;
    }
}
bool foldersOnly = args.Contains("--folders-only", StringComparer.Ordinal);
int index24Arg = Array.IndexOf(args, "--index24");
int index27Arg = Array.IndexOf(args, "--index27");
int variantMapArg = Array.IndexOf(args, "--variant-map");
var teamFilters = args.Skip(1).Where(a => a is not ("--roles-only" or "--donors" or "--preview" or "--commit" or "--full-v32-install" or "--live-preflight" or "--folders-only" or "--index24" or "--index27" or "--variant-map")).ToHashSet(StringComparer.OrdinalIgnoreCase);
string fixture = Path.Combine(root, "Debugging", "new-uniform-integration");
string recovery = Path.Combine(root, "Debugging", "v32-recovery");
string variantMap = variantMapArg >= 0 && variantMapArg + 1 < args.Length ? args[variantMapArg + 1] : Path.Combine(root, "data", "variant_map.json");
var variants = VariantMap.Parse(File.ReadAllText(variantMap));
string index24 = index24Arg >= 0 && index24Arg + 1 < args.Length ? args[index24Arg + 1]
    : fullV32Install ? Path.Combine(recovery, "nhl24-assets-current.json.gz")
    : Path.Combine(fixture, "cache", "index", "nhl24", "assets.json.gz");
string index27 = index27Arg >= 0 && index27Arg + 1 < args.Length ? args[index27Arg + 1]
    : fullV32Install ? Path.Combine(recovery, "nhl27-assets-current.json.gz")
    : Path.Combine(fixture, "cache", "index", "nhl27", "assets.json.gz");
var catalog24 = new Nhl24Catalog(LoadIndex(index24));
var catalog27 = new Nhl27Catalog(LoadIndex(index27));
if (foldersOnly)
{
    try
    {
        var check = NewUniformExport.BuildDefaultFolders(catalog24, catalog27, variants);
        Console.WriteLine($"Complete canonical set: {check.Count} uniforms.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}
if (livePreflight)
{
    var live = await RunLivePreflight(catalog24, catalog27, root);
    string livePath = Path.Combine(root, "Debugging", "live-preflight-report.json");
    File.WriteAllText(livePath, JsonSerializer.Serialize(live, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"LIVE PREFLIGHT: {live.Path}");
    return live.ErrorCount == 0 ? 0 : 6;
}
var maps = JsonSerializer.Deserialize<SlotMapStore>(File.ReadAllText(Path.Combine(root, "Debugging", "logo-slots", "slot_map.json"))) ?? new();
string Png(string name) => Path.Combine(fixture, "cache", "nhl27png", SuitePaths.Flatten(name) + ".png");
var folders = NewUniformExport.BuildDefaultFolders(catalog24, catalog27, variants);
var additions = new List<NewUniformAddition>();
var exportErrors = new List<string>();
foreach (var folder in folders)
{
    try { additions.Add(NewUniformExport.Addition(new MatchProject(), variants, catalog27, maps, folder, Png)); }
    catch (Exception ex) { exportErrors.Add($"{folder.Folder24}: {ex.Message}"); }
}
Console.WriteLine($"Canonical folders: {folders.Count}; complete additions: {additions.Count}; artwork errors: {exportErrors.Count}");
foreach (string error in exportErrors) Console.WriteLine("EXPORT ERROR: " + error);
if (exportErrors.Count > 0) return 3;

if (fullV32Install)
{
    var fullReport = RunFullV32Install(recovery, additions, maps);
    string fullReportPath = Path.Combine(recovery, "full-install-report.json");
    File.WriteAllText(fullReportPath, JsonSerializer.Serialize(fullReport, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(fullReport.Ok
        ? "FULL 64-UNIFORM INSTALL, READ-BACK AND RESTORE VERIFIED: " + fullReportPath
        : "FULL INSTALL VERIFICATION FAILED: " + fullReport.Error);
    return fullReport.Ok ? 0 : 5;
}

string game = Path.Combine(root, "Debugging", "donor-clone", "game");
Oodle.Ensure();
var index = BundleIndex.Build(game, s => Console.WriteLine(s));
bool Exists(string name) => index.ResByName.ContainsKey(name) || index.EbxByName.ContainsKey(name);
string dbFixture = Path.Combine(root, "Debugging", "row-planner");
var databases = new Dictionary<string, byte[]>
{
    ["nhl_game"] = File.ReadAllBytes(Path.Combine(dbFixture, "nhl_game.db")),
    ["initial_experience"] = File.ReadAllBytes(Path.Combine(dbFixture, "initial_experience.db")),
};
var plan = NewUniformAdditionPlanner.Build(additions, databases, Path.Combine(dbFixture, "nhl_game-meta.xml"), Exists);
var roleMaps = NewUniformApply.BuildRoleMaps(index, maps);
Console.WriteLine($"Target resources: {index.ResByName.Count:N0} RES / {index.EbxByName.Count:N0} EBX; semantic donor maps: {roleMaps.Count:N0}");
if (showDonors)
    foreach (var map in roleMaps.Where(m => m.Key.Contains("branding_jersey", StringComparison.Ordinal)
                 && m.Key.EndsWith("_color_array", StringComparison.Ordinal)).OrderBy(m => m.Key, StringComparer.Ordinal))
        Console.WriteLine($"DONOR {map.Key}: {string.Join(", ", map.Value)}");
Console.WriteLine(plan.Summary);
var selectedAdditions = plan.Additions.Where(a => teamFilters.Count == 0 || teamFilters.Contains(a.Team)).ToList();
if (rolesOnly)
{
    foreach (var addition in selectedAdditions)
    foreach (var texture in addition.Textures.Where(t => t.RequiredRoles is not null))
        Console.WriteLine($"{addition.Team} {addition.SourceKit} {texture.TargetSlot}: {string.Join(", ", texture.RequiredRoles!)}");
    return 0;
}
var results = new List<object>();
var selectedAssets = new List<NewUniformAssetPlan>();
int failed = 0;
foreach (var addition in selectedAdditions)
{
    try
    {
        var assets = NewUniformAssetPlanner.Build(index, addition, roleMaps);
        assets.ThrowIfIncomplete();
        selectedAssets.Add(assets);
        foreach (var clone in assets.Clones)
            Console.WriteLine($"  BUNDLE {clone.SourceBundleName} -> {clone.NewBundleName}; roots: "
                + string.Join(", ", clone.Entries.Where(e => e.IsClone && e.NewChunkId is null).Select(e => e.Name).Distinct()));
        var clonedNames = assets.Clones.SelectMany(c => c.Entries).Where(e => e.NewChunkId is not null)
            .Select(e => e.Name).Distinct(StringComparer.Ordinal).ToArray();
        var stockDependencies = clonedNames.Where(n => !assets.Artwork.ContainsKey(n)).ToArray();
        results.Add(new { addition.Team, addition.SourceKit, Ok = true, CloneCount = assets.Clones.Count, SlotCount = assets.Slots.Count,
            Donors = assets.Slots.Select(s => s.DonorName).Where(n => n is not null).Distinct().ToArray(),
            ClonedTextureCount = clonedNames.Length, StockDependencies = stockDependencies });
        Console.WriteLine($"OK {addition.Team} {addition.SourceKit}: {assets.Clones.Count} clones, {assets.Slots.Count} target textures, "
            + $"{stockDependencies.Length} same-team stock dependencies");
        foreach (string dependency in stockDependencies) Console.WriteLine("  STOCK DEPENDENCY: " + dependency);
        if (preview)
        {
            string directory = Path.Combine(fixture, "previews");
            Directory.CreateDirectory(directory);
            foreach (var texture in addition.Textures.Where(t => t.TargetSlot.Contains("branding_jersey", StringComparison.Ordinal)
                         && t.TargetSlot.EndsWith("_color_array", StringComparison.Ordinal)))
            {
                var slot = assets.Slots.Single(s => s.TargetName == texture.TargetSlot);
                var donor = TextureSlots.Read(game, index, slot.DonorName!);
                var layers = Ps4Codec.DecodeSlices(donor.Res, assets.Artwork[texture.TargetSlot]);
                for (int layer = 0; layer < layers.Count; layer++)
                    ImageIO.SavePng(layers[layer], Path.Combine(directory, $"{addition.Team}-{addition.SourceKit}@{layer}.png"));
                Console.WriteLine($"  PREVIEW {slot.DonorName}: {string.Join(", ", roleMaps[slot.DonorName!])}");
            }
        }
    }
    catch (Exception ex)
    {
        failed++;
        results.Add(new { addition.Team, addition.SourceKit, Ok = false, Error = ex.Message });
        Console.WriteLine($"ASSET ERROR {addition.Team} {addition.SourceKit}: {ex.Message}");
    }
}
if (failed == 0)
{
    try
    {
        BrandingCloneWriter.ValidatePlanSet(selectedAssets.SelectMany(a => a.Clones));
        Console.WriteLine("Whole-set bundle, chunk and resource collision checks passed.");
    }
    catch (Exception ex)
    {
        failed++;
        results.Add(new { Team = "__batch__", SourceKit = "all", Ok = false, Error = ex.Message });
        Console.WriteLine("WHOLE-SET ASSET ERROR: " + ex.Message);
    }
}
string suffix = teamFilters.Count == 0 ? "" : "-" + string.Join("-", selectedAdditions.Select(a => a.Team).Distinct(StringComparer.Ordinal));
string report = Path.Combine(fixture, $"preflight-report{suffix}.json");
File.WriteAllText(report, JsonSerializer.Serialize(new { plan.Summary, ExportErrors = exportErrors, Results = results },
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Report: {report}; failures: {failed}");
if (failed != 0) return 4;
if (commit)
{
    var committed = RunCommit(root, fixture, selectedAdditions, selectedAssets, databases,
        Path.Combine(dbFixture, "nhl_game-meta.xml"), index);
    string commitReport = Path.Combine(fixture, $"commit-rollback-report-{string.Join("-", selectedAdditions.Select(a => a.Team).Distinct(StringComparer.OrdinalIgnoreCase))}.json");
    File.WriteAllText(commitReport, JsonSerializer.Serialize(committed, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("COMMIT, STRUCTURAL READ-BACK AND RESTORE VERIFIED: " + commitReport);
}
return 0;

static async Task<LivePreflightResult> RunLivePreflight(Nhl24Catalog catalog24, Nhl27Catalog catalog27, string root)
{
    var settings = SuiteSettings.Load();
    var vm = new Convert27ViewModel(settings);
    await vm.LoadAsync();
    if (vm.Catalog24 is null || vm.Catalog27 is null) throw new InvalidOperationException("The live catalogs did not load.");
    var folders = NewUniformExport.BuildDefaultFolders(vm.Catalog24, vm.Catalog27, vm.Variants);
    var report = await vm.RunPreflightAsync(currentFolderOnly: false, canonicalFolders: folders);
    string path = Path.Combine(root, "Debugging", "live-preflight-items.json");
    File.WriteAllText(path, JsonSerializer.Serialize(report.Items.Select(i => new
    {
        Severity = i.Severity.ToString(),
        i.Title,
        i.Detail,
    }), new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Canonical folders checked: {folders.Count}; report items: {report.Items.Count}");
    foreach (var item in report.Items.Where(i => i.Severity == PreflightSeverity.Error))
        Console.WriteLine($"ERROR {item.Title}: {item.Detail}");

    var additionErrors = new List<string>();
    var additions = new List<NewUniformAddition>();
    foreach (var folder in folders)
    {
        try { additions.Add(NewUniformExport.Addition(new MatchProject(), vm.Variants, vm.Catalog27!, vm.SlotMaps, folder, TextureImages.Nhl27Png)); }
        catch (Exception ex) { additionErrors.Add($"{folder.Folder24}: {ex.Message}"); }
    }
    Console.WriteLine($"New-uniform additions: {additions.Count}; addition errors: {additionErrors.Count}");
    foreach (string error in additionErrors) Console.WriteLine("NEW ADDITION ERROR: " + error);
    return new LivePreflightResult
    {
        Path = path,
        Summary = report.Summary,
        ErrorCount = report.ErrorCount,
        WarningCount = report.WarningCount,
        RepairCount = report.RepairCount,
        Uniforms = folders.Count,
        AdditionCount = additions.Count,
        AdditionErrorCount = additionErrors.Count,
        ApplyTarget = settings.ApplyTargetDir,
        GameFilesModified = false,
    };
}

static FullInstallReport RunFullV32Install(string recovery, List<NewUniformAddition> additions, SlotMapStore maps)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    string target = Path.GetFullPath(Path.Combine(recovery, "current"));
    string required = Path.GetFullPath(Path.Combine(recovery, "current"));
    string? backup = null;
    string? error = null;
    int cloneCount = 0;
    int verifiedArrays = 0;
    bool readBack = false;
    string[] databasesReadBack = [];
    List<string> remaining = [];
    try
    {
        if (!string.Equals(target, required, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Full install mode is restricted to the isolated V3.2 recovery copy.");
        if (additions.Count != 64 || additions.Select(a => a.Team).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 32
            || additions.Select(a => a.Team + "\0" + a.SourceKit).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 64)
            throw new InvalidOperationException($"Full install mode requires exactly 64 distinct home/away uniforms across 32 teams; got {additions.Count}.");
        var problems = ApplyTarget.Problems(target);
        if (problems.Count > 0) throw new InvalidOperationException("Isolated V3.2 target refused:\n" + string.Join("\n", problems));
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Target guard passed: {target}");

        var settings = new SuiteSettings { ApplyTargetDir = target, Nhl24GameDir = target, Workers = Environment.ProcessorCount };
        var original = new LegacyFiles(target, settings);
        string[] logicalDatabases = ["nhl_game", "initial_experience"];
        var targetDatabases = logicalDatabases.ToDictionary(name => name,
            name => original.Read(RosterSyncRun.LegacyName(name)), StringComparer.Ordinal);
        string targetMeta = Path.Combine(recovery, "full-install-nhl_game-meta.xml");
        File.WriteAllBytes(targetMeta, original.Read("gamemodes/database/t3db/nhl_game-meta.xml"));
        Console.WriteLine("Read both authoritative V3.2 databases and nhl_game-meta.xml through LegacyFiles.");

        Oodle.Ensure();
        var index = BundleIndex.Build(target, s => Console.WriteLine(s));
        bool Exists(string name) => index.ResByName.ContainsKey(name) || index.EbxByName.ContainsKey(name);
        var additionPlan = NewUniformAdditionPlanner.Build(additions, targetDatabases, targetMeta, Exists);
        if (additionPlan.Additions.Count != 64) throw new InvalidOperationException("The target database plan did not retain all 64 additions.");
        var roleMaps = NewUniformApply.BuildRoleMaps(index, maps);
        var assets = new List<NewUniformAssetPlan>();
        foreach (var addition in additionPlan.Additions)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Planning {addition.Team} {addition.SourceKit}...");
            var asset = NewUniformAssetPlanner.Build(index, addition, roleMaps, settings.Workers);
            asset.ThrowIfIncomplete();
            assets.Add(asset);
        }
        if (assets.Count != 64) throw new InvalidOperationException($"Expected 64 asset plans, got {assets.Count}.");
        var prepared = new NewUniformApply.Prepared(target, additionPlan, assets,
            additionPlan.JerseyRows.ChangedDatabases.ToDictionary(RosterSyncRun.LegacyName,
                name => Convert.ToBase64String(additionPlan.JerseyRows.Databases[name]), StringComparer.Ordinal));
        cloneCount = prepared.CloneCount;
        int expectedArrays = assets.Sum(a => a.Artwork.Keys.Count(n =>
            n.Contains("branding_jersey", StringComparison.Ordinal) && n.EndsWith("_color_array", StringComparison.Ordinal)));
        if (expectedArrays != 64) throw new InvalidOperationException($"Expected 64 independent branding arrays, got {expectedArrays}.");
        var inspectionMod = additionPlan.ToMod("NHL 27 Fanatics 64 new uniforms - isolated verification",
            "Inspection copy only; this JSON was serialized directly into the workspace and was not saved to the real mod library.");
        File.WriteAllText(Path.Combine(recovery, "full-install-mod.json"), JsonSerializer.Serialize(inspectionMod,
            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
        File.WriteAllText(Path.Combine(recovery, "full-install-plan.json"), JsonSerializer.Serialize(new
        {
            prepared.Summary,
            Uniforms = additionPlan.Additions.Count,
            Teams = additionPlan.Additions.Select(a => a.Team).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ClonedAssetBundles = cloneCount,
            BrandingArrays = expectedArrays,
            Databases = additionPlan.JerseyRows.ChangedDatabases,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(prepared.Summary);

        backup = ApplyTarget.Backup(target, Path.Combine(recovery, "full-install-backups"));
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Coordinated backup: {backup}");
        string writerBackup = Path.Combine(recovery, "writer-backups", $"full-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
        if (Directory.Exists(writerBackup)) throw new IOException("Fresh writer-backup path already exists: " + writerBackup);
        Directory.CreateDirectory(writerBackup);
        if (Directory.EnumerateFileSystemEntries(writerBackup).Any()) throw new IOException("Writer backup is not empty: " + writerBackup);
        var files = prepared.DatabaseFiles.ToDictionary(kv => kv.Key, kv => Convert.FromBase64String(kv.Value), StringComparer.Ordinal);
        var clones = prepared.AssetPlans.SelectMany(p => p.Clones).ToList();
        NewUniformTransaction.Run(backup,
            writeAssets: () =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Writing {clones.Count} independent asset bundles...");
                BrandingCloneWriter.ApplyBatch(clones, target, writerBackup, s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}"));
            },
            writeDatabases: () =>
            {
                foreach (string line in new LegacyFiles(target, settings).Replace(files)) Console.WriteLine("DB: " + line);
            },
            readBack: () => VerifyCommitted(target, settings, prepared, files),
            restoreAndVerify: () => RestoreChecked(target, backup));
        readBack = true;
        databasesReadBack = files.Keys.Order().ToArray();
        verifiedArrays = expectedArrays;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Applied once and verified both databases plus {verifiedArrays} branding arrays; restoring...");
        RestoreChecked(target, backup);
        remaining = ApplyTarget.Compare(target, backup);
        if (remaining.Count > 0) throw new InvalidOperationException("Final restore differences: " + string.Join("; ", remaining.Take(10)));
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Restore verified with zero TOC/CAS differences.");
    }
    catch (Exception ex)
    {
        error = ex.ToString();
        Console.Error.WriteLine("FULL INSTALL ERROR: " + ex);
        if (backup is not null)
        {
            try
            {
                RestoreChecked(target, backup);
                remaining = ApplyTarget.Compare(target, backup);
                Console.WriteLine("Failure path restored the isolated target; differences: " + remaining.Count);
            }
            catch (Exception restoreEx)
            {
                error += "\nRESTORE ERROR: " + restoreEx;
                Console.Error.WriteLine("RESTORE ERROR: " + restoreEx);
            }
        }
    }
    sw.Stop();
    return new FullInstallReport
    {
        Ok = error is null && readBack && verifiedArrays == 64 && remaining.Count == 0,
        Target = target,
        Uniforms = additions.Count,
        Teams = additions.Select(a => a.Team).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        ClonedAssetBundles = cloneCount,
        IndependentlyVerifiedBrandingArrays = verifiedArrays,
        DatabasesReadBack = databasesReadBack,
        Backup = backup ?? "",
        RemainingDifferences = remaining,
        ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
        PlayableBuildsModified = false,
        Error = error,
    };
}

static object RunCommit(string root, string fixture, List<NewUniformAddition> additions,
    List<NewUniformAssetPlan> assets, Dictionary<string, byte[]> sourceDatabases, string metaPath, BundleIndex sourceIndex)
{
    var teams = selectedTeamNames(additions);
    bool detroitOnly = additions.Count == 2 && teams.SetEquals(["detroit"]);
    bool originalFixture = additions.Count == 4 && teams.SetEquals(["anaheim", "arizona"]);
    if ((!detroitOnly && !originalFixture) || assets.Count != additions.Count)
        throw new InvalidOperationException("Isolated commit mode requires Detroit home/away or the original Anaheim/Arizona fixture.");
    string target = Path.GetFullPath(Path.Combine(fixture, "apply-target", "game"));
    string required = Path.GetFullPath(Path.Combine(root, "Debugging", "new-uniform-integration", "apply-target", "game"));
    if (!string.Equals(target, required, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Commit mode is restricted to the physically copied isolated target.");
    var problems = ApplyTarget.Problems(target);
    if (problems.Count > 0) throw new InvalidOperationException("Isolated target refused:\n" + string.Join("\n", problems));

    // Backups, logs and undo data must also stay in the workspace, never in the user's real application data.
    string appData = Path.Combine(fixture, "appdata");
    Environment.SetEnvironmentVariable("APPDATA", appData);
    Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(appData, "local"));
    var settings = new SuiteSettings { ApplyTargetDir = target, Nhl24GameDir = target, Workers = Environment.ProcessorCount };
    bool Exists(string name) => sourceIndex.ResByName.ContainsKey(name) || sourceIndex.EbxByName.ContainsKey(name);
    // Match production Prepare: the actual target's current databases and meta are authoritative. The smaller
    // row-planner fixture came from a different build, so comparing against it would test the fixture, not the apply.
    var original = new LegacyFiles(target, settings);
    var targetDatabases = sourceDatabases.Keys.ToDictionary(name => name,
        name => original.Read(RosterSyncRun.LegacyName(name)), StringComparer.Ordinal);
    string targetMeta = Path.Combine(fixture, "apply-target-nhl_game-meta.xml");
    File.WriteAllBytes(targetMeta, original.Read("gamemodes/database/t3db/nhl_game-meta.xml"));
    var additionPlan = NewUniformAdditionPlanner.Build(additions, targetDatabases, targetMeta, Exists);
    var prepared = new NewUniformApply.Prepared(target, additionPlan, assets,
        additionPlan.JerseyRows.ChangedDatabases.ToDictionary(RosterSyncRun.LegacyName,
            name => Convert.ToBase64String(additionPlan.JerseyRows.Databases[name]), StringComparer.Ordinal));
    var files = prepared.DatabaseFiles.ToDictionary(kv => kv.Key, kv => Convert.FromBase64String(kv.Value), StringComparer.Ordinal);
    var clones = prepared.AssetPlans.SelectMany(p => p.Clones).ToList();
    var sw = System.Diagnostics.Stopwatch.StartNew();

    string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
    string writerBackup = Path.Combine(fixture, "writer-backups", string.Join("-", teams.Order()) + "-" + runId);
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Backing up the isolated target before {additions.Count} uniform writes...");
    string backup = ApplyTarget.Backup(target, Path.Combine(fixture, "backups"));
    NewUniformTransaction.Run(backup,
        writeAssets: () =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Writing {clones.Count} independent asset bundles...");
            BrandingCloneWriter.ApplyBatch(clones, target, writerBackup);
        },
        writeDatabases: () =>
        {
            foreach (string line in new LegacyFiles(target, settings).Replace(files)) Console.WriteLine("DB: " + line);
        },
        readBack: () => VerifyCommitted(target, settings, prepared, files),
        restoreAndVerify: () => RestoreChecked(target, backup));
    Console.WriteLine("Successful coordinated commit read back every payload, structural root, blueprint and both databases.");

    // Leave no uniforms installed in the isolated fixture after the successful write.
    RestoreChecked(target, backup);
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Successful test restored cleanly.");

    // The previous four-uniform run already proved the deliberate-failure path. Detroit performs one write/read/restore
    // cycle, so this diagnostic does not need a second full game write. The original fixture retains its failure test.
    bool observed = detroitOnly;
    if (originalFixture)
    {
        string rollbackBackup = ApplyTarget.Backup(target, Path.Combine(fixture, "backups"));
        const string marker = "DELIBERATE READ-BACK FAILURE FOR ROLLBACK TEST";
        observed = false;
        try
        {
            NewUniformTransaction.Run(rollbackBackup,
                writeAssets: () => BrandingCloneWriter.ApplyBatch(clones, target, writerBackup + "-rollback"),
                writeDatabases: () => new LegacyFiles(target, settings).Replace(files),
                readBack: () =>
                {
                    foreach (var (name, bytes) in files)
                        if (!new LegacyFiles(target, settings).Read(name).AsSpan().SequenceEqual(bytes))
                            throw new InvalidOperationException($"Database read-back differs before deliberate failure: {name}");
                    throw new InvalidDataException(marker);
                },
                restoreAndVerify: () => RestoreChecked(target, rollbackBackup));
        }
        catch (InvalidOperationException ex) when (ex.InnerException is InvalidDataException inner && inner.Message == marker)
        {
            observed = true;
            Console.WriteLine("Deliberate failure triggered the production rollback path.");
        }
        if (!observed) throw new InvalidOperationException("The deliberate failure did not follow the expected rollback path.");
    }
    var remaining = ApplyTarget.Compare(target, backup);
    if (remaining.Count > 0) throw new InvalidOperationException("Rollback differences: " + string.Join("; ", remaining));
    sw.Stop();
    return new
    {
        Target = target,
        Uniforms = additions.Count,
        Teams = teams.Order().ToArray(),
        ClonedAssetBundles = clones.Count,
        IndependentlyVerifiedBrandingArrays = assets.Sum(a => a.Artwork.Keys.Count(n =>
            n.Contains("branding_jersey", StringComparison.Ordinal) && n.EndsWith("_color_array", StringComparison.Ordinal))),
        DatabasesReadBack = files.Keys.Order().ToArray(),
        SuccessfulCommitReadBack = true,
        SuccessfulCommitRestoredCleanly = true,
        DeliberateFailureRolledBack = originalFixture && observed,
        DeliberateFailureTestSkipped = detroitOnly,
        Backup = backup,
        RemainingDifferences = remaining,
        ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
        PlayableBuildsModified = false,
    };

    static HashSet<string> selectedTeamNames(IEnumerable<NewUniformAddition> values) =>
        values.Select(a => a.Team).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

static int VerifyCommitted(string target, SuiteSettings settings, NewUniformApply.Prepared prepared,
    Dictionary<string, byte[]> files)
{
    var check = new LegacyFiles(target, settings);
    foreach (var (name, bytes) in files)
        if (!check.Read(name).AsSpan().SequenceEqual(bytes))
            throw new InvalidOperationException($"Database read-back differs: {name}");
    Console.WriteLine("Both databases read back byte-for-byte; checking only the newly written bundles...");

    // BrandingCloneWriter has already read back every CAS payload. Resolve the new bundle records directly here instead
    // of rebuilding the entire 150,000-bundle game index. This also checks the object graph the engine must resolve.
    var archives = new Dictionary<string, (TocArchive Archive, Dictionary<int, List<BundleIndex.Location>> Records)>(
        StringComparer.OrdinalIgnoreCase);
    foreach (var plan in prepared.AssetPlans.SelectMany(a => a.Clones))
    {
        string toc = Path.GetFullPath(Path.Combine(target, Path.GetRelativePath(plan.SourceGame, plan.SourceBundle.Toc)));
        if (archives.ContainsKey(toc)) continue;
        byte[] bytes = File.ReadAllBytes(toc);
        archives.Add(toc, (TocArchive.Parse(bytes), BundleIndex.Records(toc, bytes).ToDictionary(r => r.RecordAt, r => r.Entries)));
    }
    static byte[] Decode(byte[] stored)
    {
        try { return ChunkBlocks.Parse(stored).Decode(); }
        catch (InvalidDataException) { return stored; }
    }
    int checkedArrays = 0, checkedStructural = 0;
    foreach (var assets in prepared.AssetPlans)
    foreach (var plan in assets.Clones)
    {
        string toc = Path.GetFullPath(Path.Combine(target, Path.GetRelativePath(plan.SourceGame, plan.SourceBundle.Toc)));
        var (archive, records) = archives[toc];
        var bundle = archive.FindBundle(plan.NewBundleName)
            ?? throw new InvalidOperationException("New bundle is missing: " + plan.NewBundleName);
        int recordAt = checked(TocFile.HeaderSize + (int)bundle.RecordOffset);
        if (!records.TryGetValue(recordAt, out var entries))
            throw new InvalidOperationException("New bundle record is missing: " + plan.NewBundleName);
        var manifest = BundleManifest.Parse(BundleIndex.ReadBytes(entries[0]));
        string root = plan.NewBundleName.StartsWith("gen4b/", StringComparison.Ordinal)
            ? plan.NewBundleName["gen4b/".Length..] : plan.NewBundleName;
        if (root.EndsWith("_player_team_brt", StringComparison.Ordinal))
        {
            foreach (string name in new[] { root, root + "_blueprint" })
            {
                int at = manifest.Ebx.FindIndex(e => e.Name == name);
                if (at < 0) throw new InvalidOperationException($"Structural EBX is absent from {plan.NewBundleName}: {name}");
                var actual = EbxRiff.Parse(Decode(BundleIndex.ReadBytes(entries[1 + at])));
                var entry = plan.Entries.Single(e => e.IsClone && e.NewChunkId is null && e.Name == name);
                var donor = EbxRiff.Parse(Decode(BundleIndex.ReadBytes(entry.Source)));
                if (actual.Name != name || !plan.EbxGuidRemap.TryGetValue(donor.FileGuid, out var newGuid)
                    || actual.FileGuid != newGuid)
                    throw new InvalidOperationException($"Structural EBX identity differs: {name}");
                var expectedImports = donor.ImportedFileGuids.Select(id => plan.EbxGuidRemap.GetValueOrDefault(id, id));
                if (!actual.ImportedFileGuids.SequenceEqual(expectedImports))
                    throw new InvalidOperationException($"Structural EBX dependency was not remapped: {name}");
                checkedStructural++;
            }
        }
        foreach (var (name, expected) in assets.Artwork.Where(a => a.Key.Contains("branding_jersey", StringComparison.Ordinal)
                     && a.Key.EndsWith("_color_array", StringComparison.Ordinal)))
        {
            int resAt = manifest.Res.FindIndex(r => r.Name == name);
            if (resAt < 0) continue;
            var res = TextureRes.Parse(Decode(BundleIndex.ReadBytes(entries[1 + manifest.Ebx.Count + resAt])));
            if (res.PayloadSize * Ps4Codec.Slices(res.Header) != expected.Length)
                throw new InvalidOperationException($"Branding-array layout differs: {name}");
            int chunkAt = manifest.Chunks.FindIndex(c => c.Id == res.ChunkGuid);
            if (chunkAt < 0) throw new InvalidOperationException($"Branding-array chunk is absent: {name}");
            var chunk = manifest.Chunks[chunkAt];
            if (chunk.LogicalOffset != 0 || chunk.LogicalSize != expected.Length)
                throw new InvalidOperationException($"Branding-array chunk extent differs: {name}");
            var location = entries[1 + manifest.Ebx.Count + manifest.Res.Count + chunkAt];
            byte[] actual = Decode(BundleIndex.ReadBytes(location));
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException($"Independent branding-array read-back differs: {name}");
            var global = archive.FindChunk(res.ChunkGuid)
                ?? throw new InvalidOperationException($"Global texture chunk is absent: {name}");
            if (global.Offset != location.Offset || global.Size != location.Size)
                throw new InvalidOperationException($"Global texture chunk location differs: {name}");
            checkedArrays++;
        }
    }
    if (checkedArrays != prepared.AssetPlans.Count)
        throw new InvalidOperationException($"Expected one verified branding array per uniform, got {checkedArrays}.");
    Console.WriteLine($"Direct read-back verified {checkedArrays} branding arrays and {checkedStructural} structural roots/blueprints.");
    return checkedStructural;
}

static void RestoreChecked(string target, string backup)
{
    ApplyTarget.RestoreFromBackup(target, backup);
    var differences = ApplyTarget.Compare(target, backup);
    if (differences.Count > 0)
        throw new InvalidOperationException("Rollback differences: " + string.Join("; ", differences.Take(10)));
}

static AssetIndex LoadIndex(string path)
{
    using var stream = File.OpenRead(path);
    using var gzip = new GZipStream(stream, CompressionMode.Decompress);
    var stored = JsonSerializer.Deserialize<Stored>(gzip, new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() },
    }) ?? throw new InvalidDataException(path);
    return new AssetIndex(stored.Game, stored.Fingerprint, stored.Built, stored.Entries);
}

sealed record Stored(int Schema, string Game, string Fingerprint, DateTime Built, List<AssetEntry> Entries);

sealed record FullInstallReport
{
    public bool Ok { get; init; }
    public required string Target { get; init; }
    public int Uniforms { get; init; }
    public int Teams { get; init; }
    public int ClonedAssetBundles { get; init; }
    public int IndependentlyVerifiedBrandingArrays { get; init; }
    public string[] DatabasesReadBack { get; init; } = [];
    public required string Backup { get; init; }
    public List<string> RemainingDifferences { get; init; } = [];
    public double ElapsedSeconds { get; init; }
    public bool PlayableBuildsModified { get; init; }
    public string? Error { get; init; }
}

sealed record LivePreflightResult
{
    public required string Path { get; init; }
    public required string Summary { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public int RepairCount { get; init; }
    public int Uniforms { get; init; }
    public int AdditionCount { get; init; }
    public int AdditionErrorCount { get; init; }
    public required string ApplyTarget { get; init; }
    public bool GameFilesModified { get; init; }
}
