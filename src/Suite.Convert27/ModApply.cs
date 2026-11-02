using System.IO;
using System.Text;
using System.Text.Json;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// The mod library on disk (%APPDATA%\CenterIceModdingSuite\mods\*.json), the plan JSON the worker takes, and
/// apply / restore. Every write to a game goes through here, and only to <see cref="SuiteSettings.ApplyTargetDir"/>
/// after <see cref="ApplyTarget.Problems"/> finds nothing wrong (never the read view, whose Gen4b folders are links).
/// Writes are done by the NHL24Kit worker until the suite's own writer lands; before each apply the suite takes its own
/// backup, and after a restore it verifies the game against the stock baseline (and redoes the restore if needed).
/// </summary>
public static class ModApply
{
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
        // Legacy files (roster databases) are not sent to the Worker: its writer cannot reach TOC chunks. They are written
        // by the suite's own Suite.Core.Frostbite.LegacyApply from the Teams & roster page.
        var plan = new { Replacements = mods.SelectMany(m => m.Replacements).ToList() };
        var path = Path.Combine(SuitePaths.Local, "apply-plan.json");
        File.WriteAllText(path, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>Why Apply/Restore would refuse right now (empty = ready).</summary>
    public static List<string> Problems(SuiteSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.ApplyTargetDir))
            return ["No apply target set. In Settings, set ApplyTargetDir to one complete game folder (package.mft + Data + Patch, no links)."];
        var p = ApplyTarget.Problems(s.ApplyTargetDir);
        if (!Worker.Available(s)) p.Add("The NHL24Kit worker is not installed (check Settings for the runtime path).");
        return p;
    }

    /// <summary>Rebuilds from stock with the whole mod list.</summary>
    public static Task<(bool Ok, string Message)> ApplyAsync(SuiteSettings s, Action<string>? progress = null, CancellationToken ct = default)
    {
        var mods = LoadMods();
        if (TotalReplacements(mods) == 0) return Task.FromResult((false, "The mod list is empty."));
        return ApplyPlanAsync(s, WritePlan(mods), progress, ct);
    }

    public static async Task<(bool Ok, string Message)> ApplyPlanAsync(SuiteSettings s, string planPath, Action<string>? progress = null, CancellationToken ct = default)
    {
        var problems = Problems(s);
        if (problems.Count > 0) return (false, "Apply refused:\n" + string.Join("\n", problems));
        var target = Full(s.ApplyTargetDir);
        progress?.Invoke("Backing up TOCs and CAS lengths...");
        var backup = ApplyTarget.Backup(target);
        if (Baseline(target) is null) File.WriteAllText(BaselineFile(target), backup); // first backup of a stock game = baseline
        PrepareFmt(target);
        return await RunWorkerAsync(s, ["apply", target, planPath], progress, ct);
    }

    /// <summary>Restores the stock game, then verifies it against the baseline backup and fixes any difference.</summary>
    public static async Task<(bool Ok, string Message)> RestoreAsync(SuiteSettings s, Action<string>? progress = null, CancellationToken ct = default)
    {
        var problems = Problems(s);
        if (problems.Count > 0) return (false, "Restore refused:\n" + string.Join("\n", problems));
        var target = Full(s.ApplyTargetDir);
        var (ok, message) = await RunWorkerAsync(s, ["restore", target], progress, ct);
        var baseline = Baseline(target);
        if (baseline is null) return (ok, message + " (no suite baseline to verify against)");
        progress?.Invoke("Verifying against the stock backup...");
        var diffs = ApplyTarget.Compare(target, baseline);
        if (diffs.Count == 0) return (ok, message + " · verified identical to the stock backup");
        SuiteLog.Write("restore left differences, restoring from the suite backup: " + string.Join("; ", diffs.Take(10)));
        ApplyTarget.RestoreFromBackup(target, baseline);
        var after = ApplyTarget.Compare(target, baseline);
        return after.Count == 0
            ? (true, $"{message} · {diffs.Count} difference(s) fixed from the suite backup; now identical to stock")
            : (false, "Restore incomplete: " + string.Join("; ", after.Take(10)));
    }

    static async Task<(bool Ok, string Message)> RunWorkerAsync(SuiteSettings s, List<string> args, Action<string>? progress, CancellationToken ct)
    {
        try
        {
            var r = await Worker.RunRawAsync(s, args, progress, ct);
            var lines = r.StdOut.Split('\n').Select(l => l.TrimEnd('\r').TrimStart('﻿')).ToList();
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

    /// <summary>
    /// FMT backs up every initfs (*.fmtbak) and copies the whole Patch folder (5+ GB) into __FMTBackup when it opens a
    /// game without a backup marker. Give it the markers so it writes nothing big into the target.
    /// </summary>
    static void PrepareFmt(string target)
    {
        foreach (var initfs in Directory.GetFiles(target, "initfs_*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".fmtbak")))
            if (!File.Exists(initfs + ".fmtbak")) File.Copy(initfs, initfs + ".fmtbak");
        Directory.CreateDirectory(Path.Combine(target, "__FMTBackup"));
        var marker = Path.Combine(target, "__FMTBackup", "backup.bin");
        if (!File.Exists(marker)) File.WriteAllText(marker, "2099-01-01 00:00:00");
    }

    static string Full(string dir) => Path.GetFullPath(dir).TrimEnd('\\');

    static string BaselineFile(string target) =>
        Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "backups")),
            "baseline-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(target.ToLowerInvariant())))[..12] + ".txt");

    /// <summary>The backup taken before the first apply to this target (the stock state), if any.</summary>
    public static string? Baseline(string target)
    {
        var f = BaselineFile(Full(target));
        return File.Exists(f) && Directory.Exists(File.ReadAllText(f).Trim()) ? File.ReadAllText(f).Trim() : null;
    }
}
