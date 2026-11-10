using System.Buffers.Binary;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Suite.Convert27;

/// <summary>Selects the one home/away band of the season drawing shown beside the converted 3D uniform.</summary>
public static class UniformViewerReference
{
    public sealed record Selection(string TeamKey, string Role, int Kit, int Kits, string Png);

    /// <summary>The 32 current NHL teams. Arizona's NHL 24 folder now supplies Utah's uniforms.</summary>
    public static string? TeamKey(string folder) => folder.Trim().ToLowerInvariant() switch
    {
        "anaheim" => "Ducks", "arizona" or "utah" => "Mammoth", "boston" => "Bruins", "buffalo" => "Sabres",
        "calgary" => "Flames", "carolina" => "Hurricanes", "chicago" => "Blackhawks", "colorado" => "Avalanche",
        "columbus" => "BlueJackets", "dallas" => "Stars", "detroit" => "RedWings", "edmonton" => "Oilers",
        "florida" => "Panthers", "losangeles" => "Kings", "minnesota" => "Wild", "montreal" => "Canadiens",
        "nashville" => "Predators", "newjersey" => "Devils", "nyi" => "Islanders", "nyr" => "Rangers",
        "ottawa" => "Senators", "philadelphia" => "Flyers", "pittsburgh" => "Penguins", "sanjose" => "Sharks",
        "seattle" => "Kraken", "stlouis" => "Blues", "tampabay" => "Lightning", "toronto" => "MapleLeafs",
        "vancouver" => "Canucks", "vegas" => "GoldenKnights", "washington" => "Capitals", "winnipeg" => "Jets",
        _ => null,
    };

    /// <summary>Only explicit home/away folders are automatic. An alternate must never be compared with the wrong band.</summary>
    public static Selection? ForFolder(string folder24, int kits, string png)
    {
        if (kits < 1) throw new ArgumentOutOfRangeException(nameof(kits));
        var parts = folder24.Trim().Replace('\\', '/').Trim('/').Split('/');
        if (parts.Length < 4) return null;
        string? key = TeamKey(parts[^3]);
        string set = parts[^1];
        if (key is null || set is not ("home" or "away")) return null;
        string role = set == "home" ? "home" : "away";
        int kit = set == "home" ? 0 : kits - 1;
        return new Selection(key, role, kit, kits, png);
    }

    /// <summary>Uses the app's cached drawing, or the development contact-sheet copy when running from this checkout.</summary>
    public static Selection? FindLocal(string folder24, string season, params string[] additionalRoots)
    {
        var parts = folder24.Trim().Replace('\\', '/').Trim('/').Split('/');
        if (parts.Length < 4 || TeamKey(parts[^3]) is not { } key) return null;
        var roots = new List<string> { UniformReference.Dir(season) };
        roots.AddRange(additionalRoots);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Debugging", "uniform-reference", season);
            if (Directory.Exists(candidate)) roots.Add(candidate);
        }
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string png = Path.Combine(root, key + ".png");
            if (File.Exists(png)) return ForFolder(folder24, PngHeight(png), png);
        }
        return null;
    }

    /// <summary>Downloads only the selected team's reference drawing, not the whole season contact sheet.</summary>
    public static async Task<Selection?> FetchAsync(string folder24, string season, HttpClient http, CancellationToken ct = default)
    {
        var parts = folder24.Trim().Replace('\\', '/').Trim('/').Split('/');
        if (parts.Length < 4 || TeamKey(parts[^3]) is not { } key) return null;
        string dir = UniformReference.Dir(season);
        string indexFile = Path.Combine(dir, "index.html");
        if (!File.Exists(indexFile))
            File.WriteAllText(indexFile, await http.GetStringAsync($"{UniformReference.Site}/Mobile/{season}.html", ct));
        string html = await File.ReadAllTextAsync(indexFile, ct);
        var match = Regex.Match(html, $@"src=""(/Mobile/{Regex.Escape(key)}/{Regex.Escape(key)}(\d+)\.png)""", RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidDataException($"No {season} reference drawing is listed for {key}.");
        string png = Path.Combine(dir, key + ".png");
        if (!File.Exists(png))
            File.WriteAllBytes(png, await http.GetByteArrayAsync(UniformReference.Site + match.Groups[1].Value, ct));
        return ForFolder(folder24, PngHeight(png), png);
    }

    static int PngHeight(string path)
    {
        byte[] header = new byte[24];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.ReadExactly(header);
        if (!header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException($"Not a PNG reference drawing: {path}");
        int height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
        if (height <= 0 || height % UniformReference.Band != 0)
            throw new InvalidDataException($"Unexpected reference drawing height {height}: {path}");
        return height / UniformReference.Band;
    }
}
