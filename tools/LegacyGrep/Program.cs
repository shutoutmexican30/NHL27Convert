// LegacyGrep <game> <out-dir|-> <text>...: lists legacy files whose bytes contain any text (read only); with an out-dir,
// also saves each matching file there.
using System.Text;
using Suite.Core;
using Suite.Core.Frostbite;

var lf = new LegacyFiles(args[0], SuiteSettings.Load());
var names = lf.Names("gamemodes/database/t3db/nhl_game.db");
Console.WriteLine($"{names.Count} legacy files");
var needles = args.Skip(2).Select(Encoding.ASCII.GetBytes).ToList();
foreach (var n in names.Where(n => !n.EndsWith(".db")))
{
    byte[] b; try { b = lf.Read(n); } catch { continue; }
    var hits = needles.Where(x => b.AsSpan().IndexOf(x) >= 0).Select(x => Encoding.ASCII.GetString(x)).ToList();
    if (hits.Count == 0) continue;
    Console.WriteLine($"{n} ({b.Length} bytes): {string.Join(", ", hits)}");
    if (args[1] != "-") { var f = Path.Combine(args[1], n.Replace('/', '~')); Directory.CreateDirectory(args[1]); File.WriteAllBytes(f, b); }
}
