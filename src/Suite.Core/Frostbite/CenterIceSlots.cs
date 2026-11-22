using System.Text.RegularExpressions;

namespace Suite.Core.Frostbite;

/// <summary>
/// NHL 24 center-ice logos (content/worlds/rink/textures/centerlogo/ice_centerlogo_N_color, BC7 1024x1024, 11 mips,
/// PS4 tiled): read a slot, build a replacement and write it everywhere the game reads it (<see cref="ChunkReplace"/>),
/// with a backup and a read-back; any failure restores the backup.
/// Team slot = the team's arena id (Vegas 32, Seattle 33; 30 and 31 are All-Star logos). NHL 27 keeps the same numbers
/// except Utah, whose art is NHL 27 slot 30 (NHL 24 slot 22 is Utah's).
/// </summary>
public static class CenterIceSlots
{
    public const string Folder = "content/worlds/rink/textures/centerlogo/";
    public static string Name(int slot) => $"{Folder}ice_centerlogo_{slot}_color";
    public static int Nhl27Slot(int slot) => slot == 22 ? 30 : slot;

    public sealed record Slot(int Number, TextureRes Res, Guid Chunk, Rgba Image);

    public static Slot Read(string game, BundleIndex index, int slot)
    {
        var res = index.ResByName.GetValueOrDefault(Name(slot))?.FirstOrDefault() ?? throw new InvalidOperationException($"No center-ice slot {slot} in this game.");
        var stored = BundleIndex.ReadBytes(res.Data);
        byte[] header; try { header = ChunkBlocks.Parse(stored).Decode(); } catch { header = stored; }
        var tr = TextureRes.Parse(header);
        if (header[12] != 0x43) throw new InvalidOperationException($"Slot {slot} is not BC7.");
        var full = ChunkReplace.ReadAll(game, index, tr.ChunkGuid).FirstOrDefault(r => r.LogicalOffset == 0 && r.Raw.Length == tr.PayloadSize).Raw
            ?? throw new InvalidOperationException($"Slot {slot}: no full copy of its texture.");
        return new Slot(slot, tr, tr.ChunkGuid, Bc7.DecodeTiled(TextureChunk.Parse(full, tr).Mips[0], tr.Width, tr.Height));
    }

    /// <summary>Full BC7 mip chain in the slot's exact layout, red-line strip cut out like stock.</summary>
    public static byte[] Build(TextureRes res, Rgba image)
    {
        if (image.Width != res.Width || image.Height != res.Height) image = ImageIO.ResizeSmooth(image, res.Width, res.Height);
        image = CenterIceImage.CutRedLine(image);
        var mips = new List<byte[]>();
        for (int i = 0; i < res.MipCount; i++)
        {
            mips.Add(Bc7.EncodeTiled(image));
            if (i + 1 < res.MipCount) image = TextureChunk.Halve(image);
        }
        return TextureChunk.Build(res, mips).Data;
    }

    /// <summary>Writes the images into their slots of <paramref name="game"/>. Returns (ok, message, backup).</summary>
    public static (bool Ok, string Message, string? Backup) Apply(string game, IReadOnlyDictionary<int, Rgba> images, Action<string>? progress = null)
    {
        progress?.Invoke("Reading the game's bundles...");
        var index = BundleIndex.Build(game);
        var jobs = images.Select(kv => { var s = Read(game, index, kv.Key); return (s.Chunk, Data: Build(s.Res, kv.Value)); }).ToList();
        return ApplyJobs(game, jobs, progress);
    }

    /// <summary>
    /// Like <see cref="Apply"/>, but the caller supplies finished chunk bytes per slot (the full-rink build).
    /// The slot is looked up for its chunk id only; the bytes are used exactly as given.
    /// </summary>
    public static (bool Ok, string Message, string? Backup) ApplyChunks(string game, IReadOnlyDictionary<int, byte[]> data, Action<string>? progress = null)
    {
        progress?.Invoke("Reading the game's bundles...");
        var index = BundleIndex.Build(game);
        var jobs = data.Select(kv => { var s = Read(game, index, kv.Key); return (s.Chunk, Data: kv.Value); }).ToList();
        return ApplyJobs(game, jobs, progress);
    }

