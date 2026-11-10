using Suite.Core;
using Suite.Core.Frostbite;

namespace Suite.Rosters.Tests;

/// <summary>
/// Reads the configured NHL 24 game (ApplyTargetDir — a complete install or its game-UPDATE layer) through the
/// suite's own reader. Skips when no target is configured; read-only.
/// </summary>
public class MergedGameReadTests
{
    static string? Target()
    {
        var s = SuiteSettings.Load();
        foreach (var dir in new[] { s.ApplyTargetDir, s.Nhl24UpdateDir, s.Nhl24BaseDir })
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) &&
                (Directory.Exists(Path.Combine(dir, "Patch")) || Directory.Exists(Path.Combine(dir, "Data"))))
                return dir;
        return null;
    }

    [Fact]
    public void The_settings_resolve_a_game_plus_update_layout()
    {
        var s = SuiteSettings.Load();
        if (string.IsNullOrEmpty(s.Nhl24GameDir) || !Directory.Exists(s.Nhl24GameDir)) return;
        Assert.True(File.Exists(Path.Combine(s.Nhl24BaseDir, "package.mft")), "base game\\package.mft not found");
        if (Directory.Exists(Path.Combine(s.Nhl24GameDir, "game-UPDATE")))
            Assert.True(Directory.Exists(Path.Combine(s.Nhl24UpdateDir, "Patch")), "game-UPDATE exists but its Patch layer does not resolve");
    }

    [Fact]
    public void The_configured_game_tree_reads_through_the_suite_reader()
    {
        if (Target() is not { } game) return;
        var files = new LegacyFiles(game, SuiteSettings.Load());
        var meta = files.Read("gamemodes/database/t3db/nhl_game-meta.xml");
        Assert.True(meta.Length > 1000 && System.Text.Encoding.UTF8.GetString(meta).Contains("<database"),
            $"meta XML did not read ({meta.Length} bytes)");
        var db = files.Read("gamemodes/database/t3db/nhl_game.db");
        Assert.True(db.Length > 1_000_000 && db[0] == (byte)'D' && db[1] == (byte)'B', $"nhl_game.db did not read ({db.Length} bytes)");
        Assert.Equal(db.Length, (long)BitConverter.ToUInt32(db, 8)); // T3DB size field agrees
    }

    [Fact]
    public void The_scoreclock_legacy_files_resolve_on_the_configured_tree()
    {
        if (Target() is not { } game) return;
        var files = new LegacyFiles(game, SuiteSettings.Load());
        // Both movies resolve and carry real payloads (the shell scoreclock.gfx is ~16 KB, score_clock.gfx larger).
        var gfx = files.Read("nhl/ui/swf_assets/kettle/overlays/scoreclock/scoreclock.gfx");
        Assert.True(gfx.Length > 1000, $"scoreclock.gfx reads too small ({gfx.Length} bytes)");
        var movie = files.Read("nhl/ui/swf_assets/kettle/overlays/scoreclock/score_clock.gfx");
        Assert.True(movie.Length > 1000, $"score_clock.gfx reads too small ({movie.Length} bytes)");
    }
}
