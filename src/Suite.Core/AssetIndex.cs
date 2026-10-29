using System.Collections.Frozen;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suite.Core;

public enum AssetKind { Texture, TextureArray, Mesh, Material, Legacy, Sound }

/// <summary>
/// Where an asset's data lives. NHL 27 (PS5): install package / cas / tree / offset / size of the chunk.
/// NHL 24 (PS4): filled by the suite's own TOC/bundle reader (chunk id, every TOC/bundle/RES/duplicate reference, §6);
/// until that lands NHL 24 entries carry no location and reads go through the stopgap Worker.
/// </summary>
public sealed record AssetLocation(
    int? Package = null, int? Cas = null, string? Tree = null, long? Offset = null, long? Size = null,
    string? ChunkId = null, string[]? References = null);

/// <summary>One asset in one game. The id ("nhl24:content/…/leaf") is what mod lists, matches and mesh ports are keyed by.</summary>
public sealed record AssetEntry
{
    public required string Game { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public required AssetKind Kind { get; init; }
    public required string Path { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Slices { get; init; } = 1;
    public int Mips { get; init; }
    public int FirstMip { get; init; }
    public string? Format { get; init; }
    public AssetLocation? Location { get; init; }

    [JsonIgnore] public string Id => Game + ":" + Path;
    [JsonIgnore] public TextureName Name => _name ??= TextureName.Parse(Path);
    TextureName? _name;
}

/// <summary>
/// The suite's core data model: every asset of one game, with query indexes on folder, team, brand, set,
/// role, garment and path tokens. Built from the game (in parallel), persisted gzipped under
/// %LOCALAPPDATA%\CenterIceModdingSuite\index\&lt;game&gt;\, and rebuilt when the game's fingerprint changes.
/// </summary>
public sealed class AssetIndex
{
    public const int SchemaVersion = 1;

    public string Game { get; }
    public string Fingerprint { get; }
    public DateTime Built { get; }
    public IReadOnlyList<AssetEntry> Entries { get; }

    readonly FrozenDictionary<string, AssetEntry> _byPath;
    readonly FrozenDictionary<string, AssetEntry[]> _byFolder;
    readonly FrozenDictionary<string, int[]> _byToken;

    public AssetIndex(string game, string fingerprint, DateTime built, IEnumerable<AssetEntry> entries)
    {
        Game = game;
        Fingerprint = fingerprint;
        Built = built;
        var list = entries.GroupBy(e => e.Path, StringComparer.Ordinal).Select(g => g.First()).OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        Entries = list;
        _byPath = list.ToFrozenDictionary(e => e.Path, StringComparer.Ordinal);
        _byFolder = list.GroupBy(e => e.Name.Folder, StringComparer.Ordinal).ToFrozenDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        // Token → entry positions. Tokens are path segments and leaf words plus typed keys (team:, brand:, set:, role:, garment:, kind:).
        var tokens = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < list.Length; i++)
            foreach (var t in TokensOf(list[i]))
            {
                if (!tokens.TryGetValue(t, out var l)) tokens[t] = l = [];
                if (l.Count == 0 || l[^1] != i) l.Add(i);
            }
        _byToken = tokens.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
    }

    public AssetEntry? Get(string path) => _byPath.GetValueOrDefault(path);
    public AssetEntry? GetById(string id) => id.StartsWith(Game + ":", StringComparison.Ordinal) ? Get(id[(Game.Length + 1)..]) : null;
    public IReadOnlyList<AssetEntry> InFolder(string folder) => _byFolder.TryGetValue(folder, out var a) ? a : [];
    public IEnumerable<string> Folders => _byFolder.Keys;

    /// <summary>
    /// Query with space-separated terms; every term must match. "team:arizona role:color base" etc.
    /// A plain word matches a path token exactly; a word ending in * matches a token prefix; anything else falls back to a substring scan.
    /// </summary>
    public IEnumerable<AssetEntry> Query(string query)
    {
        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return Entries;
        IEnumerable<int>? hits = null;
        var scan = new List<string>();
        foreach (var term in terms)
        {
            int[]? ids = null;
            if (term.EndsWith('*'))
            {
                var prefix = term[..^1];
                ids = _byToken.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).SelectMany(kv => kv.Value).Distinct().Order().ToArray();
            }
            else if (_byToken.TryGetValue(term, out var exact)) ids = exact;
            if (ids is null) { scan.Add(term); continue; }
            hits = hits is null ? ids : hits.Intersect(ids);
        }
        var result = (hits ?? Enumerable.Range(0, Entries.Count)).Select(i => Entries[i]);
        foreach (var s in scan) result = result.Where(e => e.Path.Contains(s, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    static IEnumerable<string> TokensOf(AssetEntry e)
    {
        var n = e.Name;
        foreach (var seg in n.Folder.Split('/')) if (seg.Length > 0) yield return seg;
        foreach (var w in n.Leaf.Split('_')) if (w.Length > 0) yield return w;
        yield return "kind:" + e.Kind.ToString().ToLowerInvariant();
        if (n.Team is not null) yield return "team:" + n.Team;
        if (n.Brand is not null) yield return "brand:" + n.Brand;
        if (n.Set is not null) yield return "set:" + n.Set;
        yield return "role:" + n.Role;
        yield return "garment:" + n.Garment;
        if (n.IsBase) yield return "base";
        if (n.Layer is not null) yield return "layer:" + n.Layer;
    }

    // ------------------------------------------------------------------ persistence

    public static string Dir(string game) => SuitePaths.Ensure(System.IO.Path.Combine(SuitePaths.Local, "index", game));
    static string FileFor(string game) => System.IO.Path.Combine(Dir(game), "assets.json.gz");

    static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() },
    };

    sealed record Stored(int Schema, string Game, string Fingerprint, DateTime Built, List<AssetEntry> Entries);

    public void Save()
    {
        var path = FileFor(Game);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            JsonSerializer.Serialize(gz, new Stored(SchemaVersion, Game, Fingerprint, Built, Entries.ToList()), Json);
        File.Move(tmp, path, true);
    }

    /// <summary>Loads the saved index if it exists, matches the schema and (when given) the current game fingerprint.</summary>
    public static AssetIndex? TryLoad(string game, string? fingerprint = null)
    {
        var path = FileFor(game);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var s = JsonSerializer.Deserialize<Stored>(gz, Json);
            if (s is null || s.Schema != SchemaVersion || (fingerprint is not null && s.Fingerprint != fingerprint)) return null;
            return new AssetIndex(s.Game, s.Fingerprint, s.Built, s.Entries);
        }
        catch (Exception ex)
        {
            SuiteLog.Write($"index {game} unreadable, rebuilding: {ex.Message}");
            return null;
        }
    }

    /// <summary>Hash of the files that change when the game is patched (sizes and times of package.mft and every layout/toc root file).</summary>
    public static string FingerprintOf(IEnumerable<string> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.Where(File.Exists).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var i = new FileInfo(f);
            sb.Append(f.ToLowerInvariant()).Append('|').Append(i.Length).Append('|').Append(i.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
