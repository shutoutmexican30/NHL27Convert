using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// nhluniforms.com's season drawings as an answer key for the matcher.  Personal use only: one fan's artwork, cached
/// locally (%LOCALAPPDATA%\CenterIceModdingSuite\uniformref\&lt;season&gt;), never pasted onto a texture or shipped in a mod.
///
/// Each season page (/Mobile/2026-27.html) links one drawing per team, /Mobile/{Team}/{Team}{NN}.png (NN only counts
/// the team's seasons on the site; it goes up every year).  A drawing is 960 wide and a stack of 450-pixel kit bands:
/// home first, road last, any alternate in between (Winnipeg 2026-27: 1350 high, three kits).  In a band the jersey
/// (front and back) fills the left, socks sit at the left edge, and helmet and/or pants are in the right column, on a
/// light grey gradient.  The colours are flat, so each region's main colours are read directly and compared with the
/// jersey colour sheet of every NHL 27 kit of that team, and with last season's drawing to see what changed.
/// </summary>
public static class UniformReference
{
    public const string Site = "https://nhluniforms.com";
    public static string Dir(string season) => SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "uniformref", season));

    public sealed record Drawing(string Team, int Set, string Url, string Png);
    public sealed record Swatch(long Rgb, double Share);

    /// <summary>The season under way (it turns over in July): "2026-27" from November 2026.</summary>
    public static string CurrentSeason()
    {
        int y = DateTime.Today.Month >= 7 ? DateTime.Today.Year : DateTime.Today.Year - 1;
        return $"{y}-{(y + 1) % 100:D2}";
    }

    /// <summary>The previous season's name: "2026-27" -> "2025-26".</summary>
    public static string Previous(string season) => $"{int.Parse(season[..4]) - 1}-{season.Substring(5, 2) switch { "00" => "99", var y => (int.Parse(y) - 1).ToString("D2") }}";

    /// <summary>The season's drawings, downloaded once (a cached PNG is never fetched again).</summary>
    public static async Task<List<Drawing>> FetchAsync(string season, HttpClient http, Action<string>? progress = null, CancellationToken ct = default)
    {
        var dir = Dir(season);
        var indexFile = Path.Combine(dir, "index.html");
        if (!File.Exists(indexFile)) File.WriteAllText(indexFile, await http.GetStringAsync($"{Site}/Mobile/{season}.html", ct));
        var html = File.ReadAllText(indexFile);
        var list = new List<Drawing>();
        foreach (Match m in Regex.Matches(html, @"src=""(/Mobile/([A-Za-z]+)/\2(\d+)\.png)"""))
        {
            var team = m.Groups[2].Value;
            if (list.Any(d => d.Team == team)) continue;
            var png = Path.Combine(dir, team + ".png");
            if (!File.Exists(png))
            {
                progress?.Invoke($"nhluniforms.com {season}: {team}");
                File.WriteAllBytes(png, await http.GetByteArrayAsync(Site + m.Groups[1].Value, ct));
                await Task.Delay(250, ct);   // be gentle with a one-person site
            }
            list.Add(new Drawing(team, int.Parse(m.Groups[3].Value), Site + m.Groups[1].Value, png));
        }
        return list;
    }

    /// <summary>"RedWings" -> "Red Wings": the nickname a team's name ends with.</summary>
    public static string Nickname(string team) => Regex.Replace(team, "(?<=[a-z])(?=[A-Z])", " ");

    public const int Band = 450;
    /// <summary>Kits in a drawing, top to bottom: home first, road last.</summary>
    public static int Kits(Rgba img) => Math.Max(1, img.Height / Band);
    public static string KitLabel(int kit, int kits) => kit == 0 ? "home" : kit == kits - 1 ? "road" : kits == 3 ? "alternate" : $"alternate {kit}";

    // Regions inside one kit band: (x0, y0, x1, y1).  The jersey box leaves out the socks.
    static readonly (int X0, int Y0, int X1, int Y1) JerseyBox = (15, 15, 745, 440), SocksBox = (85, 215, 150, 445), SideBox = (750, 10, 860, 440);

    /// <summary>Main colours of one region of a kit band ("jersey", "socks", "side" = helmet/pants column).</summary>
    public static List<Swatch> Palette(Rgba img, int kit, string region)
    {
        var (x0, y0, x1, y1) = region switch { "jersey" => JerseyBox, "socks" => SocksBox, _ => SideBox };
        int top = kit * Band;
        var pixels = new List<long>();
        for (int y = top + y0; y < Math.Min(top + y1, img.Height); y++)
        {
            // The background is a grey gradient: compare each pixel with the background at the left edge of its row.
            long bg = Rgb(img, 3, y);
            for (int x = x0; x < Math.Min(x1, img.Width); x++)
            {
                if (region == "jersey" && x >= SocksBox.X0 - 8 && x < SocksBox.X1 + 8 && y >= top + SocksBox.Y0 - 8) continue;
                long c = Rgb(img, x, y);
                if (Grey(c) && Dist(c, bg) < 16) continue;
                pixels.Add(c);
            }
        }
        return Cluster(pixels, 0.04);
    }

    /// <summary>Mean per-channel difference of two drawings' jersey boxes in one band (0 = identical drawing).</summary>
    public static double JerseyDifference(Rgba a, int kitA, Rgba b, int kitB)
    {
        double sum = 0; long n = 0;
        for (int y = JerseyBox.Y0; y < JerseyBox.Y1; y += 2)
            for (int x = JerseyBox.X0; x < JerseyBox.X1; x += 2)
            {
                int ya = kitA * Band + y, yb = kitB * Band + y;
                if (ya >= a.Height || yb >= b.Height) continue;
                long ca = Rgb(a, x, ya), cb = Rgb(b, x, yb);
                sum += Math.Abs(((ca >> 16) & 255) - ((cb >> 16) & 255)) + Math.Abs(((ca >> 8) & 255) - ((cb >> 8) & 255)) + Math.Abs((ca & 255) - (cb & 255));
                n += 3;
            }
        return n == 0 ? 999 : sum / n;
    }

    /// <summary>Main colours of an image whose alpha marks what to count (an NHL 27 colour sheet).</summary>
    public static List<Swatch> Palette(Rgba img)
    {
        var pixels = new List<long>();
        for (int i = 0; i < img.Pixels.Length; i += 4 * 3)   // every third pixel is plenty for a flat sheet
            if (img.Pixels[i + 3] > 128) pixels.Add(((long)img.Pixels[i] << 16) | ((long)img.Pixels[i + 1] << 8) | img.Pixels[i + 2]);
        return Cluster(pixels, 0.04);
    }

    /// <summary>
    /// How far apart two palettes are (0 = the same colours), symmetric, share-weighted, in Lab with lightness at half
    /// weight: NHL 27's colour sheets are lifted albedo (its black is 1B1A1B, its white E7E7E7), so a plain RGB distance
    /// called Boston's own black-and-gold kit a poor match.  Under ~10 is the same colours.
    /// </summary>
    public static double Distance(IReadOnlyList<Swatch> a, IReadOnlyList<Swatch> b)
    {
        if (a.Count == 0 || b.Count == 0) return 999;
        double Side(IReadOnlyList<Swatch> p, IReadOnlyList<Swatch> q) => p.Sum(s => s.Share * q.Min(t => Perceptual(s.Rgb, t.Rgb))) / p.Sum(s => s.Share);
        // The same colours in other amounts are another kit (Boston's black home vs a gold alternate): each colour of
        // a goes to its nearest colour of b, and the shares that do not line up count too.
        var mass = new double[b.Count];
        foreach (var s in a) mass[Enumerable.Range(0, b.Count).MinBy(j => Perceptual(s.Rgb, b[j].Rgb))] += s.Share / a.Sum(x => x.Share);
        double off = 0.5 * Enumerable.Range(0, b.Count).Sum(j => Math.Abs(mass[j] - b[j].Share / b.Sum(x => x.Share)));
        return (Side(a, b) + Side(b, a)) / 2 + 30 * off;
    }

    static double Perceptual(long x, long y)
    {
        var (l1, a1, b1) = Lab(x); var (l2, a2, b2) = Lab(y);
        return Math.Sqrt(0.25 * (l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }
    static (double L, double A, double B) Lab(long c)
    {
        static double Lin(long v) { double s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        double r = Lin((c >> 16) & 255), g = Lin((c >> 8) & 255), b = Lin(c & 255);
        double x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047, y = 0.2126 * r + 0.7152 * g + 0.0722 * b, z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    public static string Hex(IEnumerable<Swatch> p) => string.Join(" ", p.Select(s => $"{s.Rgb:X6} {s.Share:P0}"));

    static long Rgb(Rgba img, int x, int y)
    {
        int i = (y * img.Width + x) * 4;
        return ((long)img.Pixels[i] << 16) | ((long)img.Pixels[i + 1] << 8) | img.Pixels[i + 2];
    }
    static bool Grey(long c) { int r = (int)(c >> 16) & 255, g = (int)(c >> 8) & 255, b = (int)c & 255; return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 12; }
    public static double Dist(long a, long b)
    {
        double dr = ((a >> 16) & 255) - ((b >> 16) & 255), dg = ((a >> 8) & 255) - ((b >> 8) & 255), db = (a & 255) - (b & 255);
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    // Colours within 36 of each other are one colour (drawing anti-aliasing, fabric texture in the NHL 27 sheet).
    static List<Swatch> Cluster(List<long> pixels, double minShare)
    {
        if (pixels.Count == 0) return [];
        var bins = pixels.GroupBy(c => c & 0xF0F0F0).Select(g => (C: g.First(), N: g.Count())).OrderByDescending(b => b.N).ToList();
        var clusters = new List<(long C, int N)>();
        foreach (var (c, n) in bins)
        {
            int i = clusters.FindIndex(k => Dist(k.C, c) < 36);
            if (i < 0) clusters.Add((c, n)); else clusters[i] = (clusters[i].C, clusters[i].N + n);
        }
        double total = pixels.Count;
        return clusters.Where(k => k.N / total >= minShare).OrderByDescending(k => k.N).Select(k => new Swatch(k.C, k.N / total)).ToList();
    }

    /// <summary>
    /// The whole check: downloads this and last season's drawings, decodes each team's NHL 27 kit colour sheets, and
    /// writes uniformref\&lt;season&gt;\report.txt.  Reads nothing from and writes nothing to any game.
    /// </summary>
    public static async Task<(string Report, string Summary)> CheckAsync(string season, SuiteSettings settings, Nhl27Catalog catalog, VariantMap variants,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CenterIceModdingSuite/1.0 (personal reference check)");
        var now = await FetchAsync(season, http, progress, ct);
        List<Drawing> before = [];
        try { before = await FetchAsync(Previous(season), http, progress, ct); } catch (HttpRequestException) { }
        var images = new TextureImages(settings);
        var lines = new List<string> { $"Uniform check against nhluniforms.com {season} ({DateTime.Now:yyyy-MM-dd HH:mm})", "",
            "Palette distance: 0 = the same colours; under ~12 is a match. Personal reference only (the site's artwork).", "" };
        int changed = 0, flagged = 0, checkedTeams = 0;
        foreach (var d in now.OrderBy(d => d.Team))
        {
            ct.ThrowIfCancellationRequested();
            var nick = Nickname(d.Team);
            var team = variants.Teams.FirstOrDefault(t => t.Name27.EndsWith(nick, StringComparison.OrdinalIgnoreCase) || t.Name24.EndsWith(nick, StringComparison.OrdinalIgnoreCase))
                ?? (d.Team == "Mammoth" ? variants.Teams.FirstOrDefault(t => t.Name27.Contains("Utah") || t.Nhl27.Folder.Contains("utah")) : null);
            var img = ImageIO.Load(d.Png);
            int kits = Kits(img);
            var last = before.FirstOrDefault(b => b.Team == d.Team) is { } l ? ImageIO.Load(l.Png) : null;
            lines.Add($"{nick} - {kits} kit(s) drawn: {string.Join(", ", Enumerable.Range(0, kits).Select(k => KitLabel(k, kits)))}");
            var pal = new List<List<Swatch>>();
            bool teamChanged = false;
            for (int k = 0; k < kits; k++)
            {
                var label = KitLabel(k, kits);
                pal.Add(Palette(img, k, "jersey"));
                // Same kit last season: home with home, road with road, an alternate with the alternate in that place.
                string vsLast = "";
                if (last is not null)
                {
                    int lastKits = Kits(last);
                    int lk = label == "home" ? 0 : label == "road" ? lastKits - 1 : k < lastKits - 1 ? k : -1;
                    if (lk < 0 || (lastKits == 1 && label != "home")) { vsLast = "  NEW (not drawn last season)"; teamChanged = true; }
                    else
                    {
                        double diff = JerseyDifference(img, k, last, lk);
                        bool isNew = diff > 10;
                        teamChanged |= isNew;
                        vsLast = isNew ? $"  CHANGED since {Previous(season)} (difference {diff:F0})" : $"  same as {Previous(season)} (difference {diff:F0})";
                    }
                }
                lines.Add($"  {label,-10} jersey {Hex(pal[k])}{vsLast}");
                lines.Add($"  {"",-10} socks  {Hex(Palette(img, k, "socks"))}");
                lines.Add($"  {"",-10} helmet/pants {Hex(Palette(img, k, "side"))}");
            }
            if (teamChanged) changed++;
            if (team is null) { lines.Add("  (no team in variant_map.json for this nickname)"); lines.Add(""); continue; }

            // Every NHL 27 kit of the team: its jersey and sock colour sheets.
            var prefix = $"{TextureName.SkaterPrefix}nhl/{team.Nhl27.Folder}/";
            var folders27 = catalog.ByFolder.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            var sheets = new Dictionary<string, (Nhl27Texture? Jersey, Nhl27Texture? Sock)>();
            foreach (var (folder, list) in folders27)
            {
                Nhl27Texture? Sheet(string garment) => list.FirstOrDefault(t => t.Name[(t.Name.LastIndexOf('/') + 1)..] is var leaf
                    && leaf.StartsWith(garment + "_") && leaf.EndsWith("_color") && !leaf.Contains("_base_") && !leaf.Contains("branding"));
                sheets[folder[(folder.LastIndexOf('/') + 1)..]] = (Sheet("jersey"), Sheet("sock"));
            }
            progress?.Invoke($"{nick}: decoding {sheets.Count} NHL 27 kit(s)");
            var pngs = await images.Nhl27Async(sheets.Values.SelectMany(s => new[] { s.Jersey, s.Sock }).OfType<Nhl27Texture>(), progress, ct);
            var kitPal = sheets.Where(s => s.Value.Jersey is not null && pngs.ContainsKey(s.Value.Jersey.Name))
                .ToDictionary(s => s.Key, s => Palette(ImageIO.Load(pngs[s.Value.Jersey!.Name])));
            if (kitPal.Count == 0) { lines.Add("  (no decodable NHL 27 jersey sheets)"); lines.Add(""); continue; }
            checkedTeams++;
            var extras = team.ExtraKits.Select(x => x.Nhl27).ToHashSet();
            for (int k = 0; k < kits; k++)
            {
                var label = KitLabel(k, kits);
                var ranked = kitPal.Select(kv => (Kit: kv.Key, D: Distance(pal[k], kv.Value))).OrderBy(x => x.D).ToList();
                var best = ranked[0];
                string top3 = string.Join(", ", ranked.Take(3).Select(x => $"'{x.Kit}' {x.D:F0} [{Hex(kitPal[x.Kit].Take(3))}]"));
                if (label is "home" or "road")
                {
                    var chosen = label == "home" ? team.Nhl27.Home : team.Nhl27.Away;
                    var mine = ranked.FirstOrDefault(x => x.Kit == chosen);
                    bool off = chosen is not null && mine.Kit is not null && best.Kit != chosen && mine.D > best.D + 6 && mine.D > 12;
                    if (off) flagged++;
                    lines.Add($"  {label}: matcher uses NHL 27 '{chosen}' ({(mine.Kit is null ? "not found" : $"distance {mine.D:F0} [{Hex(kitPal[chosen!].Take(3))}]")}){(off ? "   <-- CHECK: another kit looks closer" : "")}");
                }
                else
                {
                    // An alternate the drawing shows: is its closest NHL 27 kit one the matcher brings over (extraKits)?
                    // Covered when an extraKit is about as close as the best (Buffalo: 'seven' 14, 'twelve' 15 - the same
                    // black alternate in two NHL 27 folders).  Nothing under ~22 means NHL 27 does not have this kit.
                    var near = ranked.FirstOrDefault(x => extras.Contains(x.Kit) && x.D <= best.D + 4);
                    string verdict = near.Kit is not null ? $"'{near.Kit}' in extraKits matches ({near.D:F0})"
                        : best.D > 22 ? "no NHL 27 kit is close: NHL 27 probably does not have this one"
                        : $"closest NHL 27 kit '{best.Kit}' ({best.D:F0}) is NOT in extraKits   <-- CHECK: add it?";
                    if (near.Kit is null && best.D <= 22) flagged++;
                    lines.Add($"  {label}: {verdict}");
                }
                lines.Add($"      closest: {top3}");
            }
            lines.Add("");
        }
        var report = Path.Combine(Dir(season), "report.txt");
        File.WriteAllLines(report, lines, new UTF8Encoding(false));
        var summary = $"{now.Count} teams on nhluniforms.com {season}: {changed} with a kit changed or added since {Previous(season)}, " +
                      $"{checkedTeams} checked against NHL 27, {flagged} kit pick(s) to check (CHECK in the report).";
        return (report, summary);
    }
}
