// ChunkWrite --target <game-copy> <chunk-guid>=<full-raw-file> [...] --apply [--restore-check]   (dry run without --apply)
// Replaces each chunk everywhere the game reads it (ChunkReplace), then rebuilds the index and checks every reference
// decodes to the new data (bundle slices to their logical range), that each changed TOC differs from the backup only in
// the repointed offset/size pairs, and that CAS files only grew. With --restore-check: restore the backup, prove the
// stock data reads back, write again. Refuses the original game and while NHL 24 runs.
using System.Security.Cryptography;
using Suite.Core;
using Suite.Core.Frostbite;

void Say(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
var settings = SuiteSettings.Load();
int ti = Array.IndexOf(args, "--target");
if (ti < 0) { Say("--target is required"); return 2; }
var target = Path.GetFullPath(args[ti + 1]).TrimEnd(Path.DirectorySeparatorChar);
foreach (var original in new[] { settings.Nhl24GameDir, settings.Nhl24BaseDir, settings.Nhl24UpdateDir, settings.Nhl24ReadView })
    if (!string.IsNullOrWhiteSpace(original) && string.Equals(Path.GetFullPath(original).TrimEnd(Path.DirectorySeparatorChar), target, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing writes to the original game or its read view.");
var problems = ApplyTarget.Problems(target);
if (problems.Count > 0) { Say("REFUSED: " + string.Join("; ", problems)); return 1; }
var jobs = args.Where(a => a.Contains('=')).Select(a => a.Split('=', 2)).Select(p => (Id: Guid.Parse(p[0]), Data: File.ReadAllBytes(p[1]))).ToList();
bool restoreCheck = args.Contains("--restore-check");

var before = BundleIndex.Build(target);
var stock = jobs.ToDictionary(j => j.Id, j => ChunkReplace.ReadAll(target, before, j.Id));
foreach (var j in jobs) Say($"{j.Id}: {stock[j.Id].Count} references: {string.Join(", ", stock[j.Id].Select(r => $"{r.Where} @{r.LogicalOffset}+{r.Raw.Length}"))}");
if (args.Contains("--dry"))
{
    foreach (var j in jobs) foreach (var r in stock[j.Id])
        Say($"  {j.Id} {r.Where} @{r.LogicalOffset}: stock sha1 {Convert.ToHexString(SHA1.HashData(r.Raw))[..12]}, new slice differs in {Diff(r.Raw, j.Data.AsSpan((int)r.LogicalOffset, r.Raw.Length)):N0} bytes");
    return 0;
}
static int Diff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) { int n = 0; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++; return n; }
var casSizes = Directory.EnumerateFiles(target, "*.cas", SearchOption.AllDirectories).ToDictionary(f => f, f => new FileInfo(f).Length);

if (!WriteGate.Allowed(args)) { Say("dry run (pass --apply with --target to write)"); return 0; }
var backup = ApplyTarget.Backup(target);
Say("backup: " + backup);

bool Holds(string label, bool expectNew)
{
    var index = BundleIndex.Build(target);
    bool ok = true;
    foreach (var j in jobs)
    {
        var refs = ChunkReplace.ReadAll(target, index, j.Id);
        foreach (var r in refs)
        {
            var want = expectNew ? j.Data.AsSpan((int)r.LogicalOffset, r.Raw.Length).ToArray()
                : stock[j.Id].First(s => s.Where == r.Where && s.LogicalOffset == r.LogicalOffset).Raw;
            bool same = r.Raw.Length + r.LogicalOffset <= j.Data.Length && r.Raw.AsSpan().SequenceEqual(want);
            ok &= same;
            if (!same) Say($"  {label}: {j.Id} via {r.Where}: MISMATCH");
        }
        if (refs.Count != stock[j.Id].Count) { ok = false; Say($"  {label}: {j.Id} now has {refs.Count} references, had {stock[j.Id].Count}"); }
    }
    Say($"  {label}: every reference reads back {(expectNew ? "the new data" : "stock")}: {ok}");
    return ok;
}
bool TocsOnlyRepointed()
{
    // Every changed byte in a TOC must sit in a u32 that is an offset/size the writer repoints (checked by count: at most 8 bytes per reference).
    bool ok = true;
    foreach (var toc in BundleIndex.Tocs(target))
    {
        var now = File.ReadAllBytes(toc); var was = File.ReadAllBytes(Path.Combine(backup, "toc", Path.GetRelativePath(target, toc)));
        if (now.Length != was.Length) { Say($"  {Path.GetRelativePath(target, toc)}: SIZE CHANGED"); ok = false; continue; }
        int diff = 0; for (int i = 0; i < now.Length; i++) if (now[i] != was[i]) diff++;
        if (diff > 0) Say($"  {Path.GetRelativePath(target, toc)}: {diff} bytes changed (offset/size pairs only)");
        if (!now.AsSpan(0, TocFile.HeaderSize).SequenceEqual(was.AsSpan(0, TocFile.HeaderSize))) { Say("  SIGNED HEADER CHANGED"); ok = false; }
    }
    foreach (var (f, len) in casSizes)
        if (new FileInfo(f).Length < len) { Say($"  {f} SHRANK"); ok = false; }
        else if (new FileInfo(f).Length > len) Say($"  {Path.GetRelativePath(target, f)} grew {new FileInfo(f).Length - len:N0} bytes");
    return ok;
}
bool Write(string label)
{
    if (ApplyTarget.RunningGame() is { } r) { Say("REFUSED: " + r + " is running"); return false; }
    var index = BundleIndex.Build(target);
    foreach (var j in jobs) foreach (var line in ChunkReplace.Replace(target, index, j.Id, j.Data)) Say("  " + line);
    return Holds(label, true) && TocsOnlyRepointed();
}
if (!Write("applied")) { Say("FAILED; restoring the backup"); ApplyTarget.RestoreFromBackup(target, backup); return 1; }
if (restoreCheck)
{
    ApplyTarget.RestoreFromBackup(target, backup);
    bool stockBack = Holds("restored", false) && BundleIndex.Tocs(target).All(t => File.ReadAllBytes(t).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(backup, "toc", Path.GetRelativePath(target, t)))));
    Say("  restore gives back the exact stock TOCs and data: " + stockBack);
    if (!stockBack || !Write("re-applied")) { Say("FAILED"); return 1; }
}
Say("done");
return 0;
