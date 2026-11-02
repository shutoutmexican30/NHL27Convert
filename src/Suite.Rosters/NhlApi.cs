using System.Net.Http;
using System.Text.Json;
using Suite.Core;

namespace Suite.Rosters;

/// <summary>One player on a current NHL roster, from api-web.nhle.com.</summary>
public sealed record ApiPlayer(long Id, string First, string Last, int? Number, string Position, DateOnly? Birth, string? Headshot, string TeamAbbrev, string? Shoots = null, bool FromSearch = false);

/// <summary>
/// Reads current NHL rosters from the public NHL API (no key). Raw JSON is cached per day in
/// %LOCALAPPDATA%\CenterIceModdingSuite\nhlapi\&lt;date&gt;\ so a sync can be reviewed and re-run offline.
/// </summary>
public sealed class NhlApi(HttpClient? http = null)
{
    readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    public const string Base = "https://api-web.nhle.com/v1/";

    public static string CacheDir(DateOnly day) => SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "nhlapi", day.ToString("yyyy-MM-dd")));

    public const string SearchBase = "https://search.d3.nhle.com/api/v1/search/player";

    Task<JsonDocument> GetAsync(string path, DateOnly day, CancellationToken ct) =>
        GetUrlAsync(Base + path, path.Replace('/', '_'), day, 1000, ct);

    async Task<JsonDocument> GetUrlAsync(string url, string cacheName, DateOnly day, int pauseMs, CancellationToken ct)
    {
        var file = Path.Combine(CacheDir(day), cacheName + ".json");
        if (!File.Exists(file))
        {
            // Polite pacing; on 429 wait as long as the server asks (or back off 2, 4, 8... s) and try again.
            for (int attempt = 0; ; attempt++)
            {
                using var resp = await _http.GetAsync(url, ct);
                if ((int)resp.StatusCode == 429 && attempt < 6)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 << attempt);
                    await Task.Delay(wait, ct);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                File.WriteAllText(file, await resp.Content.ReadAsStringAsync(ct));
                break;
            }
            await Task.Delay(pauseMs, ct);
        }
        return JsonDocument.Parse(File.ReadAllText(file));
    }

    /// <summary>
    /// Active players whose name matches, from the NHL's player search. Unlike roster/&lt;team&gt;/current it still lists
    /// players on long-term injured reserve with their club. Birth date and handedness are not in the result.
    /// </summary>
    public async Task<List<ApiPlayer>> SearchAsync(string name, DateOnly day, CancellationToken ct = default)
    {
        var key = "search_" + new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        using var doc = await GetUrlAsync($"{SearchBase}?culture=en-us&limit=20&active=true&q={Uri.EscapeDataString(name)}", key, day, 400, ct);
        var list = new List<ApiPlayer>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            if (p.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.False) continue;
            var full = p.GetProperty("name").GetString() ?? "";
            int sp = full.IndexOf(' ');
            list.Add(new ApiPlayer(
                long.TryParse(p.GetProperty("playerId").GetString(), out var id) ? id : 0,
                sp < 0 ? "" : full[..sp], sp < 0 ? full : full[(sp + 1)..],
                p.TryGetProperty("sweaterNumber", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : null,
                p.TryGetProperty("positionCode", out var pc) ? pc.GetString() ?? "" : "",
                null, null,
                p.TryGetProperty("teamAbbrev", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "",
                FromSearch: true));
        }
        return list;
    }

    /// <summary>
    /// The league of a player's latest games (the last season-totals row of his latest season: "NHL", "AHL", "KHL"...),
    /// from player/&lt;id&gt;/landing. Null when he has no season rows.
    /// </summary>
    public async Task<string?> LatestLeagueAsync(long id, DateOnly day, CancellationToken ct = default)
    {
        using var doc = await GetUrlAsync($"{Base}player/{id}/landing", $"player_{id}_landing", day, 400, ct);
        if (!doc.RootElement.TryGetProperty("seasonTotals", out var totals) || totals.ValueKind != JsonValueKind.Array) return null;
        var rows = totals.EnumerateArray().ToList();
        if (rows.Count == 0) return null;
        int latest = rows.Max(r => r.GetProperty("season").GetInt32());
        var last = rows.Last(r => r.GetProperty("season").GetInt32() == latest);
        return last.TryGetProperty("leagueAbbrev", out var l) ? l.GetString() : null;
    }

    /// <summary>Team abbreviations in today's standings (all current NHL teams).</summary>
    public async Task<List<string>> TeamsAsync(DateOnly day, CancellationToken ct = default)
    {
        using var doc = await GetAsync("standings/now", day, ct);
        return doc.RootElement.GetProperty("standings").EnumerateArray()
            .Select(t => t.GetProperty("teamAbbrev").GetProperty("default").GetString()!).Distinct().Order().ToList();
    }

    public async Task<List<ApiPlayer>> RosterAsync(string abbrev, DateOnly day, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"roster/{abbrev}/current", day, ct);
        return Players(doc, abbrev);
    }

    /// <summary>A club's prospects (signed or drafted players not on its NHL roster), same shape as a roster.</summary>
    public async Task<List<ApiPlayer>> ProspectsAsync(string abbrev, DateOnly day, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"prospects/{abbrev}", day, ct);
        return Players(doc, abbrev);
    }

    static List<ApiPlayer> Players(JsonDocument doc, string abbrev)
    {
        var list = new List<ApiPlayer>();
        foreach (var group in new[] { "forwards", "defensemen", "goalies" })
        {
            if (!doc.RootElement.TryGetProperty(group, out var arr)) continue;
            foreach (var p in arr.EnumerateArray())
            {
                DateOnly? birth = p.TryGetProperty("birthDate", out var b) && DateOnly.TryParse(b.GetString(), out var d) ? d : null;
                list.Add(new ApiPlayer(
                    p.GetProperty("id").GetInt64(),
                    p.GetProperty("firstName").GetProperty("default").GetString() ?? "",
                    p.GetProperty("lastName").GetProperty("default").GetString() ?? "",
                    p.TryGetProperty("sweaterNumber", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : null,
                    p.TryGetProperty("positionCode", out var pc) ? pc.GetString() ?? "" : "",
                    birth,
                    p.TryGetProperty("headshot", out var h) ? h.GetString() : null,
                    abbrev,
                    p.TryGetProperty("shootsCatches", out var sc) ? sc.GetString() : null));
            }
        }
        return list;
    }

    /// <summary>Every current NHL roster, keyed by team abbreviation.</summary>
    public async Task<Dictionary<string, List<ApiPlayer>>> AllRostersAsync(DateOnly day, Action<string>? progress = null, CancellationToken ct = default)
    {
        var result = new Dictionary<string, List<ApiPlayer>>();
        foreach (var team in await TeamsAsync(day, ct))
        {
            progress?.Invoke($"NHL API: {team}");
            result[team] = await RosterAsync(team, day, ct);
        }
        return result;
    }

    /// <summary>Every club's prospects, keyed by team abbreviation. A club the API has no list for is skipped.</summary>
    public async Task<Dictionary<string, List<ApiPlayer>>> AllProspectsAsync(IEnumerable<string> teams, DateOnly day, Action<string>? progress = null, CancellationToken ct = default)
    {
        var result = new Dictionary<string, List<ApiPlayer>>();
        foreach (var team in teams)
        {
            progress?.Invoke($"NHL API prospects: {team}");
            try { result[team] = await ProspectsAsync(team, day, ct); }
            catch (HttpRequestException ex) { SuiteLog.Write($"NHL API prospects {team}: {ex.Message}"); }
        }
        return result;
    }
}
