// FullKitAudit --target <game> --mod <mod.json> [--report <workspace-folder>]
// Read-only final audit of a complete new-uniform installation. No game file is written.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Suite.Convert27;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

string? Option(string name)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
void Say(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
static byte[] Decode(byte[] stored)
{
    try { return ChunkBlocks.Parse(stored).Decode(); }
    catch (InvalidDataException) { return stored; }
}
static uint NameHash(string name)
{
    uint hash = 5381;
    foreach (char c in name) hash = (hash * 33) ^ c;
    return hash;
}

string target = Path.GetFullPath(Option("--target") ?? throw new ArgumentException("--target is required")).TrimEnd(Path.DirectorySeparatorChar);
string modPath = Path.GetFullPath(Option("--mod") ?? throw new ArgumentException("--mod is required"));
string workspace = Directory.GetCurrentDirectory();
string reportDir = Path.GetFullPath(Option("--report") ?? Path.Combine(workspace, "Debugging", "fanatics-final-audit"));
Require(reportDir.StartsWith(workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
    "The report folder must stay inside the workspace.");
Directory.CreateDirectory(reportDir);
if (ApplyTarget.RunningGame() is { } running)
    Say(running + " is running: continuing with a read-only audit. No game files will be changed.");

var mod = ModFile.Load(modPath) ?? throw new InvalidDataException("Could not load the uniform mod.");
var additions = mod.Additions ?? throw new InvalidDataException("The mod has no new uniforms.");
Require(additions.Count == 64, $"Expected 64 uniforms; found {additions.Count}.");
Require(additions.Select(a => a.Team).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 32, "Expected 32 teams.");
Require(additions.GroupBy(a => a.Team, StringComparer.OrdinalIgnoreCase).All(g => g.Count() == 2
    && g.Select(a => a.SourceKit).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(["home", "away"])), "Every team needs one home and one away uniform.");
Say("Auditing 64 uniforms for 32 teams. No files in the game will be changed.");

var index = BundleIndex.Build(target, Say);
additions = RegisteredKitSource.Normalize(additions, index, Say);
var archives = index.Bundles.Select(b => b.Toc).Distinct(StringComparer.OrdinalIgnoreCase)
    .ToDictionary(t => t, t => TocArchive.Parse(File.ReadAllBytes(t)), StringComparer.OrdinalIgnoreCase);
var bundleNames = archives.Values.SelectMany(a => a.Bundles.Select(b => b.Name)).ToHashSet(StringComparer.Ordinal);
var registryEntries = index.ResByName[PlayerTeamRegistry.ResourceName];
Require(registryEntries.Count == 911, $"Expected 911 registry references; found {registryEntries.Count}.");
int levelCopies = registryEntries.Count(r => r.Bundle.Toc.Replace('\\', '/').EndsWith("/levels/game/game.toc", StringComparison.OrdinalIgnoreCase));
Require(levelCopies == 2, $"Expected both levels/game/game.toc registry copies; found {levelCopies}.");
var registryPayloads = registryEntries.GroupBy(r => (r.Data.Cas, r.Data.Offset))
    .Select(g => (Location: g.Key, Raw: Decode(BundleIndex.ReadBytes(g.First().Data)))).ToList();
Require(registryPayloads.Count == 2, $"Expected two registry payloads; found {registryPayloads.Count}.");
long registryRows = 0;
foreach (var payload in registryPayloads)
{
    Require(PlayerTeamRegistry.IsSorted(payload.Raw), $"Registry is not sorted: {payload.Location.Cas}");
    foreach (var addition in additions)
    {
        var source = PlayerTeamRegistry.Rows(payload.Raw, addition.SourcePrefix.TrimEnd('/'));
        var targetRows = PlayerTeamRegistry.Rows(payload.Raw, addition.TargetPrefix.TrimEnd('/'));
        Require(source.Count > 0 && source.Count == targetRows.Count,
            $"{addition.Team} {addition.SourceKit}: {targetRows.Count} target rows, {source.Count} source rows in {payload.Location.Cas}");
        foreach (var row in targetRows)
        {
            Require(PlayerTeamRegistry.Contains(payload.Raw, row.Leaf), "Missing leaf lookup: " + row.Leaf);
            Require(PlayerTeamRegistry.Contains(payload.Raw, row.Folder + "/" + row.Leaf), "Missing folder lookup: " + row.Leaf);
            Require(bundleNames.Contains(PlayerTeamRegistry.BundleName(row.Bundle)), "Registered bundle is missing: " + PlayerTeamRegistry.BundleName(row.Bundle));
            registryRows++;
        }
    }
}
Say($"PASS: all {registryEntries.Count} registry references point to two validated payloads; {registryRows} target rows and bundles resolve.");

long resourceCopies = 0;
foreach (var addition in additions)
foreach (var texture in addition.Textures)
{
    var source = index.ResByName.GetValueOrDefault(texture.SourceSlot) ?? [];
    var targets = index.ResByName.GetValueOrDefault(texture.TargetSlot) ?? [];
    Require(source.Count > 0, "Source texture is missing: " + texture.SourceSlot);
    Require(targets.Count > 0, "Target texture is missing: " + texture.TargetSlot);
    var sourceRids = source.Select(r => r.Rid).ToHashSet();
    var sourceChunks = source.Select(r => TextureRes.Parse(Decode(BundleIndex.ReadBytes(r.Data))).ChunkGuid).ToHashSet();
    foreach (var resource in targets)
    {
        byte[] raw = Decode(BundleIndex.ReadBytes(resource.Data));
        Require(raw.Length >= 116 && BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(112)) == NameHash(texture.TargetSlot),
            "Target resource name hash is wrong: " + texture.TargetSlot);
        Require((resource.Rid & 1) == 1, $"Even resource ID on {texture.TargetSlot}: {resource.Rid:X}");
        Require(!sourceRids.Contains(resource.Rid), "Target still has the source resource ID: " + texture.TargetSlot);
        Require(!sourceChunks.Contains(TextureRes.Parse(raw).ChunkGuid), "Target still uses the source texture chunk: " + texture.TargetSlot);
        resourceCopies++;
    }
}
Say($"PASS: {resourceCopies} target texture resources have valid names, odd independent IDs, and independent chunks.");

var textureHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
foreach (var addition in additions)
{
    var texture = addition.Textures.FirstOrDefault(t => t.TargetSlot.Contains("/jersey_", StringComparison.Ordinal)
            && t.TargetSlot.EndsWith("_base_color", StringComparison.Ordinal))
        ?? addition.Textures.FirstOrDefault(t => t.TargetSlot.Contains("/jersey_", StringComparison.Ordinal)
            && t.TargetSlot.EndsWith("_color", StringComparison.Ordinal))
        ?? throw new InvalidDataException($"{addition.Team} {addition.SourceKit}: no jersey colour texture.");
    var slots = TextureSlots.ReadAll(target, index, texture.TargetSlot);
    Require(slots.Count > 0, "No full jersey texture payload: " + texture.TargetSlot);
    foreach (var slot in slots)
    {
        var images = Ps4Codec.DecodeSlices(slot.Res, slot.Stock);
        Require(images.Count > 0 && images.All(image => image.Pixels.Length == image.Width * image.Height * 4),
            "Jersey texture did not decode: " + texture.TargetSlot);
        textureHashes[$"{addition.Team}/{addition.SourceKit}/{slot.Chunk}"] = Convert.ToHexString(SHA256.HashData(slot.Stock));
    }
}
Say($"PASS: full jersey texture payloads for all {additions.Count} uniforms were read and decoded successfully.");

var settings = new SuiteSettings { ApplyTargetDir = target, Nhl24GameDir = target };
var legacy = new LegacyFiles(target, settings);
string metaPath = Path.Combine(reportDir, "nhl_game-meta.xml");
File.WriteAllBytes(metaPath, legacy.Read("gamemodes/database/t3db/nhl_game-meta.xml"));
var strings = LanguageStrings.Load(legacy.Read("loc/eng_us.db"));
var expectedKeys = new HashSet<string>(StringComparer.Ordinal);
int jerseyRows = 0;
foreach (string name in new[] { "nhl_game", "initial_experience" })
{
    var db = T3Db.Load(legacy.Read(RosterSyncRun.LegacyName(name)), metaPath);
    var table = db.Tables[JerseyRowPlanner.TableName];
    var keys = new HashSet<string>(StringComparer.Ordinal);
    foreach (var addition in additions)
    {
        int matches = Enumerable.Range(0, table.InUse).Count(row => db.GetInt(table, row, "teamid") == addition.TeamId
            && db.GetInt(table, row, "style") == addition.Style && db.GetInt(table, row, "variant") == addition.TargetVariant);
        Require(matches == 1, $"{name}: {addition.Team} {addition.SourceKit} has {matches} matching jersey rows.");
        int row = Enumerable.Range(0, table.InUse).Single(r => db.GetInt(table, r, "teamid") == addition.TeamId
            && db.GetInt(table, r, "style") == addition.Style && db.GetInt(table, r, "variant") == addition.TargetVariant);
        Require(db.GetInt(table, row, "isdefault") == 0, $"{name}: new uniform must not replace the default.");
        Require(db.GetInt(table, row, "islightcolor") == (addition.IsLight ? 1 : 0), $"{name}: wrong home/away colour role.");
        Require(db.GetInt(table, row, "fevariant") == addition.TargetVariant
            && db.GetInt(table, row, "variantleadingscorer") == addition.TargetVariant, $"{name}: variant fields do not agree.");
        Require(db.GetInt(table, row, "locstring") == 6, $"{name}: new row does not use the vintage picker label.");
        keys.Add($"VINTAGE_{addition.Style}_{db.GetInt(table, row, "teamartid")}_{addition.TargetVariant}");
        jerseyRows++;
    }
    Require(keys.Count == 64, $"{name}: expected 64 distinct picker keys; found {keys.Count}.");
    if (expectedKeys.Count == 0) expectedKeys = keys;
    else Require(expectedKeys.SetEquals(keys), $"{name}: picker keys differ between databases.");
}
foreach (string key in expectedKeys)
    Require(strings.Find(key) == FullKitInstall.DefaultJerseyName,
        $"Picker label {key} is '{strings.Find(key) ?? "<missing>"}', expected '{FullKitInstall.DefaultJerseyName}'.");
Say($"PASS: {jerseyRows} database jersey rows and all {expectedKeys.Count} picker labels read '{FullKitInstall.DefaultJerseyName}'.");

string artDir = Path.GetFullPath(Option("--art-dir") ?? Path.Combine(workspace, "Debugging", "new-uniform-integration", "cache", "nhl27png"));
string ResolveArtwork(string path) => InstalledBrandingAudit.ResolveArtwork(path, artDir);
var branding = new List<InstalledBrandingResult>();
foreach (var addition in additions)
{
    Say($"Reading installed color, normal and coefficient arrays: {addition.Team} {addition.SourceKit}");
    try { branding.Add(InstalledBrandingAudit.Addition(target, index, addition, ResolveArtwork)); }
    catch (Exception ex)
    {
        Say($"BRANDING: {addition.Team} {addition.SourceKit}: {ex.Message}");
        branding.Add(new InstalledBrandingResult(0, 0, [], [$"{addition.Team} {addition.SourceKit}: {ex.Message}"]));
    }
}
var brandingProblems = branding.SelectMany(result => result.Problems).ToArray();
File.WriteAllText(Path.Combine(reportDir, "branding-report.json"), JsonSerializer.Serialize(branding, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Say($"Branding read-back: {branding.Sum(result => result.ArrayCopies)} array copies, {branding.Sum(result => result.IntendedSponsors)} intended front sponsors, {brandingProblems.Length} problem(s).");

var report = new
{
    Target = target,
    Teams = 32,
    Uniforms = additions.Count,
    RegistryReferences = registryEntries.Count,
    RegistryPayloads = registryPayloads.Count,
    RegistryRows = registryRows,
    ResourceCopies = resourceCopies,
    DecodedJerseys = textureHashes.Count,
    DatabaseJerseyRows = jerseyRows,
    PickerLabels = expectedKeys.Count,
    PickerName = FullKitInstall.DefaultJerseyName,
    BrandingArrayCopies = branding.Sum(result => result.ArrayCopies),
    IntendedFrontSponsors = branding.Sum(result => result.IntendedSponsors),
    BrandingProblems = brandingProblems,
    CorrectedSources = additions.Where(a => (a.Team, a.SourceKit) is ("florida", "away") or ("minnesota", "away")
        or ("arizona", "away") or ("vegas", "home") or ("vegas", "away"))
        .Select(a => $"{a.Team} {a.SourceKit}: {a.SourcePrefix}").ToArray(),
    TextureHashes = textureHashes,
};
File.WriteAllText(Path.Combine(reportDir, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Say("Report: " + Path.Combine(reportDir, "report.json"));
if (brandingProblems.Length > 0)
{
    foreach (string problem in brandingProblems.Take(40)) Say("BRANDING: " + problem);
    Say($"FULL SET AUDIT FAILED: {brandingProblems.Length} installed branding problem(s). See branding-report.json. No game files were changed.");
    return 3;
}
Say($"FULL SET AUDIT PASSED: 64 uniforms, 32 teams, 911 registry references, 64 picker labels, and every installed branding array.");
return 0;
