using System.Buffers.Binary;
using System.Text;

namespace Suite.Core.Frostbite;

/// <summary>
/// Legacy files (gamemodes/..., loc/...) of one NHL 24 game folder: read and replace, the suite's own way.
/// The files live in chunks listed in the TOCs' chunk tables. An index chunk (the chunk file collector's) lists every
/// file as a 56-byte record: u64 LE name offset (into the same chunk, a zero-terminated string), stored start and stored
/// end of the blocks holding the file inside the chunk (0/0 = the whole chunk), raw offset, raw size, then the chunk id
/// (Guid.ToByteArray()). Several small files can share one chunk (nhl_game.db shares with four others).
///
/// Replacing a file of the same size: the chunk is decoded, the file's bytes are swapped in, only the blocks whose bytes
/// changed are re-encoded (the others are copied as stored), the new chunk is appended to its own CAS file, every TOC
/// entry that pointed at the old copy is repointed (offset + size), and the index records of every file in that chunk get
/// their new stored start/end; the index chunk is appended and repointed the same way. Nothing already in a CAS is
/// overwritten, so truncating the CAS files and copying the TOCs back (<see cref="ApplyTarget.RestoreFromBackup"/>)
/// restores the game exactly.
///
/// A file of another size (a custom scorebug movie): a file that owns its chunk gets the whole chunk; a file in a shared
/// chunk moves to the chunk's end, on a block boundary, in new blocks of the chunk's own block size (the scorebug chunk:
/// 256 KB blocks, absolute raw offsets). Every other file keeps its bytes and raw offset. The tail past the last other
/// file is dropped first, so applying again reuses the space instead of growing the chunk each time.
/// </summary>
public sealed class LegacyFiles
{
    readonly string _game;
    readonly List<TocFile> _tocs;
    Guid _indexId;
    byte[] _index = [];
    ChunkRef _indexRef = null!;
    TocFile _indexToc = null!;

    sealed record Record(int Pos, Guid Chunk, string Name, long StoredStart, long StoredEnd, long RawOffset, long RawSize);

    public LegacyFiles(string gameDir, SuiteSettings? settings = null)
    {
        _game = Path.GetFullPath(gameDir).TrimEnd('\\');
        Oodle.Ensure(settings);
        // Patch first: where a chunk is in both, the update's copy is the one the game reads.
        _tocs = BundleIndex.Tocs(_game).Select(TocFile.Load).ToList();
    }

    /// <summary>The copy of a chunk the game reads (first TOC that lists it, Patch before Data).</summary>
    ChunkRef? Locate(Guid id) => _tocs.Select(t => t.Find(id)).FirstOrDefault(r => r is not null);

    string CasPath(ChunkRef r) => BundleIndex.CasPath(_game, r.Patch, r.Catalog, r.Cas);

