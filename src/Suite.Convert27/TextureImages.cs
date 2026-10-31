using System.Collections.Concurrent;
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
    public static string? CompositeArtWithBaseAlpha(string artPng, string basePng, string key)
    {
        try
        {
            var art = ImageIO.Load(artPng);
            var baseImg = ImageIO.Load(basePng);
            if (art.Width != baseImg.Width || art.Height != baseImg.Height)
                art = ImageIO.Resize(art, baseImg.Width, baseImg.Height);
            for (int i = 3; i < art.Pixels.Length; i += 4) art.Pixels[i] = baseImg.Pixels[i];
            var outPath = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.DroppedImages, "composite")), SuitePaths.Flatten(key) + ".png");
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

    /// <summary>Decodes any of the given NHL 27 textures that are not cached yet. Returns name → PNG path for the ones that exist.</summary>
    public async Task<Dictionary<string, string>> Nhl27Async(IEnumerable<Nhl27Texture> textures, Action<string>? progress = null, CancellationToken ct = default)
    {
        var list = textures.DistinctBy(t => t.Name).ToList();
        var missing = list.Where(t => !File.Exists(Nhl27Png(t.Name))).ToList();
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
    public async Task<Dictionary<string, string>> Nhl24FolderAsync(string folder, Action<string>? progress = null, CancellationToken ct = default)
    {
        var dumpDir = await DumpDirAsync(folder, progress, ct);
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

    async Task<string?> DumpDirAsync(string folder, Action<string>? progress, CancellationToken ct)
    {
        var flat = SuitePaths.Flatten(folder);
        var own = Path.Combine(SuitePaths.Nhl24Refs, flat);
        if (Directory.Exists(own) && Directory.GetFiles(own, "*.dds").Length > 0) return own;
        var legacy = Path.Combine(settings.LegacyRefsDir, flat);
        if (Directory.Exists(legacy) && Directory.GetFiles(legacy, "*.dds").Length > 0) return legacy;
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
