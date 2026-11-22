using System.Buffers.Binary;

namespace Suite.Core.Frostbite;

/// <summary>
/// Writes edited vertex positions back into a mesh LOD's geometry chunk: same layout, same count, only the
/// position elements change (f32 or f16, whatever the section was discovered to use). Goes through the suite's
/// writer — backup first, every chunk copy replaced, read-back after, backup restored (and checked) on any failure.
/// Chunk-based LODs only; inline-geometry LODs are refused.
///
/// The OBJ export (Assets page) leaves out sections whose vertex layout can't be decoded, so the import skips the
/// same sections and leaves their bytes alone. Section bounds live in the mesh resource, which is not rewritten, so a
/// vertex may not leave its section's recorded bounds (plus <see cref="BoundsSlack"/>): the game culls by those bounds
/// and the suite's own reader finds the vertex layout by them.
/// </summary>
public static class MeshSetPositionsApply
{
    public const float BoundsSlack = 0.25f;

    /// <summary>Sections the OBJ export writes (and the import expects), in order.</summary>
    public static List<MeshSet.Section> EditableSections(MeshSet.Lod lod, byte[] chunk)
    {
        var list = new List<MeshSet.Section>();
        foreach (var s in lod.Sections)
        {
            try { MeshSet.Positions(lod, s, chunk); } catch { continue; }
            list.Add(s);
        }
        return list;
    }

    /// <summary>Writes the positions into a copy of the chunk; returns null and a reason when they don't fit the mesh.</summary>
    public static (byte[]? Chunk, string Message) Patch(MeshSet.Lod lod, byte[] chunk, IReadOnlyList<(float X, float Y, float Z)> verts)
    {
        var output = (byte[])chunk.Clone();
        var sections = EditableSections(lod, chunk);
        int need = sections.Sum(s => s.VertexCount);
        if (need != verts.Count)
            return (null, $"The OBJ has {verts.Count:n0} vertices, the mesh's exported sections have {need:n0}. Export fresh and keep every vertex in order (in Blender: keep vertex order, no merge/split).");
        int at = 0, moved = 0;
        foreach (var s in sections)
        {
            var b = s.Bounds;
            for (int v = 0; v < s.VertexCount; v++)
            {
                var (x, y, z) = verts[at + v];
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                    return (null, $"Vertex {at + v + 1} (section '{s.Name}') is not a number.");
                if (x < b.MinX - BoundsSlack || x > b.MaxX + BoundsSlack || y < b.MinY - BoundsSlack || y > b.MaxY + BoundsSlack ||
                    z < b.MinZ - BoundsSlack || z > b.MaxZ + BoundsSlack)
                    return (null, FormattableString.Invariant(
                        $"Vertex {at + v + 1} ({x:0.###}, {y:0.###}, {z:0.###}) leaves section '{s.Name}' bounds ({b.MinX:0.###}..{b.MaxX:0.###}, {b.MinY:0.###}..{b.MaxY:0.###}, {b.MinZ:0.###}..{b.MaxZ:0.###}). Bounds can't be changed yet; keep edits inside them."));
                int p = (int)(lod.VertexBase + s.VertexOffset + (long)v * s.Stride);
                if (s.HalfPositions)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(p), BitConverter.HalfToUInt16Bits((Half)x));
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(p + 2), BitConverter.HalfToUInt16Bits((Half)y));
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(p + 4), BitConverter.HalfToUInt16Bits((Half)z));
                }
                else
                {
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(p), x);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(p + 4), y);
                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(p + 8), z);
                }
            }
            at += s.VertexCount;
        }
        for (int i = 0; i < chunk.Length; i++) if (chunk[i] != output[i]) { moved++; }
        return (output, $"{moved:n0} bytes changed");
    }

    public static (bool Ok, string Message) Apply(string game, BundleIndex index, MeshSet mesh, BundleIndex.Res res,
        int lodIndex, IReadOnlyList<(float X, float Y, float Z)> verts, Action<string>? progress = null, string? backupRoot = null)
    {
        if (ApplyTarget.RunningGame() is { } running) return (false, running + " is running. Close the game first.");
        var problems = ApplyTarget.Problems(game);
        if (problems.Count > 0) return (false, "Apply refused:\n" + string.Join("\n", problems));
        if (lodIndex < 0 || lodIndex >= mesh.Lods.Count) return (false, $"No LOD {lodIndex}.");

        var lod = mesh.Lods[lodIndex];
        if (lod.ChunkId == Guid.Empty || !index.ChunksById.ContainsKey(lod.ChunkId))
            return (false, "This LOD stores its geometry inline in the resource; position import supports chunk-based meshes only.");

        var chunk = MeshSet.LoadLodChunk(index, res, lod);
        var (patched, why) = Patch(lod, chunk, verts);
        if (patched is null) return (false, why);
        if (patched.AsSpan().SequenceEqual(chunk)) return (true, "The OBJ matches the mesh already; nothing written.");

        progress?.Invoke("Backing up TOCs and CAS lengths...");
        var backup = ApplyTarget.Backup(game, backupRoot);
        try
        {
            progress?.Invoke("Writing the geometry chunk...");
            foreach (var line in ChunkReplace.Replace(game, index, lod.ChunkId, patched)) SuiteLog.Write("meshpos: " + line);
            progress?.Invoke("Reading back...");
            var verify = BundleIndex.Build(game);
            var copies = ChunkReplace.ReadAll(game, verify, lod.ChunkId);
            if (copies.Count == 0) throw new InvalidOperationException("The chunk is gone from the index after the write.");
            // A bundle copy may hold only part of the chunk, starting at its logical offset.
            foreach (var r in copies)
                if (r.LogicalOffset + (long)r.Raw.Length > patched.Length ||
                    !r.Raw.AsSpan().SequenceEqual(patched.AsSpan((int)r.LogicalOffset, r.Raw.Length)))
                    throw new InvalidOperationException($"Read-back differs ({r.Where}: {r.Raw.Length:n0} bytes, wrote {patched.Length:n0}).");
            return (true, $"Mesh positions written ({verts.Count:n0} vertices, LOD {lodIndex}, {copies.Count} chunk cop{(copies.Count == 1 ? "y" : "ies")}) and read back identical. Backup: {backup}");
        }
        catch (Exception ex)
        {
            SuiteLog.Write("mesh position apply failed, restoring: " + ex);
            ApplyTarget.RestoreFromBackup(game, backup);
            var diffs = ApplyTarget.Compare(game, backup);
            return (false, $"Apply failed ({ex.Message}); the game was put back from the backup" +
                (diffs.Count == 0 ? " and checked." : $", but {diffs.Count} difference(s) remain: {string.Join("; ", diffs.Take(5))}. Backup: {backup}"));
        }
    }
}
