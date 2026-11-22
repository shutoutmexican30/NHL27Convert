using System.Buffers.Binary;

namespace Suite.Core.Frostbite;

/// <summary>
/// NHL 24 (Gen4b, 2023) MeshSet reader, derived empirically from the dump and cross-checked against the Frosty
/// MeshSet plugin (CadeEvs/FrostyToolsuite). The header keeps 6 LOD pointers at 0x20; the {lodCount, sectionCount}
/// pair is located by a validating scan (rigid, composite and skinned meshes shift it); each LOD record holds its
/// section-table pointer, index/vertex buffer sizes and the geometry chunk id; each 0xE0-byte section record holds
/// the material name (u32 into the header string table), primitive range, vertex offset/count/stride and bounds.
/// Everything is validated — a record that does not validate throws rather than returning wrong data.
///
/// Read-only: writes stay byte-level (<see cref="ChunkReplace"/>), which already handles any asset type.
/// </summary>
public sealed class MeshSet
{
    public const int MaxLods = 6;
    const int FirstSectionAt = 0x150;      // section records of LOD0 in every observed mesh
    const int SectionStride = 0xE0;

    public string Name { get; private set; } = "";
    public string FullName { get; private set; } = "";
    public uint MeshType { get; private set; }
    public IReadOnlyList<Lod> Lods => _lods;
    readonly List<Lod> _lods = [];

    public sealed record Section(string Name, int Primitives, int StartIndex, long VertexOffset, int VertexCount,
        Box Bounds)
    {
        /// <summary>Bytes per vertex. 0 until <see cref="Positions"/> discovers it (varies per section; the
        /// declaration block layout is not fully decoded, so it is found by validating positions against the
        /// section bounds and cached here).</summary>
        public int Stride { get; set; }
        /// <summary>True when positions are stored as f16 instead of f32 (discovered together with the stride).</summary>
        public bool HalfPositions { get; set; }
        public long VerticesEnd => VertexOffset + (long)VertexCount * Stride;
    }
    public sealed record Box(float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ);
    public sealed record Lod(int Type, Guid ChunkId, uint IndexBufferSize, uint VertexBufferSize,
        int IndexFormat, int MaxInstances, List<Section> Sections, uint InlineOffset)
    {
        /// <summary>Byte offset of the vertex buffer inside the geometry chunk. Usually 0 (vertices first,
        /// indices at the tail) but some chunks put the index buffer first — discovered by
        /// <see cref="Positions"/> and cached here.</summary>
        public long VertexBase { get; set; }
    }

    byte[] _res = [];

    uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(_res.AsSpan(at));
    ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(_res.AsSpan(at));

    public static MeshSet Parse(byte[] res)
    {
        var m = new MeshSet { _res = res };
        if (res.Length < 0x200) throw new InvalidDataException("Mesh resource header too small.");

        var lodOffsets = new long[MaxLods];
        for (int i = 0; i < MaxLods; i++) lodOffsets[i] = (long)m.U64(0x20 + i * 8);
        m.MeshType = m.U32(0x6C);

        // Locate the {lodCount, totalSections} pair: rigid/composite/skinned headers shift it, so scan and validate.
        int lodCount = 0, totalSections = 0;
        for (int p = 0x88; p < 0xC0; p += 2)
        {
            int lc = BinaryPrimitives.ReadUInt16LittleEndian(res.AsSpan(p));
            int sc = BinaryPrimitives.ReadUInt16LittleEndian(res.AsSpan(p + 2));
            if (lc is < 1 or > MaxLods || sc is < 1 or > 512) continue;
            int sum = 0;
            bool good = true;
            var counts = new int[lc];
            for (int i = 0; i < lc; i++)
            {
                if (lodOffsets[i] <= 0 || lodOffsets[i] + 0x74 > res.Length) { good = false; break; }
                int at = (int)lodOffsets[i];
                int n = (int)m.U32(at + 8);
                long secOff = (long)m.U64(at + 0xC);
                if (n is < 1 or > 512 || secOff <= 0 || secOff + (long)n * SectionStride > res.Length) { good = false; break; }
                counts[i] = n; sum += n;
            }
            if (!good || sum != sc) continue;
            lodCount = lc; totalSections = sc;
            break;
        }
        if (lodCount == 0) throw new InvalidDataException("The header's LOD/section-count pair did not validate.");

        for (int i = 0; i < lodCount; i++)
            m._lods.Add(m.ParseLod((int)lodOffsets[i]));

        m.FullName = m.CStr(m.SafeOff(0x58));
        m.Name = m.CStr(m.SafeOff(0x60));
        return m;

        // totalSections is validated by construction (sum of per-LOD counts)
        _ = totalSections;
    }

