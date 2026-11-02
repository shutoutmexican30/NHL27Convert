namespace Suite.Core.Frostbite;

/// <summary>
/// Applies whole legacy files (roster databases) to the apply target with the suite's own writer
/// (<see cref="LegacyFiles"/>): guard, backup (TOCs + CAS lengths), write, read every file back; on any failure the game
/// is put back from that backup. Only <see cref="SuiteSettings.ApplyTargetDir"/> is ever written.
/// </summary>
public static class LegacyApply
{
    public static (bool Ok, string Message, string? Backup) Apply(SuiteSettings s, IEnumerable<LegacyReplacement> files, Action<string>? progress = null)
    {
        var list = files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
        if (list.Count == 0) return (true, "no legacy files", null);
        var problems = ApplyTarget.Problems(s.ApplyTargetDir);
        if (problems.Count > 0) return (false, "Apply refused:\n" + string.Join("\n", problems), null);
        var missing = list.Where(f => !File.Exists(f.Source)).Select(f => f.Source).ToList();
        if (missing.Count > 0) return (false, "Missing source file(s): " + string.Join(", ", missing), null);
        var target = Path.GetFullPath(s.ApplyTargetDir).TrimEnd(Path.DirectorySeparatorChar);
        var bytes = list.ToDictionary(f => f.Name, f => File.ReadAllBytes(f.Source));

        progress?.Invoke("Backing up TOCs and CAS lengths...");
        var backup = ApplyTarget.Backup(target);
        try
        {
            progress?.Invoke($"Writing {list.Count} legacy file(s)...");
            foreach (var line in new LegacyFiles(target, s).Replace(bytes)) SuiteLog.Write("legacy write: " + line);
            progress?.Invoke("Reading them back...");
            var check = new LegacyFiles(target, s);
            var wrong = bytes.Where(kv => !check.Read(kv.Key).AsSpan().SequenceEqual(kv.Value)).Select(kv => kv.Key).ToList();
            if (wrong.Count > 0) throw new InvalidOperationException("Read-back differs: " + string.Join(", ", wrong));
            return (true, $"{list.Count} legacy file(s) written and read back identical. Backup: {backup}", backup);
        }
        catch (Exception ex)
        {
            SuiteLog.Write("legacy apply failed, restoring: " + ex);
            ApplyTarget.RestoreFromBackup(target, backup);
            var left = ApplyTarget.Compare(target, backup);
            return (false, $"Apply failed ({ex.Message}); the game was put back from the backup" +
                (left.Count == 0 ? " and verified." : ": " + string.Join("; ", left.Take(5))), backup);
        }
    }
}
