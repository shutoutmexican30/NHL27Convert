// PortraitWrite <dir-with-synced-dbs> <fresh-worker-dir> [--restore-check]
// Writes every portrait in portraits\plan.json into the CIMS game copy (settings.ApplyTargetDir) in one batch with the
// suite's own bundle-chunk writer: BC3 + PS4 tiling in parallel, one append per CAS, one TOC pass. Then reads every
// portrait back, checks the roster databases are untouched, samples a few through a cache-free Worker copy, and with
// --restore-check restores the backup (verified) and writes again. Refuses while NHL 24 runs.
using System.Diagnostics;
using System.Text.Json;
using Suite.Core;
using Suite.Core.Frostbite;

void Say(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
var settings = SuiteSettings.Load();
var target = Path.GetFullPath(settings.ApplyTargetDir).TrimEnd(Path.DirectorySeparatorChar);
var problems = ApplyTarget.Problems(target);
if (problems.Count > 0) { Say("REFUSED: " + string.Join("; ", problems)); return 1; }
var clock = Stopwatch.StartNew();

var planFile = Path.Combine(SuitePaths.Local, "portraits", "plan.json");
var plan = JsonDocument.Parse(File.ReadAllText(planFile)).RootElement.EnumerateArray()
    .Where(e => e.GetProperty("Png").ValueKind == JsonValueKind.String)
    .Select(e => (Name: e.GetProperty("Name").GetString()!, Slot: "ui/textures/common/playerportraits/p" + e.GetProperty("ArtId").GetInt32(), Png: e.GetProperty("Png").GetString()!))
    .ToList();
var raw = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();
Parallel.ForEach(plan, p => raw[p.Slot] = Bc3.EncodeTiled(ImageIO.Load(p.Png)));
Say($"encoded {raw.Count} portraits (BC3, PS4 tiled) in {clock.Elapsed.TotalSeconds:F0}s");

var dbs = new[] { "nhl_game", "initial_experience", "entry_draft", "expansion_draft", "threes_campaign" };
bool DbsHold(string label)
{
    var lf = new LegacyFiles(target, settings);
    var bad = dbs.Where(d => !lf.Read(d == "nhl_game" ? "gamemodes/database/t3db/nhl_game.db" : $"gamemodes/roster/{d}.db").AsSpan()
        .SequenceEqual(File.ReadAllBytes(Path.Combine(args[0], d + ".db")))).ToList();
    Say($"  roster databases {label}: {(bad.Count == 0 ? "all 5 identical" : "DIFFERENT: " + string.Join(", ", bad))}");
    return bad.Count == 0;
}
bool PortraitsHold(string label)
{
    var back = new BundleChunks(target, settings).Read(raw.Keys.ToHashSet());
    int same = raw.Count(kv => back.TryGetValue(kv.Key, out var b) && b.AsSpan().SequenceEqual(kv.Value));
    Say($"  portraits {label}: {same}/{raw.Count} read back identical");
    return same == raw.Count;
}

var backup = ApplyTarget.Backup(target);
Say("backup: " + backup);
bool Write(string label)
{
    if (ApplyTarget.RunningGame() is { } r) { Say("REFUSED: " + r + " is running"); return false; }
    var t = Stopwatch.StartNew();
    foreach (var line in new BundleChunks(target, settings).Replace(raw, Say)) Say("  " + line);
    Say($"  written in {t.Elapsed.TotalSeconds:F0}s ({label})");
    return PortraitsHold(label) && DbsHold(label);
}
if (!Write("applied")) { Say("FAILED; restoring the backup"); ApplyTarget.RestoreFromBackup(target, backup); return 1; }

// A cache-free Worker copy (FMT's own reader) must see the new pictures too.
if (args.Length > 1 && Directory.Exists(args[1]))
{
    var cache = Path.Combine(args[1], "_GameCaches");
    if (Directory.Exists(cache)) Directory.Delete(cache, true);
    var outDir = Path.Combine(SuitePaths.Local, "portraits", "readback-worker");
    if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    Directory.CreateDirectory(outDir);
    foreach (var p in plan.Where((p, i) => p.Name is "Brad Marchand" or "Macklin Celebrini" || i == plan.Count / 2).Take(3))
    {
        await ProcessRunner.RunAsync(Path.Combine(args[1], "NHL24Kit.Worker.exe"), ["texture", target, p.Slot, outDir], args[1]);
        var dds = Path.Combine(outDir, p.Slot[(p.Slot.LastIndexOf('/') + 1)..] + ".dds");
        if (!File.Exists(dds)) { Say($"  Worker read-back {p.Name}: none"); continue; }
        var got = Dds.Load(dds); var want = ImageIO.Load(p.Png);
        double diff = 0; int n = 0;
        for (int i = 0; i < Math.Min(got.Pixels.Length, want.Pixels.Length); i += 4)
            if (want.Pixels[i + 3] > 200) { diff += Math.Abs(got.Pixels[i] - want.Pixels[i]) + Math.Abs(got.Pixels[i + 1] - want.Pixels[i + 1]) + Math.Abs(got.Pixels[i + 2] - want.Pixels[i + 2]); n += 3; }
        Say($"  Worker read-back {p.Name}: {got.Width}x{got.Height}, mean difference {(n == 0 ? 999 : diff / n):F1} (BC3 vs PNG)");
    }
}

if (args.Contains("--restore-check"))
{
    ApplyTarget.RestoreFromBackup(target, backup);
    var left = ApplyTarget.Compare(target, backup);
    Say($"restore: {left.Count} differences from the backup");
    if (left.Count > 0 || !DbsHold("restored")) { Say("FAILED restore check (left restored)"); return 1; }
    if (!Write("reapplied")) { Say("FAILED re-apply; restoring"); ApplyTarget.RestoreFromBackup(target, backup); return 1; }
}
Say($"DONE in {clock.Elapsed.TotalMinutes:F1} min");
return 0;