    static (bool Ok, string Message, string? Backup) ApplyJobs(string game, List<(Guid Chunk, byte[] Data)> jobs, Action<string>? progress)
    {
        if (ApplyTarget.RunningGame() is { } running) return (false, running + " is running. Close the game first.", null);
        var problems = ApplyTarget.Problems(game);
        if (problems.Count > 0) return (false, "Apply refused:\n" + string.Join("\n", problems), null);
        progress?.Invoke("Backing up TOCs and CAS lengths...");
        var backup = ApplyTarget.Backup(game);
        try
        {
            var index = BundleIndex.Build(game);
            foreach (var (id, data) in jobs)
            {
                foreach (var line in ChunkReplace.Replace(game, index, id, data)) SuiteLog.Write("center ice: " + line);
                index = BundleIndex.Build(game);
            }
            progress?.Invoke("Reading them back...");
            foreach (var (id, data) in jobs)
                foreach (var r in ChunkReplace.ReadAll(game, index, id))
                    if (!r.Raw.AsSpan().SequenceEqual(data.AsSpan((int)r.LogicalOffset, r.Raw.Length)))
                        throw new InvalidOperationException($"Read-back differs ({r.Where}).");
            return (true, $"{jobs.Count} center-ice slot(s) written and read back identical. Backup: {backup}", backup);
        }
        catch (Exception ex)
        {
            SuiteLog.Write("center ice apply failed, restoring: " + ex);
            ApplyTarget.RestoreFromBackup(game, backup);
            return (false, $"Apply failed ({ex.Message}); the game was put back from the backup.", backup);
        }
    }
}

/// <summary>
/// TheFaceoff.net images, fetched only when the user clicks: a center-ice page link (its main picture) or a direct image
/// link. Personal use only, with credit and a link back (the site's terms); never part of exported or shared mod packs.
/// Cached in %LOCALAPPDATA%\CenterIceModdingSuite\faceoff; at most one request per 10 seconds.
/// </summary>
public static class Faceoff
{
    public const string Credit = "Center ice art from TheFaceoff.net (thefaceoff.net), personal use only.";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    static DateTime _last = DateTime.MinValue;
    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Was treated as the site's "1-1 Placeholder" upload and refused. It is also the real 2027 Detroit page's picture
    /// (checked 2026-11-03: 1000x1000 Hockeytown without the 100), so refusing it rejected a page the user wanted. A
    /// page's own picture is what gets fetched; if a season has no art, the mismatch shows up in the preview instead.
    /// </summary>
    const string PlaceholderMedia = "";

