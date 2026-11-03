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
        if (ApplyTarget.RunningGame() is { } running) return (false, running + " is running. Close the game first.", null);
        var problems = ApplyTarget.Problems(game);
        if (problems.Count > 0) return (false, "Apply refused:\n" + string.Join("\n", problems), null);
        progress?.Invoke("Reading the game's bundles...");
        var index = BundleIndex.Build(game);
        var jobs = images.Select(kv => { var s = Read(game, index, kv.Key); return (s.Chunk, Data: Build(s.Res, kv.Value)); }).ToList();
        progress?.Invoke("Backing up TOCs and CAS lengths...");
        var backup = ApplyTarget.Backup(game);
        try
        {
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

    /// <summary>The site's "1-1 Placeholder" upload, shown for seasons whose art is not posted yet (seen 2026-11-03).</summary>
    const string PlaceholderMedia = "1355d81e56fb457f9291640812cf4f9f";

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
            if (imageUrl.Contains(PlaceholderMedia, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("That page has no center ice posted yet (placeholder picture). Try the previous season's page.");
        }
        // Wix serves resized copies under /v1/...; the part before it is the original upload.
        int v1 = imageUrl.IndexOf("/v1/", StringComparison.Ordinal);
        if (v1 > 0 && imageUrl.Contains("wixstatic.com")) imageUrl = imageUrl[..v1];
        var file = Path.Combine(Cache, Regex.Replace(Path.GetFileName(new Uri(imageUrl).AbsolutePath), "[^A-Za-z0-9._-]", "_"));
        if (!File.Exists(file)) await File.WriteAllBytesAsync(file, await GetAsync(imageUrl, ct), ct);
        File.WriteAllText(file + ".source.txt", link + "\n" + imageUrl + "\n" + Credit + "\n");
        return file;
    }
}
