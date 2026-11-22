// LegacyScan <game> <filter...> --read <name> — list legacy names, or dump one file's bytes.
using Suite.Core;
using Suite.Core.Frostbite;

if (args.Length < 1) { Console.Error.WriteLine("usage: LegacyScan <game> [filter...] [--read <name>]"); return 2; }
Oodle.Ensure(SuiteSettings.Load());
var files = new LegacyFiles(args[0], SuiteSettings.Load());
var rest = args.Skip(1).ToList();
var readAt = rest.IndexOf("--read");
if (readAt >= 0)
{
    var name = rest[readAt + 1];
    var bytes = files.Read(name);
    Console.WriteLine($"// {name}: {bytes.Length} bytes");
    Console.Out.Write(System.Text.Encoding.UTF8.GetString(bytes));
    return 0;
}
var names = files.Names("gamemodes/database/t3db/nhl_game-meta.xml");
var filters = rest.Select(f => f.ToLowerInvariant()).ToArray();
foreach (var n in names.Where(n => filters.Length == 0 || filters.Any(f => n.Contains(f))))
    Console.WriteLine(n);
Console.WriteLine($"{names.Count} legacy files");
return 0;
