using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Scorebug;

/// <summary>
/// Writes a custom scorebug into the apply target. The game plays scoreclock.gfx; score_clock.gfx is the same overlay's
/// other movie, and both are replaced with the same movie (as NHL24Kit did). Both are legacy files in one shared chunk of
/// Patch\...\globals.toc (the swf_assets collector), stock 16,139 and 115,242 bytes, so the writer places the bigger
/// custom movie at the chunk's end (<see cref="LegacyFiles"/>). Guard, backup, write, read-back and put-back-on-failure
/// are <see cref="LegacyApply"/>'s; only <see cref="SuiteSettings.ApplyTargetDir"/> is written.
/// </summary>
public static class ScorebugApply
{
    public static readonly string[] GameFiles =
    [
        "nhl/ui/swf_assets/kettle/overlays/scoreclock/scoreclock.gfx",
        "nhl/ui/swf_assets/kettle/overlays/scoreclock/score_clock.gfx",
    ];

    /// <summary>The style's movie with the settings written into its block.</summary>
    public static byte[] Build(ScorebugSettings settings)
    {
        var movie = File.ReadAllBytes(settings.Style.MoviePath);
        return ScorebugMovie.WriteSettings(movie, settings.ToSettingsString());
    }

    public static (bool Ok, string Message, string? Backup) Apply(SuiteSettings s, ScorebugSettings settings, Action<string>? progress = null)
    {
        var movie = Build(settings);
        // Read back from the bytes about to be written: the block must say exactly what was asked.
        if (ScorebugMovie.ReadSettings(movie) != settings.ToSettingsString()) return (false, "The built movie's settings block does not read back.", null);
        var file = Path.Combine(ScorebugSettings.StoreDir, "score_clock.gfx");
        File.WriteAllBytes(file, movie);
        var (ok, message, backup) = LegacyApply.Apply(s, GameFiles.Select(n => new LegacyReplacement(n, file)), progress);
        if (ok) message = $"{settings.Style.Name} scorebug written ({movie.Length:N0} bytes, {settings.ToSettingsString().Length} settings characters) into both " +
                          $"scoreclock movies and read back identical. Backup: {backup}";
        return (ok, message, backup);
    }

    /// <summary>What the game has now: the style whose movie matches (settings aside) and its settings, or null for stock.</summary>
    public static (ScorebugStyle? Style, string? Settings, int Bytes) Read(string game, SuiteSettings s)
    {
        var bytes = new LegacyFiles(game, s).Read(GameFiles[0]);
        if (!ScorebugMovie.HasBlock(bytes)) return (null, null, bytes.Length);
        var body = ScorebugMovie.Unpack(bytes).Body.Length;
        var style = ScorebugStyle.All.FirstOrDefault(st => File.Exists(st.MoviePath) && ScorebugMovie.Unpack(File.ReadAllBytes(st.MoviePath)).Body.Length == body);
        return (style, ScorebugMovie.ReadSettings(bytes), bytes.Length);
    }
}
