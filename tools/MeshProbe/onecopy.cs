using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);
var apply = new RinkMeshApply(game);
var copies = apply.Find(index);

foreach (var c in copies.Take(4))
{
    var toc = File.ReadAllBytes(c.Toc);
    uint RO(int at) => BinaryPrimitives.ReadUInt32BigEndian(toc.AsSpan(at));
    string layer = c.Toc.Contains("game-UPDATE") ? "PATCH" : "BASE ";
    Console.WriteLine($"{layer} {Path.GetFileName(c.Toc)} resPair ({RO(c.ResPairAt)},{RO(c.ResPairAt + 4)}) chunkPair ({RO(c.ChunkPairAt)},{RO(c.ChunkPairAt + 4)}) manPair ({RO(c.ManPairAt)},{RO(c.ManPairAt + 4)})");
    byte[] Stored(string cas, uint off, uint size) { using var fs = new FileStream(cas, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); fs.Position = off; var b = new byte[size]; fs.ReadExactly(b); return b; }
    var rs = Stored(c.ResCas, RO(c.ResPairAt), RO(c.ResPairAt + 4));
    Console.WriteLine($"  res first16 {Convert.ToHexString(rs.AsSpan(0, 16))}");
    try { var d = ChunkBlocks.Parse(rs).Decode(); Console.WriteLine($"  res decodes to {d.Length}"); } catch (Exception ex) { Console.WriteLine($"  res parse: {ex.Message}"); }
    var ms = Stored(c.ManCas, RO(c.ManPairAt), RO(c.ManPairAt + 4));
    Console.WriteLine($"  manifest at pair: {ms.Length} bytes, first8 {Convert.ToHexString(ms.AsSpan(0, 8))}");
    var ms2 = Stored(c.ManCas, c.ManifestOff, c.ManifestSize);
    Console.WriteLine($"  manifest at ManifestOff: {ms2.Length} bytes, first8 {Convert.ToHexString(ms2.AsSpan(0, 8))}");
}
