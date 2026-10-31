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

/// <summary>Hand-editable settings (settings.json). Every path can be changed in the app or in the file.</summary>
public sealed class SuiteSettings
{
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
        return changed;
    }

    /// <summary>Folder with package.mft and Data\ (NHL24PC: game\; extracted dump: the folder itself).</summary>
    [JsonIgnore] public string Nhl24BaseDir => File.Exists(Path.Combine(Nhl24GameDir, "game", "package.mft")) ? Path.Combine(Nhl24GameDir, "game") : Nhl24GameDir;
    /// <summary>Folder whose Patch\ holds the update (NHL24PC: game-UPDATE\; extracted dump: the folder itself).</summary>
    [JsonIgnore] public string Nhl24UpdateDir => Directory.Exists(Path.Combine(Nhl24GameDir, "game-UPDATE", "Patch")) ? Path.Combine(Nhl24GameDir, "game-UPDATE") : Nhl24GameDir;

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
