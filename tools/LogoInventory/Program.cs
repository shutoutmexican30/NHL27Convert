// LogoInventory [output.tsv] - every branding / helmet logo sheet in NHL 24 and NHL 27, as a sortable TSV.
// Reads the asset indexes the app already built, so open the Convert 27 page once first. Never touches a game.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Suite.Core;

var outPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Roaming, "data")), "logo_inventory.tsv");

var refs = LoadRefs();
var rows = new List<string>
{
    string.Join('\t', "game", "league", "team", "brand", "set", "garment", "kind", "layer", "role_guess", "decoded_png", "asset_path"),
};

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
        var exists = File.Exists(png);
        var kind = n.IsArrayName ? "array" : n.Garment == "helmetlogo" ? "helmet" : "layer";
        rows.Add(string.Join('\t',
            game, n.League, n.Team, n.Brand, n.Set, n.Garment, kind,
            n.Layer?.ToString() ?? "", exists ? GuessRole(png, refs) : "(not decoded)", exists ? png : "", path));
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
File.WriteAllLines(outPath, rows, new UTF8Encoding(false));
Console.WriteLine($"RESULT {rows.Count - 1} sheets -> {outPath}");
return 0;

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

// 32x32 alpha silhouette comparison, same tolerance the matcher uses.
static string GuessRole(string png, Dictionary<string, double[]> refs)
{
    const int N = 32;
    const double accept = 24;
    double[] sil;
    try
    {
        var img = ImageIO.Load(png);
        sil = new double[N * N];
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
    }
    catch { return ""; }

    var mean = sil.Average();
    if (mean < 4 || sil.All(v => Math.Abs(v - mean) < 2)) return "blank";
    var best = refs.Select(r => (r.Key, d: sil.Zip(r.Value, (a, b) => Math.Abs(a - b)).Average()))
        .Where(r => r.d <= accept).OrderBy(r => r.d).Select(r => r.Key).FirstOrDefault();
    if (best is "patch_epw") return "epw_tag";
    if (best is not null) return best.StartsWith("brand_", StringComparison.Ordinal) ? "brand:" + best[6..] : best;
    return "sponsor/other";
}