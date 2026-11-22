using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);

void Dump(string name)
{
    var stored = BundleIndex.ReadBytes(index.ResByName[name][0].Data);
    byte[] r = stored; try { r = ChunkBlocks.Parse(stored).Decode(); } catch { }
    uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(at));
    Console.WriteLine($"{name.Split('/')[^1]} (res {r.Length}):");
    // LOD0 at lodOffsets[0]
    long lod0 = (long)BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(0x20));
    int secCount = (int)U32((int)lod0 + 8);
    long secOff = (long)BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan((int)lod0 + 0xC));
    for (int i = 0; i < Math.Min(secCount, 3); i++)
    {
        int s = (int)secOff + i * 0xE0;
        Console.Write($"  sec{i} @0x{s:X}: bytes 0x30..0x4F: {Convert.ToHexString(r.AsSpan(s + 0x30, 32))}");
        Console.WriteLine($"  u64@0x40=0x{BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(s + 0x40)):X}");
        for (int d = 0; d < 2; d++)
        {
            int at = s + 0x48 + d * 44;
            var strides = Enumerable.Range(0, 4).Select(k => r[at + 32 + k * 2]).ToList();
            var decls = string.Join(" ", Enumerable.Range(0, 8).Select(e => $"{r[at + e * 4]:x2}{r[at + e * 4 + 1]:x2}{r[at + e * 4 + 2]:x2}{r[at + e * 4 + 3]:x2}"));
            Console.WriteLine($"     decl{d}@0x{at:X}: strides {string.Join(",", strides)}  elems {decls}");
        }
    }
    Console.WriteLine();
}

Dump("content/worlds/rink/ice/components/rink_ice_nhl_mesh");
Dump("content/character/skater/player/skate/bauer/vapor/components/player_skate_bauer_vapor_hyperlite_guard_half_mesh");
