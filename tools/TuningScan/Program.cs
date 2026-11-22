// TuningScan <game> <needle> [more needles...]
// Read-only. Builds the bundle index and scans every EBX payload (decoded) for the ASCII needle(s) —
// the way to find a tunable asset by the property name inside it, whatever the asset's file name is.
using System.Collections.Concurrent;
using Suite.Core;
using Suite.Core.Frostbite;

if (args.Length < 2) { Console.Error.WriteLine("usage: TuningScan <game> <needle> [more needles...]"); return 2; }
Oodle.Ensure(SuiteSettings.Load());
var index = BundleIndex.Build(args[0]);
var needles = args.Skip(1).Select(n => System.Text.Encoding.ASCII.GetBytes(n)).ToArray();
var hits = new ConcurrentDictionary<string, List<int>>(StringComparer.Ordinal);

Parallel.ForEach(index.EbxByName, kv =>
{
    foreach (var e in kv.Value)
    {
        byte[] raw;
        try
        {
            var stored = BundleIndex.ReadBytes(e.Data);
            try { raw = ChunkBlocks.Parse(stored).Decode(); } catch { raw = stored; }
        }
        catch { continue; }
        foreach (var needle in needles)
        {
            int at = 0, found = 0;
            while (found < 3 && (at = raw.AsSpan(at).IndexOf(needle)) >= 0)
            {
                hits.AddOrUpdate(kv.Key, [at], (_, l) => l);
                at += needle.Length;
                found++;
            }
            if (found > 0) break;
        }
    }
});

foreach (var (name, at) in hits.OrderBy(h => h.Key, StringComparer.Ordinal))
    Console.WriteLine($"{name}  @{string.Join(",", at)}");
Console.WriteLine($"{hits.Count} asset(s) contain the needle");
return 0;
