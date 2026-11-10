// GoaliePalette [output.tsv] - reproducibly derive the general viewer palette from NHL 24 stock goalie-pad zones.
// Reads the reference NHL 24 DB only. It never reads or writes an installed game or any goalie-equipment row.
using Suite.Core;
using Suite.Rosters;

string workspace = Directory.GetCurrentDirectory();
string reference = Path.Combine(workspace, "reference", "nhl24-full-csv", "gamemodes", "database", "t3db");
string output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(workspace, "data", "team_goalie_colors_24.tsv"));
if (!output.StartsWith(workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("The generated palette must stay inside the workspace.");
var db = T3Db.Load(File.ReadAllBytes(Path.Combine(reference, "nhl_game.db")), Path.Combine(reference, "nhl_game-meta.xml"));
var goalie = db.Tables["stockgoalieequipment"];
var teams = db.Tables["exhibitionteams"];
var lines = new List<string>
{
    "# General viewer palette derived only from NHL 24 stockgoalieequipment, league 0, all padszone1..15 colors.",
    "# Primary: most frequent chromatic color; ties use the numeric color. Without one, the most frequent dark neutral wins.",
    "# Secondary: most frequent different-hue chromatic color, then dark neutral, then any remaining color; same tie-break.",
    "# These are UI colors, not a goalie-equipment edit. Team 22 receives the existing Utah override in TeamColors.",
    "# index\tname\tc0\tc1"
};

static bool Accent(int color)
{
    int r = color >> 16, g = color >> 8 & 255, b = color & 255;
    return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) >= 24;
}
static bool Dark(int color) => (color >> 16) < 80 && (color >> 8 & 255) < 80 && (color & 255) < 80;
static double Hue(int color)
{
    double r = (color >> 16) / 255.0, g = (color >> 8 & 255) / 255.0, b = (color & 255) / 255.0;
    double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
    if (d == 0) return 0;
    double h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
    return h < 0 ? h + 360 : h;
}
static bool DifferentHue(int a, int b)
{
    double d = Math.Abs(Hue(a) - Hue(b));
    return Math.Min(d, 360 - d) >= 15;
}
static int Pick(IEnumerable<int> colors, Dictionary<int, int> counts) => colors.Distinct()
    .OrderByDescending(c => counts[c]).ThenBy(c => c).First();

for (int team = 0; team < 32; team++)
{
    var counts = new Dictionary<int, int>();
    for (int row = 0; row < goalie.InUse; row++)
    {
        if (db.GetInt(goalie, row, "team") != team || db.GetInt(goalie, row, "league") != 0) continue;
        for (int zone = 1; zone <= 15; zone++)
        {
            int color = checked((int)db.GetInt(goalie, row, $"padszone{zone}color"));
            counts[color] = counts.GetValueOrDefault(color) + 1;
        }
    }
    if (counts.Count == 0) throw new InvalidDataException($"NHL 24 team {team} has no stock NHL goalie-pad colors.");
    var accents = counts.Keys.Where(Accent).ToArray();
    int primary = accents.Length > 0 ? Pick(accents, counts)
        : counts.Keys.Any(Dark) ? Pick(counts.Keys.Where(Dark), counts) : Pick(counts.Keys, counts);
    var remaining = counts.Keys.Where(c => c != primary).ToArray();
    if (remaining.Length == 0) throw new InvalidDataException($"NHL 24 team {team} has only one goalie-pad color.");
    var secondAccent = remaining.Where(c => Accent(c) && (!Accent(primary) || DifferentHue(c, primary))).ToArray();
    int secondary = secondAccent.Length > 0 ? Pick(secondAccent, counts)
        : remaining.Any(Dark) ? Pick(remaining.Where(Dark), counts) : Pick(remaining, counts);
    int teamRow = Enumerable.Range(0, teams.InUse).Single(r => db.GetInt(teams, r, "index") == team);
    string name = $"{db.GetString(teams, teamRow, "cityname")} {db.GetString(teams, teamRow, "teamname")}".Trim();
    lines.Add($"{team}\t{name}\t{primary:X6}\t{secondary:X6}");
    Console.WriteLine($"{team,2} {name,-28} #{primary:X6} #{secondary:X6}");
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllLines(output, lines);
Console.WriteLine("Generated NHL 24 viewer palette: " + output);
