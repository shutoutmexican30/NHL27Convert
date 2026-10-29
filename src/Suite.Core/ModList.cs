using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suite.Core;

/// <summary>
/// One texture replacement. Same shape as the NHL24Kit plan format (Slot, Source, Layers, Alpha),
/// so a saved mod can also be applied with the stopgap Worker and compared byte for byte.
/// </summary>
public sealed record TextureReplacement(string Slot, string Source, Dictionary<int, string>? Layers = null, string? Alpha = null);

/// <summary>
/// A named, hand-editable mod: a JSON file in %APPDATA%\CenterIceModdingSuite\mods.
/// The mod list is what Apply writes; the kept list (game state after an apply) is separate and lives with the writer.
/// </summary>
public sealed class ModFile
{
    public int Format { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "textures";
    public string Comment { get; set; } = "";
    public DateTime Saved { get; set; }
    public List<TextureReplacement> Replacements { get; set; } = [];

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return Path.Combine(SuitePaths.Mods, (safe.Length == 0 ? "mod" : safe) + ".json");
    }

    public static ModFile? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<ModFile>(File.ReadAllText(path), Json) : null;

    public string Save()
    {
        Saved = DateTime.Now;
        var path = PathFor(Name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
        return path;
    }

    /// <summary>The Worker's plan.json shape ({"Replacements":[...]}).</summary>
    public string ToWorkerPlan() => JsonSerializer.Serialize(new { Replacements }, Json);
}
