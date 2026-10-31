// Apply/restore round trip on a COPY of the game:
//   ApplyTest <copy-of-game> <slot> <replacement.png>
// 1. shows the guard refusing the real game folder and the read view,
// 2. hashes every file of the copy, 3. applies one replacement, 4. lists what changed,
// 5. restores, 6. re-hashes and compares with step 2.
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Suite.Convert27;
using Suite.Core;

if (args.Length < 3) { Console.WriteLine("usage: ApplyTest <copy-of-game> <slot> <replacement.png>"); return 2; }
var copy = Path.GetFullPath(args[0]).TrimEnd('\\');
var settings = SuiteSettings.Load();
if (string.Equals(copy, Path.GetFullPath(settings.Nhl24BaseDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
    string.Equals(copy, Path.GetFullPath(settings.Nhl24GameDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("REFUSED: that is the real game folder. Use a copy.");
    return 1;
}

Console.WriteLine("== guard on the real game and the read view (expected: refused)");
foreach (var dir in new[] { settings.Nhl24GameDir, settings.Nhl24BaseDir, settings.Nhl24ReadView })
{
    var p = ApplyTarget.Problems(dir);
    Console.WriteLine($"  {dir}: {(p.Count == 0 ? "ACCEPTED (unexpected!)" : "refused")}");
    foreach (var x in p.Take(3)) Console.WriteLine($"     - {x}");
}

settings.ApplyTargetDir = copy; // in memory only; settings.json is not saved
Console.WriteLine($"== guard on the copy {copy}");
foreach (var x in ModApply.Problems(settings)) Console.WriteLine($"     - {x}");
if (ModApply.Problems(settings).Count > 0) return 1;

Console.WriteLine("== hashing every file of the copy (before)");
var before = Hash(copy);
Console.WriteLine($"   {before.Count} files");

var plan = Path.Combine(Path.GetTempPath(), "cims-applytest-plan.json");
File.WriteAllText(plan, JsonSerializer.Serialize(new { Replacements = new[] { new TextureReplacement(args[1], args[2]) } }));
Console.WriteLine($"== apply 1 replacement: {args[1]} <- {args[2]}");
var t = DateTime.Now;
var (ok, msg) = await ModApply.ApplyPlanAsync(settings, plan, line => { if (line.StartsWith("ERROR") || line.StartsWith("RESULT")) Console.WriteLine("   " + line[..Math.Min(300, line.Length)]); });
Console.WriteLine($"   apply ok={ok} in {(DateTime.Now - t).TotalSeconds:F0}s: {msg[..Math.Min(400, msg.Length)]}");
var baseline = ModApply.Baseline(copy);
if (baseline is not null)
{
    var changed = ApplyTarget.Compare(copy, baseline);
    Console.WriteLine($"   changes vs stock backup: {changed.Count}");
    foreach (var c in changed.Take(12)) Console.WriteLine($"     {c}");
}

Console.WriteLine("== restore");
t = DateTime.Now;
var (rok, rmsg) = await ModApply.RestoreAsync(settings);
Console.WriteLine($"   restore ok={rok} in {(DateTime.Now - t).TotalSeconds:F0}s: {rmsg[..Math.Min(400, rmsg.Length)]}");

Console.WriteLine("== hashing every file of the copy (after restore)");
var after = Hash(copy);
var differ = before.Where(kv => after.TryGetValue(kv.Key, out var h) && h != kv.Value).Select(kv => kv.Key).ToList();
var missing = before.Keys.Except(after.Keys).ToList();
var added = after.Keys.Except(before.Keys).ToList();
Console.WriteLine($"   identical: {before.Count - differ.Count - missing.Count}/{before.Count} · changed {differ.Count} · missing {missing.Count} · added {added.Count}");
foreach (var d in differ.Take(10)) Console.WriteLine($"     changed: {d}");
foreach (var d in missing.Take(10)) Console.WriteLine($"     missing: {d}");
foreach (var d in added.Take(10)) Console.WriteLine($"     added: {d}");
Console.WriteLine(differ.Count == 0 && missing.Count == 0 ? "RESULT PASS: every original file is byte-identical after restore" : "RESULT FAIL");
return differ.Count == 0 && missing.Count == 0 ? 0 : 1;

static Dictionary<string, string> Hash(string root)
{
    var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
    var d = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
    {
        using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        d[Path.GetRelativePath(root, f)] = Convert.ToHexString(SHA256.HashData(fs));
    });
    return new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase);
}
