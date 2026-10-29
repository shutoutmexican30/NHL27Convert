using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suite.Core;

/// <summary>Loads each game's <see cref="AssetIndex"/> from disk, or builds it when it is missing or the game changed.</summary>
public static class IndexStore
{
    public const string Nhl24 = "nhl24";
    public const string Nhl27 = "nhl27";

    public static string BridgeScript => Path.Combine(AppContext.BaseDirectory, "tools", "nhl27_bridge.py");

    public static string Nhl24Fingerprint(SuiteSettings s) => AssetIndex.FingerprintOf([
        Path.Combine(s.Nhl24BaseDir, "package.mft"),
        Path.Combine(s.Nhl24BaseDir, "Data", "layout.toc"),
        Path.Combine(s.Nhl24UpdateDir, "Patch", "layout.toc"),
    ]);

    public static string Nhl27Fingerprint(SuiteSettings s)
    {
        var files = new List<string> { Path.Combine(s.Nhl27GameRoot, "Data", "Ps5", "layout.toc"), Path.Combine(s.Nhl27GameRoot, "Patch", "Ps5", "layout.toc") };
        var cache = Path.Combine(s.Nhl27ViewerDir, "cache");
        if (Directory.Exists(cache)) files.AddRange(Directory.GetFiles(cache, "*.meta.json"));
        return AssetIndex.FingerprintOf(files);
    }

    public static async Task<AssetIndex> GetAsync(string game, SuiteSettings s, bool rebuild = false, Action<string>? progress = null, CancellationToken ct = default)
    {
        var fp = game == Nhl24 ? Nhl24Fingerprint(s) : Nhl27Fingerprint(s);
        if (!rebuild && await Task.Run(() => AssetIndex.TryLoad(game, fp), ct) is { } loaded) return loaded;
        progress?.Invoke($"Building the {game.ToUpperInvariant()} asset index...");
        var index = game == Nhl24 ? await BuildNhl24Async(s, fp, ct) : await BuildNhl27Async(s, fp, ct);
        index.Save();
        SuiteLog.Write($"index {game}: {index.Entries.Count} assets, fingerprint {fp}");
        return index;
    }

    /// <summary>NHL 24: every TextureAsset/TextureArrayAsset, listed by the stopgap Worker through the read view.</summary>
    static async Task<AssetIndex> BuildNhl24Async(SuiteSettings s, string fp, CancellationToken ct)
    {
        var view = Nhl24ReadView.Ensure(s);
        var json = await Worker.RunAsync(s, ["find", view], ct);
        var rows = JsonSerializer.Deserialize<List<FindRow>>(json) ?? [];
        var entries = rows.AsParallel().Select(r => new AssetEntry
        {
            Game = Nhl24, Kind = r.Array ? AssetKind.TextureArray : AssetKind.Texture, Path = r.Name,
        });
        return new AssetIndex(Nhl24, fp, DateTime.Now, entries);
    }

    /// <summary>NHL 27: every named texture from the NHL27 reader's scan cache, with its PS5 chunk location.</summary>
    static async Task<AssetIndex> BuildNhl27Async(SuiteSettings s, string fp, CancellationToken ct)
    {
        var tmp = Path.Combine(AssetIndex.Dir(Nhl27), "bridge-index.json");
        var r = await ProcessRunner.RunAsync(s.PythonExe, ["-I", BridgeScript, "index", "--viewer", s.Nhl27ViewerDir, "--out", tmp], ct: ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("NHL 27 index failed: " + r.StdErr.Trim());
        List<BridgeRow> rows;
        await using (var fs = File.OpenRead(tmp)) rows = await JsonSerializer.DeserializeAsync<List<BridgeRow>>(fs, cancellationToken: ct) ?? [];
        File.Delete(tmp);
        var entries = rows.AsParallel().Select(t => new AssetEntry
        {
            Game = Nhl27, Kind = t.Slices > 1 || t.Name.EndsWith("_array", StringComparison.Ordinal) ? AssetKind.TextureArray : AssetKind.Texture,
            Path = t.Name, Width = t.Width, Height = t.Height, Slices = t.Slices, Mips = t.Mips, FirstMip = t.FirstMip, Format = t.Format,
            Location = new AssetLocation(t.Pkg, t.Cas, t.Tree, t.Offset, t.Size),
        });
        return new AssetIndex(Nhl27, fp, DateTime.Now, entries);
    }

    sealed class FindRow
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("array")] public bool Array { get; set; }
    }

    sealed class BridgeRow
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("slices")] public int Slices { get; set; } = 1;
        [JsonPropertyName("mips")] public int Mips { get; set; }
        [JsonPropertyName("firstMip")] public int FirstMip { get; set; }
        [JsonPropertyName("format")] public string Format { get; set; } = "";
        [JsonPropertyName("pkg")] public int Pkg { get; set; }
        [JsonPropertyName("cas")] public int Cas { get; set; }
        [JsonPropertyName("tree")] public string Tree { get; set; } = "Data";
        [JsonPropertyName("offset")] public long Offset { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
