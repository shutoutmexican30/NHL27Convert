// LegacyStrings: read and same-size string-replace legacy files (loc/gamemodes).
//   LegacyStrings dump <game> <out-dir> <name>...
//   LegacyStrings replace <game> <mod.json> [--dry]
// mod.json: { "replacements": [ { "file": "gamemodes/...", "old": "83,500,000", "new": "104,100,000" } ] }
// Strings are matched as UTF-8 unless "utf16": true. Replacements must be same byte length.
using System.Text;
using System.Text.Json;
using Suite.Core;
using Suite.Core.Frostbite;

string? Arg(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var game = args.Length > 1 ? args[1] : "";
if (args.Length < 2) { Console.Error.WriteLine("usage: LegacyStrings dump <game> <out> <name>... | replace <game> <mod.json> [--dry]"); return 2; }

var lf = new LegacyFiles(game, SuiteSettings.Load());

if (args[0] == "dump")
{
    var outp = Arg("--out") ?? Arg("--out-dir") ?? (args.Length > 2 && !args[2].StartsWith("-") ? args[2] : ".");
    foreach (var name in args.Skip(3))
    {
        var b = lf.Read(name);
        var stem = name.Split('/')[^1];
        Directory.CreateDirectory(outp);
        File.WriteAllBytes(Path.Combine(outp, stem), b);
        Console.WriteLine($"{name}: {b.Length} bytes -> {Path.Combine(outp, stem)}");
    }
    return 0;
}

if (args[0] == "verify")
{
    foreach (var name in args.Skip(2))
    {
        var copies = lf.ReadAll(name, everyToc: true);
        foreach (var c in copies)
        {
            var t = System.Text.Encoding.UTF8.GetString(c);
            foreach (var line in t.Split('\n'))
                if (line.StartsWith("156,48")) Console.WriteLine("  COPY: " + line.Trim());
        }
    }
    return 0;
}
if (args[0] == "replace")
{
    var doc = JsonDocument.Parse(File.ReadAllText(args[2]));
    var edits = new Dictionary<string, List<(byte[] Old, byte[] New)>>();
    foreach (var e in doc.RootElement.GetProperty("replacements").EnumerateArray())
    {
        var file = e.GetProperty("file").GetString()!;
        var utf16 = e.TryGetProperty("utf16", out var u) && u.GetBoolean();
        var enc = utf16 ? Encoding.Unicode : Encoding.UTF8;
        var old = enc.GetBytes(e.GetProperty("old").GetString()!);
        var nw = enc.GetBytes(e.GetProperty("new").GetString()!);
        var allowResize = e.TryGetProperty("allowResize", out var ar) && ar.GetBoolean();
        if (old.Length != nw.Length && !allowResize)
        { Console.Error.WriteLine($"REFUSED (length differs, set allowResize): {file}: '{e.GetProperty("old").GetString()}' -> '{e.GetProperty("new").GetString()}'"); return 1; }
        if (!edits.TryGetValue(file, out var l)) edits[file] = l = new();
        l.Add((old, nw));
    }
    var dry = args.Contains("--dry");
    var total = 0;
    foreach (var (file, repls) in edits)
    {
        byte[] Edit(byte[] b)
        {
            foreach (var (o, n) in repls)
            {
                int idx, from = 0, count = 0;
                while ((idx = IndexOf(b, o, from)) >= 0) { b[idx] = n[0]; Array.Copy(n, 0, b, idx, n.Length); from = idx + n.Length; count++; }
                Console.WriteLine($"  {file}: replaced x{count}: {Encoding.UTF8.GetString(o)} -> {Encoding.UTF8.GetString(n)}");
                total += count;
            }
            return b;
        }
        if (dry) { Edit(lf.Read(file)); }
        else foreach (var line in lf.ReplaceEach(new Dictionary<string, Func<byte[], byte[]>> { [file] = Edit })) Console.WriteLine("  WRITE-LOG: " + line);
    }
    Console.WriteLine(dry ? $"DRY: {total} replacement(s) would apply." : $"APPLIED {total} replacement(s).");
    return 0;
}
return 2;

static int IndexOf(byte[] hay, byte[] needle, int start)
{
    for (int i = start; i + needle.Length <= hay.Length; i++)
    {
        int j = 0; while (j < needle.Length && hay[i + j] == needle[j]) j++;
        if (j == needle.Length) return i;
    }
    return -1;
}
