using System.Buffers.Binary;
using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);
var apply = new RinkMeshApply(game);
var copies = apply.Find(index);
Console.WriteLine($"copies: {copies.Count}");

// unique (cas, toc pairs) and try parsing each res/chunk stored entry
int failRes = 0, failChunk = 0, ok = 0;
foreach (var c in copies)
{
    var toc = File.ReadAllBytes(c.Toc);
    uint RO(int at) => BinaryPrimitives.ReadUInt32BigEndian(toc.AsSpan(at));
    byte[] Stored(uint off, uint size) { using var fs = new FileStream(c.Cas, FileMode.Open, FileAccess.Read, FileShare.Read); fs.Position = off; var b = new byte[size]; fs.ReadExactly(b); return b; }
    try { ChunkBlocks.Parse(Stored(RO(c.ResPairAt), RO(c.ResPairAt + 4))).Decode(); } catch { failRes++; Console.WriteLine($"RES parse fail: {Path.GetFileName(c.Toc)} pair 0x{c.ResPairAt:X} cas {Path.GetFileName(c.Cas)}"); }
    try { ChunkBlocks.Parse(Stored(RO(c.ChunkPairAt), RO(c.ChunkPairAt + 4))).Decode(); } catch (Exception ex) { failChunk++; if (failChunk <= 3) { var s = Stored(RO(c.ChunkPairAt), RO(c.ChunkPairAt + 4)); Console.WriteLine($"CHUNK parse fail ({ex.Message}): {Path.GetFileName(c.Toc)} cas {Path.GetFileName(c.Cas)} size {s.Length}"); Console.WriteLine("  first32: " + Convert.ToHexString(s.AsSpan(0, 32))); } }
    ok++;
}
Console.WriteLine($"ok {ok}, res fails {failRes}, chunk fails {failChunk}");