    byte[] ReadStored(ChunkRef r)
    {
        var path = CasPath(r);
        if (!File.Exists(path)) throw new FileNotFoundException("CAS file not found for a chunk", path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Position = r.Offset;
        var b = new byte[r.Size];
        fs.ReadExactly(b);
        return b;
    }

    /// <summary>Finds the index chunk through the same globals-first lookup used by writes.</summary>
    void EnsureIndex(string anyName)
    {
        if (_index.Length > 0) return;
        // Read-back must not decompress every small asset chunk in contentlaunchsb before reaching the database
        // collectors in globals.toc. Keep the existing all-TOC fallback for unusual game layouts.
        var (toc, id, raw, reference) = FindAllIndexes(anyName).First();
        _indexToc = toc; _index = raw; _indexId = id; _indexRef = reference;
    }

    List<Record> Records()
    {
        var list = new List<Record>();
        var raw = _index;
        for (int p = 0; p + 56 <= raw.Length; p += 4)
        {
            long nameOff = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(p));
            if (nameOff <= 0 || nameOff >= raw.Length) continue;
            long s0 = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(p + 8)), s1 = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(p + 16)),
                 ro = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(p + 24)), rs = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(p + 32));
            if (s0 < 0 || s1 < s0 || ro < 0 || rs <= 0 || rs > 1L << 32 || s1 > 1L << 32) continue;
            int end = Array.IndexOf(raw, (byte)0, (int)nameOff);
            if (end < 0 || end - nameOff < 3 || end - nameOff > 260) continue;
            var name = Encoding.ASCII.GetString(raw, (int)nameOff, end - (int)nameOff);
            if (!name.Contains('/') || name.Any(c => c < 0x20 || c > 0x7E)) continue;
            var id = new Guid(raw.AsSpan(p + 40, 16));
            if (Locate(id) is null) continue;
            list.Add(new Record(p, id, name, s0, s1, ro, rs));
            p += 52; // records do not overlap
        }
        return list;
    }

    /// <summary>Every legacy file name in the index that lists <paramref name="anyKnown"/> (read only).</summary>
    public List<string> Names(string anyKnown)
    {
        EnsureIndex(anyKnown);
        return Records().Select(r => r.Name).Distinct().Order(StringComparer.Ordinal).ToList();
    }

    public byte[] Read(string name)
    {
        EnsureIndex(name);
        var rec = Records().FirstOrDefault(r => r.Name == name) ?? throw new InvalidDataException($"No legacy file {name}.");
        var raw = ChunkBlocks.Parse(ReadStored(Locate(rec.Chunk)!)).Decode();
        return raw.AsSpan((int)rec.RawOffset, (int)rec.RawSize).ToArray();
    }

    sealed record Rebuilt(byte[] Stored, List<(int OldStart, int OldEnd, int NewStart, int NewEnd)> Map, int Changed);

    /// <summary>Re-encodes the blocks of <paramref name="blocks"/> whose raw bytes differ in <paramref name="newRaw"/>.</summary>
    static Rebuilt Rebuild(ChunkBlocks blocks, byte[] oldRaw, byte[] newRaw)
    {
        var ms = new MemoryStream();
        var map = new List<(int, int, int, int)>();
        int at = 0, changed = 0;
        foreach (var b in blocks.Blocks)
        {
            int start = (int)ms.Length;
            bool same = oldRaw.AsSpan(at, b.RawSize).SequenceEqual(newRaw.AsSpan(at, b.RawSize));
            ms.Write(same ? b.Stored : ChunkBlocks.EncodeBlock(newRaw.AsSpan(at, b.RawSize)));
            if (!same) changed++;
            map.Add((b.StoredStart, b.StoredEnd, start, (int)ms.Length));
            at += b.RawSize;
        }
        var stored = ms.ToArray();
        if (!ChunkBlocks.Parse(stored).Decode().AsSpan().SequenceEqual(newRaw)) throw new InvalidOperationException("A rebuilt chunk does not decode to the new bytes.");
        return new Rebuilt(stored, map, changed);
    }

    /// <summary>
    /// Re-encodes a chunk whose raw bytes changed and grew or shrank at the end: block i of <paramref name="sizes"/> is
    /// copied as stored when it is an old block with the same raw size and bytes, else encoded. Map entries exist for the
    /// old blocks that survive (old stored range → new), and <paramref name="newRanges"/> gives every new block's range.
    /// </summary>
    static Rebuilt RebuildResized(ChunkBlocks blocks, byte[] oldRaw, byte[] newRaw, List<int> sizes, out List<(int Start, int End)> newRanges)
    {
        var ms = new MemoryStream();
        var map = new List<(int, int, int, int)>();
        newRanges = [];
        int at = 0, changed = 0;
        for (int i = 0; i < sizes.Count; i++)
        {
            int start = (int)ms.Length, size = sizes[i];
            var old = i < blocks.Blocks.Count ? blocks.Blocks[i] : null;
            bool same = old is not null && old.RawSize == size && at + size <= oldRaw.Length &&
                        oldRaw.AsSpan(at, size).SequenceEqual(newRaw.AsSpan(at, size));
            ms.Write(same ? old!.Stored : ChunkBlocks.EncodeBlock(newRaw.AsSpan(at, size)));
            if (!same) changed++;
            if (old is not null) map.Add((old.StoredStart, old.StoredEnd, start, (int)ms.Length));
            newRanges.Add((start, (int)ms.Length));
            at += size;
        }
        var stored = ms.ToArray();
        if (!ChunkBlocks.Parse(stored).Decode().AsSpan().SequenceEqual(newRaw)) throw new InvalidOperationException("A rebuilt chunk does not decode to the new bytes.");
        return new Rebuilt(stored, map, changed);
    }

    /// <summary>
    /// Replaces legacy files (name → new bytes). Same-size files are swapped in place; other sizes are placed as the
    /// class summary says. Appends to CAS files and rewrites TOCs in place; take <see cref="ApplyTarget.Backup"/> first.
    /// The base game and the update each carry an index with their own copy of a file (the scorebug's scoreclock.gfx
    /// is in Patch globals.toc AND Data globals.toc, each its own chunk) - every copy is written, or one stays stock.
    /// Returns one log line per change.
    /// </summary>
    public List<string> Replace(IReadOnlyDictionary<string, byte[]> files)
    {
        var log = new List<string>();
        if (files.Count == 0) return log;
        var indexes = FindAllIndexes(files.Keys.First());
        var missing = files.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var (toc, id, raw, refr) in indexes)
        {
            _indexToc = toc; _indexId = id; _index = raw; _indexRef = refr;
            var records = Records();
            var here = files.Where(f => records.Any(r => r.Name == f.Key)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
            missing.ExceptWith(here.Keys);
            if (here.Count == 0) continue;
            log.AddRange(ReplaceViaIndex(here, records));
        }
        if (missing.Count > 0) throw new InvalidDataException($"No legacy file {missing.First()}.");
        return log;
    }

    /// <summary>Every index chunk listing <paramref name="anyName"/>, with the TOC that carries it.</summary>
    List<(TocFile Toc, Guid Id, byte[] Raw, ChunkRef Ref)> FindAllIndexes(string anyName)
    {
        var needle = Encoding.ASCII.GetBytes(anyName + "\0");
        var found = new List<(TocFile, Guid, byte[], ChunkRef)>();
        // Legacy collectors live in globals.toc; only a miss there justifies the slow scan of every TOC.
        foreach (var globalsOnly in new[] { true, false })
        {
            foreach (var toc in _tocs)
            {
                bool isGlobals = toc.Path.EndsWith("globals.toc", StringComparison.OrdinalIgnoreCase);
                if (globalsOnly != isGlobals) continue;
                foreach (var id in toc.Chunks)
                {
                    if (toc.Find(id) is not { } r || r.Size > 4_000_000) continue;
                    byte[] raw;
                    try { raw = ChunkBlocks.Parse(ReadStored(r)).Decode(); } catch { continue; }
                    if (raw.AsSpan().IndexOf(needle) < 0) continue;
                    _index = raw;
                    if (Records().Any(x => x.Name == anyName)) found.Add((toc, id, raw, r));
                }
            }
            if (found.Count > 0) break;
        }
        if (found.Count == 0) throw new InvalidDataException($"No legacy file index lists {anyName}.");
        return found;
    }

    /// <summary>Replaces the files through one index chunk — one copy of the game (base or update).</summary>
    List<string> ReplaceViaIndex(IReadOnlyDictionary<string, byte[]> files, List<Record> records)
    {
        var log = new List<string>();
        var newIndex = (byte[])_index.Clone();
        var appends = new List<(Guid Id, ChunkRef Old, byte[] Stored)>();

        foreach (var group in files.GroupBy(f => records.First(r => r.Name == f.Key).Chunk))
        {
            var chunk = group.Key;
            var where = _indexToc.Find(chunk) ?? Locate(chunk)!;
            var blocks = ChunkBlocks.Parse(ReadStored(where));
            var oldRaw = blocks.Decode();
            var inChunk = records.Where(r => r.Chunk == chunk).ToList();
            var resized = group.Where(f => f.Value.Length != records.First(r => r.Name == f.Key).RawSize).ToList();
            if (resized.Count > 0)
            {
                ReplaceResized(chunk, where, blocks, oldRaw, group.ToList(), inChunk, newIndex, appends, log);
                continue;
            }
            var newRaw = (byte[])oldRaw.Clone();
            foreach (var (name, bytes) in group)
                bytes.CopyTo(newRaw, (int)records.First(r => r.Name == name).RawOffset);
            var rebuilt = Rebuild(blocks, oldRaw, newRaw);
            // Every file sharing the chunk gets its stored range moved to the new block positions.
            foreach (var rec in records.Where(r => r.Chunk == chunk && !(r.StoredStart == 0 && r.StoredEnd == 0)))
            {
                var first = rebuilt.Map.FindIndex(m => m.OldStart == rec.StoredStart);
                var last = rebuilt.Map.FindIndex(m => m.OldEnd == rec.StoredEnd);
                if (first < 0 || last < first) throw new InvalidDataException($"{rec.Name}: its stored range does not sit on block boundaries.");
                BinaryPrimitives.WriteInt64LittleEndian(newIndex.AsSpan(rec.Pos + 8), rebuilt.Map[first].NewStart);
                BinaryPrimitives.WriteInt64LittleEndian(newIndex.AsSpan(rec.Pos + 16), rebuilt.Map[last].NewEnd);
            }
            appends.Add((chunk, where, rebuilt.Stored));
            log.Add($"{string.Join(", ", group.Select(g => g.Key))}: chunk {chunk} {where.Size} -> {rebuilt.Stored.Length} stored bytes, " +
                    $"{rebuilt.Changed} of {rebuilt.Map.Count} blocks re-encoded");
        }

        // The index chunk itself (same raw size; only stored ranges changed).
        var indexBlocks = ChunkBlocks.Parse(ReadStored(_indexRef));
        var indexRebuilt = Rebuild(indexBlocks, _index, newIndex);
        appends.Add((_indexId, _indexRef, indexRebuilt.Stored));

        // Write: CAS data first (append only), then the TOC entries that pointed at the old copies.
        var dirty = new HashSet<TocFile>();
        foreach (var (chunkId, old, stored) in appends)
        {
            var cas = CasPath(old);
            long offset;
            using (var fs = new FileStream(cas, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                offset = fs.Position;
                if (offset + stored.Length > uint.MaxValue) throw new InvalidOperationException($"{cas} would grow past 4 GB.");
                fs.Write(stored);
                fs.Flush(true);
            }
            int repointed = 0;
            // Every TOC entry of this chunk id that pointed at the copy the game read (stale copies elsewhere are left).
            foreach (var toc in _tocs)
                    if (toc.Find(chunkId) is { } r && r.SameLocation(old))
                    {
                        toc.Repoint(r.EntryPos, (uint)offset, (uint)stored.Length);
                        dirty.Add(toc);
                        repointed++;
                    }
            if (repointed == 0) throw new InvalidOperationException("No TOC entry points at a chunk being replaced.");
            log.Add($"appended {stored.Length} bytes to {Path.GetRelativePath(_game, cas)} @ {offset}; {repointed} TOC entr{(repointed == 1 ? "y" : "ies")} repointed");
        }
        foreach (var toc in dirty) toc.Save();
        return log;
    }

    /// <summary>
    /// The index chunk also carries one [u64 raw size][chunk guid] record per data chunk (past the file table). A resize
    /// must update it: the game sizes its decode buffer by that record, and a resized file is placed at the chunk's end —
    /// with the record stale the file reads as out of range and the game silently drops it (an invisible scorebug).
    /// The file records are already rewritten by then, so only the stale per-chunk record still holds the old size.
    /// </summary>
    static void UpdateChunkSizeRecord(byte[] index, Guid chunk, long oldSize, long newSize)
    {
        var guid = chunk.ToByteArray();
        int found = 0;
        for (int p = 8; p + 16 <= index.Length; p++)
        {
            if (!index.AsSpan(p, 16).SequenceEqual(guid)) continue;
            if (BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(p - 8)) != oldSize) continue;
            BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(p - 8), newSize);
            found++;
        }
        if (found == 0) throw new InvalidDataException($"Chunk {chunk}: the index has no [raw size][guid] record to follow a resize.");
    }

    /// <summary>One chunk where at least one file changes size (see the class summary). Edits <paramref name="newIndex"/>.</summary>
    void ReplaceResized(Guid chunk, ChunkRef where, ChunkBlocks blocks, byte[] oldRaw, List<KeyValuePair<string, byte[]>> files,
        List<Record> inChunk, byte[] newIndex, List<(Guid Id, ChunkRef Old, byte[] Stored)> appends, List<string> log)
    {
        static void Set(byte[] index, Record r, long s0, long s1, long ro, long rs)
        {
            BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(r.Pos + 8), s0);
            BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(r.Pos + 16), s1);
            BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(r.Pos + 24), ro);
            BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(r.Pos + 32), rs);
        }
        int block = blocks.Blocks[0].RawSize;

        // The file owns the whole chunk: the chunk becomes the new file.
        if (inChunk.Count == 1 && files.Count == 1 && inChunk[0].StoredStart == 0 && inChunk[0].StoredEnd == 0 && inChunk[0].RawOffset == 0)
        {
            var bytes = files[0].Value;
            var stored = ChunkBlocks.EncodeBlocks(bytes, Math.Clamp(block, 4096, 262144));
            if (!ChunkBlocks.Parse(stored).Decode().AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("A rebuilt chunk does not decode to the new bytes.");
            Set(newIndex, inChunk[0], 0, 0, 0, bytes.Length);
            UpdateChunkSizeRecord(newIndex, chunk, oldRaw.Length, bytes.Length);
            appends.Add((chunk, where, stored));
            log.Add($"{files[0].Key}: own chunk {chunk}, {inChunk[0].RawSize} -> {bytes.Length} bytes");
            return;
        }

        // Shared chunk: every block but the last must have the same raw size, so a block-aligned raw offset is unambiguous.
        if (blocks.Blocks.Take(blocks.Blocks.Count - 1).Any(b => b.RawSize != block))
            throw new InvalidDataException($"Chunk {chunk}: blocks of mixed sizes; a file of another size cannot be placed in it.");
        if (inChunk.Any(r => r.StoredStart == 0 && r.StoredEnd == 0))
            throw new InvalidDataException($"Chunk {chunk}: shared, but a file in it claims the whole chunk.");
        var names = files.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var kept = inChunk.Where(r => !names.Contains(r.Name)).ToList();
        long keptEnd = kept.Count == 0 ? 0 : kept.Max(r => r.RawOffset + r.RawSize);
        int baseAt = (int)((keptEnd + block - 1) / block * block);

        // Same-size files in this chunk are written in place first (they sit before baseAt only if they are kept files'
        // neighbours; a same-size file is never moved).
        var ms = new MemoryStream();
        ms.Write(oldRaw, 0, Math.Min(baseAt, oldRaw.Length));
        ms.SetLength(baseAt);                                    // zero padding up to the block boundary
        ms.Position = baseAt;
        var placed = new List<(Record Rec, long Offset, int Size)>();
        foreach (var (name, bytes) in files)
        {
            var rec = inChunk.First(r => r.Name == name);
            if (bytes.Length == rec.RawSize && rec.RawOffset + rec.RawSize <= baseAt) { placed.Add((rec, rec.RawOffset, bytes.Length)); continue; }
            // Two names with the same bytes (the scorebug's two movies) share one copy.
            var twin = placed.FirstOrDefault(p => p.Offset >= baseAt && files.First(f => f.Key == p.Rec.Name).Value.AsSpan().SequenceEqual(bytes));
            if (twin.Rec is not null) { placed.Add((rec, twin.Offset, bytes.Length)); continue; }
            if (ms.Position % block != 0) { ms.SetLength((ms.Position + block - 1) / block * block); ms.Position = ms.Length; }
            placed.Add((rec, ms.Position, bytes.Length));
            ms.Write(bytes);
        }
        var newRaw = ms.ToArray();
        foreach (var (rec, offset, _) in placed)
            files.First(f => f.Key == rec.Name).Value.CopyTo(newRaw, offset);

        var sizes = new List<int>();
        for (int at = 0; at < newRaw.Length; at += block) sizes.Add(Math.Min(block, newRaw.Length - at));
        var rebuilt = RebuildResized(blocks, oldRaw, newRaw, sizes, out var ranges);

        foreach (var rec in kept)
        {
            var first = rebuilt.Map.FindIndex(m => m.OldStart == rec.StoredStart);
            var last = rebuilt.Map.FindIndex(m => m.OldEnd == rec.StoredEnd);
            if (first < 0 || last < first) throw new InvalidDataException($"{rec.Name}: its stored range does not sit on block boundaries.");
            // A kept file's blocks all lie before baseAt, so their old and new indices are the same.
            Set(newIndex, rec, ranges[first].Start, ranges[last].End, rec.RawOffset, rec.RawSize);
        }
        foreach (var (rec, offset, size) in placed)
        {
            int first = (int)(offset / block), last = (int)((offset + size - 1) / block);
            Set(newIndex, rec, ranges[first].Start, ranges[last].End, offset, size);
        }
        UpdateChunkSizeRecord(newIndex, chunk, oldRaw.Length, newRaw.Length);
        appends.Add((chunk, where, rebuilt.Stored));
        log.Add($"{string.Join(", ", files.Select(f => $"{f.Key} ({f.Value.Length} bytes)"))}: chunk {chunk} raw {oldRaw.Length} -> {newRaw.Length}, " +
                $"stored {where.Size} -> {rebuilt.Stored.Length}, {rebuilt.Changed} of {sizes.Count} blocks encoded; placed at block boundary {baseAt}");
    }
}