    public static string Cache => SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "faceoff"));

    static async Task<byte[]> GetAsync(string url, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var wait = _last + TimeSpan.FromSeconds(10) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _last = DateTime.UtcNow;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("CenterIceModdingSuite/1.0 (personal use)");
            using var resp = await Http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Downloads the picture behind <paramref name="link"/> (page or image) into the cache; returns the file.</summary>
    public static async Task<string> FetchAsync(string link, CancellationToken ct = default)
    {
        var uri = new Uri(link.Trim());
        if (!uri.Host.EndsWith("thefaceoff.net", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith("wixstatic.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only thefaceoff.net links (or their image links) are fetched.");
        string imageUrl = uri.ToString();
        if (uri.Host.EndsWith("thefaceoff.net", StringComparison.OrdinalIgnoreCase))
        {
            var html = System.Text.Encoding.UTF8.GetString(await GetAsync(imageUrl, ct));
            var m = Regex.Match(html, "<meta[^>]+property=\"og:image\"[^>]+content=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(html, "https://static\\.wixstatic\\.com/media/[^\"'\\s)]+", RegexOptions.IgnoreCase);
            if (!m.Success) throw new InvalidOperationException("No picture found on that page.");
            imageUrl = System.Net.WebUtility.HtmlDecode(m.Groups[m.Groups.Count > 1 && m.Groups[1].Success ? 1 : 0].Value);
            // A season whose art is not posted yet uses the site's shared placeholder picture (one fixed upload).
            if (PlaceholderMedia.Length > 0 && imageUrl.Contains(PlaceholderMedia, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("That page has no center ice posted yet (placeholder picture). Try the previous season's page.");
        }
        return await DownloadAsync(imageUrl, link, ct);
    }

    /// <summary>A picture embedded in a rink page: the size the page states for it, its wixstatic media id, and the page it links to when it has one.</summary>
    public sealed record PageImage(int Width, int Height, string MediaId, string? Href)
    {
        /// <summary>The full-size original on the wixstatic CDN.</summary>
        public string Url => $"https://static.wixstatic.com/media/{MediaId}";
    }

    public const double LayoutAspect = 5000.0 / 2236.0;

    /// <summary>True when a picture is a top-down rink layout (TheFaceoff.net draws them ~5000x2236); the stated
    /// width/height arrive in either order, so the aspect is checked both ways.</summary>
    public static bool IsLayoutShape(PageImage image)
    {
        var lo = Math.Min(image.Width, image.Height);
        var hi = Math.Max(image.Width, image.Height);
        return hi >= 2000 && Math.Abs(hi / (double)lo - LayoutAspect) <= 0.02 * LayoutAspect;
    }

    // The site's page data writes {"width","height"...,"uri"} or {"height","width"...,"uri"}, sometimes with the
    // picture's own link. Both orders and the optional link are read.
    static readonly Regex PageImageEntry = new(
        "\\{\"(?<a>width|height)\":(?<av>\\d+),\"(?<b>width|height)\":(?<bv>\\d+),[^{}]{0,400}?\"uri\":\"(?<id>84911f_[0-9a-f]{32}~mv2\\.\\w+)\"[^{}]{0,300}?(?:\\{\"href\":\"(?<href>[^\"]+)\")?",
        RegexOptions.Compiled);

    public static IReadOnlyList<PageImage> ParsePageImages(string html) =>
        PageImageEntry.Matches(html).Select(m =>
        {
            var av = int.Parse(m.Groups["av"].Value);
            var bv = int.Parse(m.Groups["bv"].Value);
            var (w, h) = m.Groups["a"].Value == "width" ? (av, bv) : (bv, av);
            var href = m.Groups["href"].Success ? m.Groups["href"].Value.Replace("\\/", "/", StringComparison.Ordinal) : null;
            return new PageImage(w, h, m.Groups["id"].Value, href);
        }).DistinctBy(i => i.MediaId).ToList();

    /// <summary>
    /// The page's top-down rink layout for full-rink mode: not the og:image (a page's main picture is often a
    /// banner or a center-logo thumbnail), but the embedded ~5000x2236 layout, preferring the one that links to
    /// the page itself. Same politeness, cache and credit as <see cref="FetchAsync"/>.
    /// </summary>
    public static async Task<string> FetchLayoutAsync(string link, CancellationToken ct = default)
    {
        var uri = new Uri(link.Trim());
        if (!uri.Host.EndsWith("thefaceoff.net", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fetch a rink layout from a thefaceoff.net rink page link.");
        var html = System.Text.Encoding.UTF8.GetString(await GetAsync(uri.ToString(), ct));
        var page = uri.ToString().TrimEnd('/');
        PageImage? own = null, any = null;
        foreach (var image in ParsePageImages(html))
        {
            if (!IsLayoutShape(image)) continue;
            if (image.Href is { } href && href.TrimEnd('/').Equals(page, StringComparison.OrdinalIgnoreCase)) { own = image; break; }
            any ??= image;
        }
        var layout = own ?? any
            ?? throw new InvalidOperationException("That page has no top-down rink layout (no ~5000x2236 picture). If it is a team-season page, try its previous season.");
        return await DownloadAsync(layout.Url, link + "\nlayout " + layout.Width + "x" + layout.Height, ct);
    }

    /// <summary>The original upload (wix serves resized copies under /v1/...; the part before it is the full size), cached.</summary>
    static async Task<string> DownloadAsync(string imageUrl, string sourceLink, CancellationToken ct)
    {
        int v1 = imageUrl.IndexOf("/v1/", StringComparison.Ordinal);
        if (v1 > 0 && imageUrl.Contains("wixstatic.com")) imageUrl = imageUrl[..v1];
        var file = Path.Combine(Cache, Regex.Replace(Path.GetFileName(new Uri(imageUrl).AbsolutePath), "[^A-Za-z0-9._-]", "_"));
        if (!File.Exists(file)) await File.WriteAllBytesAsync(file, await GetAsync(imageUrl, ct), ct);
        File.WriteAllText(file + ".source.txt", sourceLink + "\n" + imageUrl + "\n" + Credit + "\n");
        return file;
    }
}
