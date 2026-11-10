// UniformRestore --target <game> --mod <name|path> [--ref <stock game>] [--filter substring]... [--apply] [--restore-after]
// Reads the stock bytes for every slot named by the mod from the reference game and writes them into every target copy
// with one batch ChunkReplace pass. Rosters, portraits, scorebugs and non-mod textures are untouched. Dry run by default.
using System.Security.Cryptography;
using Suite.Core;
using Suite.Core.Frostbite;

string Value(string key)
{
    int i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
}
if (args.Length == 0 || !args.Contains("--target") || !args.Contains("--mod"))
{
    Console.Error.WriteLine("usage: UniformRestore --target <game> --mod <name|path> [--ref <stock game>] [--filter substring]... [--apply] [--restore-after]");
    return 2;
}
var target = Path.GetFullPath(Value("--target")).TrimEnd(Path.DirectorySeparatorChar);
var reference = Path.GetFullPath(string.IsNullOrWhiteSpace(Value("--ref")) ? SuiteSettings.Load().Nhl24ReadView : Value("--ref")).TrimEnd(Path.DirectorySeparatorChar);
var settings = SuiteSettings.Load();
var modPath = Value("--mod").EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Value("--mod") : ModFile.PathFor(Value("--mod"));
var mod = ModFile.Load(modPath) ?? throw new FileNotFoundException("mod not found: " + modPath);
var filters = args.SkipWhile(a => a != "--filter").Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
var replacements = mod.Replacements.GroupBy(r => r.Slot).Select(g => g.Last())
    .Select(r => r.Layers is null ? r : r with { Layers = r.Layers.Where(kv => !kv.Value.StartsWith("coeff:", StringComparison.Ordinal)).ToDictionary() })
    .Where(r => r.Source.Length > 0 || r.Layers is { Count: > 0 })
    .Where(r => filters.Count == 0 || filters.Any(f => r.Slot.Contains(f, StringComparison.OrdinalIgnoreCase)))
    .ToList();
if (replacements.Count == 0)
{
    Console.Error.WriteLine("The mod has no restorable texture slots" + (filters.Count > 0 ? " matching: " + string.Join(", ", filters) : ""));
    return 1;
}

Oodle.Ensure(settings);
Console.Error.WriteLine($"Reading stock resources from {reference}...");
var stockIndex = BundleIndex.Build(reference, s => Console.Error.WriteLine("  " + s));
Console.Error.WriteLine($"Reading {Path.GetFileName(target)}...");
var targetIndex = BundleIndex.Build(target, s => Console.Error.WriteLine("  " + s));

var jobs = new List<(string Name, Guid Chunk, byte[] Data)>();
foreach (var r in replacements)
{
    var stockSlots = TextureSlots.ReadAll(reference, stockIndex, r.Slot);
    var stockByChunk = stockSlots.ToDictionary(s => s.Chunk);
    foreach (var slot in TextureSlots.ReadAll(target, targetIndex, r.Slot))
    {
        if (!stockByChunk.TryGetValue(slot.Chunk, out var stock))
            throw new InvalidOperationException($"{r.Slot}: chunk {slot.Chunk} exists in the target but not the stock reference.");
        if (slot.Res.PayloadSize != stock.Res.PayloadSize)
            throw new InvalidOperationException($"{r.Slot}: target payload is {slot.Res.PayloadSize} bytes, stock is {stock.Res.PayloadSize}.");
        jobs.Add((r.Slot, slot.Chunk, stock.Stock));
    }
}
Console.WriteLine($"{replacements.Count} texture slot(s), {jobs.Count} chunk copy/copies to restore");
foreach (var g in jobs.GroupBy(j => j.Name).OrderBy(g => g.Key).Take(10))
    Console.WriteLine("  " + g.Key);
if (jobs.GroupBy(j => j.Name).Count() > 10) Console.WriteLine("  ...");
if (!WriteGate.Allowed(args))
{
    Console.WriteLine("dry run (pass --apply with --target to write)");
    return 0;
}

var problems = ApplyTarget.Problems(target);
if (problems.Count > 0)
{
    Console.Error.WriteLine("Restore refused:\n" + string.Join("\n", problems));
    return 1;
}
var before = jobs.ToDictionary(j => (j.Name, j.Chunk),
    j => ChunkReplace.ReadAll(target, targetIndex, j.Chunk)
         .Select(r => (r.Where, r.LogicalOffset, Hash: SHA1.HashData(r.Raw))).ToList());
var backup = ApplyTarget.Backup(target);
try
{
    foreach (var line in ChunkReplace.ReplaceMany(target, targetIndex, jobs.Select(j => (j.Chunk, j.Data)).ToList()))
        SuiteLog.Write("uniform restore: " + line);
    var afterIndex = BundleIndex.Build(target);
    foreach (var (name, chunk, data) in jobs)
        foreach (var rb in ChunkReplace.ReadAll(target, afterIndex, chunk))
            if (rb.LogicalOffset + (long)rb.Raw.Length > data.Length || !rb.Raw.AsSpan().SequenceEqual(data.AsSpan((int)rb.LogicalOffset, rb.Raw.Length)))
                throw new InvalidOperationException($"Read-back differs: {name} via {rb.Where}.");
    Console.WriteLine($"{jobs.Select(j => j.Name).Distinct().Count()} texture(s) restored from stock ({jobs.Count} chunk copies) and read back identical.");
    Console.WriteLine("backup: " + backup);

    if (args.Contains("--restore-after"))
    {
        ApplyTarget.RestoreFromBackup(target, backup);
        var restoredIndex = BundleIndex.Build(target);
        foreach (var (name, chunk, _) in jobs)
            foreach (var rb in ChunkReplace.ReadAll(target, restoredIndex, chunk))
            {
                var want = before[(name, chunk)].First(b => b.Where == rb.Where && b.LogicalOffset == rb.LogicalOffset).Hash;
                if (!SHA1.HashData(rb.Raw).AsSpan().SequenceEqual(want)) throw new InvalidOperationException($"Restore read-back differs: {name} via {rb.Where}.");
            }
        var left = ApplyTarget.Compare(target, backup);
        if (left.Count > 0) throw new InvalidOperationException("Restore comparison failed: " + string.Join("; ", left.Take(5)));
        Console.WriteLine("restore-after: the target is byte-identical to the backup again");
    }
    return 0;
}
catch (Exception ex)
{
    SuiteLog.Write("uniform restore failed, restoring: " + ex);
    ApplyTarget.RestoreFromBackup(target, backup);
    Console.Error.WriteLine($"Restore failed ({ex.Message}); the target was put back from the backup.");
    return 1;
}
