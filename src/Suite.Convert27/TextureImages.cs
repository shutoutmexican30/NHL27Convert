using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// Turns textures from both games into PNG files the viewer and the writer can use.
/// NHL 27: decoded by the NHL27 reader through tools\nhl27_bridge.py, one process per core.
/// NHL 24: top-mip DDS dumps (reused from NHL24Kit's refs folder or made by the stopgap Worker), decoded here in parallel.
/// </summary>
public sealed class TextureImages(SuiteSettings settings)
{
    readonly SemaphoreSlim _workerGate = new(1, 1);
    /// <summary>NHL 27 textures being decoded right now (name → gate set when the PNG is written).</summary>
    readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _inflight = new(StringComparer.Ordinal);

    public static string Nhl27Png(string name) => Path.Combine(SuitePaths.Nhl27Images, SuitePaths.Flatten(name) + ".png");

    /// <summary>
    /// A PNG with the art of <paramref name="artPng"/> and the alpha of <paramref name="basePng"/> (the fabric-region
    /// map). Base slots get this so a sponsor that only exists in the plain colour map still reaches the game while the
    /// fabric regions stay intact.
    /// </summary>
    public static string? CompositeArtWithBaseAlpha(string artPng, string basePng, string key, string? cacheDirectory = null)
    {
        try
        {
            var art = ImageIO.Load(artPng);
            var baseImg = ImageIO.Load(basePng);
            if (art.Width != baseImg.Width || art.Height != baseImg.Height)
                art = ImageIO.Resize(art, baseImg.Width, baseImg.Height);
            for (int i = 3; i < art.Pixels.Length; i += 4) art.Pixels[i] = baseImg.Pixels[i];
            // The logical key joins two full asset paths with '|': invalid on Windows,
            // and even sanitised it can exceed the filename/path limit. Keep it opaque.
            var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            var outPath = Path.Combine(SuitePaths.Ensure(cacheDirectory ?? Path.Combine(SuitePaths.DroppedImages, "composite")), cacheKey + ".png");
            var stamp = File.Exists(outPath) ? File.GetLastWriteTimeUtc(outPath) : DateTime.MinValue;
            if (stamp < File.GetLastWriteTimeUtc(artPng) || stamp < File.GetLastWriteTimeUtc(basePng))
                ImageIO.SavePng(art, outPath);
            return outPath;
        }
        catch (Exception ex)
        {
            SuiteLog.Write("composite art+alpha: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Paints <paramref name="logoPng"/> onto <paramref name="basePng"/> at a fraction of the map (x/y = centre, size =
    /// the logo's longest side as a fraction of the map). The destination alpha is kept, so a fabric-region map stays
    /// valid; only RGB and the logo's own coverage change.
    /// </summary>
    public static string? Stamp(string basePng, string logoPng, double x, double y, double size, string key, string? cacheDirectory = null)
    {
        try
        {
            var art = ImageIO.Load(basePng);
            var logo = ImageIO.Load(logoPng);
            double scale = Math.Max(0.01, Math.Min(4.0, size)) * Math.Min(art.Width, art.Height) / Math.Max(1, Math.Max(logo.Width, logo.Height));
            int w = Math.Max(1, (int)Math.Round(logo.Width * scale));
            int h = Math.Max(1, (int)Math.Round(logo.Height * scale));
            var small = ImageIO.ResizeSmooth(logo, w, h);
            int left = (int)Math.Round(x * art.Width - w / 2.0);
            int top = (int)Math.Round(y * art.Height - h / 2.0);
            for (int sy2 = 0; sy2 < h; sy2++)
            {
                int dy = top + sy2;
                if (dy < 0 || dy >= art.Height) continue;
                for (int sx2 = 0; sx2 < w; sx2++)
                {
                    int dx = left + sx2;
                    if (dx < 0 || dx >= art.Width) continue;
                    int si = (sy2 * w + sx2) * 4, di = (dy * art.Width + dx) * 4;
                    double a = small.Pixels[si + 3] / 255.0;
                    if (a <= 0) continue;
                    for (int c = 0; c < 3; c++)
                        art.Pixels[di + c] = (byte)Math.Round(art.Pixels[di + c] * (1 - a) + small.Pixels[si + c] * a);
                }
            }
            var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                key + "|" + x.ToString("F4") + "|" + y.ToString("F4") + "|" + size.ToString("F4") + "|" +
                File.GetLastWriteTimeUtc(basePng).Ticks + "|" + File.GetLastWriteTimeUtc(logoPng).Ticks)));
            var outPath = Path.Combine(SuitePaths.Ensure(cacheDirectory ?? Path.Combine(SuitePaths.DroppedImages, "stamp")), cacheKey + ".png");
            if (!File.Exists(outPath)) ImageIO.SavePng(art, outPath);
            return outPath;
        }
        catch (Exception ex)
        {
            SuiteLog.Write("stamp: " + ex.Message);
            return null;
        }
    }

