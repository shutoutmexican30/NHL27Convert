// Stopgap NHL 24 mesh exporter: loads the game through FMT (read only, via a read view) and writes the highest
// LOD of each requested mesh asset as GLB (positions, UVs, indices, one primitive per section, named after the
// section's material), plus export-report.json with per-section vertex layouts and bounding boxes.
// FMT is used for the PS4 containers and the MeshSet header; the vertex/index decoding below is our own.
// usage: MeshExport <game-or-view> <out-dir> <mesh-ebx>...
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using FMT.Logging;
using FMT.ProfileSystem;
using FrostySdk.Frostbite.IO.Output;
using FrostySdk.Managers;
using FrostySdk.Resources;
using v2k4FIFAModdingCL;

if (args.Length < 3)
{
    Console.WriteLine("usage: MeshExport <game-or-view> <out-dir> <mesh-ebx>...");
    return 2;
}
var (view, outDir) = (args[0], args[1]);
Directory.CreateDirectory(outDir);

ProfileManager.Initialize("NHL24PS4");
if (!await GameInstanceSingleton.InitializeSingletonAsync(Path.Combine(view, "package.mft"), loadCache: true, new ConsoleLogger()))
{
    Console.WriteLine("ERROR FMT could not load the game.");
    return 1;
}

var report = new List<object>();
if (args[2] == "--texture-resource")
{
    var probes = new List<object>();
    foreach (var name in args.Skip(3))
    {
        var res = AssetManager.Instance.GetResEntry(name) ?? throw new InvalidDataException("Missing texture resource: " + name);
        var raw = AssetManager.Instance.GetRes(res).ToArray();
        var stem = name.Split('/')[^1];
        File.WriteAllBytes(Path.Combine(outDir, stem + ".res"), raw);
        var chunks = new List<object>(); var seen = new HashSet<Guid>();
        for (int at = 0; at + 16 <= raw.Length; at += 4)
        {
            var id = new Guid(raw.AsSpan(at, 16));
            if (!AssetManager.Instance.Chunks.ContainsKey(id) || !seen.Add(id)) continue;
            var data = AssetManager.Instance.GetChunk(AssetManager.Instance.GetChunkEntry(id)).ToArray();
            File.WriteAllBytes(Path.Combine(outDir, stem + "." + id + ".chunk"), data);
            chunks.Add(new { id, offsetInHeader = at, bytes = data.Length });
            Console.WriteLine($"TEXTURE CHUNK {name} {id} {data.Length} bytes");
        }
        probes.Add(new { name, resourceBytes = raw.Length, chunks });
    }
    File.WriteAllText(Path.Combine(outDir, "texture-resources.json"), JsonSerializer.Serialize(probes, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
if (args[2] == "--inventory")
{
    var needles = args.Skip(3).ToArray();
    var entries = AssetManager.Instance.EBX.Values.Where(e => needles.Any(n => e.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
        .Select(e => new { e.Name, e.Type }).ToList();
    File.WriteAllText(Path.Combine(outDir, "asset-inventory.json"), JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"INVENTORY {entries.Count} matching EBX assets");
    return 0;
}
foreach (var name in args.Skip(2))
{
    try
    {
        var ebx = AssetManager.Instance.GetEbxEntry(name) ?? throw new InvalidOperationException("no such EBX asset");
        var meshSet = new MeshSetToFbxExport().LoadMeshSet(ebx);
        var res = AssetManager.Instance.GetResEntry(name);
        if (res is not null)
            File.WriteAllBytes(Path.Combine(outDir, name.Split('/')[^1] + ".meshset"), AssetManager.Instance.GetRes(res).ToArray());
        var lod = meshSet.Lods[0];
        byte[] data = lod.ChunkId != Guid.Empty
            ? AssetManager.Instance.GetChunk(AssetManager.Instance.GetChunkEntry(lod.ChunkId)).ToArray()
            : lod.InlineData ?? throw new InvalidOperationException("LOD 0 has neither a chunk nor inline data");
        File.WriteAllBytes(Path.Combine(outDir, name.Split('/')[^1] + ".lod0.bin"), data);
        int indexSize = lod.IndexUnitSize / 8;

        var prims = new List<Prim>();
        var sections = new List<object>();
        foreach (var s in lod.Sections)
        {
            var layout = new List<string>();
            if (s.Name == "" || s.VertexCount == 0 || s.PrimitiveCount == 0) { sections.Add(new { s.Name, skipped = true }); continue; }
            var decl = s.GeometryDeclDesc[0];
            var pos = new Vector3[s.VertexCount];
            var uv = new Vector2[s.VertexCount];
            bool hasUv = false;
            long streamBase = s.VertexOffset;
            for (int j = 0; j < decl.Streams.Length; j++)
            {
                int stride = decl.Streams[j].VertexStride;
                if (stride == 0) continue;
                int elemOffset = 0;
                foreach (var el in decl.Elements)
                {
                    if (el.StreamIndex != j || el.Usage == VertexElementUsage.Unknown) continue;
                    layout.Add($"s{j}+{elemOffset} {el.Usage} {el.Format} ({el.Size})");
                    for (int k = 0; k < s.VertexCount; k++)
                    {
                        long p = streamBase + (long)k * stride + elemOffset;
                        if (el.Usage == VertexElementUsage.Pos)
                            pos[k] = ReadVec3(data, p, el.Format);
                        else if ((int)el.Usage == 33 && !hasUvSet(el))
                            uv[k] = ReadVec2(data, p, el.Format);
                    }
                    if ((int)el.Usage == 33) hasUv = true;
                    elemOffset += el.Size;
                }
                streamBase += (long)stride * s.VertexCount;
            }
            var idx = new uint[s.PrimitiveCount * 3];
            long ib = lod.VertexBufferSize + (long)s.StartIndex * indexSize;
            for (int i = 0; i < idx.Length; i++)
                idx[i] = indexSize == 4 ? BitConverter.ToUInt32(data, (int)(ib + i * 4)) : BitConverter.ToUInt16(data, (int)(ib + i * 2));
            var min = pos.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var max = pos.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            prims.Add(new Prim(s.Name, pos, hasUv ? uv : null, idx));
            sections.Add(new { s.Name, s.VertexCount, s.PrimitiveCount, uv = hasUv, min = new[] { min.X, min.Y, min.Z }, max = new[] { max.X, max.Y, max.Z }, layout, metadata = Facts(s) });
        }
        var file = Path.Combine(outDir, name.Split('/')[^1] + ".glb");
        Glb.Write(file, name.Split('/')[^1], prims);
        Console.WriteLine($"OK {name} sections={prims.Count} verts={prims.Sum(p => p.Pos.Length)} -> {file}");
        report.Add(new { name, file, type = ebx.Type, lodType = lod.Type.ToString(), lods = meshSet.Lods.Count, indexSize, meshMetadata = Facts(meshSet), lodMetadata = Facts(lod), sections });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR {name}: {ex.GetType().Name}: {ex.Message}");
        report.Add(new { name, error = ex.ToString() });
    }
}
File.WriteAllText(Path.Combine(outDir, "export-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return 0;

static bool hasUvSet(GeometryDeclarationDesc.Element _) => false;

static Dictionary<string, object> Facts(object value)
{
    var result = new Dictionary<string, object>();
    foreach (var property in value.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
    {
        if (property.GetIndexParameters().Length > 0 || !property.CanRead) continue;
        try
        {
            var v = property.GetValue(value);
            if (v is null) continue;
            var t = v.GetType();
            if (t.IsPrimitive || t.IsEnum || v is string || v is Guid) result[property.Name] = v.ToString();
            else if (property.Name.Contains("Bone") && v is System.Collections.IEnumerable sequence)
                result[property.Name] = sequence.Cast<object>().Take(512).Select(x => x?.ToString()).ToArray();
        }
        catch (Exception ex) { result[property.Name] = "unavailable: " + ex.GetBaseException().Message; }
    }
    return result;
}

static float Half(byte[] d, long p) => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(d, (int)p));

static Vector3 ReadVec3(byte[] d, long p, VertexElementFormat f) => f switch
{
    VertexElementFormat.Float3 or VertexElementFormat.Float4 => new(BitConverter.ToSingle(d, (int)p), BitConverter.ToSingle(d, (int)p + 4), BitConverter.ToSingle(d, (int)p + 8)),
    VertexElementFormat.Half3 or VertexElementFormat.Half4 => new(Half(d, p), Half(d, p + 2), Half(d, p + 4)),
    _ => throw new NotSupportedException($"position format {f}"),
};

static Vector2 ReadVec2(byte[] d, long p, VertexElementFormat f) => f switch
{
    VertexElementFormat.Float2 => new(BitConverter.ToSingle(d, (int)p), BitConverter.ToSingle(d, (int)p + 4)),
    VertexElementFormat.Half2 => new(Half(d, p), Half(d, p + 2)),
    VertexElementFormat.Short2N => new(BitConverter.ToInt16(d, (int)p) / 32767f * 0.5f + 0.5f, BitConverter.ToInt16(d, (int)p + 2) / 32767f * 0.5f + 0.5f),
    _ => throw new NotSupportedException($"uv format {f}"),
};

record Prim(string Material, Vector3[] Pos, Vector2[] Uv, uint[] Idx);

static class Glb
{
    /// <summary>Minimal glTF 2.0 binary: one mesh, one primitive per section, one material per section name.</summary>
    public static void Write(string path, string meshName, List<Prim> prims)
    {
        using var bin = new MemoryStream();
        var bw = new BinaryWriter(bin);
        var views = new List<object>();
        var accessors = new List<object>();
        var primitives = new List<object>();
        var materials = new List<object>();
        int View(Action write, int target)
        {
            long start = bin.Position;
            write();
            while (bin.Position % 4 != 0) bw.Write((byte)0);
            views.Add(new { buffer = 0, byteOffset = start, byteLength = bin.Position - start, target });
            return views.Count - 1;
        }
        foreach (var p in prims)
        {
            var min = p.Pos.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var max = p.Pos.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            int pv = View(() => { foreach (var v in p.Pos) { bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z); } }, 34962);
            accessors.Add(new { bufferView = pv, componentType = 5126, count = p.Pos.Length, type = "VEC3", min = new[] { min.X, min.Y, min.Z }, max = new[] { max.X, max.Y, max.Z } });
            int posAcc = accessors.Count - 1;
            int? uvAcc = null;
            if (p.Uv is not null)
            {
                int uvv = View(() => { foreach (var v in p.Uv) { bw.Write(v.X); bw.Write(v.Y); } }, 34962);
                accessors.Add(new { bufferView = uvv, componentType = 5126, count = p.Uv.Length, type = "VEC2" });
                uvAcc = accessors.Count - 1;
            }
            int iv = View(() => { foreach (var i in p.Idx) bw.Write(i); }, 34963);
            accessors.Add(new { bufferView = iv, componentType = 5125, count = p.Idx.Length, type = "SCALAR" });
            materials.Add(new { name = p.Material, doubleSided = true });
            var attrs = new Dictionary<string, int> { ["POSITION"] = posAcc };
            if (uvAcc is not null) attrs["TEXCOORD_0"] = uvAcc.Value;
            primitives.Add(new { attributes = attrs, indices = accessors.Count - 1, material = materials.Count - 1 });
        }
        var json = JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0", generator = "CIMS MeshExport" },
            scene = 0,
            scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new[] { new { mesh = 0, name = meshName } },
            meshes = new[] { new { name = meshName, primitives } },
            materials,
            accessors,
            bufferViews = views,
            buffers = new[] { new { byteLength = bin.Length } },
        });
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - jsonBytes.Length % 4) % 4;
        using var fs = File.Create(path);
        var w = new BinaryWriter(fs);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonBytes.Length + jsonPad + 8 + bin.Length));
        w.Write((uint)(jsonBytes.Length + jsonPad)); w.Write(0x4E4F534Au); w.Write(jsonBytes); for (int i = 0; i < jsonPad; i++) w.Write((byte)' ');
        w.Write((uint)bin.Length); w.Write(0x004E4942u); w.Write(bin.ToArray());
    }
}

sealed class ConsoleLogger : ILogger
{
    public void Log(string text, params object[] vars) { }
    public void LogWarning(string text, params object[] vars) { }
    public void LogError(string text, params object[] vars) => Console.WriteLine("fmt: " + text);
    public void LogProgress(int progress) { }
}
