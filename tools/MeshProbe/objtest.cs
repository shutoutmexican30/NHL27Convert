using System;
using System.IO;
using System.Linq;
using System.Text;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);

var name = "content/worlds/rink/ice/components/rink_ice_nhl_mesh";
var res = index.ResByName[name][0];
var mesh = MeshSet.Load(game, index, name);
var lod = mesh.Lods[0];
var chunk = MeshSet.LoadLodChunk(index, res, lod);
var idx = MeshSet.Indices(lod, chunk);

var sb = new StringBuilder();
sb.AppendLine("# " + name);
int offset = 1; long totalV = 0, totalF = 0;
foreach (var s in lod.Sections)
{
    var p = MeshSet.Positions(lod, s, chunk);
    sb.AppendLine("g " + (s.Name.Length == 0 ? "section" : s.Name));
    for (int v = 0; v < s.VertexCount; v++)
        sb.AppendLine(FormattableString.Invariant($"v {p[v * 3]:0.######} {p[v * 3 + 1]:0.######} {p[v * 3 + 2]:0.######}"));
    totalV += s.VertexCount;
    if (idx.Length > 0)
    {
        int start = s.StartIndex, count = Math.Min(s.Primitives * 3, idx.Length - start);
        for (int t = start; t + 2 < start + count; t += 3)
        {
            if (idx[t] >= s.VertexCount || idx[t + 1] >= s.VertexCount || idx[t + 2] >= s.VertexCount) break;
            sb.AppendLine($"f {idx[t] + offset} {idx[t + 1] + offset} {idx[t + 2] + offset}");
            totalF++;
        }
    }
    offset += s.VertexCount;
}
File.WriteAllText(@"C:\dev\NHL27Convert\tools\MeshProbe\analysis\rink_ice.obj", sb.ToString());
Console.WriteLine($"OBJ: {totalV} verts, {totalF} faces, {sb.Length:n0} chars -> tools\\MeshProbe\\analysis\\rink_ice.obj");

// count v lines back
int vlines = File.ReadLines(@"C:\dev\NHL27Convert\tools\MeshProbe\analysis\rink_ice.obj").Count(l => l.StartsWith("v "));
Console.WriteLine($"re-parsed v lines: {vlines} (match: {vlines == totalV})");

// sample of face indices sanity: max index
int maxIdx = 0;
foreach (var l in File.ReadLines(@"C:\dev\NHL27Convert\tools\MeshProbe\analysis\rink_ice.obj"))
    if (l.StartsWith("f "))
        foreach (var tok in l.Split(' ').Skip(1))
            maxIdx = Math.Max(maxIdx, int.Parse(tok));
Console.WriteLine($"max face index {maxIdx} (should be <= {totalV})");
