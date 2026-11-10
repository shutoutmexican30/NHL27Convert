// LegacyWrite read <game> <dir-with-stock-files> <name>...
//     Reads legacy files with the suite's own reader and compares them with files exported earlier (by the Worker).
// LegacyWrite apply <game> <mod.json> [--restore-check]
//     Applies the mod's legacy replacements with the suite's own writer (backup first), reads every file back, and with
//     --restore-check restores from the backup, verifies the game is identical to it and holds the old files, then
//     applies again and re-checks. Refuses the original game folder and a running game.
using System.Security.Cryptography;
using System.Text.Json;
using Suite.Core;
using Suite.Core.Frostbite;

void Say(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
if (args.Length < 3) { Console.WriteLine("usage: LegacyWrite read|export <game> <dir> <name>... | apply <game> <mod.json> [--restore-check]"); return 2; }
var settings = SuiteSettings.Load();
var game = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar);
Oodle.Ensure(settings);
Say("oodle: " + Oodle.LoadedFrom);

if (args[0] == "chunk")
{
    var id = Guid.Parse(args[2]);
    var reference = BundleIndex.Tocs(game).Select(TocFile.Load).Select(t => t.Find(id)).FirstOrDefault(r => r is not null)
        ?? throw new InvalidDataException("No top-level TOC entry for " + id);
    var cas = BundleIndex.CasPath(game, reference.Patch, reference.Catalog, reference.Cas);
    using var stream = File.OpenRead(cas); stream.Position = reference.Offset;
    var stored = new byte[reference.Size]; stream.ReadExactly(stored);
    var raw = ChunkBlocks.Parse(stored).Decode();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
    File.WriteAllBytes(args[3], raw);
    Say($"chunk {id}: {stored.Length} stored -> {raw.Length} raw bytes from {cas}");
    return 0;
}

if (args[0] == "read")
{
    var lf = new LegacyFiles(game, settings);
    bool ok = true;
    foreach (var name in args.Skip(3))
    {
        var mine = lf.Read(name);
        var theirs = File.ReadAllBytes(Path.Combine(args[2], name.Replace('/', Path.DirectorySeparatorChar)));
        bool same = mine.AsSpan().SequenceEqual(theirs);
        Say($"{name}: {mine.Length} bytes, {(same ? "identical" : "DIFFERENT")}");
        ok &= same;
    }
    return ok ? 0 : 1;
}

if (args[0] == "export")
{
    var lf = new LegacyFiles(game, settings);
    foreach (var name in args.Skip(3))
    {
        var path = Path.GetFullPath(Path.Combine(args[2], name.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(args[2]).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Export path escapes its directory.");
        var bytes = lf.Read(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        Say($"exported {name}: {bytes.Length} bytes");
    }
    return 0;
}

if (args[0] != "apply") return 2;
foreach (var original in new[] { settings.Nhl24GameDir, settings.Nhl24BaseDir, settings.Nhl24UpdateDir, settings.Nhl24ReadView })
    if (!string.IsNullOrEmpty(original) && string.Equals(Path.GetFullPath(original).TrimEnd(Path.DirectorySeparatorChar), game, StringComparison.OrdinalIgnoreCase))
    { Say("REFUSED: that is the original game (or its read view)."); return 1; }
var problems = ApplyTarget.Problems(game);
if (problems.Count > 0) { Say("REFUSED: " + string.Join("; ", problems)); return 1; }

var mod = ModFile.Load(args[2]) ?? throw new FileNotFoundException(args[2]);
var files = (mod.Legacy ?? []).ToDictionary(l => l.Name, l => File.ReadAllBytes(l.Source));
Say($"mod {mod.Name}: {files.Count} legacy files");

var before = new LegacyFiles(game, settings);
var old = files.Keys.ToDictionary(n => n, before.Read);
var backup = ApplyTarget.Backup(game);
Say("backup: " + backup);
var tails = TailHashes(game, backup);

bool Apply()
{
    var lf = new LegacyFiles(game, settings);
    foreach (var line in lf.Replace(files)) Say("  " + line);
    return Holds(files, "applied");
}
bool Holds(Dictionary<string, byte[]> expected, string label)
{
    var lf = new LegacyFiles(game, settings);
    bool all = true;
    foreach (var (name, bytes) in expected)
    {
        bool same = lf.Read(name).AsSpan().SequenceEqual(bytes);
        Say($"  read back {name}: {(same ? "identical" : "DIFFERENT")} ({label})");
        all &= same;
    }
    return all;
}

if (!Apply()) { Say("FAILED read-back; restoring."); ApplyTarget.RestoreFromBackup(game, backup); return 1; }
var diffs = ApplyTarget.Compare(game, backup);
Say($"changes vs backup: {diffs.Count}");
foreach (var d in diffs) Say("  " + d);

if (args.Contains("--restore-check"))
{
    Say("restore from backup...");
    ApplyTarget.RestoreFromBackup(game, backup);
    var left = ApplyTarget.Compare(game, backup);
    bool tailsOk = TailHashes(game, backup).SequenceEqual(tails);
    Say($"after restore: {left.Count} differences (CAS lengths + TOC hashes); CAS tails {(tailsOk ? "identical" : "DIFFERENT")}");
    if (left.Count > 0 || !tailsOk || !Holds(old, "restored")) { Say("FAILED restore check (game left restored)."); return 1; }
    Say("apply again...");
    if (!Apply()) { Say("FAILED re-apply; restoring."); ApplyTarget.RestoreFromBackup(game, backup); return 1; }
}
Say("DONE");
return 0;

// The last MB before each CAS file's backed-up end: proves the bytes the game already had were never touched.
static List<string> TailHashes(string game, string backup)
{
    var snap = ApplyTarget.LoadSnapshot(backup);
    var list = new List<string>();
    foreach (var (rel, len) in snap.CasLengths.OrderBy(k => k.Key))
    {
        using var fs = File.OpenRead(Path.Combine(game, rel));
        long n = Math.Min(len, 1 << 20);
        fs.Position = len - n;
        var buf = new byte[n];
        fs.ReadExactly(buf);
        list.Add(rel + ":" + Convert.ToHexString(SHA256.HashData(buf)));
    }
    return list;
}
