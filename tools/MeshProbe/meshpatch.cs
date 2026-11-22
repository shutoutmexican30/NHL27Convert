using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Core.FullRink;

// Full-rink mesh patcher: validates, patches, writes, verifies.
var game = args.Length > 0 ? args[0] : @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var outDir = @"C:\dev\NHL27Convert\tools\MeshProbe\out";
Directory.CreateDirectory(outDir);

Console.WriteLine("Building bundle index...");
var index = BundleIndex.Build(game, p => Console.WriteLine("  " + p));

var apply = new RinkMeshApply(game);
var copies = apply.Find(index);
Console.WriteLine($"patch copies: {copies.Count}");
foreach (var c in copies)
    Console.WriteLine($"  {Path.GetFileName(c.Toc)} manPair 0x{c.ManPairAt:X} resPair 0x{c.ResPairAt:X} chunkPair 0x{c.ChunkPairAt:X} resCas {Path.GetFileName(c.ResCas)} chunkCas {Path.GetFileName(c.ChunkCas)}");

var (res, chunk, sections) = apply.Read(index, copies[0]);
Console.WriteLine($"mesh: RES {res.Length:N0} bytes, chunk {chunk.Length:N0} bytes, {sections.Count} sections");
foreach (var s in sections)
    Console.WriteLine($"  [{s.Index,2}] {s.Name,-28} tris {s.Tris,4} verts {s.Verts,4} pos +0x{s.PosOff:X5} bbox ({s.MinX,7:F3},{s.MinZ,7:F3})-({s.MaxX,7:F3},{s.MaxZ,7:F3})");

// Patch (res is modified in place, so clone it first).
var newRes = (byte[])res.Clone();
var log = new List<string>();
var newChunk = FullRinkMesh.Patch(newRes, chunk, sections, log);
foreach (var l in log) Console.WriteLine("  " + l);

// Sanity-render: every section's vertices projected into the texture frame.
RenderFrame(newRes, newChunk, sections, Path.Combine(outDir, "mesh_stretched.png"));

// Write.
var report = apply.Apply(newRes, newChunk, index, copies, p => Console.WriteLine("  " + p));
foreach (var l in report) Console.WriteLine("  " + l);

// Verify: fresh read of the first copy must show the stretched logo.
var index2 = BundleIndex.Build(game);
var (_, chunk2, sections2) = apply.Read(index2, apply.Find(index2)[0]);
var logo2 = sections2.First(s => s.Name == "ice_centerlogo_mat");
Console.WriteLine($"verify: centre logo bbox now ({logo2.MinX:F2},{logo2.MinZ:F2})-({logo2.MaxX:F2},{logo2.MaxZ:F2}), expect +-15.64 x +-31.28");
var cl = sections2.First(s => s.Name == "ice_centerline_mat");
Console.WriteLine($"verify: centreline collapsed to ({cl.MinX:F2},{cl.MinY:F3},{cl.MinZ:F2})");
bool ok = Math.Abs(logo2.MaxX - 15.64) < 0.05 && Math.Abs(logo2.MaxZ - 31.28) < 0.05
    && Math.Abs(cl.MinY - FullRinkMesh.CollapseY) < 0.001;
Console.WriteLine(ok ? "VERIFY PASS" : "VERIFY FAIL");
return ok ? 0 : 1;

static void RenderFrame(byte[] res, byte[] chunk, List<FullRinkMesh.Section> sections, string path)
{
    // Dump vertices per section as a scatter in frame space; a python script turns it into a picture.
    using var w = new StreamWriter(path + ".csv");
    w.WriteLine("section,x,z");
    foreach (var s in sections)
        for (int v = 0; v < s.Verts; v++)
        {
            int at = s.PosOff + v * 12;
            float x = BitConverter.ToSingle(chunk, at);
            float z = BitConverter.ToSingle(chunk, at + 8);
            w.WriteLine($"{s.Name},{x},{z}");
        }
}
