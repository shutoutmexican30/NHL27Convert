using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Suite.Core;
using Suite.Core.Frostbite;

var settings = SuiteSettings.Load();
var view = Nhl24ReadView.Ensure(settings);
var index = BundleIndex.Build(view);

var rx = new Regex(@"ice_hotspot_(\d+)_(\d+)_color$", RegexOptions.Compiled);
var slots = index.ResByName.Keys.Select(k => (Name: k, M: rx.Match(k)))
    .Where(t => t.M.Success)
    .Select(t => (Set: int.Parse(t.M.Groups[1].Value), Index: int.Parse(t.M.Groups[2].Value), Name: t.Name))
    .ToList();

string Md5(int set, int idx)
{
    if (!index.ResByName.TryGetValue(IceHotspots.Name(set, idx), out var rl) || rl.FirstOrDefault() is not { } res)
        throw new InvalidOperationException($"missing {set}/{idx}");
    var stored = BundleIndex.ReadBytes(res.Data);
    byte[] header = stored; try { header = ChunkBlocks.Parse(stored).Decode(); } catch { }
    var tr = TextureRes.Parse(header);
    var full = ChunkReplace.ReadAll(view, index, tr.ChunkGuid).FirstOrDefault(r => r.LogicalOffset == 0 && r.Raw.Length == tr.PayloadSize).Raw
        ?? throw new InvalidOperationException($"no data {set}/{idx}");
    return Convert.ToHexString(MD5.HashData(full));
}

var set0 = slots.Where(s => s.Set == 0).Select(s => s.Index).ToHashSet();
var set1 = slots.Where(s => s.Set == 1).Select(s => s.Index).ToHashSet();
var both = set0.Intersect(set1).OrderBy(i => i).ToList();
Console.WriteLine($"set0-only: {set0.Count - both.Count}, set1-only: {set1.Count - both.Count}, in BOTH sets: {both.Count}");

var same = new System.Collections.Concurrent.ConcurrentBag<int>();
var diff = new System.Collections.Concurrent.ConcurrentBag<int>();
Parallel.ForEach(both, i =>
{
    try
    {
        if (Md5(0, i) == Md5(1, i)) same.Add(i); else diff.Add(i);
    }
    catch (Exception ex) { Console.WriteLine($"  err {i}: {ex.Message}"); }
});
Console.WriteLine("identical art in both sets (" + same.Count + "):");
foreach (var i in same.OrderBy(x => x)) Console.Write(" " + i);
Console.WriteLine();
Console.WriteLine("same index but DIFFERENT art (" + diff.Count + "):");
foreach (var i in diff.OrderBy(x => x)) Console.Write(" " + i);
Console.WriteLine();

// zoom sheet of the identical league-wide ones
var outDir = Path.Combine(Path.GetTempPath(), "hotspots");
var zoom = Path.Combine(outDir, "league_wide");
Directory.CreateDirectory(zoom);
foreach (var i in same.OrderBy(x => x))
{
    foreach (var set in new[] { 0, 1 })
    {
        var dst = Path.Combine(zoom, $"{i}_s{set}.png");
        if (File.Exists(dst)) continue;
        var s = IceHotspots.Read(view, index, set, i);
        ImageIO.SavePng(s.Image, dst);
    }
}
Console.WriteLine("zoomed to " + zoom);
