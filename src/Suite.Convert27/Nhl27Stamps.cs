using System.Text.Json;

namespace Suite.Convert27;

/// <summary>
/// NHL 27's own stamp hotspot XML (number, name, captain and logo spots) per kit folder, read once from the NHL 27 dump
/// with the FMT NHL 27 reader (data\nhl27_stamp_hotspots.json). NHL 27 places sleeve numbers per kit (Ottawa's Fanatics
/// kits at 23.5% of the sheet, lower on the arm than NHL 24's 27.15%), and our kits paint NHL 27's sheets, so the number
/// spots follow NHL 27's kit when it has one.
/// </summary>
public static class Nhl27Stamps
{
    static Dictionary<string, string>? _byFolder;

    /// <summary>
    /// The kit's NHL 27 hotspot XML, or null when there is none or it does not parse: NHL 27 ships four malformed stamps
    /// (Washington ten and Carolina eleven have a mismatched end tag, Buffalo six and Nashville three a duplicate
    /// attribute); those kits keep their NHL 24 spots instead of failing the install.
    /// </summary>
    public static string? Xml(string folder27)
    {
        _byFolder ??= Load();
        return _byFolder.TryGetValue(folder27.TrimEnd('/'), out var xml) && Parses(xml) ? xml : null;
    }

    public static bool Parses(string xml)
    {
        try { new System.Xml.XmlDocument { XmlResolver = null }.LoadXml(xml); return true; }
        catch (System.Xml.XmlException) { return false; }
    }

    static Dictionary<string, string> Load()
    {
        foreach (string path in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "data", "nhl27_stamp_hotspots.json"),
            Path.Combine(SuitePaths.Roaming, "data", "nhl27_stamp_hotspots.json"),
        })
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        return [];
    }
}
