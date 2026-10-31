using System.IO;
using System.Text.Json;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// The mod library on disk (%APPDATA%\CenterIceModdingSuite\mods\*.json), the plan JSON the worker takes, and
/// the worker's apply / restore. Every write to the game goes through here — never through the read-only
/// <see cref="Worker.RunAsync"/> helpers.
/// </summary>
public static class ModApply
{
    /// <summary>
    /// Off until the apply target is fixed: the worker was pointed at the read view, whose Gen4b folders are links into
    /// the real game while its initfs/layout files are copies, so a write would split between the two trees.
    /// </summary>
    public const bool Enabled = false;
    public const string DisabledReason = "Disabled until the apply target is fixed (it must be the real, closed, backed-up game folder).";

    public static List<ModFile> LoadMods()
    {
        var dir = SuitePaths.Mods;
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.json")
            .Select(ModFile.Load)
            .OfType<ModFile>()
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static int TotalReplacements(IEnumerable<ModFile> mods) => mods.Sum(m => m.Replacements.Count);

    public static string WritePlan(IEnumerable<ModFile> mods)
    {
        var plan = new { Replacements = mods.SelectMany(m => m.Replacements).ToList() };
        var path = Path.Combine(SuitePaths.Local, "apply-plan.json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>Rebuilds from stock with the whole mod list. Returns (ok, message).</summary>
    public static Task<(bool Ok, string Message)> ApplyAsync(SuiteSettings s, Action<string>? progress = null, CancellationToken ct = default) =>
        RunAsync(s, "apply", LoadMods(), progress, ct);

    /// <summary>Restores the stock game and deletes the applied record.</summary>
    public static Task<(bool Ok, string Message)> RestoreAsync(SuiteSettings s, Action<string>? progress = null, CancellationToken ct = default) =>
        RunAsync(s, "restore", [], progress, ct);

    static async Task<(bool Ok, string Message)> RunAsync(SuiteSettings s, string command, List<ModFile> mods, Action<string>? progress, CancellationToken ct)
    {
        if (!Enabled) return (false, DisabledReason);
        if (!Worker.Available(s)) return (false, "The NHL24Kit worker is not installed (check Settings for the runtime path).");
        if (s.Nhl24GameDir.Length == 0 || !SuiteSettings.IsNhl24Dir(s.Nhl24GameDir))
            return (false, "Set the NHL 24 game folder in Settings first.");
        var args = new List<string> { command };
        var view = Nhl24ReadView.Ensure(s);
        args.Add(view);
        if (command == "apply")
        {
            if (TotalReplacements(mods) == 0) return (false, "The mod list is empty.");
            args.Add(WritePlan(mods));
        }
        try
        {
            var r = await Worker.RunRawAsync(s, args, progress, ct);
            var lines = r.StdOut.Split('\n').Select(l => l.TrimEnd('\r').TrimStart('\uFEFF')).ToList();
            var result = lines.LastOrDefault(l => l.StartsWith("RESULT ", StringComparison.Ordinal));
            var error = lines.FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal));
            if (r.ExitCode != 0 || error is not null)
                return (false, (error ?? result ?? r.StdErr.Trim()).Trim());
            return (true, result is null ? "done" : result["RESULT ".Length..]);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
