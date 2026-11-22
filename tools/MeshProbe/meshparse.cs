using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);

var names = index.ResByName.Keys.Where(k => k.EndsWith("_mesh", StringComparison.Ordinal)).ToList();
var sample = names.Where((_, i) => i % 37 == 0).Take(500).ToList();
sample.Add("content/worlds/rink/ice/components/rink_ice_nhl_mesh");
sample.Add("content/character/skater/player/skate/bauer/vapor/components/player_skate_bauer_vapor_hyperlite_guard_half_mesh");

int ok = 0, fail = 0;
var failMsgs = new System.Collections.Generic.Dictionary<string, int>();
var types = new System.Collections.Generic.Dictionary<uint, int>();
foreach (var name in sample.Distinct())
{
    try
    {
        var res = index.ResByName[name][0];
        var mesh = MeshSet.Load(game, index, name);
        long verts = 0;
        for (int li = 0; li < mesh.Lods.Count; li++)
        {
            var lod = mesh.Lods[li];
            try
            {
                var chunk = MeshSet.LoadLodChunk(index, res, lod);
                foreach (var s in lod.Sections)
                {
                    var p = MeshSet.Positions(lod, s, chunk);
                    verts += s.VertexCount;
                    int bad = 0;
                    for (int v = 0; v < s.VertexCount; v++)
                        if (float.IsNaN(p[v * 3]) || float.IsNaN(p[v * 3 + 1]) || float.IsNaN(p[v * 3 + 2])) bad++;
                    if (bad > 0) throw new InvalidDataException($"{s.Name}: {bad} NaN positions");
                    if (s.VertexCount > 0 && s.Stride < 8) throw new InvalidDataException($"{s.Name}: stride {s.Stride}");
                }
                var idx = MeshSet.Indices(lod, chunk);
                if (lod.IndexBufferSize > 0 && idx.Length == 0) throw new InvalidDataException("index buffer not found");
            }
            catch (Exception lex)
            {
                throw new InvalidDataException($"lod{li} inline={lod.InlineOffset != 0}: {lex.Message}");
            }
        }
        ok++;
        Console.WriteLine($"OK   {name.Split('/')[^1],-58} lods {mesh.Lods.Count} secs {mesh.Lods.Sum(l => l.Sections.Count),3} verts {verts,7} type {mesh.MeshType}");
    }
    catch (Exception ex)
    {
        fail++;
        Console.WriteLine($"FAIL {name.Split('/')[^1],-58} {ex.Message}");
        var key = ex.Message.Split(':')[0];
        failMsgs[key] = failMsgs.TryGetValue(key, out var n) ? n + 1 : 1;
    }
}
Console.WriteLine($"\nparsed {ok}/{ok + fail}");
var total = index.ResByName.Keys.Count(k => k.EndsWith("_mesh", StringComparison.Ordinal));
Console.WriteLine($"total _mesh resources in game: {total}");
foreach (var kv in failMsgs.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {kv.Value,4} × {kv.Key}");
