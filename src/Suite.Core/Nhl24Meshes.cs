namespace Suite.Core;

/// <summary>
/// The NHL 24 skater geometry the viewer shows: highest LOD of each worn piece, exported as GLB by
/// tools\MeshExport (stopgap: FMT reads the PS4 containers, the vertex/index decode is ours).
/// </summary>
public static class Nhl24Meshes
{
    const string P = "content/character/skater/";

    /// <summary>EBX asset → viewer part. jersey/pant/sock take the matched textures; the rest use gear/skin materials.</summary>
    public static readonly (string Ebx, string Part)[] Skater =
    [
        ($"{P}player/jersey/adidas/components/player_jersey_adidas_notuck_large_lod0_mesh", "jersey"),
        ($"{P}player/pant/common/components/player_pant_common_tightlong_lod0_mesh", "pant"),
        ($"{P}player/sock/common/components/player_sock_common_notuck_mesh", "sock"),
        ($"{P}player/glove/common/components/player_glove_common_left_mesh", "glove"),
        ($"{P}player/glove/common/components/player_glove_common_right_mesh", "glove"),
        ($"{P}player/skate/bauer/vapor/components/player_skate_bauer_vapor_hyperlite_notuck_mesh", "skate"),
        ($"{P}player/helmet/bauer/reakt/components/player_helmet_bauer_reakt_150_mesh", "helmet"),
        ($"{P}shared/hands/common/components/hands_both_mesh", "hands"),
        ($"{P}shared/head/generic/male_60000/components/head_male_60000_mesh", "head"),
    ];

    public static string Dir => SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "meshes", "nhl24"));
    public static string HostDir => Path.Combine(SuitePaths.Local, "fmthost");
    public static string FileFor(string ebx) => Path.Combine(Dir, ebx[(ebx.LastIndexOf('/') + 1)..] + ".glb");

    public static IEnumerable<(string File, string Part)> Available() =>
        Skater.Select(m => (FileFor(m.Ebx), m.Part)).Where(m => File.Exists(m.Item1));

    /// <summary>Exports any missing piece. Returns a reason when extraction is not possible, else null.</summary>
    public static async Task<string?> EnsureAsync(SuiteSettings s, Action<string>? progress = null, CancellationToken ct = default)
    {
        var missing = Skater.Where(m => !File.Exists(FileFor(m.Ebx))).Select(m => m.Ebx).ToList();
        if (missing.Count == 0) return null;
        var exe = Path.Combine(HostDir, "MeshExport.exe");
        if (!File.Exists(exe)) return $"mesh exporter not installed ({exe})";
        // FMT only opens a game folder that carries an explicit modify ACL; the Worker's game link has one.
        Nhl24ReadView.Ensure(s);
        var link = Path.Combine(s.WorkerRuntimeDir, "gamelinks", Path.GetFileName(s.Nhl24ReadView.TrimEnd('\\')));
        if (!Directory.Exists(link)) return $"no FMT game link at {link} (run any NHL 24 index build first)";
        progress?.Invoke($"Extracting {missing.Count} NHL 24 skater meshes...");
        var r = await ProcessRunner.RunAsync(exe, [link, Dir, .. missing], HostDir, line => SuiteLog.Write("mesh " + line), ct);
        var still = Skater.Count(m => !File.Exists(FileFor(m.Ebx)));
        return still == 0 ? null : $"{still} meshes not extracted (exit {r.ExitCode}); see suite.log";
    }
}
