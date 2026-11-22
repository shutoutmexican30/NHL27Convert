using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);

string[] knownNormal = { "_nrm", "_normal", "_n_" , "_norm"};
string[] knownColor = { "_color", "_alb", "_diffuse" };

foreach (var group in new[] { ("NORMAL", knownNormal), ("COLOR", knownColor) })
{
    Console.WriteLine("=== " + group.Item1);
    int shown = 0;
    foreach (var name in index.ResByName.Keys)
    {
        if (shown >= 6) break;
        if (name.EndsWith("_mesh", StringComparison.Ordinal)) continue;
        if (!group.Item2.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;
        try
        {
            var res = index.ResByName[name][0];
            var stored = BundleIndex.ReadBytes(res.Data);
            byte[] h; try { h = ChunkBlocks.Parse(stored).Decode(); } catch { h = stored; }
            if (h.Length < 48) continue;
            var tr = TextureRes.Parse(h);   // throws for non BC1/3/5
            Console.Write($"{name.Split('/')[^1],-52} [{string.Join(" ", h.Take(20).Select(b => b.ToString("X2")))}] blockBytes={tr.BlockBytes}\n");
            shown++;
        }
        catch { }
    }
}
