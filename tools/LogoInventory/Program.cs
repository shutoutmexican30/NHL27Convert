// LogoInventory [output.tsv] - every branding / helmet logo sheet in both games, with a guessed role.
// League-wide marks are recognised by their silhouette (shield, adidas, Fanatics, EPW, blank); a team's own layer 0 is used
// as that team's crest reference, so a crest is no longer lumped in with sponsors. Read only: indexes, PNGs and a TSV.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Suite.Core;

var outPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Roaming, "data")), "logo_inventory.tsv");

var refs = LoadRefs();
var found = new List<Sheet>();

foreach (var game in new[] { "nhl24", "nhl27" })
{
    var index = Path.Combine(SuitePaths.Local, "index", game, "assets.json.gz");
    if (!File.Exists(index)) { Console.Error.WriteLine($"no index for {game}: {index}"); continue; }
    using var gz = new GZipStream(File.OpenRead(index), CompressionMode.Decompress);
    using var doc = JsonDocument.Parse(gz);
    if (!doc.RootElement.TryGetProperty("Entries", out var entries)) continue;

    foreach (var e in entries.EnumerateArray())
    {
        var path = e.GetProperty("Path").GetString();
        if (string.IsNullOrEmpty(path)) continue;
        var n = TextureName.Parse(path);
        if (n.Garment is not ("branding_jersey" or "branding_pant" or "helmetlogo")) continue;
        if (n.Role != "color") continue;
        var png = DecodedPath(game, path);
        found.Add(new Sheet(game, n, path, File.Exists(png) ? png : null));
    }
}

// One crest reference per team and garment: layer 0 of the branding array, which is always the team crest.
var crests = new Dictionary<string, double[]>(StringComparer.Ordinal);
foreach (var s in found.Where(s => s.Png is not null && s.Name.Layer == 0 && s.Name.Garment.StartsWith("branding", StringComparison.Ordinal)))
{
    var key = CrestKey(s);
    if (crests.ContainsKey(key)) continue;
    if (Silhouette(s.Png!) is { } sil) crests[key] = sil;
}
Console.WriteLine($"crest references: {crests.Count} (team + garment)");

var rows = new List<string>
{
    string.Join('\t', "game", "league", "team", "brand", "set", "garment", "kind", "layer", "role_guess", "decoded_png", "asset_path"),
};
foreach (var s in found)
{
    var kind = s.Name.IsArrayName ? "array" : s.Name.Garment == "helmetlogo" ? "helmet" : "layer";
    var role = "(not decoded)";
    if (s.Png is not null)
    {
        double[]? crest = crests.TryGetValue(CrestKey(s), out var c) ? c : null;
        role = GuessRole(s.Png, refs, crest);
    }
    rows.Add(string.Join('\t', s.Game, s.Name.League, s.Name.Team, s.Name.Brand, s.Name.Set, s.Name.Garment, kind,
        s.Name.Layer?.ToString() ?? "", role, s.Png ?? "", s.Path));
}

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
File.WriteAllLines(outPath, rows, new UTF8Encoding(false));
Console.WriteLine($"RESULT {rows.Count - 1} sheets -> {outPath}");
return 0;

static string CrestKey(Sheet s) => $"{s.Game}|{s.Name.League}|{s.Name.Team}|{s.Name.Garment}";

static string DecodedPath(string game, string name) => game == "nhl27"
    ? Path.Combine(SuitePaths.Nhl27Images, SuitePaths.Flatten(name) + ".png")
    : Path.Combine(SuitePaths.Nhl24Images, SuitePaths.Flatten(name) + ".png");

static Dictionary<string, double[]> LoadRefs()
{
    var refs = new Dictionary<string, double[]>(StringComparer.Ordinal);
    var path = Path.Combine(AppContext.BaseDirectory, "data", "logo_refs.json");
    if (!File.Exists(path)) return refs;
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    foreach (var p in doc.RootElement.GetProperty("refs").EnumerateObject())
        refs[p.Name] = p.Value.EnumerateArray().Select(v => v.GetDouble()).ToArray();
    return refs;
}

// 32x32 alpha silhouette, same as the matcher uses.
static double[]? Silhouette(string png)
{
    const int N = 32;
    try
    {
        var img = ImageIO.Load(png);
        var sil = new double[N * N];
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            int sx0 = x * img.Width / N, sx1 = Math.Max(sx0 + 1, (x + 1) * img.Width / N);
            int sy0 = y * img.Height / N, sy1 = Math.Max(sy0 + 1, (y + 1) * img.Height / N);
            long sum = 0; int count = 0;
            for (int sy = sy0; sy < sy1 && sy < img.Height; sy++)
            for (int sx = sx0; sx < sx1 && sx < img.Width; sx++) { sum += img.Pixels[(sy * img.Width + sx) * 4 + 3]; count++; }
            sil[y * N + x] = count == 0 ? 0 : (double)sum / count;
        }
        return sil;
    }
    catch { return null; }
}

static double Difference(double[] a, double[] b)
{
    double sum = 0;
    for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
    return sum / a.Length;
}

static string GuessRole(string png, Dictionary<string, double[]> refs, double[]? crest)
{
    const double accept = 24;
    if (Silhouette(png) is not { } sil) return "";
    var mean = sil.Average();
    if (mean < 4 || sil.All(v => Math.Abs(v - mean) < 2)) return "blank";
    var best = refs.Select(r => (r.Key, d: Difference(sil, r.Value)))
        .Where(r => r.d <= accept).OrderBy(r => r.d).Select(r => r.Key).FirstOrDefault();
    if (best is "patch_epw") return "epw_tag";
    if (best is not null) return best.StartsWith("brand_", StringComparison.Ordinal) ? "brand:" + best[6..] : best;
    if (crest is not null && Difference(sil, crest) <= accept) return "crest";
    return "sponsor/other";
}

sealed record Sheet(string Game, TextureName Name, string Path, string? Png);