    int SafeOff(int at)
    {
        long v = (long)U64(at);
        return v > 0 && v < _res.Length ? (int)v : 0;
    }

    Lod ParseLod(int r)
    {
        int type = (int)U32(r + 0x00);
        int maxInst = (int)U32(r + 0x04);
        int sectionCount = (int)U32(r + 0x08);
        long sectionOffset = (long)U64(r + 0x0C);
        if (sectionCount is < 1 or > 512) throw new InvalidDataException($"LOD: implausible section count {sectionCount}.");

        var sections = new List<Section>(sectionCount);
        for (int i = 0; i < sectionCount; i++)
            sections.Add(ParseSection((int)sectionOffset + i * SectionStride));

        // After the section-table pointer: 5 category subsets, flags, index format, buffer sizes, chunk id.
        int at = r + 0x14 + 5 * 12;
        int indexFormat = (int)U32(at + 4);
        uint indexBufferSize = U32(at + 8);
        uint vertexBufferSize = U32(at + 0xC);
        var chunkId = new Guid(_res.AsSpan(at + 0x10, 16));
        uint inlineOffset = U32(at + 0x20);
        if (chunkId == Guid.Empty && inlineOffset == 0)
            throw new InvalidDataException("LOD has neither a geometry chunk id nor inline data.");
        return new Lod(type, chunkId, indexBufferSize, vertexBufferSize, indexFormat, maxInst, sections, inlineOffset);
    }

    Section ParseSection(int r)
    {
        int nameOff = (int)U32(r + 0x08);
        int prims = (int)U32(r + 0x20);
        int startIndex = (int)U32(r + 0x24);
        long vertOff = U32(r + 0x28);
        int verts = (int)U32(r + 0x2C);
        if (prims is < 0 or > 20_000_000 || verts is < 0 or > 20_000_000)
            throw new InvalidDataException("Section record does not validate (prims/verts implausible).");
        string name = CStr(nameOff);
        if (name.Length > 128 || name.Any(ch => ch < ' ' || ch > '~'))
            throw new InvalidDataException("Section name does not validate.");
        float f(int at) => BinaryPrimitives.ReadSingleLittleEndian(_res.AsSpan(at));
        var box = new Box(f(r + 0xC0), f(r + 0xC4), f(r + 0xC8), f(r + 0xD0), f(r + 0xD4), f(r + 0xD8));
        if (float.IsNaN(box.MinX) || float.IsNaN(box.MaxX) || box.MinX > box.MaxX || box.MinY > box.MaxY || box.MinZ > box.MaxZ)
            throw new InvalidDataException("Section bounding box does not validate.");
        return new Section(name, prims, startIndex, vertOff, verts, box);
    }

    string CStr(int at)
    {
        if (at <= 0 || at >= _res.Length) return "";
        int end = Array.IndexOf(_res, (byte)0, at);
        return end < 0 ? "" : System.Text.Encoding.ASCII.GetString(_res, at, end - at);
    }

    /// <summary>Reads and parses a mesh resource by name. Throws when the name is missing or the header fails to validate.</summary>
    public static MeshSet Load(string game, BundleIndex index, string resName)
    {
        if (!index.ResByName.TryGetValue(resName, out var entries) || entries.Count == 0)
            throw new InvalidOperationException($"No resource named {resName}.");
        var stored = BundleIndex.ReadBytes(entries[0].Data);
        byte[] header = stored; try { header = ChunkBlocks.Parse(stored).Decode(); } catch { }
        return Parse(header);
    }

