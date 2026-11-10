// ResList <game> <name-filter> — every EBX/RES whose name contains the filter, and how many copies each has.
using Suite.Core;
using Suite.Core.Frostbite;

if (args.Length < 2) { Console.Error.WriteLine("usage: ResList <game> <name-filter>"); return 2; }
Oodle.Ensure(SuiteSettings.Load());
var index = BundleIndex.Build(args[0]);
var names = index.EbxByName.Keys.Concat(index.ResByName.Keys)
    .Where(n => n.Contains(args[1], StringComparison.OrdinalIgnoreCase))
    .Order().ToList();
foreach (var n in names)
{
    var e = index.EbxByName.GetValueOrDefault(n)?.Count ?? 0;
    var r = index.ResByName.GetValueOrDefault(n)?.Count ?? 0;
    Console.WriteLine($"{(e > 0 ? $"EBX x{e}" : ""),8}{(r > 0 ? $"RES x{r}" : ""),8}  {n}");
}
Console.WriteLine($"{names.Count} name(s)");
return 0;
