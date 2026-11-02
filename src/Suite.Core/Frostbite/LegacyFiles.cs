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
/// Replacing a file (same size only, which the roster layout guard guarantees): the chunk is decoded, the file's bytes
/// are swapped in, only the blocks whose bytes changed are re-encoded (the others are copied as stored), the new chunk
/// is appended to its own CAS file, every TOC entry that pointed at the old copy is repointed (offset + size), and the
/// index records of every file in that chunk get their new stored start/end; the index chunk is appended and repointed
/// the same way. Nothing already in a CAS is overwritten, so truncating the CAS files and copying the TOCs back
/// (<see cref="ApplyTarget.RestoreFromBackup"/>) restores the game exactly.
/// </summary>
public sealed class LegacyFiles
{
    readonly string _game;
    readonly List<TocFile> _tocs;
    Guid _indexId;
    byte[] _index = [];
    ChunkRef _indexRef = null!;

    sealed record Record(int Pos, Guid Chunk, string Name, long StoredStart, long StoredEnd, long RawOffset, long RawSize);

    public LegacyFiles(string gameDir, SuiteSettings? settings = null)
    {
        _game = Path.GetFullPath(gameDir).TrimEnd('\\');
        Oodle.Ensure(settings);
        // Patch first: where a chunk is in both, the update's copy is the one the game reads.
        _tocs = new[] { "Patch", "Data" }.Select(s => Path.Combine(_game, s)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.toc", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            .Select(TocFile.Load).ToList();
    }

    /// <summary>The copy of a chunk the game reads (first TOC that lists it, Patch before Data).</summary>
    ChunkRef? Locate(Guid id) => _tocs.Select(t => t.Find(id)).FirstOrDefault(r => r is not null);

    string CasPath(ChunkRef r) => Path.Combine(_game, r.Patch ? "Patch" : "Data", "Gen4b", "superbundlelayout",
        $"nhl_installpackage_{r.Catalog:D2}", $"cas_{r.Cas:D2}.cas");

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

    /// <summary>Finds the index chunk: a small chunk whose records name <paramref name="anyName"/>.</summary>
    void EnsureIndex(string anyName)
    {
        if (_index.Length > 0) return;
        var needle = Encoding.ASCII.GetBytes(anyName + "\0");
        foreach (var toc in _tocs)
            foreach (var id in toc.Chunks)
            {
                var r = Locate(id)!;
                if (r.Size > 4_000_000) continue;
                byte[] raw;
                try { raw = ChunkBlocks.Parse(ReadStored(r)).Decode(); } catch { continue; }
                if (raw.AsSpan().IndexOf(needle) < 0) continue;
                _index = raw; _indexId = id; _indexRef = r;
                if (Records().Any(x => x.Name == anyName)) return;
                _index = [];
            }
        throw new InvalidDataException($"No legacy file index lists {anyName}.");
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
    /// Replaces legacy files (name → new bytes, each the same size as the original). Appends to CAS files and rewrites
    /// TOCs in place; take <see cref="ApplyTarget.Backup"/> first. Returns one log line per change.
    /// </summary>
    public List<string> Replace(IReadOnlyDictionary<string, byte[]> files)
    {
        var log = new List<string>();
        if (files.Count == 0) return log;
        EnsureIndex(files.Keys.First());
        var records = Records();
        var newIndex = (byte[])_index.Clone();
        var appends = new List<(Guid Id, ChunkRef Old, byte[] Stored)>();

        foreach (var group in files.GroupBy(f => (records.FirstOrDefault(r => r.Name == f.Key) ?? throw new InvalidDataException($"No legacy file {f.Key}.")).Chunk))
        {
            var chunk = group.Key;
            var where = Locate(chunk)!;
            var blocks = ChunkBlocks.Parse(ReadStored(where));
            var oldRaw = blocks.Decode();
            var newRaw = (byte[])oldRaw.Clone();
            foreach (var (name, bytes) in group)
            {
                var rec = records.First(r => r.Name == name);
                if (bytes.Length != rec.RawSize) throw new InvalidOperationException($"{name}: {bytes.Length} bytes, the game's file is {rec.RawSize}. Same size only.");
                bytes.CopyTo(newRaw, (int)rec.RawOffset);
            }
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
}
