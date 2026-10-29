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
    public string Nhl27GameRoot { get; set; } = @"D:\Archives\PS5\PPSA34063\[DLPSGAME.COM]-PPSA34063\[DLPSGAME.COM]-PPSA34063\PPSA34063-app0";
    public string Nhl27ViewerDir { get; set; } = @"D:\Tools\NHL27TextureViewer";
    public string Nhl24GameDir { get; set; } = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
    /// <summary>Read-only merged view of game\ + game-UPDATE\ that the stopgap Worker reads through (FMT writes its backups here, never into the game).</summary>
    public string Nhl24ReadView { get; set; } = Path.Combine(SuitePaths.Local, "nhl24view");
    /// <summary>Stopgap backend: a built NHL24Kit Worker runtime folder (with FrostbiteProfiles).</summary>
    public string WorkerRuntimeDir { get; set; } = @"C:\Users\devin\AppData\Local\NHL24Kit\runtimes\b935118cf7c257434248dbe2";
    /// <summary>Older NHL24Kit texture dumps that can be reused instead of re-dumping.</summary>
    public string LegacyRefsDir { get; set; } = @"C:\Users\devin\AppData\Local\NHL24Kit\refs";
    public string PythonExe { get; set; } = "python";
    public int Workers { get; set; } = Environment.ProcessorCount;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static SuiteSettings Load()
    {
        try
        {
            if (File.Exists(SuitePaths.SettingsFile))
                return JsonSerializer.Deserialize<SuiteSettings>(File.ReadAllText(SuitePaths.SettingsFile), Json) ?? new();
        }
        catch (Exception ex)
        {
            SuiteLog.Write("settings.json unreadable, using defaults: " + ex.Message);
        }
        var s = new SuiteSettings();
        s.Save();
        return s;
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
