using System.Net.Http;
using System.Text.Json;
using Suite.Core;

namespace Suite.Rosters;

/// <summary>One player's portrait: the NHL 24 slot it goes into and the image it gets.</summary>
public sealed record PortraitAssignment(string GameId, string Name, string Team, long ApiId, int ArtId, bool NewSlot, string Headshot, string? Png);

/// <summary>
/// Portraits for every player on a current NHL roster, from the NHL API headshots (NHL 27 ships no portraits; NHL 24's
/// own show the team jersey, so traded players would show their old club). Personal use only: these are NHL.com photos,
/// never part of a shared mod pack.
/// NHL 24: a player's bio artid names the texture ui/textures/common/playerportraits/p&lt;artid&gt; (BC3, 512x340) and
/// hasportrait = 1 shows it. A player who already owns a slot (no other player in any database uses that artid) keeps
/// it and only the image changes; anyone else gets a slot no database references, and artid/hasportrait are set in
/// every database that has him. Framing: see <see cref="ToGame"/>.
/// </summary>
public static class Portraits
{
    public const string SlotFolder = "ui/textures/common/playerportraits/";
    public static string Dir => SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "portraits"));

    /// <summary>
    /// Plans slots. <paramref name="dumps"/>: every database's dump after the sync, main database first.
    /// <paramref name="slots"/>: the portrait texture names that exist in NHL 24 ("p1005", ...).
    /// </summary>
    public static List<PortraitAssignment> Plan(IReadOnlyList<string> dumps, Dictionary<string, List<ApiPlayer>> api,
        Dictionary<string, int> teamMap, IReadOnlySet<string> slots, List<string> warnings)
    {
        // Who uses which artid, in every database.
        var users = new Dictionary<int, HashSet<string>>();
        foreach (var dir in dumps)
        {
            var bio = T3Csv.Load(dir, "exhibitionplayerbiotable");
            foreach (var r in bio.Rows.Take(bio.ValidCount))
            {
                int art = bio.Int(r, "artid");
                if (!users.TryGetValue(art, out var set)) users[art] = set = [];
                set.Add(bio.Get(r, "game_id"));
            }
        }
        var main = T3Csv.Load(dumps[0], "exhibitionplayerbiotable");
        var mainRows = main.Rows.Take(main.ValidCount).ToList();
        var players = mainRows.Select((r, i) => new DbPlayer(i, main.Get(r, "game_id"), main.Get(r, "firstname"), main.Get(r, "lastname"),
            RosterSync.BioDate(main.Int(r, "year"), main.Int(r, "month"), main.Int(r, "day")), main.Int(r, "team"), main.Int(r, "jersey"),
            main.Int(r, "position"), main.Int(r, "hasportrait") == 1, main.Int(r, "artid"))).ToList();
        var byLast = players.ToLookup(x => RosterSync.Norm(x.Last));
        var byFull = players.ToLookup(x => RosterSync.Norm(x.First + x.Last));

        var free = new Queue<int>(slots.Where(s => s.StartsWith('p') && int.TryParse(s[1..], out _)).Select(s => int.Parse(s[1..]))
            .Where(id => !users.ContainsKey(id)).Order());
        var list = new List<PortraitAssignment>();
        var done = new HashSet<string>();
        foreach (var (abbrev, roster) in api.OrderBy(kv => kv.Key))
        {
            if (!teamMap.ContainsKey(abbrev)) continue;
            foreach (var p in roster)
            {
                var m = RosterSync.Match(p, byLast, byFull);
                if (m is null || !done.Add(m.GameId)) continue;
                int art = m.HeadId; // DbPlayer.HeadId carries artid here
                bool owned = m.HasPortrait && slots.Contains("p" + art) && users.TryGetValue(art, out var u) && u.Count == 1;
                bool fresh = false;
                if (!owned)
                {
                    if (free.Count == 0) { warnings.Add($"No free portrait slot left for {p.First} {p.Last}"); continue; }
                    art = free.Dequeue();
                    users[art] = [m.GameId];
                    fresh = true;
                }
                var url = p.Headshot ?? $"https://assets.nhle.com/mugs/nhl/{Season()}/{abbrev}/{p.Id}.png";
                list.Add(new PortraitAssignment(m.GameId, $"{p.First} {p.Last}", abbrev, p.Id, art, fresh, url, null));
            }
        }
        return list;
    }

    static string Season()
    {
        var now = DateTime.Today;
        int start = now.Month >= 7 ? now.Year : now.Year - 1;
        return $"{start}{start + 1}";
    }

    /// <summary>Downloads (cached) and converts every headshot. Returns the assignments with their PNGs (null = failed).</summary>
    public static async Task<List<PortraitAssignment>> PrepareAsync(List<PortraitAssignment> plan, Action<string>? progress = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var raw = SuitePaths.Ensure(Path.Combine(Dir, "headshots"));
        var game = SuitePaths.Ensure(Path.Combine(Dir, "game"));
        var result = new List<PortraitAssignment>();
        int n = 0;
        foreach (var a in plan)
        {
            progress?.Invoke($"Portraits {++n}/{plan.Count}");
            var src = Path.Combine(raw, $"{a.ApiId}_{Path.GetFileName(new Uri(a.Headshot).AbsolutePath)}");
            try
            {
                if (!File.Exists(src))
                {
                    var bytes = await http.GetByteArrayAsync(a.Headshot, ct);
                    await File.WriteAllBytesAsync(src, bytes, ct);
                    await Task.Delay(150, ct);
                }
                var png = Path.Combine(game, $"p{a.ArtId}.png");
                ImageIO.SavePng(ToGame(ImageIO.Load(src)), png);
                result.Add(a with { Png = png });
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or NotSupportedException)
            {
                SuiteLog.Write($"portrait {a.Name}: {ex.Message}");
                result.Add(a);
            }
        }
        File.WriteAllText(Path.Combine(Dir, "plan.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    /// <summary>
    /// NHL 24 portrait framing: head and shoulders, face about 1.45x the NHL headshot's at the same eye line. The square
    /// headshot is scaled (bilinear) to 1.45 x the canvas height, centred, and moved up so its eyes stay where they were
    /// (y = 185 of 340, as in the stock portraits); what falls outside the 512x340 canvas is cut. Transparent background.
    /// </summary>
    public static Rgba ToGame(Rgba src, double zoom = 1.45, int eyeY = 185)
    {
        const int W = 512, H = 340;
        int size = (int)Math.Round(H * zoom);
        var face = Bilinear(src, size * src.Width / Math.Max(1, src.Height), size);
        int left = (W - face.Width) / 2, top = -(int)Math.Round((zoom - 1) * eyeY);
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            int sy = y - top;
            if (sy < 0 || sy >= face.Height) continue;
            for (int x = 0; x < W; x++)
            {
                int sx = x - left;
                if (sx < 0 || sx >= face.Width) continue;
                Array.Copy(face.Pixels, (sy * face.Width + sx) * 4, px, (y * W + x) * 4, 4);
            }
        }
        return new Rgba(W, H, px);
    }

    /// <summary>Bilinear resize with premultiplied alpha (no dark fringes on the transparent edge).</summary>
    static Rgba Bilinear(Rgba src, int w, int h)
    {
        var dst = new byte[w * h * 4];
        double fx = (double)src.Width / w, fy = (double)src.Height / h;
        for (int y = 0; y < h; y++)
        {
            double sy = Math.Clamp((y + 0.5) * fy - 0.5, 0, src.Height - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, src.Height - 1); double ty = sy - y0;
            for (int x = 0; x < w; x++)
            {
                double sx = Math.Clamp((x + 0.5) * fx - 0.5, 0, src.Width - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, src.Width - 1); double tx = sx - x0;
                double a = 0, r = 0, g = 0, bl = 0;
                foreach (var (xx, yy, wgt) in new[] { (x0, y0, (1 - tx) * (1 - ty)), (x1, y0, tx * (1 - ty)), (x0, y1, (1 - tx) * ty), (x1, y1, tx * ty) })
                {
                    int i = (yy * src.Width + xx) * 4;
                    double al = src.Pixels[i + 3] / 255.0 * wgt;
                    r += src.Pixels[i] * al; g += src.Pixels[i + 1] * al; bl += src.Pixels[i + 2] * al; a += al;
                }
                int o = (y * w + x) * 4;
                if (a > 0) { dst[o] = (byte)Math.Round(r / a); dst[o + 1] = (byte)Math.Round(g / a); dst[o + 2] = (byte)Math.Round(bl / a); }
                dst[o + 3] = (byte)Math.Round(a * 255);
            }
        }
        return new Rgba(w, h, dst);
    }
}
