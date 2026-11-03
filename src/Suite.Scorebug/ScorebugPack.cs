using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suite.Scorebug;

/// <summary>
/// A .nhl24bug pack (NHL24Kit's format, kept as is so packs move between the two): a zip with pack.json
/// (Format, Name, Author, Created, Scorebug{Style, Values, TeamColours}, Mods) and an optional preview.png.
/// A pack without a Style is an ESPN+ pack (the first NHL24Kit packs had only that style).
/// Mods are other NHL24Kit mod sets with their files under files/; the suite reads and keeps them in the manifest but
/// does not apply them (it only writes the scorebug).
/// </summary>
public static class ScorebugPack
{
    public const string Extension = ".nhl24bug";
    public const int Format = 1;
    public const string PreviewEntry = "preview.png";

    public sealed class Manifest
    {
        public int Format { get; set; } = ScorebugPack.Format;
        public string? Name { get; set; }
        public string? Author { get; set; }
        public string? Created { get; set; }
        public ScorebugSettings.Stored? Scorebug { get; set; }
        public List<JsonElement> Mods { get; set; } = [];
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public sealed record Loaded(Manifest Manifest, ScorebugStyle Style, byte[]? Preview);

    public static Loaded Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("pack.json") ?? throw new InvalidDataException("This is not a scorebug pack (no pack.json).");
        Manifest manifest;
        using (var r = new StreamReader(entry.Open())) manifest = JsonSerializer.Deserialize<Manifest>(r.ReadToEnd(), Json) ?? throw new InvalidDataException("pack.json is empty.");
        if (manifest.Format > Format) throw new InvalidDataException($"This pack has format {manifest.Format}; the suite reads format {Format}.");
        if (string.IsNullOrWhiteSpace(manifest.Name)) manifest.Name = Path.GetFileNameWithoutExtension(path);
        byte[]? preview = null;
        if (zip.GetEntry(PreviewEntry) is { Length: > 0 and < 8 << 20 } p)
        {
            using var s = p.Open();
            var ms = new MemoryStream();
            s.CopyTo(ms);
            preview = ms.ToArray();
        }
        return new Loaded(manifest, ScorebugStyle.ById(manifest.Scorebug?.Style), preview);
    }

    public static void Write(string path, string name, string author, ScorebugSettings settings, byte[]? preview)
    {
        var manifest = new Manifest
        {
            Name = name, Author = author, Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Scorebug = settings.ToStored(),
        };
        var tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("pack.json").Open())) w.Write(JsonSerializer.Serialize(manifest, Json));
            if (preview is { Length: > 0 })
                using (var s = zip.CreateEntry(PreviewEntry, CompressionLevel.NoCompression).Open()) s.Write(preview);
        }
        File.Move(tmp, path, true);
    }
}
