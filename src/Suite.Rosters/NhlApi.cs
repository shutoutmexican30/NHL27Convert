using System.Net.Http;
using System.Text.Json;
using Suite.Core;

namespace Suite.Rosters;

/// <summary>One player on a current NHL roster, from api-web.nhle.com.</summary>
public sealed record ApiPlayer(long Id, string First, string Last, int? Number, string Position, DateOnly? Birth, string? Headshot, string TeamAbbrev);

/// <summary>
/// Reads current NHL rosters from the public NHL API (no key). Raw JSON is cached per day in
/// %LOCALAPPDATA%\CenterIceModdingSuite\nhlapi\&lt;date&gt;\ so a sync can be reviewed and re-run offline.
/// </summary>
public sealed class NhlApi(HttpClient? http = null)
{
    readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    public const string Base = "https://api-web.nhle.com/v1/";

    public static string CacheDir(DateOnly day) => SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "nhlapi", day.ToString("yyyy-MM-dd")));

    async Task<JsonDocument> GetAsync(string path, DateOnly day, CancellationToken ct)
    {
        var file = Path.Combine(CacheDir(day), path.Replace('/', '_') + ".json");
        if (!File.Exists(file))
        {
            // Polite pacing; on 429 wait as long as the server asks (or back off 2, 4, 8... s) and try again.
            for (int attempt = 0; ; attempt++)
            {
                using var resp = await _http.GetAsync(Base + path, ct);
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
            await Task.Delay(1000, ct);
        }
        return JsonDocument.Parse(File.ReadAllText(file));
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
                    abbrev));
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
}
