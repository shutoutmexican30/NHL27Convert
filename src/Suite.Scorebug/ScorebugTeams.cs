using System.Text.Json;
using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Scorebug;

/// <summary>An NHL club as the scorebug sees it: team index, abbreviation, name, row colours.</summary>
public sealed record ScorebugTeam(int Id, string Abbr, string Name, string Primary, string Secondary)
{
    /// <summary>The team's art id (its t&lt;id&gt; logo): the team index, except Vegas (30) and Seattle (31), whose art is 32 and 33
    /// (30 and 31 are the All-Star logos, as with the center ice slots).</summary>
    public int LogoId => Id >= 30 ? Id + 2 : Id;

    public override string ToString() => $"{Abbr}  {Name}";
}

/// <summary>
/// The 32 clubs for the scorebug preview, from the suite's team data: index and abbreviation from data\nhlapi_teams.tsv,
/// names and colours (0 = primary, 1 = secondary) from data\team_colors*.tsv (<see cref="TeamColors"/>), and the hand
/// overrides in data\teams_overrides.json last (team 22 is Utah Mammoth in the CIMS copy, not Arizona).
/// Logos are the game's own ui/textures/common/teamlogos/medium/t&lt;id&gt;, decoded once into PNGs.
/// </summary>
public static class ScorebugTeams
{
    static List<ScorebugTeam>? _all;

    public static IReadOnlyList<ScorebugTeam> All => _all ??= Load();

    public static ScorebugTeam? ByAbbr(string? abbr) => All.FirstOrDefault(t => t.Abbr == abbr);

    static List<ScorebugTeam> Load()
    {
        var dir = TeamColors.Dir;
        // NHL 24's table first (the game being modded); NHL 27's only where NHL 24 has no row or a black one.
        // (TeamColors itself prefers NHL 27, whose first colours are uniform colours: Detroit white, Anaheim black.)
        var names = new Dictionary<int, string>();
        var table = new Dictionary<int, string[]>();
        foreach (var file in new[] { TeamColors.ShippedPath24, TeamColors.ShippedPath27 })
            if (File.Exists(file))
                foreach (var line in File.ReadLines(file).Where(l => l.Length > 0 && l[0] != '#'))
                    if (line.Split('\t') is [var i, var n, .. var rest] && int.TryParse(i, out var id))
                    {
                        names.TryAdd(id, n);
                        var colours = rest.Where(c => c.Length == 6).ToArray();
                        if (colours.Length > 0 && colours[0] != "000000") table.TryAdd(id, colours);
                    }

        var overrides = new Dictionary<int, (string? Name, string? Pri, string? Sec)>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "teams_overrides.json")));
            foreach (var t in doc.RootElement.GetProperty("teams").EnumerateArray())
            {
                string? S(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var colours = t.TryGetProperty("colors", out var c) ? c : default;
                overrides[t.GetProperty("index").GetInt32()] = (S(t, "teamname"),
                    colours.ValueKind == JsonValueKind.Object ? S(colours, "primary") : null, colours.ValueKind == JsonValueKind.Object ? S(colours, "secondary") : null);
            }
        }
        catch (Exception ex) { SuiteLog.Write("scorebug teams: teams_overrides.json not read: " + ex.Message); }

        var list = new List<ScorebugTeam>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "nhlapi_teams.tsv")).Skip(1))
        {
            if (line.Split('\t') is not [var abbr, var index, ..] || !int.TryParse(index, out var id)) continue;
            // Anaheim has a row in neither table: its own orange and gold.
            var colours = table.GetValueOrDefault(id) ?? (id == 0 ? ["F47A38", "B9975B"] : []);
            string pri = colours.ElementAtOrDefault(0) ?? "333333", sec = colours.ElementAtOrDefault(1) ?? "111111";
            var name = names.GetValueOrDefault(id, abbr);
            if (overrides.TryGetValue(id, out var o))
            {
                name = o.Name ?? name;
                pri = o.Pri?.TrimStart('#') ?? pri;
                sec = o.Sec?.TrimStart('#') ?? sec;
            }
            list.Add(new ScorebugTeam(id, abbr, name, pri.ToUpperInvariant(), sec.ToUpperInvariant()));
        }
        return list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string LogoName(int id) => $"ui/textures/common/teamlogos/medium/t{id}";

    /// <summary>
    /// Writes t&lt;id&gt;.png into <paramref name="dir"/> for every club whose PNG is not there yet, decoded from the game's
    /// medium team logos. Returns how many were written; logos the game lacks are logged and skipped.
    /// </summary>
    public static int ExportLogos(string game, string dir, Action<string>? progress = null)
    {
        Directory.CreateDirectory(dir);
        var missing = All.Where(t => !File.Exists(Path.Combine(dir, $"t{t.LogoId}.png"))).ToList();
        if (missing.Count == 0) return 0;
        progress?.Invoke("Reading the game's team logos...");
        var index = BundleIndex.Shared(game);
        int done = 0;
        foreach (var t in missing)
        {
            try
            {
                var slot = TextureSlots.Read(game, index, LogoName(t.LogoId));
                var image = Ps4Codec.DecodeSlices(slot.Res, slot.Stock)[0];
                var tmp = Path.Combine(dir, $"t{t.LogoId}.png.tmp");
                ImageIO.SavePng(image, tmp);
                File.Move(tmp, Path.Combine(dir, $"t{t.LogoId}.png"), true);
                done++;
            }
            catch (Exception ex) { SuiteLog.Write($"scorebug logo t{t.LogoId} ({t.Abbr}): {ex.Message}"); }
        }
        return done;
    }
}
