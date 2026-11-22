using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);

var name = index.ResByName.Keys.First(k => k.EndsWith("crowd_head_male_chubby_2_liutmask_swarmmesh_mesh", StringComparison.Ordinal));
var res = index.ResByName[name][0];
var stored = BundleIndex.ReadBytes(res.Data);
byte[] hdr = stored; try { hdr = ChunkBlocks.Parse(stored).Decode(); } catch { }

// LOD0: idx 52064 B, vtx 258384 B. find u16 windows of 26032 values all < 60000
int need = 26032;
for (long at = 0; at + 52064 <= hdr.Length; at += 2)
{
    bool ok = true;
    for (int i = 0; i < need; i++)
    {
        uint v = BitConverter.ToUInt16(hdr, (int)(at + i * 2));
        if (v >= 60000) { ok = false; break; }
    }
    if (ok) Console.WriteLine($"u16 window at {at} (0x{at:X})");
    if (at > 400000) break;
}
Console.WriteLine("scan done");
