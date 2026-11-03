using System.IO;
using System.Text;
using System.Text.Json;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// The mod library on disk (%APPDATA%\CenterIceModdingSuite\mods\*.json) and apply / restore. Every write to a game
/// goes through here, and only to <see cref="SuiteSettings.ApplyTargetDir"/> after <see cref="ApplyTarget.Problems"/>
/// finds nothing wrong. Writes use the suite's own writer (no NHL24Kit Worker): backup before, read-back after, and
/// Restore undoes the last apply from its backup.
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

    /// <summary>
    /// Every replacement must point at a source image that still exists (or be a generated coeff). Checked before an
    /// apply so a moved or renamed file is reported up front instead of failing halfway through a write.
    /// </summary>
    public static List<string> SourceProblems(IEnumerable<ModFile> mods)
    {
        var problems = new List<string>();
        foreach (var mod in mods)
        foreach (var l in mod.Legacy ?? [])
            if (!File.Exists(l.Source)) problems.Add($"{mod.Name}: {l.Name} has no source file ({l.Source})");
        foreach (var mod in mods)
        foreach (var r in mod.Replacements)
        {
            var slot = r.Slot.Split('/')[^1];
            if (r.Layers is { Count: > 0 })
            {
                foreach (var (layer, source) in r.Layers)
                {
                    if (source.StartsWith("coeff:", StringComparison.Ordinal)) continue;
                    if (!File.Exists(source)) problems.Add($"{mod.Name}: layer {layer} of {slot} has no source file ({source})");
                }
                continue;
            }
            if (r.Source.Length == 0) { problems.Add($"{mod.Name}: {slot} is an array entry with no layers"); continue; }
            if (!File.Exists(r.Source)) problems.Add($"{mod.Name}: {slot} has no source file ({r.Source})");
        }
        return problems;
    }

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
        return ApplyTarget.Problems(s.ApplyTargetDir);
    }

    /// <summary>
    /// Writes the whole mod list on top of the game as it is now (rosters, portraits, center ice and other suite writes
    /// stay), with the suite's own writer: every reference of each texture, full mip chains, PS4 tiling, array layers one
    /// by one, backup first and read-back after (<see cref="Suite.Core.Frostbite.TextureSlots"/>).
    /// </summary>
    public static Task<(bool Ok, string Message)> ApplyAsync(SuiteSettings s, Action<string>? progress = null, CancellationToken ct = default)
    {
        var mods = LoadMods();
        if (TotalReplacements(mods) == 0) return Task.FromResult((false, "The mod list is empty."));
        var missing = SourceProblems(mods);
        if (missing.Count > 0)
            return Task.FromResult((false, $"Apply refused: {missing.Count} source file(s) are missing.\n" +
                string.Join("\n", missing.Take(12)) + (missing.Count > 12 ? $"\n… and {missing.Count - 12} more" : "")));
        WritePlan(mods);   // kept on disk as a record of what was applied
        return ApplyReplacementsAsync(s, mods.SelectMany(m => m.Replacements).ToList(), progress, ct);
    }

    public static Task<(bool Ok, string Message)> ApplyPlanAsync(SuiteSettings s, string planPath, Action<string>? progress = null, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(planPath));
        var list = JsonSerializer.Deserialize<List<TextureReplacement>>(doc.RootElement.GetProperty("Replacements").GetRawText()) ?? [];
        return ApplyReplacementsAsync(s, list, progress, ct);
    }

    static async Task<(bool Ok, string Message)> ApplyReplacementsAsync(SuiteSettings s, List<TextureReplacement> list, Action<string>? progress, CancellationToken ct)
    {
        var problems = Problems(s);
        if (problems.Count > 0) return (false, "Apply refused:\n" + string.Join("\n", problems));
        var target = Full(s.ApplyTargetDir);
        // Coeff layers generated by the matcher ("coeff:...") stay stock: the base coeff rule.
        list = list.Select(r => r.Layers is null ? r : r with { Layers = r.Layers.Where(kv => !kv.Value.StartsWith("coeff:", StringComparison.Ordinal)).ToDictionary() })
                   .Where(r => r.Source.Length > 0 || r.Layers is { Count: > 0 }).ToList();
        var (ok, message, backup) = await Task.Run(() => Suite.Core.Frostbite.TextureSlots.Apply(target, list, progress), ct);
        if (ok && backup is not null) File.AppendAllText(UndoFile(target), backup + "\n");
        return (ok, message);
    }

    /// <summary>Undoes the last texture apply: puts the game back to the backup taken right before it, and verifies it.</summary>
    public static Task<(bool Ok, string Message)> RestoreAsync(SuiteSettings s, Action<string>? progress = null, CancellationToken ct = default)
    {
        var problems = Problems(s);
        if (problems.Count > 0) return Task.FromResult((false, "Restore refused:\n" + string.Join("\n", problems)));
        if (ApplyTarget.RunningGame() is { } running) return Task.FromResult((false, running + " is running. Close the game first."));
        var target = Full(s.ApplyTargetDir);
        var undo = File.Exists(UndoFile(target)) ? File.ReadAllLines(UndoFile(target)).Where(l => l.Length > 0).ToList() : [];
        if (undo.Count == 0 || !Directory.Exists(undo[^1])) return Task.FromResult((false, "Nothing to undo: no texture apply recorded for this game."));
        return Task.Run(() =>
        {
            progress?.Invoke("Restoring the backup taken before the last apply...");
            ApplyTarget.RestoreFromBackup(target, undo[^1]);
            var left = ApplyTarget.Compare(target, undo[^1]);
            if (left.Count > 0) return (false, "Restore incomplete: " + string.Join("; ", left.Take(10)));
            File.WriteAllLines(UndoFile(target), undo.Take(undo.Count - 1));
            return (true, $"Last texture apply undone; the game matches {Path.GetFileName(undo[^1])}. Anything written after that apply was undone too.");
        }, ct);
    }

    static string UndoFile(string target) =>
        Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "backups")),
            "texture-applies-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(target.ToLowerInvariant())))[..12] + ".txt");

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
