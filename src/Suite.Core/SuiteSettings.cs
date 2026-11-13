using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suite.Core;

/// <summary>Where the suite keeps its own files. Nothing here is inside a game folder.</summary>
public static class SuitePaths
{
    public const string AppName = "CenterIceModdingSuite";

    public static string Roaming => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName));
    public static string Local => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));

    public static string SettingsFile => Path.Combine(Roaming, "settings.json");
    public static string Projects => Ensure(Path.Combine(Roaming, "projects"));
    public static string Mods => Ensure(Path.Combine(Roaming, "mods"));
    public static string Nhl27Index => Path.Combine(Local, "nhl27_index.json");
    public static string Nhl24Index => Path.Combine(Local, "nhl24_textures.json");
    public static string Nhl27Images => Ensure(Path.Combine(Local, "nhl27png"));
    public static string Nhl24Refs => Ensure(Path.Combine(Local, "nhl24refs"));
    public static string Nhl24Images => Ensure(Path.Combine(Local, "nhl24png"));
    public static string DroppedImages => Ensure(Path.Combine(Roaming, "images"));
    public static string Log => Path.Combine(Local, "suite.log");

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Turns an asset path into a file-system-safe name.</summary>
    public static string Flatten(string assetPath) => assetPath.Replace('/', '_').Replace('\\', '_').Replace('#', '@');
}

/// <summary>One NHL 24 installation. Selecting a profile always switches its read source and apply target together.</summary>
public sealed class GameBuildProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "NHL 24";
    public string GameDir { get; set; } = "";
    public string ApplyTargetDir { get; set; } = "";
}

/// <summary>Hand-editable settings (settings.json). Every path can be changed in the app or in the file.</summary>
public sealed class SuiteSettings
{
    /// <summary>Configured NHL 24 installations, shown in the main window's build selector.</summary>
    public List<GameBuildProfile> GameBuilds { get; set; } = [];
    public string ActiveGameBuildId { get; set; } = "";

