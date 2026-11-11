using System.Text.Json;
using System.Text.Json.Serialization;
using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Convert27;

/// <summary>
/// Which NHL 27 uniforms each game folder already has, so Add to game (All teams) skips them instead of adding a second
/// copy under another variant. A record only counts while its kit's assets are still in the game, so an undo or a
/// restored backup makes the uniform addable again.
/// </summary>
public sealed class InstalledKits
{
    public sealed class Kit
    {
        [JsonPropertyName("game")] public string Game { get; set; } = "";
        [JsonPropertyName("team")] public string Team { get; set; } = "";
        [JsonPropertyName("kit")] public string SourceKit { get; set; } = "";
        [JsonPropertyName("folder27")] public string? Folder27 { get; set; }
        [JsonPropertyName("target")] public string TargetPrefix { get; set; } = "";
        [JsonPropertyName("variant")] public int Variant { get; set; }
        [JsonPropertyName("added")] public DateTime Added { get; set; }
    }

    [JsonPropertyName("kits")] public List<Kit> Kits { get; set; } = [];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string PathName => Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Roaming, "data")), "installed_kits.json");

    public static InstalledKits Load(string? path = null)
    {
        path ??= PathName;
        try { return File.Exists(path) ? JsonSerializer.Deserialize<InstalledKits>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (Exception ex) { SuiteLog.Write("installed_kits.json unreadable: " + ex.Message); return new(); }
    }

    public void Save(string? path = null) => File.WriteAllText(path ?? PathName, JsonSerializer.Serialize(this, Json));

    static string Key(string game) => Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
    static string Folder(string? f) => (f ?? "").Trim().TrimEnd('/');

    public void Record(string game, IEnumerable<NewUniformAddition> added)
    {
        foreach (var a in added.Where(a => !string.IsNullOrWhiteSpace(a.TargetPrefix)))
        {
            Kits.RemoveAll(k => Key(k.Game) == Key(game) && k.TargetPrefix.TrimEnd('/') == a.TargetPrefix.TrimEnd('/'));
            Kits.Add(new Kit
            {
                Game = game, Team = a.Team, SourceKit = a.SourceKit, Folder27 = Folder(a.SourceFolder27),
                TargetPrefix = a.TargetPrefix.TrimEnd('/') + "/", Variant = a.TargetVariant, Added = DateTime.Now,
            });
        }
    }

    /// <summary>The recorded kit for this uniform whose assets are still in the game, or null.</summary>
    public Kit? Present(string game, NewUniformAddition addition, Func<string, bool> folderHasAssets) =>
        Kits.Where(k => Key(k.Game) == Key(game) && k.Team == addition.Team && k.SourceKit == addition.SourceKit
                && Folder(k.Folder27) == Folder(addition.SourceFolder27))
            .FirstOrDefault(k => folderHasAssets(k.TargetPrefix));

    /// <summary>Removes uniforms already in the game from a batch; returns what was skipped.</summary>
    public List<(NewUniformAddition Addition, Kit Kit)> SkipPresent(string game, List<NewUniformAddition> additions, Func<string, bool> folderHasAssets)
    {
        var skipped = additions.Select(a => (a, k: Present(game, a, folderHasAssets))).Where(x => x.k is not null)
            .Select(x => (x.a, x.k!)).ToList();
        foreach (var (a, _) in skipped) additions.Remove(a);
        return skipped;
    }

    /// <summary>True when any EBX or RES lives under the folder (a cloned kit is present).</summary>
    public static bool FolderHasAssets(BundleIndex index, string prefix)
    {
        string p = prefix.TrimEnd('/') + "/";
        return index.EbxByName.Keys.Any(n => n.StartsWith(p, StringComparison.Ordinal))
            || index.ResByName.Keys.Any(n => n.StartsWith(p, StringComparison.Ordinal));
    }
}
