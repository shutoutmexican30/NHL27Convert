// LocDump27 <nhl27 game root> <nhl24 game root> <plan.tsv>
// Read-only. For every planned alternate: NHL 27's jersey name (VINTAGE_2_<teamartid>_<variant>) from NHL 27's English
// table, and whether NHL 24's English table already has that text. Nothing is written.
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

var settings = SuiteSettings.Load();
string root27 = args[0], root24 = args[1], planPath = args[2];
string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
byte[] eng27;
try { eng27 = File.Exists(root27) ? File.ReadAllBytes(root27) : new LegacyFiles(root27, settings).Read("loc/eng_us.db"); }
catch (Exception ex)
{
    Console.WriteLine("NHL 27 language table could not be read: " + ex.GetType().Name + ": " + ex.Message);
    foreach (string known in new[] { "gamemodes/database/t3db/nhl_game.db", "gamemodes/roster/initial_experience.db", "gamemodes/database/t3db/nhl_game-meta.xml" })
    {
        try
        {
            var names = new LegacyFiles(root27, settings).Names(known);
            Console.WriteLine($"index listing {known}: {names.Count} files; language-like:");
            foreach (var n in names.Where(n => n.Contains("loc", StringComparison.OrdinalIgnoreCase) || n.Contains("eng", StringComparison.OrdinalIgnoreCase) || n.Contains("lang", StringComparison.OrdinalIgnoreCase)).Take(40))
                Console.WriteLine("  " + n);
        }
        catch (Exception e2) { Console.WriteLine($"  {known}: {e2.Message}"); }
    }
    return 2;
}
var s27 = LanguageStrings.Load(eng27);
var s24 = LanguageStrings.Load(new LegacyFiles(root24, settings).Read("loc/eng_us.db"));
Console.WriteLine($"NHL 27 eng_us: {eng27.Length} bytes");
foreach (string k in new[] { "ALTERNATEJERSEY", "OLD_HOME", "TXT_CHL_HOME" })
    Console.WriteLine($"  {k}: 27='{s27.Find(k)}' 24='{s24.Find(k)}'");

var csv = File.ReadAllLines(Path.Combine(repo, "reference", "nhl27-full-csv", "stockteamjerseys.csv"));
var head = csv[0].Split(',').Select(h => h.Trim('"')).ToList();
var rows = csv.Skip(1).Select(l => l.Split(',').Select(v => v.Trim('"')).ToArray()).Where(v => v.Length == head.Count).ToList();
string Col(string[] r, string n) => r[head.IndexOf(n)];
var vm = VariantMap.Load();
foreach (var line in File.ReadAllLines(planPath).Skip(1))
{
    var p = line.Split('\t');
    string team = p[0]; int v27 = int.Parse(p[2]);
    var t = vm.Teams.First(x => x.Nhl24.Folder == team && x.Index is >= 0 and <= 31);
    var r = rows.FirstOrDefault(x => Col(x, "teamid") == t.Index.ToString() && Col(x, "style") == "2" && Col(x, "variant") == v27.ToString());
    if (r is null) { Console.WriteLine($"{team}\t{v27}\t(no NHL 27 row)"); continue; }
    string key = $"VINTAGE_2_{Col(r, "teamartid")}_{v27}";
    string? name27 = s27.Find(key);
    Console.WriteLine($"{team}\t{p[3]}\tloc27={Col(r, "locstring")}\t{key}\tNHL27='{name27}'\tNHL24 same key='{s24.Find(key)}'");
}
return 0;