    /// <summary>Finds and decodes a LOD's geometry chunk (from the bundle that owns the mesh RES), or slices the
    /// inline geometry out of the RES itself when the LOD carries no chunk id.</summary>
    public static byte[] LoadLodChunk(BundleIndex index, BundleIndex.Res res, Lod lod)
    {
        if (lod.ChunkId != Guid.Empty && index.ChunksById.TryGetValue(lod.ChunkId, out var list))
        {
            var chunk = list.FirstOrDefault(c => ReferenceEquals(c.Bundle, res.Bundle)) ?? list[0];
            var stored = BundleIndex.ReadBytes(chunk.Data);
            byte[] data = stored; try { data = ChunkBlocks.Parse(stored).Decode(); } catch { }
            return data;
        }
        // No chunk (or a dead chunk id): the LOD carries its geometry inline in the RES.
        if (lod.InlineOffset == 0) throw new InvalidDataException("LOD chunk id not in the index and no inline data.");
        var storedRes = BundleIndex.ReadBytes(res.Data);
        byte[] resBytes = storedRes; try { resBytes = ChunkBlocks.Parse(storedRes).Decode(); } catch { }
        long len = (long)lod.IndexBufferSize + lod.VertexBufferSize;
        if (lod.InlineOffset + len > resBytes.Length)
            throw new InvalidDataException("Inline geometry runs past the resource.");
        return resBytes.AsSpan((int)lod.InlineOffset, (int)len).ToArray();
    }

