using System.Buffers.Binary;
using Suite.Core.Frostbite;
using System.Text;

// Probe v2: locate the NHL 24 rink ice mesh chunk via the EBX resource table (GUID byte order fixed).
var game = args.Length > 0 ? args[0] : @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var outDir = args.Length > 1 ? args[1] : @"C:\dev\NHL27Convert\tools\MeshProbe\out";
Directory.CreateDirectory(outDir);

Console.WriteLine("Building bundle index...");
var index = BundleIndex.Build(game, p => Console.WriteLine("  " + p));
Console.WriteLine($"Index: {index.Bundles.Count:N0} bundles, {index.EbxByName.Count:N0} EBX, {index.ChunksById.Count:N0} chunk ids");

// Export every chunk id + RES rid for offline scanning of the mesh resource header.
using (var w = new StreamWriter(Path.Combine(outDir, "ids.txt")))
{
    foreach (var (id, list) in index.ChunksById)
        foreach (var c in list)
            w.WriteLine($"CHUNK {Convert.ToHexString(id.ToByteArray())} {c.Data.Size} {c.Data.Cas} {c.Data.Offset}");
    foreach (var (name, list) in index.ResByName)
        foreach (var r in list)
            w.WriteLine($"RES {Convert.ToHexString(BitConverter.GetBytes(r.Rid))} {r.Data.Size} {r.Data.Cas} {r.Data.Offset} {name}");
}

const string meshName = "content/worlds/rink/ice/components/rink_ice_nhl_mesh";
if (!index.EbxByName.TryGetValue(meshName, out var ebxList)) { Console.WriteLine("mesh EBX not found"); return; }
Console.WriteLine($"{meshName}: {ebxList.Count} copies");

// GUID transforms to try: raw manifest bytes, and EA nibble-swap (each byte hi<->lo).
static byte[] NibbleSwap(byte[] b)
{
    var o = new byte[b.Length];
    for (int i = 0; i < b.Length; i++) o[i] = (byte)((b[i] >> 4) | (b[i] << 4));
    return o;
}

var variants = new Dictionary<string, Guid>(); // transformed-byte-key -> chunk id
foreach (var (id, _) in index.ChunksById)
{
    variants[GuidKey(id.ToByteArray())] = id;
    variants[GuidKey(NibbleSwap(id.ToByteArray()))] = id;
}
static string GuidKey(byte[] b) => Convert.ToHexString(b);

foreach (var ebx in ebxList)
{
    var ebxBytes = Suite.Core.Frostbite.ChunkBlocks.Parse(BundleIndex.ReadBytes(ebx.Data)).Decode();
    var bytes = ebxBytes;
    File.WriteAllBytes(Path.Combine(outDir, "mesh_decompressed.ebx"), bytes);
    Console.WriteLine($"EBX {bytes.Length:N0} bytes (decompressed), {Path.GetFileName(ebx.Data.Cas)} @{ebx.Data.Offset}, TOC {Path.GetFileName(ebx.Bundle.Toc)}");
    Console.WriteLine($"  head: {BitConverter.ToString(bytes, 0, 64)}");

    // Parse EFIX: data offsets, pointer offsets, resource offsets tables; each resource offset points into
    // EBXD where a 16-byte chunk GUID sits.
    var resources = EfixResources(bytes);
    Console.WriteLine($"  EFIX resources: {resources.Count}");
    foreach (var (off, guidBytes) in resources)
    {
        var raw = new Guid(guidBytes);
        Guid hit = Guid.Empty;
        string how = "";
        if (rawHit(raw) is { } a) { hit = a; how = "RAW MATCH"; }
        else if (Nibble(raw) is { } c) { hit = c; how = "NIBBLE MATCH"; }
        else if (HalfHit(guidBytes) is { } h) { hit = h; how = "HALF(8B) MATCH"; }
        Console.WriteLine($"    EBXD+{off:X4}: {raw}  [{BitConverter.ToString(guidBytes)}] {how}");
        if (hit != Guid.Empty && index.ChunksById.TryGetValue(hit, out var cl))
            foreach (var c2 in cl)
                Console.WriteLine($"      -> chunk data {c2.Data.Size:N0} bytes, cas {Path.GetFileName(c2.Data.Cas)} @{c2.Data.Offset} logical {c2.LogicalOffset:X}+{c2.LogicalSize:X}");
    }

    Guid? rawHit(Guid g) => variants.TryGetValue(GuidKey(g.ToByteArray()), out var v) ? v : null;
    Guid? Nibble(Guid g) => variants.TryGetValue(GuidKey(NibbleSwap(g.ToByteArray())), out var v) ? v : null;
    Guid? HalfHit(byte[] gb)
    {
        // The mesh data may be a RES resource referenced by its 8-byte RID.
        ulong le = BitConverter.ToUInt64(gb, 0), be = BinaryPrimitives.ReadUInt64BigEndian(gb);
        foreach (var list in index.ResByName.Values)
            foreach (var r in list)
                if (r.Rid == le || r.Rid == be)
                {
                    Console.WriteLine($"      RES match: {r.Name} type 0x{r.Type:X8} rid 0x{r.Rid:X16} data {r.Data.Size:N0} bytes, cas {Path.GetFileName(r.Data.Cas)} @{r.Data.Offset}");
                    var resBytes = Suite.Core.Frostbite.ChunkBlocks.Parse(BundleIndex.ReadBytes(r.Data)).Decode();
                    File.WriteAllBytes(Path.Combine(outDir, "mesh_res.bin"), resBytes);
                    Console.WriteLine($"      decoded {resBytes.Length:N0} bytes -> mesh_res.bin, head {BitConverter.ToString(resBytes, 0, Math.Min(32, resBytes.Length))}");
                    return Guid.Empty; // sentinel: handled
                }
        return null;
    }

    // Dump all strings in the EBX.
    File.WriteAllBytes(Path.Combine(outDir, "mesh_" + Path.GetFileName(ebx.Bundle.Toc) + ".ebx"), bytes);
    var strs = new List<string>();
    int start = -1;
    for (int i = 0; i <= bytes.Length; i++)
    {
        bool ascii = i < bytes.Length && bytes[i] is >= 0x20 and <= 0x7E;
        if (ascii && start < 0) start = i;
        if (!ascii && start >= 0)
        {
            if (i - start >= 4) strs.Add(Encoding.ASCII.GetString(bytes, start, i - start));
            start = -1;
        }
    }
    Console.WriteLine("  strings: " + string.Join(" | ", strs));
    break; // one copy is enough for the probe
}