    [JsonIgnore]
    public GameBuildProfile? ActiveGameBuild =>
        GameBuilds?.FirstOrDefault(b => string.Equals(b.Id, ActiveGameBuildId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Selects one configured build and synchronizes the legacy paths used by all readers and writers.</summary>
    public bool SelectGameBuild(string id)
    {
        var build = GameBuilds?.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
        if (build is null) return false;
        ActiveGameBuildId = build.Id;
        Nhl24GameDir = build.GameDir;
        ApplyTargetDir = build.ApplyTargetDir;
        return true;
    }

    /// <summary>Adds or selects a build. Folder pickers use this so a new source cannot retain another build's apply target.</summary>
    public GameBuildProfile? UseGameBuild(string gameDir, string applyTargetDir, string? name = null)
    {
        GameBuilds ??= [];
        gameDir = gameDir.Trim();
        applyTargetDir = applyTargetDir.Trim();
        if (gameDir.Length == 0)
        {
            ActiveGameBuildId = Nhl24GameDir = ApplyTargetDir = "";
            return null;
        }
        var build = GameBuilds.FirstOrDefault(b => SamePath(b.GameDir, gameDir));
        if (build is null)
        {
            var key = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(gameDir.ToLowerInvariant())))[..12].ToLowerInvariant();
            build = new GameBuildProfile { Id = "build-" + key, GameDir = gameDir };
            GameBuilds.Add(build);
        }
        if (!string.IsNullOrWhiteSpace(name)) build.Name = name.Trim();
        else if (string.IsNullOrWhiteSpace(build.Name) || build.Name == "NHL 24")
            build.Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(gameDir)) is { Length: > 0 } folder ? folder : "NHL 24";
        build.GameDir = gameDir;
        build.ApplyTargetDir = applyTargetDir;
        SelectGameBuild(build.Id);
        return build;
    }

    /// <summary>Migrates pre-profile settings once and keeps the active profile authoritative on later loads.</summary>
    public bool EnsureGameBuilds()
    {
        var before = JsonSerializer.Serialize(new { ActiveGameBuildId, Nhl24GameDir, ApplyTargetDir, Count = GameBuilds?.Count ?? -1 });
        GameBuilds ??= [];
        if (ActiveGameBuild is null)
        {
            var matching = GameBuilds.FirstOrDefault(b => SamePath(b.GameDir, Nhl24GameDir));
            if (matching is not null) SelectGameBuild(matching.Id);
            else if (!string.IsNullOrWhiteSpace(Nhl24GameDir)) UseGameBuild(Nhl24GameDir, ApplyTargetDir);
            else if (GameBuilds.FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.Id)) is { } first) SelectGameBuild(first.Id);
        }
        else SelectGameBuild(ActiveGameBuild.Id);
        var after = JsonSerializer.Serialize(new { ActiveGameBuildId, Nhl24GameDir, ApplyTargetDir, Count = GameBuilds.Count });
        return before != after;
    }

    static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(GameLayout.Normalize(a), GameLayout.Normalize(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase); }
    }

    // Paths start empty and are filled by AutoDetect() or the folder pickers; nothing is tied to one machine.
    public string Nhl27GameRoot { get; set; } = "";
    public string Nhl27ViewerDir { get; set; } = "";
    /// <summary>NHL 24 folder: either an NHL24PC build (game\ + game-UPDATE\) or one extracted game folder with package.mft.</summary>
    public string Nhl24GameDir { get; set; } = "";
    /// <summary>Read-only merged view of game\ + game-UPDATE\ that the stopgap Worker reads through (FMT writes its backups here, never into the game).</summary>
    public string Nhl24ReadView { get; set; } = "";
    /// <summary>Stopgap backend: a built NHL24Kit Worker runtime folder (with FrostbiteProfiles).</summary>
    public string WorkerRuntimeDir { get; set; } = "";
    /// <summary>Older NHL24Kit texture dumps that can be reused instead of re-dumping.</summary>
    public string LegacyRefsDir { get; set; } = "";
    /// <summary>
    /// The one folder Apply/Restore may write to: a single real game tree (package.mft + Data + Patch, no links).
    /// Empty = Apply is refused. See <see cref="ApplyTarget"/>.
    /// </summary>
    public string ApplyTargetDir { get; set; } = "";
    /// <summary>
    /// "Allow in-place writes to this game folder": game folders the user explicitly allowed the in-place database
    /// writers (Contracts) to write even though they are the original game. Empty by default. It lifts only the
    /// original-game guard for those folders; the running-game check, backup, read-back and restore still apply.
    /// </summary>
    public List<string> InPlaceWriteAllowedDirs { get; set; } = [];

    public bool InPlaceWriteAllowed(string dir) =>
        !string.IsNullOrWhiteSpace(dir) && (InPlaceWriteAllowedDirs ?? []).Any(d => !string.IsNullOrWhiteSpace(d) &&
            string.Equals(Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
    /// <summary>The name new uniforms show in the jersey picker.</summary>
    public string NewUniformJerseyName { get; set; } = "Fanatics 26-27";
    /// <summary>Last folder of replacement textures used in Uniform Studio.</summary>
    public string ReplacementDir { get; set; } = "";

    public string PythonExe { get; set; } = "python";
    public int Workers { get; set; } = Environment.ProcessorCount;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static SuiteSettings Load()
    {
        SuiteSettings s;
        try
        {
            s = File.Exists(SuitePaths.SettingsFile)
                ? JsonSerializer.Deserialize<SuiteSettings>(File.ReadAllText(SuitePaths.SettingsFile), Json) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            SuiteLog.Write("settings.json unreadable, using defaults: " + ex.Message);
            s = new();
        }
        if (s.AutoDetect()) s.Save();
        return s;
    }

    /// <summary>Fills empty or missing paths by looking in the usual places. Returns true when something changed.</summary>
    public bool AutoDetect()
    {
        bool changed = false;
        void Fill(Func<string> get, Action<string> set, Func<string, bool> valid, Func<IEnumerable<string>> candidates)
        {
            var cur = get();
            if (!string.IsNullOrEmpty(cur) && valid(cur)) return;
            var found = candidates().FirstOrDefault(c => { try { return valid(c); } catch { return false; } });
            if (found is not null && found != cur) { set(found); changed = true; }
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();

        Fill(() => Nhl24ReadView, v => Nhl24ReadView = v, v => v.Length > 0, () => [Path.Combine(SuitePaths.Local, "nhl24view")]);
        Fill(() => Nhl24GameDir, v => Nhl24GameDir = v, IsNhl24Dir, () => Nhl24Candidates(home, roots));
        Fill(() => Nhl27GameRoot, v => Nhl27GameRoot = v, v => Directory.Exists(Path.Combine(v, "Data", "Ps5")), () => Nhl27Candidates(roots));
        Fill(() => Nhl27ViewerDir, v => Nhl27ViewerDir = v, v => Directory.Exists(Path.Combine(v, "frostbite")),
            () => roots.SelectMany(r => new[] { Path.Combine(r, "Tools", "NHL27TextureViewer"), Path.Combine(r, "NHL27TextureViewer") }));
        Fill(() => WorkerRuntimeDir, v => WorkerRuntimeDir = v,
            v => File.Exists(Path.Combine(v, "NHL24Kit.Worker.exe")) && File.Exists(Path.Combine(v, "FrostbiteProfiles", "NHL24PS4Profile.json")),
            () => Directory.Exists(Path.Combine(local, "NHL24Kit", "runtimes"))
                ? Directory.GetDirectories(Path.Combine(local, "NHL24Kit", "runtimes")).OrderByDescending(Directory.GetLastWriteTimeUtc)
                : []);
        Fill(() => LegacyRefsDir, v => LegacyRefsDir = v, Directory.Exists, () => [Path.Combine(local, "NHL24Kit", "refs")]);
        changed |= EnsureGameBuilds();
        return changed;
    }

    /// <summary>Folder with package.mft and Data\ (NHL24PC: game\; extracted dump: the folder itself).</summary>
    [JsonIgnore] public string Nhl24BaseDir => File.Exists(Path.Combine(Nhl24GameDir, "game", "package.mft")) ? Path.Combine(Nhl24GameDir, "game") : Nhl24GameDir;
    /// <summary>Folder whose Patch\ holds the update (NHL24PC: game-UPDATE\; extracted dump: the folder itself).</summary>
    [JsonIgnore] public string Nhl24UpdateDir => Directory.Exists(Path.Combine(Nhl24GameDir, "game-UPDATE", "Patch")) ? Path.Combine(Nhl24GameDir, "game-UPDATE") : Nhl24BaseDir;

    public static bool IsNhl24Dir(string v) =>
        File.Exists(Path.Combine(v, "game", "package.mft")) || (File.Exists(Path.Combine(v, "package.mft")) && Directory.Exists(Path.Combine(v, "Data")));

    static IEnumerable<string> Nhl24Candidates(string home, List<string> roots)
    {
        foreach (var parent in new[] { Path.Combine(home, "Downloads"), Path.Combine(home, "Desktop"), Path.Combine(home, "Documents") }
                     .Concat(roots.SelectMany(r => new[] { r, Path.Combine(r, "Games"), Path.Combine(r, "Emulators") })))
        {
            if (!Directory.Exists(parent)) continue;
            foreach (var d in SafeDirs(parent).Where(d => Path.GetFileName(d).Contains("NHL24", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(d).StartsWith("CUSA3793", StringComparison.OrdinalIgnoreCase)))
            {
                yield return d;
                foreach (var sub in SafeDirs(d)) yield return sub;
            }
        }
    }

    static IEnumerable<string> Nhl27Candidates(List<string> roots)
    {
        foreach (var r in roots)
        foreach (var parent in new[] { Path.Combine(r, "Archives", "PS5"), Path.Combine(r, "Games", "PS5"), Path.Combine(r, "Games") })
        {
            if (!Directory.Exists(parent)) continue;
            foreach (var d in SafeDirs(parent).Where(d => Path.GetFileName(d).Contains("PPSA34063", StringComparison.OrdinalIgnoreCase)))
            foreach (var app0 in Directory.EnumerateDirectories(d, "*-app0", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }))
                yield return app0;
        }
    }

    static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return []; }
    }
    public void Save() => File.WriteAllText(SuitePaths.SettingsFile, JsonSerializer.Serialize(this, Json));
}

public static class SuiteLog
{
    static readonly object Gate = new();
    public static event Action<string>? Written;

    public static void Write(string line)
    {
        var text = $"{DateTime.Now:HH:mm:ss} {line}";
        lock (Gate)
        {
            try { File.AppendAllText(SuitePaths.Log, text + Environment.NewLine); } catch { }
        }
        Written?.Invoke(text);
    }
}
