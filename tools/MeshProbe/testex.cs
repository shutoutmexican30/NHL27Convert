using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);

// sample textures the way the Asset browser does
var names = index.ResByName.Keys.Where(k => !k.EndsWith("_mesh", StringComparison.Ordinal)).ToList();
var sample = names.Where((_, i) => i % 211 == 0).Take(60).ToList();
sample.AddRange(index.ResByName.Keys.Where(k => k.Contains("centerlogo")).Take(4));

int bc7 = 0, bcX = 0, fail = 0;
foreach (var name in sample.Distinct())
{
    try
    {
        var res = index.ResByName[name][0];
        var stored = BundleIndex.ReadBytes(res.Data);
        byte[] header; try { header = ChunkBlocks.Parse(stored).Decode(); } catch { header = stored; }
        if (header.Length > 48 && header[12] == 0x43)
        {
            var tr = TextureRes.Parse(header);
            var full = ChunkReplace.ReadAll(game, index, tr.ChunkGuid)
                .FirstOrDefault(r => r.LogicalOffset == 0 && r.Raw.Length == tr.PayloadSize).Raw
                ?? throw new InvalidOperationException("no chunk copy");
            var img = Bc7.DecodeTiled(TextureChunk.Parse(full, tr).Mips[0], tr.Width, tr.Height);
            bc7++;
            Console.WriteLine($"BC7  {name.Split('/')[^1],-50} {tr.Width}x{tr.Height}");
            continue;
        }
        try
        {
            var tr = TextureRes.Parse(header);
            long size = tr.PayloadSize * Ps4Codec.Slices(header);
            var full = ChunkReplace.ReadAll(game, index, tr.ChunkGuid)
                .FirstOrDefault(r => r.LogicalOffset == 0 && r.Raw.Length == size).Raw
                ?? throw new InvalidOperationException("no chunk copy");
            var mip = TextureChunk.Parse(full[..(int)tr.PayloadSize], tr).Mips[0];
            var img = tr.BlockBytes == 8 ? Bc1.DecodeTiled(mip, tr.Width, tr.Height) : Bc5.DecodeTiled(mip, tr.Width, tr.Height);
            bcX++;
            Console.WriteLine($"BC{tr.BlockBytes}  {name.Split('/')[^1],-50} {tr.Width}x{tr.Height}");
        }
        catch (Exception ex2)
        {
            fail++;
            if (fail <= 8) Console.WriteLine($"??   {name.Split('/')[^1],-50} {ex2.Message}");
        }
    }
    catch (Exception ex)
    {
        fail++;
        if (fail <= 8) Console.WriteLine($"ERR  {name.Split('/')[^1],-50} {ex.Message}");
    }
}
Console.WriteLine($"\nBC7: {bc7}, BC1/5: {bcX}, not-texture/fail: {fail} of {sample.Distinct().Count()}");