static List<(int Off, byte[] Guid)> EfixResources(byte[] b)
{
    var result = new List<(int, byte[])>();
    // RIFF EBX: 'RIFF' at 16 per earlier dump; EBXD chunk then EFIX. Reuse EbxRiff's discovery logic inline.
    if (Encoding.ASCII.GetString(b, 0, 4) != "RIFF") { Console.WriteLine("  not RIFF at 0 (decompressed)"); return result; }
    int shift = 0;
    int ebxdSize = BitConverter.ToInt32(b, shift + 16);
    int ebxdEnd = shift + 20 + ebxdSize;
    int efix = (ebxdEnd + 1) & ~1;
    if (Encoding.ASCII.GetString(b, efix, 4) != "EFIX") { Console.WriteLine($"  no EFIX at {efix}"); return result; }
    int efixSize = BitConverter.ToInt32(b, efix + 4);
    int p = efix + 8, efixEnd = p + efixSize;
    p += 16; // file GUID
    int DataStart = shift + 32; // Align(shift+20, 16)
    uint Count() { uint v = BitConverter.ToUInt32(b, p); p += 4; return v; }
    void Skip(uint count, int stride) => p += checked((int)count * stride);
    Skip(Count(), 16);      // class GUIDs
    Skip(Count(), 4);       // signatures
    _ = Count();            // exported instances (count only)
    uint dataCount = Count(); int dataAt = p; Skip(dataCount, 4);
    uint ptrCount = Count(); int ptrAt = p; Skip(ptrCount, 4);
    uint resCount = Count(); int resAt = p; Skip(resCount, 4);
    Console.WriteLine($"  EFIX: data={dataCount} ptrs={ptrCount} resources={resCount}");
    for (int i = 0; i < resCount; i++)
    {
        int off = DataStart + BitConverter.ToInt32(b, resAt + i * 4);
        if (off + 16 <= b.Length) result.Add((off, b.AsSpan(off, 16).ToArray()));
    }
    return result;
}

// The mesh geometry chunk named by the resource header.
var meshId = new Guid(Convert.FromHexString("9196F5AC46F93935D1F4459DCDD5CCED"));
if (index.ChunksById.TryGetValue(meshId, out var meshChunks))
{
    var mc = meshChunks[0];
    Console.WriteLine($"mesh chunk: {meshChunks.Count} copies, using {Path.GetFileName(mc.Data.Cas)} @{mc.Data.Offset} ({mc.Data.Size:N0} stored)");
    var decoded = Suite.Core.Frostbite.ChunkBlocks.Parse(BundleIndex.ReadBytes(mc.Data)).Decode();
    File.WriteAllBytes(Path.Combine(outDir, "mesh_chunk.bin"), decoded);
    Console.WriteLine($"decoded {decoded.Length:N0} bytes -> mesh_chunk.bin");
}
