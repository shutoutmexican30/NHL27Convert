// GearPlan [outDir] - which goalie equipment models each game ships, and what the DBs actually reference.
// Read-only: reads both asset indexes and the equipment CSV dumps, writes a TSV. Never touches a game.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Suite.Core;

var outDir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(SuitePaths.Roaming, "data");
string[] partNames = ["pads", "blocker", "trapper", "helmet", "mask", "stick"];
var knownMaps = new HashSet<string>(StringComparer.Ordinal)
{ "color", "normal", "coeff", "idmap", "bentnormal", "ferender", "recolor1", "recolor2", "recolor3", "base" };

// goalie_blocker_bauer_supreme_fuse_left_base_color -> blocker / bauer / supreme_fuse / left / base_color
// Peel from the right: the map suffix (two tokens for base_*), then the side, everything left of the brand is the model.
static (string Part, string Brand, string Model, string Side, string Map)? ParseLeaf(string leaf)
{
    if (!leaf.StartsWith("goalie_", StringComparison.Ordinal)) return null;
    var t = leaf["goalie_".Length..].Split('_', StringSplitOptions.RemoveEmptyEntries);
    if (t.Length < 4) return null;
    string part = t[0], brand = t[1], side = "";
    int end = t.Length;                       // exclusive
    string map = t[end - 1];
    end--;
    if (end - 1 >= 2 && t[end - 1] == "base") { map = "base_" + map; end--; }
    if (end - 1 >= 2 && (t[end - 1] is "left" or "right" or "none")) { side = t[end - 1]; end--; }
    if (end <= 2) return null;
    return (part, brand, string.Join('_', t[2..end]), side, map);
}


var catalogs = new Dictionary<string, Dictionary<Key, Acc>>(StringComparer.Ordinal);
foreach (var game in new[] { "nhl24", "nhl27" })
{
    var index = Path.Combine(SuitePaths.Local, "index", game, "assets.json.gz");
    if (!File.Exists(index)) { Console.Error.WriteLine($"no index for {game}"); continue; }
    using var gz = new GZipStream(File.OpenRead(index), CompressionMode.Decompress);
    using var doc = JsonDocument.Parse(gz);
    if (!doc.RootElement.TryGetProperty("Entries", out var entries)) continue;
    var models = new Dictionary<Key, Acc>();
    foreach (var e in entries.EnumerateArray())
    {
        var path = e.GetProperty("Path").GetString();
        if (path is null || !path.Contains("/goalie/", StringComparison.Ordinal)) continue;
        if (ParseLeaf(path.Split('/')[^1]) is not { } p) continue;
        if (!knownMaps.Contains(p.Map)) continue;
        if (path.Contains("_branding_", StringComparison.Ordinal) || path.Contains("_logo", StringComparison.Ordinal)) continue;
        if (Array.IndexOf(partNames, p.Part) < 0) continue;
        var key = new Key(p.Part, p.Brand, p.Model);
        if (!models.TryGetValue(key, out var acc)) models[key] = acc = new Acc();
        acc.Maps.Add(p.Map);
        if (p.Side.Length > 0) acc.Sides.Add(p.Side);
        acc.Textures++;
    }
    catalogs[game] = models;
    Console.WriteLine($"{game}: {models.Count} goalie model(s)");
}

var rows = new List<string> { string.Join('\t', "game", "part", "brand", "model", "maps", "textures", "sides") };
foreach (var game in catalogs.Keys.Order(StringComparer.Ordinal))
    foreach (var kv in catalogs[game].OrderBy(k => k.Key.Part).ThenBy(k => k.Key.Brand).ThenBy(k => k.Key.Model))
        rows.Add(string.Join('\t', game, kv.Key.Part, kv.Key.Brand, kv.Key.Model,
            string.Join(",", kv.Value.Maps.Order(StringComparer.Ordinal)), kv.Value.Textures, kv.Value.Sides.Count));

var m24 = catalogs["nhl24"];
var m27 = catalogs["nhl27"];
var shared = m24.Keys.Intersect(m27.Keys).Count();
var only27 = m27.Keys.Except(m24.Keys).OrderBy(k => k.Part).ThenBy(k => k.Brand).ThenBy(k => k.Model).ToList();
var only24 = m24.Keys.Except(m27.Keys).Count();
Console.WriteLine();
Console.WriteLine($"in both games              : {shared}");
Console.WriteLine($"NHL 27 only (not addable)  : {only27.Count}");
Console.WriteLine($"NHL 24 only                : {only24}");
Console.WriteLine();
Console.WriteLine("NHL 27-only models by part:");
foreach (var g in only27.GroupBy(k => k.Part).OrderBy(g => g.Key))
    Console.WriteLine($"   {g.Key,-8} {g.Count(),3}   e.g. {string.Join(", ", g.Take(3).Select(k => k.Brand + "/" + k.Model))}");

var dumpRoot = @"C:\Users\devin\Documents\Codex\2026-10-20\where-are-the-imported-rosters-located\RosterConversion";
foreach (var game in new[] { "nhl24-csv", "nhl27-csv" })
{
    var f = Path.Combine(dumpRoot, game, "stockteamequipmenttable.csv");
    if (!File.Exists(f)) { Console.WriteLine($"{game}: no equipment dump"); continue; }
    var lines = File.ReadAllLines(f);
    var head = lines[0].Split(',');
    int bi = Array.FindIndex(head, h => h.Trim('"') == "brand"), ei = Array.FindIndex(head, h => h.Trim('"') == "equipment");
    if (bi < 0) { Console.WriteLine($"{game}: no brand column"); continue; }
    var brands = new HashSet<string>(StringComparer.Ordinal);
    var equip = new HashSet<string>(StringComparer.Ordinal);
    foreach (var line in lines.Skip(1))
    {
        var c = line.Split(',');
        if (c.Length <= bi) continue;
        brands.Add(c[bi].Trim('"'));
        if (ei >= 0 && c.Length > ei) equip.Add(c[ei].Trim('"'));
    }
    Console.WriteLine($"{game}: {brands.Count} brand value(s) [{string.Join(" ", brands.Order().Take(12))}], {equip.Count} equipment value(s) [{string.Join(" ", equip.Order().Take(16))}]");
}

var dir = Path.Combine(outDir, "gear-feasibility");
Directory.CreateDirectory(dir);
File.WriteAllLines(Path.Combine(dir, "goalie-models.tsv"), rows, new UTF8Encoding(false));
Console.WriteLine();
Console.WriteLine($"written: {Path.Combine(dir, "goalie-models.tsv")} ({rows.Count - 1} rows)");
return 0;

sealed class Acc
{
    public readonly HashSet<string> Maps = new(StringComparer.Ordinal);
    public readonly HashSet<string> Sides = new(StringComparer.Ordinal);
    public int Textures;
}
sealed record Key(string Part, string Brand, string Model);
