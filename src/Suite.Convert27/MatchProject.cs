using System.Text.Json;
using System.Text.Json.Serialization;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>One NHL 24 folder (a team's uniform set) and the NHL 27 folder it takes art from.</summary>
public sealed class FolderMatch
{
    public string Folder24 { get; set; } = "";
    public string? Folder27 { get; set; }
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";
    public bool Manual { get; set; }
    public List<SlotMatch> Rows { get; set; } = [];
}

/// <summary>A saved matcher session (%APPDATA%\CenterIceModdingSuite\projects\*.json). Hand-editable.</summary>
public sealed class MatchProject
{
    public int Format { get; set; } = 1;
    public string Name { get; set; } = "Convert 27";
    public Dictionary<string, string> TeamMap { get; set; } = new(Matcher.DefaultTeamMap);
    public List<FolderMatch> Folders { get; set; } = [];

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        IgnoreReadOnlyProperties = true,
    };

    public static string DefaultPath => Path.Combine(SuitePaths.Projects, "convert27.json");

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
    }

    public static MatchProject Load(string? path = null)
    {
        path ??= DefaultPath;
        return File.Exists(path) ? JsonSerializer.Deserialize<MatchProject>(File.ReadAllText(path), Json) ?? new() : new();
    }

    public FolderMatch? Find(string folder24) => Folders.FirstOrDefault(f => f.Folder24 == folder24);

    /// <summary>
    /// The rows that change something, as a mod file. NHL 27 sources become the decoded PNGs
    /// (decode them first with <see cref="TextureImages.Nhl27Async"/>); dropped files are used as they are.
    /// </summary>
    public ModFile ToModFile(string name, IEnumerable<FolderMatch>? only = null)
    {
        var mod = new ModFile { Name = name, Kind = "textures", Comment = "Made by the Convert 27 matcher." };
        foreach (var folder in only ?? Folders)
        foreach (var row in folder.Rows)
        {
            if (row.IsArray)
            {
                var layers = row.Layers
                    .Select(l => (l.Layer, Path: l.FilePath ?? (l.Source27 is null ? null : TextureImages.Nhl27Png(l.Source27))))
                    .Where(l => l.Path is not null)
                    .ToDictionary(l => l.Layer, l => l.Path!);
                if (layers.Count > 0) mod.Replacements.Add(new TextureReplacement(row.Slot, "", layers, row.Alpha));
                continue;
            }
            var source = row.Kind switch
            {
                SourceKind.Nhl27 when row.Source27 is not null => TextureImages.Nhl27Png(row.Source27),
                SourceKind.File when row.FilePath is not null => row.FilePath,
                _ => null,
            };
            if (source is not null) mod.Replacements.Add(new TextureReplacement(row.Slot, source, null, row.Alpha));
        }
        return mod;
    }
}
