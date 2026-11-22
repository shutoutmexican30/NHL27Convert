using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);
var name = "content/character/skater/player/skate/bauer/vapor/components/player_skate_bauer_vapor_hyperlite_guard_half_mesh";
var stored = BundleIndex.ReadBytes(index.ResByName[name][0].Data);
byte[] r = stored; try { r = ChunkBlocks.Parse(stored).Decode(); } catch { }
uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(at));
ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(at));
float F(int at) => BinaryPrimitives.ReadSingleLittleEndian(r.AsSpan(at));

Console.WriteLine($"resLen {r.Length}");
Console.WriteLine("u32 dump 0x88..0x160:");
for (int at = 0x88; at < 0x160; at += 4)
    Console.WriteLine($"  +0x{at:X3}: {U32(at),10}  0x{U32(at):X8}  f={F(at):G6}");
Console.WriteLine("u64 candidates 0xA0..0x160 (value in res range):");
for (int at = 0xA0; at < 0x160; at += 8)
{
    ulong v = U64(at);
    if (v > 0 && v < (ulong)r.Length) Console.WriteLine($"  +0x{at:X3}: 0x{v:X} ({v})");
}
