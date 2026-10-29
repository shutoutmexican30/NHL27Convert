namespace Suite.Core;

public enum GameAccess { Missing, Read, ReadWrite }

public sealed record GameInfo(string Id, string Title, string Platform, string Path, GameAccess Access, string Status);

/// <summary>Finds the supported games and says what the suite can do with each.</summary>
public static class GameDetector
{
    public static IReadOnlyList<GameInfo> Detect(SuiteSettings s) => [DetectNhl27(s), DetectNhl24(s)];

    public static GameInfo DetectNhl27(SuiteSettings s)
    {
        var root = s.Nhl27GameRoot;
        if (!Directory.Exists(Path.Combine(root, "Data", "Ps5")))
            return new("nhl27", "NHL 27", "PS5", root, GameAccess.Missing, "Not found. Set the PPSA34063 app0 folder in Settings.");
        var index = AssetIndex.TryLoad(IndexStore.Nhl27) is { } ix ? $"asset index: {ix.Entries.Count:N0} textures" : "asset index not built yet";
        var viewer = Directory.Exists(Path.Combine(s.Nhl27ViewerDir, "frostbite")) ? "reader found" : "NHL27 texture reader missing";
        return new("nhl27", "NHL 27", "PS5", root, GameAccess.Read, $"Read only (source game): {viewer}, {index}.");
    }

    public static GameInfo DetectNhl24(SuiteSettings s)
    {
        var dir = s.Nhl24GameDir;
        var baseDir = Path.Combine(dir, "game");
        var update = Path.Combine(dir, "game-UPDATE");
        if (!File.Exists(Path.Combine(baseDir, "package.mft")))
            return new("nhl24", "NHL 24", "PS4", dir, GameAccess.Missing, "Not found. Set the NHL24PC folder (with game\\ and game-UPDATE\\) in Settings.");
        var patch = Directory.Exists(Path.Combine(update, "Patch")) ? "update 1.60 in game-UPDATE\\Patch" : "no update folder";
        return new("nhl24", "NHL 24", "PS4", dir, GameAccess.ReadWrite, $"Read/write (target game): base in game\\Data, {patch}.");
    }
}
