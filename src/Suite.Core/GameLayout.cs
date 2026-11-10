namespace Suite.Core;

/// <summary>
/// Where the two NHL 24 layers live. A target may be one merged game folder (package.mft + Data\ + Patch\), an
/// NHL24PC-style install root (game\ + game-UPDATE\), just the base game\ folder, or the update layer itself.
/// The patch layer is always the folder named Patch: inside the game folder when merged, otherwise inside game-UPDATE.
/// </summary>
public static class GameLayout
{
    public static string Normalize(string dir) => Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>The folder holding package.mft and the base Data\ layer.</summary>
    public static string BaseDir(string dir)
    {
        dir = Normalize(dir);
        if (File.Exists(Path.Combine(dir, "package.mft")) && Directory.Exists(Path.Combine(dir, "Data"))) return dir;
        var game = Path.Combine(dir, "game");
        if (File.Exists(Path.Combine(game, "package.mft")) && Directory.Exists(Path.Combine(game, "Data"))) return game;
        // An update layer named game-UPDATE sits beside the base game folder; its TOC entries may still refer
        // to base-game Data CAS files, so the base for that layer is the sibling game\ folder.
        var parent = Directory.GetParent(dir)?.FullName;
        var sibling = parent is null ? "" : Path.Combine(parent, "game");
        if (File.Exists(Path.Combine(sibling, "package.mft")) && Directory.Exists(Path.Combine(sibling, "Data"))) return sibling;
        return dir;
    }

    /// <summary>The folder whose Patch\ directory is the update layer (the base folder itself when merged).</summary>
    public static string UpdateDir(string dir)
    {
        var game = BaseDir(dir);
        // A merged NHL24-PC profile uses the update inside game\Patch. The split NHL24PC layout uses game-UPDATE\Patch.
        if (Directory.Exists(Path.Combine(game, "Patch"))) return game;
        var parent = Directory.GetParent(game)?.FullName;
        var update = parent is null ? "" : Path.Combine(parent, "game-UPDATE");
        return Directory.Exists(Path.Combine(update, "Patch")) ? update : game;
    }

    public static string DataDir(string dir) => Path.Combine(BaseDir(dir), "Data");
    public static string PatchDir(string dir) => Path.Combine(UpdateDir(dir), "Patch");
    public static bool HasPatch(string dir) => Directory.Exists(PatchDir(dir));

    /// <summary>Writable/searchable roots, patch first: where a chunk exists in both layers, the patch copy wins.</summary>
    public static IEnumerable<string> Layers(string dir)
    {
        if (Directory.Exists(PatchDir(dir))) yield return PatchDir(dir);
        if (Directory.Exists(DataDir(dir))) yield return DataDir(dir);
    }
}
