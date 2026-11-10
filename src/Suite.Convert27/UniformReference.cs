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
        // And the body colour must agree: Minnesota's green alternate is not NHL 27's white 'twentytwo', however close
        // its green trim is.  Full lightness weight here, so white against green counts in full.
        static double Body(long x, long y) { var (l1, a1, b1) = Lab(x); var (l2, a2, b2) = Lab(y); return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2)); }
        // Only as far as a has a clear body colour: Dallas's alternate is black 45% / green 35%, no body colour at all.
        double clear = Math.Clamp((a[0].Share - (a.Count > 1 ? a[1].Share : 0)) / a.Sum(x => x.Share) / 0.25, 0, 1);
        double body = Body(a[0].Rgb, b[0].Rgb);
        return (Side(a, b) + Side(b, a)) / 2 + 30 * off + 0.25 * clear * body;
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

    /// <summary>One kit band of a drawing at half size, then each NHL 27 colour sheet as a 225-pixel square.</summary>
    public static void SideBySide(string path, Rgba drawing, int kit, IReadOnlyList<Rgba> sheets)
    {
        const int H = 225, DrawW = 480;
        int width = DrawW + H * sheets.Count;
        var px = new byte[width * H * 4];
        void Put(int x, int y, Rgba src, int sx, int sy)
        {
            int s = (sy * src.Width + sx) * 4, t = (y * width + x) * 4;
            px[t] = src.Pixels[s]; px[t + 1] = src.Pixels[s + 1]; px[t + 2] = src.Pixels[s + 2]; px[t + 3] = 255;
        }
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < DrawW; x++)
                if (kit * Band + y * 2 < drawing.Height && x * 2 < drawing.Width) Put(x, y, drawing, x * 2, kit * Band + y * 2);
            for (int i = 0; i < sheets.Count; i++)
                for (int x = 0; x < H; x++) Put(DrawW + i * H + x, y, sheets[i], x * sheets[i].Width / H, y * sheets[i].Height / H);
        }
        ImageIO.SavePng(new Rgba(width, H, px), path);
    }

    /// <summary>
    /// A browsable season contact sheet. Full team drawings stay visible at a useful size, and any matcher comparison
    /// images created by <see cref="CheckAsync"/> appear beneath their team. All paths are local relative filenames.
    /// </summary>
    public static string ContactSheetHtml(string season, IReadOnlyList<Drawing> drawings, Func<Drawing, IEnumerable<string>> comparisons)
    {
        static string H(string value) => System.Net.WebUtility.HtmlEncode(value);
        var html = new StringBuilder();
        html.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .AppendLine($"<title>NHL uniforms {H(season)} — visual review</title><style>")
            .AppendLine(":root{color-scheme:dark;font:15px/1.45 system-ui,sans-serif}body{margin:0;background:#101820;color:#edf3f7}header{position:sticky;top:0;z-index:2;padding:18px 24px;background:#101820f2;border-bottom:1px solid #344452}h1{margin:0 0 6px;font-size:25px}p{margin:5px 0;color:#aebdca}a{color:#7fc8ff}#filter{box-sizing:border-box;width:min(440px,100%);padding:10px 12px;margin-top:10px;border:1px solid #506474;border-radius:7px;background:#192630;color:white;font:inherit}main{display:grid;grid-template-columns:repeat(auto-fill,minmax(310px,1fr));gap:18px;padding:22px}.card{min-width:0;padding:14px;background:#1a2731;border:1px solid #354955;border-radius:10px}.card h2{margin:0 0 8px;font-size:19px}.card>img{display:block;width:100%;height:auto;background:#aab0b4;border-radius:5px}.comparisons{margin-top:12px}.comparisons summary{cursor:pointer;color:#b9d9eb}.comparisons img{width:100%;height:auto;margin-top:9px;border-radius:5px}.source{font-size:13px}footer{padding:0 24px 25px;color:#aebdca}</style></head><body>")
            .AppendLine($"<header><h1>NHL uniforms {H(season)} — visual review</h1><p>Reference drawings: <a href=\"{Site}/{H(season)}/{H(season)}.html\">nhluniforms.com</a>. Personal reference only; never used as game textures.</p><p><a href=\"report.txt\">Open the detailed palette and mapping report</a></p><input id=\"filter\" type=\"search\" placeholder=\"Filter teams…\" aria-label=\"Filter teams\"></header><main>");
        foreach (var drawing in drawings.OrderBy(d => d.Team))
        {
            string team = drawing.Team switch
            {
                "Ducks" => "Anaheim Ducks", "Bruins" => "Boston Bruins", "Sabres" => "Buffalo Sabres",
                "Flames" => "Calgary Flames", "Hurricanes" => "Carolina Hurricanes", "Blackhawks" => "Chicago Blackhawks",
                "Avalanche" => "Colorado Avalanche", "BlueJackets" => "Columbus Blue Jackets", "Stars" => "Dallas Stars",
                "RedWings" => "Detroit Red Wings", "Oilers" => "Edmonton Oilers", "Panthers" => "Florida Panthers",
                "Kings" => "Los Angeles Kings", "Wild" => "Minnesota Wild", "Canadiens" => "Montreal Canadiens",
                "Predators" => "Nashville Predators", "Devils" => "New Jersey Devils", "Islanders" => "New York Islanders",
                "Rangers" => "New York Rangers", "Senators" => "Ottawa Senators", "Flyers" => "Philadelphia Flyers",
                "Penguins" => "Pittsburgh Penguins", "Blues" => "St. Louis Blues", "Sharks" => "San Jose Sharks",
                "Kraken" => "Seattle Kraken", "Lightning" => "Tampa Bay Lightning", "MapleLeafs" => "Toronto Maple Leafs",
                "Mammoth" => "Utah Mammoth", "Canucks" => "Vancouver Canucks", "GoldenKnights" => "Vegas Golden Knights",
                "Capitals" => "Washington Capitals", "Jets" => "Winnipeg Jets", _ => Nickname(drawing.Team),
            };
            html.AppendLine($"<section class=\"card\" data-team=\"{H(team.ToLowerInvariant())}\"><h2>{H(team)}</h2>")
                .AppendLine($"<a href=\"{H(drawing.Url)}\"><img loading=\"lazy\" src=\"{H(Path.GetFileName(drawing.Png))}\" alt=\"{H(team)} reference uniforms\"></a>")
                .AppendLine($"<div class=\"source\"><a href=\"{H(drawing.Url)}\">Source drawing</a></div>");
            var looks = comparisons(drawing).ToList();
            if (looks.Count > 0)
            {
                html.AppendLine($"<details class=\"comparisons\"><summary>Matcher comparisons ({looks.Count})</summary>");
                foreach (var look in looks)
                    html.AppendLine($"<a href=\"{H(Path.GetFileName(look))}\"><img loading=\"lazy\" src=\"{H(Path.GetFileName(look))}\" alt=\"{H(team)} matcher comparison\"></a>");
                html.AppendLine("</details>");
            }
            html.AppendLine("</section>");
        }
        html.AppendLine("</main><footer>Compare crest, shoulder marks, sponsor placement, colours and striping. Palette scores are hints; visual review decides. In-game appearance still requires a game check.</footer>")
            .AppendLine("<script>document.getElementById('filter').addEventListener('input',e=>{const q=e.target.value.trim().toLowerCase();document.querySelectorAll('.card').forEach(c=>c.hidden=!c.dataset.team.includes(q));});</script></body></html>");
        return html.ToString();
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
                // Kits are named as variant_map.json names them: the adidas folder alone ("ten"), any other maker's with
                // the maker ("rbk/home"), so Dallas's old Reebok 'home' is never taken for its adidas 'home'.
                var rel = folder[prefix.Length..];
                sheets[rel.StartsWith("adidas/") ? rel["adidas/".Length..] : rel] = (Sheet("jersey"), Sheet("sock"));
            }
            progress?.Invoke($"{nick}: decoding {sheets.Count} NHL 27 kit(s)");
            var pngs = await images.Nhl27Async(sheets.Values.SelectMany(s => new[] { s.Jersey, s.Sock }).OfType<Nhl27Texture>(), progress, ct);
            // Only the current maker's kits can be this season's (rbk/ and nineties/ are history).
            var kitPal = sheets.Where(s => !s.Key.Contains('/') && s.Value.Jersey is not null && pngs.ContainsKey(s.Value.Jersey.Name))
                .ToDictionary(s => s.Key, s => Palette(ImageIO.Load(pngs[s.Value.Jersey!.Name])));
            if (kitPal.Count == 0) { lines.Add("  (no decodable NHL 27 jersey sheets)"); lines.Add(""); continue; }
            // Colours narrow it down; the eye decides (Dallas's big-star alternate scores closer to the black 'twentythree'
            // than to 'thirteen', which is that very jersey).  So every flag gets the drawing beside the candidates.
            string Look(string teamName, string label, Rgba drawing, int kit, IEnumerable<string> shownKits)
            {
                var shown = shownKits.Distinct().Where(kitPal.ContainsKey).Take(5).ToList();
                var path = Path.Combine(Dir(season), $"look_{teamName}_{label.Replace(' ', '_')}.png");
                SideBySide(path, drawing, kit, shown.Select(s => ImageIO.Load(pngs[sheets[s].Jersey!.Name])).ToList());
                return $"look: {path} (the drawing, then NHL 27 {string.Join(", ", shown.Select(s => $"'{s}'"))})";
            }
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
                    lines.Add($"  {label}: matcher uses NHL 27 '{chosen}' ({(mine.Kit is null ? "not found" : $"distance {mine.D:F0} [{Hex(kitPal[chosen!].Take(3))}]")}){(off ? "   <-- LOOK: another kit's colours are closer" : "")}");
                    if (off) lines.Add("      " + Look(d.Team, label, img, k, new[] { chosen! }.Concat(ranked.Take(3).Select(x => x.Kit))));
                }
                else
                {
                    // An alternate the drawing shows: is its closest NHL 27 kit one the matcher brings over (extraKits)?
                    // Covered when an extraKit is about as close as the best (Buffalo: 'seven' 14, 'twelve' 15 - the same
                    // black alternate in two NHL 27 folders).  Nothing under ~22 means NHL 27 does not have this kit.
                    var near = ranked.FirstOrDefault(x => extras.Contains(x.Kit) && x.D <= best.D + 4);
                    string verdict = near.Kit is not null ? $"'{near.Kit}' in extraKits matches ({near.D:F0})"
                        : best.D > 22 ? "no NHL 27 kit is close: NHL 27 probably does not have this one"
                        : $"closest NHL 27 kit '{best.Kit}' ({best.D:F0}) is NOT in extraKits   <-- LOOK before adding it";
                    lines.Add($"  {label}: {verdict}");
                    if (near.Kit is null && best.D <= 22)
                    {
                        flagged++;
                        lines.Add("      " + Look(d.Team, label, img, k, ranked.Take(3).Select(x => x.Kit).Concat(extras.Where(kitPal.ContainsKey))));
                    }
                }
                lines.Add($"      closest: {top3}");
            }
            lines.Add("");
        }
        var report = Path.Combine(Dir(season), "report.txt");
        File.WriteAllLines(report, lines, new UTF8Encoding(false));
        var contact = ContactSheetHtml(season, now, d => Directory.EnumerateFiles(Dir(season), $"look_{d.Team}_*.png").Order());
        File.WriteAllText(Path.Combine(Dir(season), "contact-sheet.html"), contact, new UTF8Encoding(false));
        var summary = $"{now.Count} teams on nhluniforms.com {season}: {changed} with a kit changed or added since {Previous(season)}, " +
                      $"{checkedTeams} checked against NHL 27, {flagged} to look at (LOOK in the report, each with a side-by-side picture).";
        return (report, summary);
    }
}
