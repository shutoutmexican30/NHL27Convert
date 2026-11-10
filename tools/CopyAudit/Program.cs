// CopyAudit <game>: for every NHL skater uniform texture, how many distinct chunks its RES entries point at and in
// which tocs they live. A name with chunks in BOTH Data and Patch was duplicated by the update; before the
// write-every-copy fix only the Patch copy was written, and the game read the stock base copy. Read only.
using Suite.Core;
using Suite.Core.Frostbite;

var game = Path.GetFullPath(args[0]);
Oodle.Ensure(SuiteSettings.Load());
var index = BundleIndex.Build(game, s => Console.Error.WriteLine(s));

var rows = new List<(string Name, string Team, string Set, int Copies, string Tocs)>();
foreach (var (name, entries) in index.ResByName)
{
    if (!name.Contains("/character/skater/league/nhl/", StringComparison.Ordinal)) continue;
    if (!name.EndsWith("_color_array") && !name.EndsWith("_normal_array") && !name.EndsWith("_color") && !name.EndsWith("_normal")) continue;
    var parts = name.Split('/');
    var team = parts.Length > 5 ? parts[5] : "?";
    var set = parts.Length > 7 ? $"{parts[6]}/{parts[7]}" : "?";
    var chunks = new HashSet<Guid>();
    var tocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var res in entries)
    {
        var stored = BundleIndex.ReadBytes(res.Data);
        byte[] header; try { header = ChunkBlocks.Parse(stored).Decode(); } catch { header = stored; }
        if (header.Length < 48) continue;
        chunks.Add(new Guid(header.AsSpan(32, 16)));
        tocs.Add(Path.GetFileName(res.Bundle.Toc).Contains("contentsb") ? (res.Bundle.Toc.Contains("Patch") ? "Patch" : "Data") : Path.GetFileName(res.Bundle.Toc));
    }
    if (chunks.Count > 0) rows.Add((name, team, set, chunks.Count, string.Join("+", tocs.Order())));
}

var dual = rows.Where(r => r.Copies > 1).ToList();
Console.WriteLine($"{rows.Count} uniform textures, {dual.Count} duplicated by the update (base + update copy)");
foreach (var g in dual.GroupBy(r => r.Team).OrderBy(g => g.Key))
    Console.WriteLine($"  {g.Key}: {g.Count()} textures with 2 copies — sets {string.Join(", ", g.Select(r => r.Set).Distinct().Order())}");
Console.WriteLine();
foreach (var g in rows.GroupBy(r => r.Team).OrderBy(g => g.Key))
    Console.WriteLine($"{g.Key,-14} {g.Count(),4} textures, duplicated: {g.Count(r => r.Copies > 1)}");
