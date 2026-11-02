// PortraitApply <dir-with-synced-dbs> <fresh-worker-dir>
// Portraits into the CIMS game copy (settings.ApplyTargetDir) only:
//  1. the synced roster databases (with the new portrait slots) through the suite's legacy writer, read back,
//  2. a suite backup, then the portrait pictures (portraits\plan.json) through the NHL24Kit Worker,
//  3. checks: the databases still read back identical; sample pictures read back through a cache-free Worker copy,
//  4. restore test: Worker restore, the game must equal the backup from step 2 again; then the pictures again + checks.
// Refuses while NHL 24 runs. Personal use only: NHL API headshots.
using System.IO;
using System.Text.Json;
using Suite.Convert27;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

void Say(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
if (args.Length < 2) { Console.WriteLine("usage: PortraitApply <dir-with-synced-dbs> <fresh-worker-dir>"); return 2; }
var settings = SuiteSettings.Load();
var target = Path.GetFullPath(settings.ApplyTargetDir).TrimEnd(Path.DirectorySeparatorChar);
var problems = ApplyTarget.Problems(target);
if (problems.Count > 0) { Say("REFUSED: " + string.Join("; ", problems)); return 1; }
Say("target: " + target);

// 1. Databases.
var legacy = RosterSyncRun.Databases.Select(d => new LegacyReplacement(RosterSyncRun.LegacyName(d), Path.Combine(args[0], d + ".db"))).ToList();
var (lok, lmsg, _) = LegacyApply.Apply(settings, legacy, Say);
Say($"databases: ok={lok} {lmsg}");
if (!lok) return 1;
bool DbsHold(string label)
{
    var lf = new LegacyFiles(target, settings);
    var bad = legacy.Where(l => !lf.Read(l.Name).AsSpan().SequenceEqual(File.ReadAllBytes(l.Source))).Select(l => l.Name).ToList();
    Say($"  databases {label}: {(bad.Count == 0 ? "all 5 identical" : "DIFFERENT: " + string.Join(", ", bad))}");
    return bad.Count == 0;
}

// 2. Pictures.
var plan = JsonSerializer.Deserialize<List<PortraitAssignment>>(File.ReadAllText(Path.Combine(Portraits.Dir, "plan.json")))!.Where(p => p.Png is not null).ToList();
var planPath = Path.Combine(Portraits.Dir, "worker-plan.json");
File.WriteAllText(planPath, JsonSerializer.Serialize(new { Replacements = plan.Select(p => new TextureReplacement(Portraits.SlotFolder + "p" + p.ArtId, p.Png!)).ToList() }));
var backup = ApplyTarget.Backup(target);
Say($"backup before pictures: {backup}");
async Task<bool> Pictures(string label)
{
    if (ApplyTarget.RunningGame() is { } r) { Say($"REFUSED: {r} is running."); return false; }
    Say($"writing {plan.Count} pictures through the Worker ({label})...");
    var (ok, msg) = await ModApply.ApplyPlanAsync(settings, planPath, l => { if (l.StartsWith("ERROR")) Say("  " + l); });
    Say($"  pictures ok={ok}: {msg[..Math.Min(200, msg.Length)]}");
    return ok && DbsHold("after pictures") && await Sample(label);
}
// Sample pictures through a Worker copy without an asset cache (the shared cache would show the old data).
async Task<bool> Sample(string label)
{
    var fresh = args[1];
    var cache = Path.Combine(fresh, "_GameCaches");
    if (Directory.Exists(cache)) Directory.Delete(cache, true);
    var outDir = Path.Combine(Portraits.Dir, "readback-" + label);
    if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    Directory.CreateDirectory(outDir);
    var picks = plan.Where((p, i) => i % 150 == 0 || p.Name is "Brad Marchand" or "Macklin Celebrini" || p.NewSlot && i % 20 == 0).Take(10).ToList();
    bool all = true;
    foreach (var p in picks)
    {
        var r = await ProcessRunner.RunAsync(Path.Combine(fresh, "NHL24Kit.Worker.exe"), ["texture", target, Portraits.SlotFolder + "p" + p.ArtId, outDir], fresh);
        var dds = Path.Combine(outDir, $"p{p.ArtId}.dds");
        if (!File.Exists(dds)) { Say($"  {p.Name}: no read-back ({r.StdOut.Split('\n').FirstOrDefault(l => l.StartsWith("ERROR"))})"); all = false; continue; }
        var got = Dds.Load(dds); var want = ImageIO.Load(p.Png!);
        double diff = 0; int n = 0;
        for (int i = 0; i < Math.Min(got.Pixels.Length, want.Pixels.Length); i += 4)
            if (want.Pixels[i + 3] > 200) { diff += Math.Abs(got.Pixels[i] - want.Pixels[i]) + Math.Abs(got.Pixels[i + 1] - want.Pixels[i + 1]) + Math.Abs(got.Pixels[i + 2] - want.Pixels[i + 2]); n += 3; }
        double mad = n == 0 ? 999 : diff / n;
        bool ok = got.Width == 512 && got.Height == 340 && mad < 12;
        Say($"  {p.Name} (p{p.ArtId}): {got.Width}x{got.Height}, mean difference {mad:F1} {(ok ? "OK" : "MISMATCH")}");
        all &= ok;
    }
    return all;
}

if (!await Pictures("applied")) { Say("FAILED; game left as is for inspection (backup: " + backup + ")"); return 1; }

// 4. Restore test.
Say("restore test (Worker restore)...");
var rr = await Worker.RunRawAsync(settings, ["restore", target]);
var left = ApplyTarget.Compare(target, backup);
Say($"  after restore: {left.Count} differences from the backup taken before the pictures");
foreach (var d in left.Take(10)) Say("    " + d);
if (left.Count > 0 || !DbsHold("after restore")) { Say("FAILED restore test"); return 1; }
if (!await Pictures("reapplied")) return 1;
Say("DONE");
return 0;
