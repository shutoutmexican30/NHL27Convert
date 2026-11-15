using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suite.Core;

/// <summary>
/// One texture replacement. Same shape as the NHL24Kit plan format (Slot, Source, Layers, Alpha),
/// so a saved mod can also be applied with the stopgap Worker and compared byte for byte.
/// </summary>
public sealed record TextureReplacement(string Slot, string Source, Dictionary<int, string>? Layers = null, string? Alpha = null);

/// <summary>
/// One whole legacy file replaced (a roster database, for example "gamemodes/database/t3db/nhl_game.db").
/// Same shape as the NHL24Kit plan's Legacy entries (Name, Source).
/// </summary>
public sealed record LegacyReplacement(string Name, string Source);

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
    /// <summary>Independent uniforms and new jersey rows. Never included in the ordinary replacement Apply path.</summary>
    public List<NewUniformAddition>? Additions { get; set; }
    /// <summary>Whole legacy files (roster databases from the roster sync). Null in texture mods.</summary>
    public List<LegacyReplacement>? Legacy { get; set; }

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
        BackupExisting(path);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
        return path;
    }

    /// <summary>
    /// Keeps the last 30 copies of a mod in mods\history before each overwrite, so a bad save
    /// (for example a partial add replacing the whole list) is never final.
    /// </summary>
    static void BackupExisting(string path)
    {
        if (!File.Exists(path)) return;
        var dir = Path.Combine(Path.GetDirectoryName(path)!, "history");
        Directory.CreateDirectory(dir);
        var stem = Path.GetFileNameWithoutExtension(path);
        var copy = Path.Combine(dir, $"{stem}.{File.GetLastWriteTime(path):yyyyMMdd-HHmmss}.json");
        try
        {
            if (!File.Exists(copy)) File.Copy(path, copy);
            foreach (var old in Directory.EnumerateFiles(dir, stem + ".*.json").OrderByDescending(f => f).Skip(30).ToList())
                File.Delete(old);
        }
        catch (Exception ex) { SuiteLog.Write("mod backup failed: " + ex.Message); }
    }

    /// <summary>The Worker's plan.json shape ({"Replacements":[...], "Legacy":[...]}).</summary>
    public string ToWorkerPlan() => JsonSerializer.Serialize(
        new Dictionary<string, object?> { ["Replacements"] = Replacements, ["Legacy"] = Legacy }, Json);
}
