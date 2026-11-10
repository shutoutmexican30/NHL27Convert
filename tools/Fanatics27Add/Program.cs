using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

// Fanatics27Add --target <V3.2 root> [--out <dir>] [--source-mod <slot-safe mod>]
// Plans and exports Fanatics 27 Home/Away as converted adidas alternate uniforms. It does not write the game.
var target = Path.GetFullPath(Args.Value("--target") ?? @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC").TrimEnd('\\');
var outDir = Path.GetFullPath(Args.Value("--out") ?? Path.Combine("Debugging", "fanatics27-add"));
var sourceModPath = Args.Value("--source-mod") ?? ModFile.PathFor("NHL 27 uniforms slot-safe");
Directory.CreateDirectory(outDir);
var settings = SuiteSettings.Load();
Oodle.Ensure(settings);
var sourceMod = ModFile.Load(sourceModPath) ?? throw new FileNotFoundException("source mod not found: " + sourceModPath);
var variants = VariantMap.Load();
var meta = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes", "database", "t3db", "nhl_game-meta.xml");
var lf = new LegacyFiles(target, settings);
Console.Error.WriteLine("Reading the target bundle index...");
var bundleIndex = BundleIndex.Build(target, s => Console.Error.WriteLine("  " + s));
var dbs = new Dictionary<string, T3Db>
{
    ["nhl_game"] = T3Db.Load(lf.Read("gamemodes/database/t3db/nhl_game.db"), meta),
    ["initial_experience"] = T3Db.Load(lf.Read("gamemodes/roster/initial_experience.db"), meta),
};
var words = "home away two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty twentyone twentytwo twentythree twentyfour twentyfive".Split(' ');
var folderById = new Dictionary<int, string>
{
    [0] = "anaheim", [1] = "winnipeg", [2] = "boston", [3] = "buffalo", [4] = "calgary", [5] = "carolina",
    [6] = "chicago", [7] = "colorado", [8] = "columbus", [9] = "dallas", [10] = "detroit", [11] = "edmonton",
    [12] = "florida", [13] = "losangeles", [14] = "minnesota", [15] = "montreal", [16] = "nashville",
    [17] = "newjersey", [18] = "nyi", [19] = "nyr", [20] = "ottawa", [21] = "philadelphia", [22] = "arizona",
    [23] = "pittsburgh", [24] = "stlouis", [25] = "sanjose", [26] = "tampabay", [27] = "toronto",
    [28] = "vancouver", [29] = "washington", [30] = "vegas", [31] = "seattle",
};

var selections = new List<Selection>();
var plan = new List<string> { "team\tfolder\thomeVariant\tawayVariant\thomeSource27\tawaySource27" };
foreach (var (team, folder) in folderById.OrderBy(kv => kv.Key))
{
    var t = dbs["nhl_game"].Tables["stockteamjerseys"];
    var candidates = Enumerable.Range(0, t.InUse)
        .Where(r => dbs["nhl_game"].GetInt(t, r, "teamid") == team
                 && dbs["nhl_game"].GetInt(t, r, "style") == 2
                 && dbs["nhl_game"].GetInt(t, r, "variant") >= 2)
        .OrderBy(r => dbs["nhl_game"].GetInt(t, r, "active") == 1 ? 0 : 1)
        .ThenBy(r => dbs["nhl_game"].GetInt(t, r, "variant"))
        .Select(r => (int)dbs["nhl_game"].GetInt(t, r, "variant"))
        .ToList();
    var vt = variants.ByNhl24Folder(folder) ?? throw new InvalidOperationException($"No variant_map entry for {folder}");
    if (vt.Nhl27.Home is null || vt.Nhl27.Away is null) throw new InvalidOperationException($"No NHL 27 default home/away for {folder}");
    if (vt.Nhl24.Home is null || vt.Nhl24.Away is null) throw new InvalidOperationException($"No NHL 24 default home/away for {folder}");
    // The source mod's slots live in the NHL 24 default home/away folders; their layer images are the NHL 27 art.
    // A candidate fits when its jersey branding arrays exist. Source layers beyond the target's slice count are
    // dropped (and reported) per the user's rule: use the slots NHL 24 has, skip the rest for manual stamping.
    int? Drops(int v, string sourceSet)
    {
        var word = words[v];
        int drops = 0;
        var sawJersey = false;
        foreach (var r in sourceMod.Replacements)
        {
            if (r.Layers is not { Count: > 0 }) continue;
            if (!IsSourceArray(r.Slot, folder, sourceSet)) continue;
            var isJersey = r.Slot.Contains("branding_jersey_", StringComparison.OrdinalIgnoreCase);
            var depth = TargetDepth(target, bundleIndex, MapTarget(r.Slot, sourceSet, word));
            if (isJersey)
            {
                if (depth < 0) return null;
                sawJersey = true;
            }
            if (depth >= 0) drops += r.Layers.Keys.Count(k => k >= depth);
        }
        return sawJersey ? drops : null;
    }
    int Rank(int v) => activeVariants.Contains(v) ? 0 : 1;
    var homePick = candidates.Where(v => Drops(v, vt.Nhl24.Home).HasValue).OrderBy(v => Drops(v, vt.Nhl24.Home)!.Value).ThenBy(Rank).ThenBy(v => v).Cast<int?>().FirstOrDefault() ?? -1;
    var awayPick = candidates.Where(v => v != homePick && Drops(v, vt.Nhl24.Away).HasValue).OrderBy(v => Drops(v, vt.Nhl24.Away)!.Value).ThenBy(Rank).ThenBy(v => v).Cast<int?>().FirstOrDefault() ?? -1;
    var homeVariant = homePick;
    var awayVariant = awayPick;
    selections.Add(new Selection(team, folder, homeVariant, awayVariant, vt.Nhl24.Home, vt.Nhl24.Away));
    plan.Add($"{team}\t{folder}\t{(homeVariant >= 0 ? words[homeVariant] : "NONE")}\t{(awayVariant >= 0 ? words[awayVariant] : "NONE")}\t{vt.Nhl24.Home}\t{vt.Nhl24.Away}");
}
File.WriteAllLines(Path.Combine(outDir, "fanatics27-plan.tsv"), plan);

// Depth diagnostic: every adidas set's branding-array slice counts vs what the source art needs.
var depthLines = new List<string> { "team\tfolder\tside\tsourceSet\tneeded\tsetWord\tjerseyColor\tjerseyNormal\tpantColor\tpantNormal" };
foreach (var (team, folder) in folderById.OrderBy(kv => kv.Key))
{
    var vt = variants.ByNhl24Folder(folder)!;
    foreach (var (side, sourceSet) in new[] { ("home", vt.Nhl24.Home!), ("away", vt.Nhl24.Away!) })
    {
        var needed = sourceMod.Replacements
            .Where(r => r.Layers is { Count: > 0 } && IsSourceArray(r.Slot, folder, sourceSet))
            .Select(r => r.Layers!.Keys.Max() + 1).DefaultIfEmpty(0).Max();
        foreach (var w in "home away two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty twentyone twentytwo twentythree".Split(' '))
        {
            const string prefix = "content/character/skater/league/nhl/";
            int D(string kind, string suffix) => TargetDepth(target, bundleIndex, $"{prefix}{folder}/adidas/{w}/branding_{kind}_adidas_{folder}_{w}_{suffix}_array");
            depthLines.Add($"{team}\t{folder}\t{side}\t{sourceSet}\t{needed}\t{w}\t{D("jersey", "color")}\t{D("jersey", "normal")}\t{D("pant", "color")}\t{D("pant", "normal")}");
        }
    }
}
File.WriteAllLines(Path.Combine(outDir, "fanatics27-depths.tsv"), depthLines);

var textureMod = new ModFile
{
    Name = "Fanatics 27 additions textures",
    Kind = "textures",
    Comment = "Safe branding-array art only: NHL 27 Fanatics home/away arrays written into selected adidas alternate containers. No UV-bound garment sheets.",
};
var textureReport = new List<string> { "team\tfolder\tside\ttargetVariant\tsourceSet\ttargetSlot\taction" };
foreach (var s in selections)
{
    var sides = new List<(string side, int targetVariant, string sourceSet)>();
    if (s.HomeVariant >= 0) sides.Add(("home", s.HomeVariant, s.HomeSource27));
    if (s.AwayVariant >= 0) sides.Add(("away", s.AwayVariant, s.AwaySource27));
    if (s.HomeVariant < 0) textureReport.Add($"{s.Team}\t{s.Folder}\thome\tNONE\t{s.HomeSource27}\t\tno usable adidas alternate with jersey arrays");
    if (s.AwayVariant < 0) textureReport.Add($"{s.Team}\t{s.Folder}\taway\tNONE\t{s.AwaySource27}\t\tno usable adidas alternate with jersey arrays");
    foreach (var (side, targetVariant, sourceSet) in sides)
    {
        foreach (var r in sourceMod.Replacements)
        {
            if (r.Layers is not { Count: > 0 }) continue;
            if (!IsSourceArray(r.Slot, s.Folder, sourceSet)) continue;
            var targetSlot = MapTarget(r.Slot, sourceSet, words[targetVariant]);
            var depth = TargetDepth(target, bundleIndex, targetSlot);
            var needed = r.Layers.Keys.Max() + 1;
            if (depth < 0)
            {
                textureReport.Add($"{s.Team}\t{s.Folder}\t{side}\t{words[targetVariant]}\t{sourceSet}\t{targetSlot}\ttarget array missing; skipped");
                continue;
            }
            if (depth < needed)
            {
                textureReport.Add($"{s.Team}\t{s.Folder}\t{side}\t{words[targetVariant]}\t{sourceSet}\t{targetSlot}\ttarget array has {depth} layers, needs {needed}; skipped");
                continue;
            }
            textureMod.Replacements.Add(new TextureReplacement(targetSlot, "", r.Layers, r.Alpha));
            textureReport.Add($"{s.Team}\t{s.Folder}\t{side}\t{words[targetVariant]}\t{sourceSet}\t{targetSlot}\twritten");
        }
    }
}
var texturePath = Path.Combine(outDir, "fanatics27-textures.mod.json");
File.WriteAllText(texturePath, System.Text.Json.JsonSerializer.Serialize(textureMod, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
File.WriteAllLines(Path.Combine(outDir, "fanatics27-texture-report.tsv"), textureReport);

var dbMod = new ModFile
{
    Name = "Fanatics 27 additions db",
    Kind = "legacy",
    Comment = "Marks selected adidas alternate rows as Fanatics 27 Home/Away. Existing rows are updated in place; home/away adidas defaults remain untouched.",
    Legacy = [],
};
var report = new List<string> { "db\tteam\tfolder\tvariant\tside\taction" };
foreach (var (dbName, db) in dbs)
{
    var t = db.Tables["stockteamjerseys"];
    var changed = false;
    foreach (var s in selections)
    {
        foreach (var (side, variant) in new[] { ("home", s.HomeVariant), ("away", s.AwayVariant) })
        {
            if (variant < 0)
            {
                report.Add($"{dbName}\t{s.Team}\t{s.Folder}\tNONE\t{side}\tno usable adidas alternate with jersey arrays");
                continue;
            }
            var row = Enumerable.Range(0, t.InUse).FirstOrDefault(r =>
                db.GetInt(t, r, "teamid") == s.Team && db.GetInt(t, r, "style") == 2 && db.GetInt(t, r, "variant") == variant, -1);
            if (row < 0)
            {
                report.Add($"{dbName}\t{s.Team}\t{s.Folder}\t{words[variant]}\t{side}\trow missing; skipped");
                continue;
            }
            db.SetInt(t, row, "isdefault", 0);
            db.SetInt(t, row, "active", 1);
            db.SetInt(t, row, "islightcolor", side == "away" ? 1 : 0);
            db.SetInt(t, row, "locstring", side == "away" ? 1 : 0);
            changed = true;
            report.Add($"{dbName}\t{s.Team}\t{s.Folder}\t{words[variant]}\t{side}\tmarked");
        }
    }
    if (changed)
    {
        var saved = db.Save();
        var raw = lf.Read(dbName == "nhl_game" ? "gamemodes/database/t3db/nhl_game.db" : "gamemodes/roster/initial_experience.db");
        if (saved.Length != raw.Length) throw new InvalidOperationException($"{dbName}: saved DB length changed");
        var path = Path.Combine(outDir, dbName + ".db");
        File.WriteAllBytes(path, saved);
        dbMod.Legacy!.Add(new LegacyReplacement(dbName == "nhl_game" ? "gamemodes/database/t3db/nhl_game.db" : "gamemodes/roster/initial_experience.db", path));
    }
}
var dbPath = Path.Combine(outDir, "fanatics27-db.mod.json");
File.WriteAllText(dbPath, System.Text.Json.JsonSerializer.Serialize(dbMod, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
File.WriteAllLines(Path.Combine(outDir, "fanatics27-db-report.tsv"), report);

Console.WriteLine($"planned teams: {selections.Count}; texture replacements: {textureMod.Replacements.Count}; DB files: {dbMod.Legacy?.Count ?? 0}");
Console.WriteLine(texturePath);
Console.WriteLine(dbPath);

static string MapTarget(string sourceSlot, string sourceSet, string word) => sourceSlot
    .Replace($"/adidas/{sourceSet}/", $"/adidas/{word}/", StringComparison.OrdinalIgnoreCase)
    .Replace($"_{sourceSet}_", $"_{word}_", StringComparison.OrdinalIgnoreCase);

static int TargetDepth(string game, BundleIndex index, string name)
{
    var res = index.ResByName.GetValueOrDefault(name)?.FirstOrDefault();
    if (res is null) return -1;
    var stored = BundleIndex.ReadBytes(res.Data);
    byte[] header; try { header = ChunkBlocks.Parse(stored).Decode(); } catch { header = stored; }
    return Ps4Codec.Slices(header);
}

static bool Viable(string game, BundleIndex index, ModFile sourceMod, string folder, string sourceSet, string word)
{
    var matched = 0;
    foreach (var r in sourceMod.Replacements)
    {
        if (r.Layers is not { Count: > 0 }) continue;
        if (!IsSourceArray(r.Slot, folder, sourceSet)) continue;
        matched++;
        if (TargetDepth(game, index, MapTarget(r.Slot, sourceSet, word)) < r.Layers.Keys.Max() + 1) return false;
    }
    return matched > 0;
}

static bool IsSourceArray(string slot, string folder, string sourceSet)
{
    if (!slot.Contains($"/nhl/{folder}/adidas/{sourceSet}/", StringComparison.OrdinalIgnoreCase)) return false;
    if (!slot.Contains("branding_jersey_", StringComparison.OrdinalIgnoreCase) && !slot.Contains("branding_pant_", StringComparison.OrdinalIgnoreCase)) return false;
    return slot.EndsWith("_color_array", StringComparison.OrdinalIgnoreCase) || slot.EndsWith("_normal_array", StringComparison.OrdinalIgnoreCase);
}

sealed record Selection(int Team, string Folder, int HomeVariant, int AwayVariant, string HomeSource27, string AwaySource27);
static class Args
{
    public static string? Value(string key)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
