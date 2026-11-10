// StateCheckpoint --target <isolated-game-copy> [--backups <folder>]
// StateCheckpoint --target <isolated-game-copy> --restore <checkpoint> --apply
// Creates or restores an exact TOC/CAS checkpoint, then independently compares every managed file.
using Suite.Core;
using Suite.Core.Frostbite;

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
void Say(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
const string TestRoot = @"D:\CIMS-test\";
string target = Path.GetFullPath(Opt("--target") ?? throw new ArgumentException("--target is required"))
    .TrimEnd(Path.DirectorySeparatorChar);
if (!target.StartsWith(TestRoot, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("State checkpoints are restricted to isolated copies under D:\\CIMS-test.");
if (ApplyTarget.RunningGame() is { } running) throw new InvalidOperationException(running + " is running. Close the game first.");
if (ApplyTarget.Problems(target) is { Count: > 0 } problems)
    throw new InvalidOperationException("Unsafe checkpoint target: " + string.Join("; ", problems));

if (Opt("--restore") is { } restore)
{
    if (!WriteGate.Allowed(args)) { Say("DRY RUN: pass --apply to restore this checkpoint. Nothing was written."); return 0; }
    string checkpoint = Path.GetFullPath(restore);
    if (!Directory.Exists(checkpoint)) throw new DirectoryNotFoundException(checkpoint);
    ApplyTarget.RestoreFromBackup(target, checkpoint);
    var differences = ApplyTarget.Compare(target, checkpoint);
    if (differences.Count > 0) throw new InvalidDataException("Restore differences: " + string.Join("; ", differences.Take(10)));
    Say($"EXACT STATE RESTORED AND COMPARED: 0 differences from {checkpoint}.");
    return 0;
}

string backup = ApplyTarget.Backup(target, Opt("--backups") ?? Path.Combine(TestRoot, "fullkit-backups"));
var left = ApplyTarget.Compare(target, backup);
if (left.Count > 0) throw new InvalidDataException("Checkpoint differences: " + string.Join("; ", left.Take(10)));
Say($"EXACT STATE CHECKPOINT VERIFIED: 0 differences. {backup}");
return 0;
