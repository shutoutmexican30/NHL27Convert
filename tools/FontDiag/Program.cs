// FontDiag <nhl24 game (read only)> <team/word27> ... : why NHL 27 numbers do not fit NHL 24's font layout.
// Per band: glyph counts of NHL 27's sheet vs the team's NHL 24 home sheet, and Verify problems after mapping.
using Suite.Convert27;
using Suite.Core;
using Suite.Core.Frostbite;

var settings = SuiteSettings.Load();
Oodle.Ensure(settings);
var index = BundleIndex.Shared(args[0]);
if (args.Length > 3 && args[1] == "--compare-kits")
{
    // --compare-kits <prefixA> <prefixB>: resource leaf names (kit id stripped) with size and slices on both sides.
    string Leaf(string n, string pre) { string id = pre.TrimEnd('/').Split('/')[^3] + "_" + pre.TrimEnd('/').Split('/')[^1]; return n[(pre.TrimEnd('/').Length + 1)..].Replace(id, "<kit>"); }
    Dictionary<string, string> Side(string pre) => index.ResByName.Keys.Where(n => n.StartsWith(pre.TrimEnd('/') + "/", StringComparison.Ordinal))
        .ToDictionary(n => Leaf(n, pre), n => { try { var s = TextureSlots.Read(args[0], index, n); var d = Ps4Codec.DecodeSlices(s.Res, s.Stock); return $"{d[0].Width}x{d[0].Height} x{d.Count}"; } catch { return "?"; } });
    var a = Side(args[2]); var b = Side(args[3]);
    foreach (var k in a.Keys.Union(b.Keys).Order())
        Console.WriteLine($"{k,-60} {(a.TryGetValue(k, out var x) ? x : "-"),-14} {(b.TryGetValue(k, out var y) ? y : "-"),-14} {(x == y ? "" : "DIFF")}");
    var ebxA = index.EbxByName.Keys.Count(n => n.StartsWith(args[2].TrimEnd('/') + "/")); var ebxB = index.EbxByName.Keys.Count(n => n.StartsWith(args[3].TrimEnd('/') + "/"));
    Console.WriteLine($"EBX: {ebxA} vs {ebxB}");
    return;
}
if (args.Length > 2 && args[1] == "--teams")
{
    // --teams <regex>: exhibitionteams rows whose names match, with their art id and jersey rows (read only).
    var lf = new LegacyFiles(args[0], settings);
    string meta = Path.Combine(Path.GetTempPath(), "fontdiag-meta.xml");
    File.WriteAllBytes(meta, lf.Read("gamemodes/database/t3db/nhl_game-meta.xml"));
    var db = Suite.Rosters.T3Db.Load(lf.Read("gamemodes/database/t3db/nhl_game.db"), meta);
    var teams = db.Tables["exhibitionteams"]; var jerseys = db.Tables["stockteamjerseys"];
    var rx = new System.Text.RegularExpressions.Regex(args[2], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    for (int r = 0; r < teams.InUse; r++)
    {
        string S(string f) { try { return db.GetString(teams, r, f); } catch { return ""; } }
        long I(string f) { try { return db.GetInt(teams, r, f); } catch { return -1; } }
        string all = $"{S("cityname")} {S("teamname")} {S("nickname")} {S("abbrname")} {S("artabbr")}";
        if (!rx.IsMatch(all)) continue;
        long id = I("teamid"), art = I("artid");
        var rows = Enumerable.Range(0, jerseys.InUse).Where(j => db.GetInt(jerseys, j, "teamid") == id)
            .Select(j => $"s{db.GetInt(jerseys, j, "style")}v{db.GetInt(jerseys, j, "variant")}{(db.GetInt(jerseys, j, "active") == 1 ? "" : "(off)")}").ToList();
        Console.WriteLine($"team {id} '{all.Trim()}' artid {art}: {rows.Count} jersey rows {string.Join(" ", rows)}");
    }
    var byArt = Enumerable.Range(0, jerseys.InUse).Where(j => db.GetInt(jerseys, j, "teamartid") == 22 && db.GetInt(jerseys, j, "teamid") != 22)
        .Select(j => $"team {db.GetInt(jerseys, j, "teamid")} s{db.GetInt(jerseys, j, "style")}v{db.GetInt(jerseys, j, "variant")}").ToList();
    Console.WriteLine($"jersey rows of other teams using art id 22 (Arizona/Utah art): {byArt.Count} {string.Join(", ", byArt.Take(30))}");
    return;
}
if (args.Length > 1 && args[1] == "--loc")
{
    var strings = Suite.Rosters.LanguageStrings.Load(new LegacyFiles(args[0], settings).Read("loc/eng_us.db"));
    foreach (string key in args.Skip(2).Where(k => !k.StartsWith("text=")))
        Console.WriteLine($"{key} = {strings.Find(key) ?? "<missing>"}");
    if (args.Skip(2).FirstOrDefault(k => k.StartsWith("text=")) is { } want)
        for (int style = 0; style < 4; style++)
        for (int art = 0; art < 1200; art++)
        for (int v = 0; v < 40; v++)
            if (strings.Find($"VINTAGE_{style}_{art}_{v}") is { } t && t.Contains(want[5..], StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"VINTAGE_{style}_{art}_{v} = {t}");
    return;
}
if (args.Length > 1 && args[1] == "--dump")
{
    // --dump <out dir> <resource name>...: decode installed textures (first slice) to PNGs, read only.
    foreach (string name in args.Skip(3))
    {
        try
        {
            var slot = TextureSlots.Read(args[0], index, name);
            var slices = Ps4Codec.DecodeSlices(slot.Res, slot.Stock);
            for (int i = 0; i < slices.Count; i++) ImageIO.SavePng(slices[i], Path.Combine(args[2], name[(name.LastIndexOf('/') + 1)..] + $"_{i}.png"));
            Console.WriteLine($"{name}: {slices.Count} slice(s)");
        }
        catch (Exception e) { Console.WriteLine($"{name}: {e.Message}"); }
    }
    return;
}
foreach (string kit in args.Skip(1))
{
    var p = kit.Split('/');
    string png27 = TextureImages.Nhl27Png($"content/character/skater/league/nhl/{p[0]}/adidas/{p[1]}/font_adidas_{p[0]}_{p[1]}_color");
    string own = $"content/character/skater/league/nhl/{p[0]}/adidas/home/font_adidas_{p[0]}_home_color";
    var s = TextureSlots.Read(args[0], index, own);
    var f24 = Ps4Codec.DecodeSlices(s.Res, s.Stock)[0];
    var f27 = ImageIO.Load(png27);
    Console.WriteLine($"== {kit}: 27 {f27.Width}x{f27.Height}, 24 {f24.Width}x{f24.Height}");
    for (int b = 0; b < FontRemap.BandCount; b++)
        Console.WriteLine($"  band {b}: glyphs 27={FontRemap.Glyphs(f27, b).Count} 24={FontRemap.Glyphs(f24, b).Count}");
    var pairs = FontRemap.Map(f27, f24);
    if (pairs is null) { Console.WriteLine("  Map: null (glyph counts differ)"); continue; }
    var remapped = FontRemap.Apply(f27, f24, pairs, f24);
    if (Environment.GetEnvironmentVariable("FONTDIAG_OUT") is { Length: > 0 } outDir)
    {
        ImageIO.SavePng(f24, Path.Combine(outDir, $"{p[0]}_{p[1]}_24.png"));
        ImageIO.SavePng(remapped, Path.Combine(outDir, $"{p[0]}_{p[1]}_remapped.png"));
    }
    var problems = FontRemap.Verify(remapped, f24);
    Console.WriteLine($"  Map: {pairs.Count} pairs; Verify: {(problems.Count == 0 ? "OK" : string.Join("; ", problems.Take(6)))}");
}