    /// <summary>Decodes any of the given NHL 27 textures that are not cached yet. Returns name → PNG path for the ones that exist.</summary>
    public async Task<Dictionary<string, string>> Nhl27Async(IEnumerable<Nhl27Texture> textures, Action<string>? progress = null, CancellationToken ct = default)
    {
        var list = textures.DistinctBy(t => t.Name).ToList();
        var missing = list.Where(t => !File.Exists(Nhl27Png(t.Name))
            || t.Slices > 1 && Enumerable.Range(0, t.Slices).Any(i => !File.Exists(Nhl27Png($"{t.Name}#{i}")))).ToList();
        if (missing.Count > 0)
        {
            // De-duplicate in-flight decodes: two simultaneous calls for the same texture would race the
            // bridge's .tmp.png, so only the first caller decodes and everyone else waits for that result.
            var mine = new List<Nhl27Texture>();
            var waiters = new List<TaskCompletionSource<bool>>();
            foreach (var t in missing)
            {
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_inflight.TryAdd(t.Name, gate)) { mine.Add(t); waiters.Add(gate); }
                else if (_inflight.TryGetValue(t.Name, out var other)) waiters.Add(other);
            }
            if (mine.Count > 0)
            {
                var jobFile = Path.Combine(SuitePaths.Local, $"decode-{Guid.NewGuid():N}.json");
                try
                {
                    var jobs = mine.Select(t => new
                    {
                        name = t.Name, pkg = t.Pkg, cas = t.Cas, tree = t.Tree, offset = t.Offset, size = t.Size,
                        width = t.Width, height = t.Height, format = t.Format, @out = Nhl27Png(t.Name),
                        slices = t.Slices,
                        sliceOuts = t.Slices > 1 ? Enumerable.Range(0, t.Slices).Select(i => Nhl27Png($"{t.Name}#{i}")).ToArray() : null,
                    }).ToList();
                    File.WriteAllText(jobFile, JsonSerializer.Serialize(jobs));
                    int done = 0;
                    var r = await ProcessRunner.RunAsync(settings.PythonExe,
                        ["-I", IndexStore.BridgeScript, "decode", "--viewer", settings.Nhl27ViewerDir, "--game", settings.Nhl27GameRoot,
                         "--jobs", jobFile, "--workers", settings.Workers.ToString()],
                        onLine: line =>
                        {
                            var n = Interlocked.Increment(ref done);
                            if (line.StartsWith("ERR", StringComparison.Ordinal)) SuiteLog.Write("NHL 27 decode " + line);
                            progress?.Invoke($"Decoding NHL 27 textures {n}/{jobs.Count}");
                        }, ct: ct);
                    if (r.ExitCode != 0) SuiteLog.Write("NHL 27 decode failed: " + r.StdErr.Trim());
                }
                finally
                {
                    try { File.Delete(jobFile); } catch { }
                    foreach (var t in mine) if (_inflight.TryRemove(t.Name, out var gate)) gate.TrySetResult(true);
                }
            }
            if (waiters.Count > 0) await Task.WhenAll(waiters.Select(w => w.Task)).WaitAsync(ct);
        }
        return list.Where(t => File.Exists(Nhl27Png(t.Name))).ToDictionary(t => t.Name, t => Nhl27Png(t.Name));
    }

    /// <summary>Stock NHL 24 images for every texture in a folder. Keys are slot names; array layers are "slot#layer".</summary>
    public async Task<Dictionary<string, string>> Nhl24FolderAsync(string folder, Action<string>? progress = null,
        CancellationToken ct = default, IReadOnlyList<string>? expected = null)
    {
        var dumpDir = await DumpDirAsync(folder, progress, ct, expected);
        var result = new ConcurrentDictionary<string, string>();
        if (dumpDir is null) return [];
        var files = Directory.GetFiles(dumpDir, "*.dds");
        int done = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = settings.Workers, CancellationToken = ct }, (dds, _) =>
        {
            var leaf = Path.GetFileNameWithoutExtension(dds);
            var key = folder + "/" + leaf;
            var png = Path.Combine(SuitePaths.Nhl24Images, SuitePaths.Flatten(key) + ".png");
            try
            {
                if (!File.Exists(png) || File.GetLastWriteTimeUtc(png) < File.GetLastWriteTimeUtc(dds))
                    ImageIO.SavePng(ImageIO.Shrink(Dds.Load(dds), 2048), png);
                result[key] = png;
            }
            catch (Exception ex)
            {
                SuiteLog.Write($"NHL 24 decode {leaf}: {ex.Message}");
            }
            progress?.Invoke($"Decoding NHL 24 textures {Interlocked.Increment(ref done)}/{files.Length}");
            return ValueTask.CompletedTask;
        });
        return new Dictionary<string, string>(result);
    }

    /// <summary>
    /// A cached dump is only usable when it holds the arrays the folder actually has. Carolina and Calgary ship no branding
    /// art in their cached dumps, which made every branding layer read as blank; a partial cache is re-dumped, not trusted.
    /// </summary>
    public static bool DumpCovers(string dumpDir, IReadOnlyList<string>? expected)
    {
        if (expected is null || expected.Count == 0) return true;
        var have = Directory.GetFiles(dumpDir, "*.dds").Select(f => Path.GetFileNameWithoutExtension(f) ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in expected)
        {
            var leaf = name.Split('/')[^1];
            if (!leaf.EndsWith("_color_array", StringComparison.Ordinal)) continue;
            bool any = have.Contains(leaf) || have.Any(h => h.StartsWith(leaf + "#", StringComparison.OrdinalIgnoreCase));
            if (!any) return false;
        }
        return true;
    }

    async Task<string?> DumpDirAsync(string folder, Action<string>? progress, CancellationToken ct, IReadOnlyList<string>? expected = null)
    {
        var flat = SuitePaths.Flatten(folder);
        var own = Path.Combine(SuitePaths.Nhl24Refs, flat);
        if (Directory.Exists(own) && Directory.GetFiles(own, "*.dds").Length > 0 && DumpCovers(own, expected)) return own;
        var legacy = Path.Combine(settings.LegacyRefsDir, flat);
        if (Directory.Exists(legacy) && Directory.GetFiles(legacy, "*.dds").Length > 0 && DumpCovers(legacy, expected))
            return legacy;
        if (Directory.Exists(legacy) || Directory.Exists(own))
            SuiteLog.Write($"NHL 24 dump for {folder} does not cover its arrays; re-dumping into the suite cache");
        if (!Worker.Available(settings)) return null;

        await _workerGate.WaitAsync(ct);
        try
        {
            progress?.Invoke($"Dumping NHL 24 {folder} (stopgap Worker)...");
            var view = Nhl24ReadView.Ensure(settings);
            await Worker.RunAsync(settings, ["refs", view, folder, own], ct);
            return own;
        }
        catch (Exception ex)
        {
            SuiteLog.Write($"NHL 24 dump of {folder} failed: {ex.Message}");
            return null;
        }
        finally
        {
            _workerGate.Release();
        }
    }

    /// <summary>A viewer-sized PNG copy of any image the user dropped (PNG/JPG/TGA/DDS...).</summary>
    public static string PreviewOf(string path)
    {
        if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) && new FileInfo(path).Length < 24_000_000)
            return path;
        var info = new FileInfo(path);
        var key = $"{SuitePaths.Flatten(path.Replace(':', '_'))}_{info.Length}_{info.LastWriteTimeUtc.Ticks}";
        var png = Path.Combine(SuitePaths.DroppedImages, "preview", key + ".png");
        if (!File.Exists(png)) ImageIO.SavePng(ImageIO.Shrink(ImageIO.Load(path), 2048), png);
        return png;
    }
}
