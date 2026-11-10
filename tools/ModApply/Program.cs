// ModApply <game> <mod name|path to json> [slot-name filter]... [--chunk-size N]
// Applies a saved mod with the app's writer (backup, all chunk copies, read-back). The writer
// itself refuses to run while the game is open and verifies the target folder first. --chunk-size
// applies the replacements in small verified batches, which isolates read-back mismatches caused
// by one bad slot without leaving the whole mod unapplied.
using System.Security.Cryptography;
using System.Text;
using Suite.Core;
using Suite.Core.Frostbite;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ModApply <game> <mod name|path to json> [slot-name filter]... [--chunk-size N]");
    return 2;
}
var game = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar);
Oodle.Ensure(SuiteSettings.Load());
var modPath = args[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? args[1] : ModFile.PathFor(args[1]);
var mod = ModFile.Load(modPath) ?? throw new FileNotFoundException("mod not found: " + modPath);
var rest = args.Skip(2).ToList();
int chunkSize = 0;
int skip = 0;
int ci = rest.IndexOf("--chunk-size");
if (ci >= 0)
{
    if (ci + 1 >= rest.Count || !int.TryParse(rest[ci + 1], out chunkSize) || chunkSize <= 0)
    {
        Console.Error.WriteLine("--chunk-size needs a positive integer");
        return 2;
    }
    rest.RemoveRange(ci, 2);
}
int si = rest.IndexOf("--skip");
if (si >= 0)
{
    if (si + 1 >= rest.Count || !int.TryParse(rest[si + 1], out skip) || skip < 0)
    {
        Console.Error.WriteLine("--skip needs a non-negative integer");
        return 2;
    }
    rest.RemoveRange(si, 2);
}
var filters = rest.SelectMany(a => a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
var repls = mod.Replacements
    .Where(r => filters.Count == 0 || filters.Any(f => r.Slot.Contains(f, StringComparison.OrdinalIgnoreCase)))
    .ToList();
Console.WriteLine($"{repls.Count} replacement(s) from '{mod.Name}'" + (filters.Count > 0 ? $" (filter: {string.Join(", ", filters)})" : ""));
if (repls.Count == 0) return 1;
// Same rules as the app's apply (Suite.Convert27/ModApply.cs): matcher's "coeff:..." layers stay stock,
// and every source image must exist before anything is written.
repls = repls.Select(r => r.Layers is null ? r : r with { Layers = r.Layers.Where(kv => !kv.Value.StartsWith("coeff:", StringComparison.Ordinal)).ToDictionary() })
             .Where(r => r.Source.Length > 0 || r.Layers is { Count: > 0 }).Skip(skip).ToList();
if (skip > 0) Console.WriteLine($"Skipped the first {skip} applicable replacement(s).");
var missing = repls.SelectMany(r => r.Layers is { Count: > 0 } ? r.Layers.Values.AsEnumerable() : [r.Source])
    .Where(p => !File.Exists(p)).Distinct().ToList();
if (missing.Count > 0)
{
    Console.Error.WriteLine($"Apply refused: {missing.Count} source file(s) are missing:");
    foreach (var p in missing.Take(10)) Console.Error.WriteLine("  " + p);
    return 1;
}

void RecordBackup(string backup)
{
    var undoFile = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "backups")),
        "texture-applies-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(game.ToLowerInvariant())))[..12] + ".txt");
    File.AppendAllText(undoFile, backup + "\n");
}

if (chunkSize > 0)
{
    for (int at = 0, n = 1; at < repls.Count; at += chunkSize, n++)
    {
        var group = repls.Skip(at).Take(chunkSize).ToList();
        Console.WriteLine($"Applying chunk {n}/{(repls.Count + chunkSize - 1) / chunkSize} ({group.Count} replacement(s))...");
        var (ok, message, backup) = TextureSlots.Apply(game, group, Console.WriteLine);
        Console.WriteLine(message);
        if (!ok) return 1;
        if (backup is not null)
        {
            RecordBackup(backup);
            Console.WriteLine("backup: " + backup);
        }
    }
    return 0;
}

var (singleOk, singleMessage, singleBackup) = TextureSlots.Apply(game, repls, Console.WriteLine);
Console.WriteLine(singleMessage);
if (singleOk && singleBackup is not null)
{
    RecordBackup(singleBackup);
    Console.WriteLine("backup: " + singleBackup);
}
return singleOk ? 0 : 1;