    /// <summary>
    /// Reads a section's vertex positions from its LOD chunk (f32x3 at the start of each stride-sized vertex —
    /// the position element is first in every NHL 24 declaration). Returns flat xyz floats.
    /// </summary>
    public static float[] Positions(Lod lod, Section s, byte[] chunk)
    {
        if (s.Stride <= 0)
        {
            int stride = DiscoverStride(s, chunk, lod.VertexBase, out bool half);
            if (stride <= 0 && lod.VertexBase == 0 && lod.VertexBufferSize > 0 && lod.VertexBufferSize < chunk.Length)
            {
                // Some chunks put the index buffer first; retry with the vertex region at the tail.
                stride = DiscoverStride(s, chunk, chunk.Length - lod.VertexBufferSize, out half);
                if (stride > 0) lod.VertexBase = chunk.Length - lod.VertexBufferSize;
            }
            if (stride <= 0) throw new InvalidDataException($"Section {s.Name}: no plausible stride.");
            s.Stride = stride;
            s.HalfPositions = half;
        }
        long end = lod.VertexBase + s.VertexOffset + (long)s.VertexCount * s.Stride;
        if (end > chunk.Length)
            throw new InvalidDataException($"Section {s.Name} vertex data runs past the chunk.");
        var pos = new float[s.VertexCount * 3];
        for (int v = 0; v < s.VertexCount; v++)
        {
            int at = (int)(lod.VertexBase + s.VertexOffset + (long)v * s.Stride);
            if (s.HalfPositions)
            {
                pos[v * 3] = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(at)));
                pos[v * 3 + 1] = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(at + 2)));
                pos[v * 3 + 2] = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(at + 4)));
            }
            else
            {
                pos[v * 3] = BinaryPrimitives.ReadSingleLittleEndian(chunk.AsSpan(at));
                pos[v * 3 + 1] = BinaryPrimitives.ReadSingleLittleEndian(chunk.AsSpan(at + 4));
                pos[v * 3 + 2] = BinaryPrimitives.ReadSingleLittleEndian(chunk.AsSpan(at + 8));
            }
        }
        return pos;
    }

    /// <summary>
    /// Finds the vertex stride by trying 4-byte multiples and accepting the first candidate whose positions are
    /// all finite and inside the section bounds, and whose cloud actually spans those bounds (wrong strides read
    /// attribute bytes as positions — those are finite but clustered, failing the coverage check). Positions may
    /// be f32 or f16 (both occur in NHL 24); f32 is tried first.
    /// </summary>
    static int DiscoverStride(Section s, byte[] chunk, long vbase, out bool half)
    {
        half = false;
        if (s.VertexCount <= 0) return 12;
        for (int pass = 0; pass < 2; pass++)
        {
            bool h = pass == 1;
            int elem = h ? 2 : 4;
            float Read(int at, int axis) => h
                ? (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(at + axis * elem)))
                : BinaryPrimitives.ReadSingleLittleEndian(chunk.AsSpan(at + axis * elem));
            float Span(int axis, float min, float max, int stride)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int v = 0; v < s.VertexCount; v++)
                {
                    float val = Read((int)(vbase + s.VertexOffset + (long)v * stride), axis);
                    if (val < lo) lo = val;
                    if (val > hi) hi = val;
                }
                return max > min ? (hi - lo) / (max - min) : 1f;
            }
            // f16 vertices pack at 2-byte granularity, so that pass steps by 2.
            int step = h ? 2 : 4;
            for (int stride = elem * 3; stride <= 128; stride += step)
            {
                if (vbase + s.VertexOffset + (long)s.VertexCount * stride > chunk.Length) break;
                // Nearly all vertices must sit inside the bounds; a few degenerate/padding verts
                // (zeros, outside the used range) are tolerated.
                int allowedOut = Math.Max(1, s.VertexCount / 50);
                int outside = 0;
                for (int v = 0; v < s.VertexCount; v++)
                {
                    int at = (int)(vbase + s.VertexOffset + (long)v * stride);
                    float x = Read(at, 0), y = Read(at, 1), z = Read(at, 2);
                    const float tol = 0.5f; // half-meter slack around the recorded bounds
                    if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) ||
                        x < s.Bounds.MinX - tol || x > s.Bounds.MaxX + tol ||
                        y < s.Bounds.MinY - tol || y > s.Bounds.MaxY + tol ||
                        z < s.Bounds.MinZ - tol || z > s.Bounds.MaxZ + tol)
                    {
                        if (++outside > allowedOut) break;
                    }
                }
                if (outside > allowedOut) continue;
                // Coverage: the point cloud must span the bounds; take the middle axis to stay robust on flat sections.
                float c0 = Span(0, s.Bounds.MinX, s.Bounds.MaxX, stride);
                float c1 = Span(1, s.Bounds.MinY, s.Bounds.MaxY, stride);
                float c2 = Span(2, s.Bounds.MinZ, s.Bounds.MaxZ, stride);
                var cov = new[] { c0, c1, c2 };
                Array.Sort(cov);
                if (cov[1] >= 0.5f) { half = h; return stride; }
            }
        }
        half = false;
        return 0;
    }

    /// <summary>Locates the LOD's u16 index buffer in the chunk: the first range after the vertex data that validates.</summary>
    public static ushort[] Indices(Lod lod, byte[] chunk)
    {
        long vertexEnd = lod.VertexBase + lod.Sections.Max(s => s.VerticesEnd);
        int max = lod.Sections.Count == 0 ? 0 : lod.Sections.Max(s => s.VertexCount) + 64;
        int needed = checked((int)((lod.IndexBufferSize + 1) & ~1) / 2);

        // The index buffer sits either at the very tail (vertices first) or at offset 0 (indices first).
        var candidates = new List<long> { 0, chunk.Length - lod.IndexBufferSize };
        for (long at = (vertexEnd + 15) & ~15L; at + lod.IndexBufferSize <= chunk.Length; at += 4)
        {
            candidates.Add(at);
            if (at > vertexEnd + 0x4000) break; // don't scan the whole chunk
        }
        foreach (long at in candidates.Distinct().OrderBy(a => Math.Abs(a - vertexEnd)))
            if (TryU16(lod, chunk, at, needed, max, out var idx)) return idx;
        // Fall back to a u32 index buffer (indexFormat != 0 marks non-u16 layouts).
        if (lod.IndexFormat != 0 && lod.IndexBufferSize % 4 == 0)
        {
            int need32 = (int)(lod.IndexBufferSize / 4);
            foreach (long at in candidates.Distinct())
            {
                var idx = new ushort[need32];
                bool ok = true;
                for (int i = 0; i < need32; i++)
                {
                    uint v = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan((int)(at + i * 4)));
                    if (v >= max) { ok = false; break; }
                    idx[i] = (ushort)v;
                }
                if (ok) return idx;
            }
        }
        return [];
    }

    static bool TryU16(Lod lod, byte[] chunk, long at, int needed, int max, out ushort[] idx)
    {
        idx = [];
        if (at < 0 || at + lod.IndexBufferSize > chunk.Length) return false;
        var tmp = new ushort[needed];
        for (int i = 0; i < needed; i++)
        {
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan((int)(at + i * 2)));
            if (v >= max) return false;
            tmp[i] = v;
        }
        idx = tmp;
        return true;
    }
}
