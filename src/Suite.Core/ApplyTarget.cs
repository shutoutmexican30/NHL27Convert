using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Suite.Core;

/// <summary>
/// Guards every write to a game. A folder is only an apply target when it is one real, writable game tree
/// (package.mft + Data + Patch, every TOC and CAS inside it, no junctions or symbolic links anywhere) and the game
/// is not running. Before a write the TOCs and CAS lengths are backed up here, independently of the writer's own
/// state, so a restore can be verified and, if needed, redone from this backup.
/// </summary>
public static class ApplyTarget
{
    /// <summary>Process names of the game and the emulators that run it.</summary>
    static readonly string[] GameProcesses = ["shadps4", "nhl24", "nhl24_new", "nhl24pc"];

    public static List<string> Problems(string dir)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) { problems.Add($"Apply target '{dir}' does not exist."); return problems; }
        dir = Path.GetFullPath(dir).TrimEnd('\\');
        if (!File.Exists(Path.Combine(dir, "package.mft"))) problems.Add("No package.mft: this is not one complete game folder.");
        foreach (var sub in new[] { "Data", "Patch" })
            if (!Directory.Exists(Path.Combine(dir, sub))) problems.Add($"No {sub}\\ folder: base game and update must be in the same folder.");

        // No links: every folder and file must physically live inside this tree.
        var links = new List<string>();
        var stack = new Stack<string>([dir]);
        while (stack.Count > 0 && links.Count < 5)
        {
            var d = stack.Pop();
            var info = new DirectoryInfo(d);
            if (d != dir && (info.Attributes & FileAttributes.ReparsePoint) != 0) { links.Add(d); continue; }
            try
            {
                foreach (var s in info.EnumerateDirectories()) stack.Push(s.FullName);
                foreach (var f in info.EnumerateFiles())
                    if ((f.Attributes & FileAttributes.ReparsePoint) != 0) links.Add(f.FullName);
            }
            catch (Exception ex) { problems.Add($"Cannot read {d}: {ex.Message}"); }
        }
        if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0) links.Add(dir);
        foreach (var l in links) problems.Add($"Link found ({l}): the target must not point into another folder.");

        // Writable: no read-only TOC/CAS, and one TOC can be opened for writing (nothing is written).
        var files = Tocs(dir).Concat(Cas(dir)).ToList();
        if (files.Count == 0) problems.Add("No TOC or CAS files found.");
        var readOnly = files.FirstOrDefault(f => new FileInfo(f).IsReadOnly);
        if (readOnly is not null) problems.Add($"Read-only file: {readOnly}");
        var probe = Tocs(dir).FirstOrDefault();
        if (probe is not null)
        {
            try { using var _ = new FileStream(probe, FileMode.Open, FileAccess.ReadWrite, FileShare.Read); }
            catch (Exception ex) { problems.Add($"Cannot open {probe} for writing: {ex.Message}"); }
        }
        if (RunningGame() is { } p) problems.Add($"The game is running ({p}). Close it first.");
        if (Directory.Exists(dir + "-mods")) problems.Add($"{dir}-mods exists; shadPS4 would load it over the game. Remove it first.");
        return problems;
    }

    public static string? RunningGame()
    {
        foreach (var name in GameProcesses)
        {
            var p = Process.GetProcessesByName(name);
            if (p.Length > 0) return $"{name}.exe";
        }
        return null;
    }

    public static IEnumerable<string> Tocs(string dir) => Files(dir, "*.toc");
    public static IEnumerable<string> Cas(string dir) => Files(dir, "*.cas");

    static IEnumerable<string> Files(string dir, string pattern) =>
        new[] { "Data", "Patch" }.Select(s => Path.Combine(dir, s)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, pattern, new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }));

    public sealed record Snapshot(string Game, DateTime Taken, Dictionary<string, long> CasLengths, Dictionary<string, string> TocHashes);

    /// <summary>Copies every TOC and records every CAS length and TOC hash into a new backup folder. Returns the folder.</summary>
    public static string Backup(string dir)
    {
        dir = Path.GetFullPath(dir).TrimEnd('\\');
        var backup = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "backups", $"{DateTime.Now:yyyyMMdd-HHmmss}"));
        var tocs = Tocs(dir).ToList();
        var hashes = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(tocs, toc =>
        {
            var rel = Path.GetRelativePath(dir, toc);
            var dst = Path.Combine(backup, "toc", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(toc, dst, true);
            hashes[rel] = Sha256(toc);
        });
        var snap = new Snapshot(dir, DateTime.Now,
            Cas(dir).ToDictionary(f => Path.GetRelativePath(dir, f), f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(hashes, StringComparer.OrdinalIgnoreCase));
        File.WriteAllText(Path.Combine(backup, "snapshot.json"), JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
        SuiteLog.Write($"backup of {dir}: {tocs.Count} TOCs, {snap.CasLengths.Count} CAS lengths -> {backup}");
        return backup;
    }

    public static Snapshot LoadSnapshot(string backup) =>
        JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(Path.Combine(backup, "snapshot.json")))!;

    /// <summary>Differences between the game now and a backup (CAS lengths, TOC hashes, files added or missing).</summary>
    public static List<string> Compare(string dir, string backup)
    {
        dir = Path.GetFullPath(dir).TrimEnd('\\');
        var snap = LoadSnapshot(backup);
        var diffs = new List<string>();
        var cas = Cas(dir).ToDictionary(f => Path.GetRelativePath(dir, f), f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, len) in snap.CasLengths)
            if (!cas.TryGetValue(rel, out var now)) diffs.Add($"CAS missing: {rel}");
            else if (now != len) diffs.Add($"CAS length {rel}: {len} -> {now}");
        foreach (var rel in cas.Keys.Except(snap.CasLengths.Keys, StringComparer.OrdinalIgnoreCase)) diffs.Add($"CAS added: {rel}");
        var tocs = Tocs(dir).ToList();
        var now2 = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(tocs, t => now2[Path.GetRelativePath(dir, t)] = Sha256(t));
        foreach (var (rel, hash) in snap.TocHashes)
            if (!now2.TryGetValue(rel, out var h)) diffs.Add($"TOC missing: {rel}");
            else if (h != hash) diffs.Add($"TOC changed: {rel}");
        foreach (var rel in now2.Keys.Except(snap.TocHashes.Keys, StringComparer.OrdinalIgnoreCase)) diffs.Add($"TOC added: {rel}");
        return diffs;
    }

    /// <summary>Puts the game back to a backup: truncates appended CAS data and copies the TOCs back.</summary>
    public static void RestoreFromBackup(string dir, string backup)
    {
        dir = Path.GetFullPath(dir).TrimEnd('\\');
        var snap = LoadSnapshot(backup);
        foreach (var (rel, len) in snap.CasLengths)
        {
            var path = Path.Combine(dir, rel);
            var now = new FileInfo(path).Length;
            if (now < len) throw new InvalidOperationException($"{rel} is shorter than in the backup; it was changed outside the suite.");
            if (now > len) using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write)) fs.SetLength(len);
        }
        foreach (var rel in snap.TocHashes.Keys) File.Copy(Path.Combine(backup, "toc", rel), Path.Combine(dir, rel), true);
        SuiteLog.Write($"restored {dir} from {backup}");
    }

    static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
