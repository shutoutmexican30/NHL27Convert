using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);
var rx = new Regex(@"ice_hotspot_(\d+)_(\d+)_color$");

var groups = index.ResByName.Keys
    .Select(k => (Name: k, M: rx.Match(k)))
    .Where(t => t.M.Success)
    .Select(t => (t.Name, A: int.Parse(t.M.Groups[1].Value), B: int.Parse(t.M.Groups[2].Value)))
    .GroupBy(t => t.A).OrderBy(g => g.Key).ToList();

Console.WriteLine($"arena ids (first index): {groups.Count}");
foreach (var g in groups)
    Console.WriteLine($"  {g.Key,3}: {g.Count()} hotspot(s), second index {g.Min(t => t.B)}..{g.Max(t => t.B)}");

// Decode the first arena's hotspots and one from a mid arena; report format and save pngs.
Directory.CreateDirectory(@"C:\dev\NHL27Convert\tools\MeshProbe\out\hotspots");
void Dump(int arena)
{
    var g = groups.FirstOrDefault(x => x.Key == arena);
    if (g is null) { Console.WriteLine($"arena {arena} not found"); return; }
    foreach (var t in g.OrderBy(t => t.B))
    {
        try
        {
            var res = index.ResByName[t.Name][0];
            var stored = BundleIndex.ReadBytes(res.Data);
            byte[] header = stored; try { header = ChunkBlocks.Parse(stored).Decode(); } catch { }
            var tr = TextureRes.Parse(header);
            Console.WriteLine($"  arena {arena}/{t.B}: {tr.Width}x{tr.Height} fmt 0x{header[12]:x2} mips {tr.MipCount} payload {tr.PayloadSize}");
            var full = ChunkReplace.ReadAll(game, index, tr.ChunkGuid).FirstOrDefault(r => r.LogicalOffset == 0 && r.Raw.Length == tr.PayloadSize).Raw;
            if (full is null) { Console.WriteLine("    no full chunk copy"); continue; }
            var tc = TextureChunk.Parse(full, tr);
            if (header[12] != 0x43) { Console.WriteLine("    not BC7, skipped decode"); continue; }
            var img = Bc7.DecodeTiled(tc.Mips[0], tr.Width, tr.Height);
                ImageIO.SavePng(img, $@"C:\dev\NHL27Convert\tools\MeshProbe\out\hotspots\a{arena}_{t.B}.png");
        }
        catch (Exception ex) { Console.WriteLine($"  arena {arena}/{t.B}: ERROR {ex.Message}"); }
    }
}
Dump(groups[0].Key);
Dump(groups[^1].Key);
