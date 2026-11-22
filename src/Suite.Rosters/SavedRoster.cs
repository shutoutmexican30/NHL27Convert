using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;

namespace Suite.Rosters;

/// <summary>
/// A roster saved in game (an emulator save folder: user\home\&lt;id&gt;\savedata\&lt;CUSA3793x&gt;\&lt;name&gt;\ with
/// DATA, INDEX, NAME). Two INDEX layouts exist and both are handled:
///   legacy (fork ≤ 0.8): 80-byte records of name[32], 8 bytes, size/start/end u64 — the file record points
///     straight into DATA and u32 at start == size - 16.
///   current (fork 0.9+, the layout saves are written in today): the same 80-byte record stride, plus
///     __SaveLoadFileInfo / __SaveLoadMetaData / __SaveloadVer records whose triple (total, start, end) with
///     start + total == end carves the system blocks out of DATA. The file's bytes are DATA minus those ranges
///     (before, between, or after them — the emulator writes them at either end); the file record's own numbers
///     are guest-side metadata (they can point past EOF), so the file region is found by validating the
///     container, never by trusting those numbers.
/// DATA itself is unchanged in both: length-prefixed container blocks [u64 payloadLen][8-byte tag][payload];
/// the save's payload is 0x01, an LZ4 frame and the decoded length (u32). Writes keep DATA's total size and the
/// INDEX bytes untouched by padding the file block to its old capacity, so every offset in INDEX stays valid.
/// </summary>
public sealed class SavedRoster
{
    public string Dir { get; }
    public string Name { get; }

    SavedRoster(string dir, string name) { Dir = dir; Name = name; }

    /// <summary>NHL 24 save folders holding a roster under <paramref name="savedataRoot"/> (…\savedata\CUSA37933).</summary>
    public static List<SavedRoster> Find(string savedataRoot)
    {
        var found = new List<SavedRoster>();
        if (!Directory.Exists(savedataRoot)) return found;
        foreach (var dir in Directory.GetDirectories(savedataRoot))
        {
            var s = new SavedRoster(dir, Path.GetFileName(dir));
            try { if (s.ReadDb().Length > 0) found.Add(s); }
            catch { /* not a roster save (a profile, settings...) */ }
        }
        return found;
    }

    /// <summary>
    /// Creates a new save folder <paramref name="name"/> under <paramref name="savedataRoot"/> holding
    /// <paramref name="db"/> as its roster — the same thing as saving a roster in game, so the game lists it
    /// under Load &gt; Rosters and this suite can edit it. Laid out like the current emulator writes: DATA is
    /// __SaveLoadFileInfo / __SaveloadVer / __SaveLoadMetaData blocks with the file block after them, INDEX is
    /// 80-byte records with (version, start, size, end) for the file and (total, start, end) for the system
    /// blocks. sce_sys is copied from <paramref name="sceSysSource"/> (a save folder that has one).
    /// </summary>
    public static SavedRoster Create(string savedataRoot, string name, byte[] db, string? sceSysSource = null)
    {
        if (db.Length < 12 || DbLength(db) > db.Length || db.Length - DbLength(db) > 64) throw new ArgumentException("Not a T3DB database + trailer.", nameof(db));
        var dir = Path.Combine(savedataRoot, name);
        if (Directory.Exists(dir)) throw new InvalidOperationException($"{name} already exists.");
        Directory.CreateDirectory(dir);

        var frame = Encode(db);
        var payload = new byte[1 + frame.Length + 4];
        payload[0] = 1;
        frame.CopyTo(payload, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1 + frame.Length), (uint)db.Length);
        var file = Wrap(payload);
        var info = Wrap(Encoding.ASCII.GetBytes("{\"IsBackup\":false}"));
        var ver = Wrap(new byte[72]);
        var meta = Wrap(new byte[236]);

        var blocks = new (string Name, byte[] Bytes)[] { ("__SaveLoadFileInfo", info), ("__SaveloadVer", ver), ("__SaveLoadMetaData", meta), (name, file) };
        var data = new MemoryStream();
        var index = new byte[80 * blocks.Length];
        for (int i = 0; i < blocks.Length; i++)
        {
            long start = data.Length;
            data.Write(blocks[i].Bytes);
            Encoding.ASCII.GetBytes(blocks[i].Name).CopyTo(index, i * 80);
            index[i * 80 + 32] = 1;
            if (blocks[i].Name == name)
            {
                BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(i * 80 + 40), 1);          // version
                BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(i * 80 + 48), start);      // start
                BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(i * 80 + 56), blocks[i].Bytes.Length); // size
            }
            else
            {
                BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(i * 80 + 40), blocks[i].Bytes.Length);
                BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(i * 80 + 48), start);
                BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(i * 80 + 56), data.Length);
            }
        }
        File.WriteAllBytes(Path.Combine(dir, "DATA"), data.ToArray());
        File.WriteAllBytes(Path.Combine(dir, "INDEX"), index);
        File.WriteAllText(Path.Combine(dir, "NAME"), name);
        if (sceSysSource is not null && Directory.Exists(Path.Combine(sceSysSource, "sce_sys")))
            CopyDir(Path.Combine(sceSysSource, "sce_sys"), Path.Combine(dir, "sce_sys"));
        var save = new SavedRoster(dir, name);
        if (!save.ReadDb().AsSpan().SequenceEqual(db)) throw new InvalidOperationException("The new save does not read back the same roster.");
        return save;
    }

    static byte[] Wrap(byte[] payload)
    {
        var b = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)payload.Length);
        b[4] = 0x08; b[5] = 0x00; b[6] = 0x3E; b[7] = 0x23;   // the tag the game's own blocks carry
        b.AsSpan(8, 4).Fill(0xE4); b[12] = 0x18;
        payload.CopyTo(b, 16);
        return b;
    }

    /// <summary>Every home's save folder for NHL 24 next to a game folder (…\NHL24PC\game → …\NHL24PC\user\home\*),
    /// across every title id the emulator has saves for. Users without a savedata folder are skipped.</summary>
    public static IEnumerable<string> SavedataRoots(string gameDir)
    {
        var home = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gameDir.TrimEnd('\\', '/')))!, "user", "home");
        if (!Directory.Exists(home)) yield break;
        foreach (var user in Directory.GetDirectories(home))
        {
            var savedata = Path.Combine(user, "savedata");
            if (!Directory.Exists(savedata)) continue;
            foreach (var title in Directory.GetDirectories(savedata))
                yield return title;
        }
    }

    sealed record Entry(int Pos, string Name, long A, long B, long C);

    List<Entry> ReadIndex(byte[] index)
    {
        if (index.Length % 80 != 0 || index.Length == 0) throw new InvalidDataException("INDEX is not a list of 80-byte records.");
        var list = new List<Entry>();
        for (int pos = 0; pos < index.Length; pos += 80)
        {
            var name = Encoding.ASCII.GetString(index, pos, 32).Split('\0')[0];
            long a = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(pos + 40)),
                 b = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(pos + 48)),
                 c = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(pos + 56));
            if (name.Length == 0 || name.Any(ch => ch < 0x20 || ch > 0x7E)) throw new InvalidDataException($"INDEX record at {pos} has no name.");
            list.Add(new Entry(pos, name, a, b, c));
        }
        return list;
    }

    /// <summary>System-block ranges (start, end) carved out of DATA by the __SaveLoad* records.</summary>
    static List<(long Start, long End)> SystemRanges(List<Entry> entries)
    {
        var ranges = new List<(long Start, long End)>();
        foreach (var e in entries)
            if (e.Name.StartsWith("__Save", StringComparison.OrdinalIgnoreCase) && e.A > 0 && e.A + e.B == e.C)
                ranges.Add((e.B, e.C));
        return ranges.OrderBy(r => r.Start).ToList();
    }

    /// <summary>The contiguous DATA ranges not covered by the system blocks, largest first.</summary>
    static List<(long Start, long End)> FileRanges(long dataLen, List<(long Start, long End)> system)
    {
        var ranges = new List<(long Start, long End)>();
        long at = 0;
        foreach (var (s, e) in system)
        {
            if (s > at) ranges.Add((at, s));
            at = Math.Max(at, e);
        }
        if (at < dataLen) ranges.Add((at, dataLen));
        return ranges.Where(r => r.End - r.Start > 20).OrderByDescending(r => r.End - r.Start).ToList();
    }

    /// <summary>Validates the container block at <paramref name="start"/> and returns its span when it is the LZ4 roster.</summary>
    (long BlockStart, long BlockEnd, byte[] Db)? TryRange(byte[] data, long start, long end)
    {
        if (start < 0 || end > data.Length || end - start < 20) return null;
        var payloadLen = (long)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)start, 4));
        if (payloadLen < 5 || payloadLen > end - start - 16) return null;
        var raw = data.AsSpan((int)start + 16, (int)payloadLen);
        if (raw[0] != 1) return null;
        byte[] db = null!;
        // Canonical block: [1][frame][u32 length]. A block this writer padded (to keep DATA's total size)
        // has zeros after the frame instead, with the u32 inside — try both shapes.
        try { db = Decode(raw[1..^4].ToArray()); } catch { }
        if (db is null || db.Length < 12 || db[0] != 'D' || db[1] != 'B' || DbLength(db) > db.Length || db.Length - DbLength(db) > 64)
            try
            {
                db = Decode(raw[1..].ToArray());
                if (db.Length < 12 || db[0] != 'D' || db[1] != 'B' || DbLength(db) > db.Length || db.Length - DbLength(db) > 64) return null;
            }
            catch { return null; }
        return (start, start + 16 + payloadLen, db);
    }

    (byte[] Data, List<Entry> Entries, Entry File, long BlockStart, long BlockEnd, byte[] Db) Load()
    {
        var data = File.ReadAllBytes(Path.Combine(Dir, "DATA"));
        var index = File.ReadAllBytes(Path.Combine(Dir, "INDEX"));
        var entries = ReadIndex(index);
        var file = entries.FirstOrDefault(e => e.Name == Name)
                   ?? throw new InvalidDataException($"No record named {Name}.");
        // Legacy layout: the file record's numbers are the DATA extent (u32 at start == size - 16 there).
        if (file.A + file.B == file.C && file.C <= data.Length)
        {
            var hit = TryRange(data, file.B, file.C);
            if (hit is not null && hit.Value.BlockEnd == file.C)
                return (data, entries, file, hit.Value.BlockStart, hit.Value.BlockEnd, hit.Value.Db);
        }
        // Current layout: find the file block by validation in the ranges outside the system blocks.
        foreach (var (s, e) in FileRanges(data.Length, SystemRanges(entries)))
        {
            var hit = TryRange(data, s, e);
            if (hit is not null)
                return (data, entries, file, hit.Value.BlockStart, hit.Value.BlockEnd, hit.Value.Db);
        }
        throw new InvalidDataException($"No roster payload found in DATA (file record '{Name}').");
    }

    public byte[] ReadDb() => Load().Db;

    /// <summary>
    /// The saved roster is a T3DB database followed by a short save trailer (18 bytes in the saves seen so far);
    /// the database's own size field says where the trailer starts. The trailer is kept as it is.
    /// </summary>
    public static int DbLength(byte[] saved) => (int)BinaryPrimitives.ReadUInt32LittleEndian(saved.AsSpan(8));

    static byte[] Decode(byte[] frame)
    {
        using var input = LZ4Stream.Decode(new MemoryStream(frame));
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    static byte[] Encode(byte[] db)
    {
        var output = new MemoryStream();
        // Max compression: the frame must fit the save's block, which was packed by the game itself.
        // Same frame shape the game's own saves decode: 64 KB independent blocks, content checksum.
        var settings = new LZ4EncoderSettings { BlockSize = 64 * 1024, ChainBlocks = false, ContentChecksum = true, CompressionLevel = LZ4Level.L12_MAX };
        using (var enc = LZ4Stream.Encode(output, settings, leaveOpen: true)) enc.Write(db);
        return output.ToArray();
    }

    /// <summary>
    /// Replaces the roster in this save with <paramref name="db"/> (database + trailer, as <see cref="ReadDb"/> returns it).
    /// The folder is copied to <paramref name="backupDir"/> first (once per folder: an existing backup is kept).
    /// The new block is padded to the old block's capacity with zeroes, so DATA keeps its total size and every
    /// offset the INDEX names stays valid; the INDEX itself is left byte-identical. Refuses when the new payload
    /// does not fit (a T3DB edit never grows the database, so this should not happen) and always reads back.
    /// </summary>
    public void WriteDb(byte[] db, string backupDir)
    {
        if (db.Length < 12 || DbLength(db) > db.Length || db.Length - DbLength(db) > 64) throw new ArgumentException("Not a saved roster (T3DB database + trailer).", nameof(db));
        if (ApplyTargetRefuses(out var running)) throw new InvalidOperationException(running);
        var (data, entries, file, blockStart, blockEnd, _) = Load();
        var backup = Path.Combine(backupDir, Name);
        if (!Directory.Exists(backup)) CopyDir(Dir, backup);

        var frame = Encode(db);
        var capacity = blockEnd - blockStart - 16;       // what the old block's u32 payloadLen reserved
        byte[] newData;
        if (frame.Length + 5 <= capacity)
        {
            // In place, padded: DATA keeps its total size and every INDEX offset stays valid.
            var block = new byte[blockEnd - blockStart];
            BinaryPrimitives.WriteUInt32LittleEndian(block, (uint)capacity);      // same capacity the INDEX offsets were built for
            data.AsSpan((int)blockStart + 4, 12).CopyTo(block.AsSpan(4));         // keep the block's own header bytes
            block[16] = 1;
            frame.CopyTo(block, 17);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(17 + frame.Length), (uint)db.Length);
            newData = (byte[])data.Clone();
            block.CopyTo(newData, (int)blockStart);
        }
        else
        {
            // The re-encode is larger than the block. Rebuild DATA: keep every other block's bytes, place the
            // new file block in the file's region, and repair the __SaveLoad* triples (total, start, end) in
            // INDEX so they name the moved system blocks again. The file record's own numbers are guest-side
            // metadata; they are kept as the game wrote them.
            newData = Rebuild(data, SystemRanges(entries), blockStart, blockEnd, frame, db);
        }

        Replace(Path.Combine(Dir, "DATA"), newData);
        if (_shifted.Count > 0)
        {
            Replace(Path.Combine(Dir, "INDEX"), RepairIndex(File.ReadAllBytes(Path.Combine(Dir, "INDEX")), entries));
            _shifted.Clear();
        }
        if (!ReadDb().AsSpan().SequenceEqual(db)) throw new InvalidOperationException("The rewritten save does not read back the same roster.");
    }

    readonly Dictionary<(long Start, long End), (long At, long Total)> _shifted = [];

    byte[] Rebuild(byte[] data, List<(long Start, long End)> system, long blockStart, long blockEnd, byte[] frame, byte[] db)
    {
        var fileBlock = new byte[16 + 5 + frame.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(fileBlock, (uint)(5 + frame.Length));
        data.AsSpan((int)blockStart + 4, 12).CopyTo(fileBlock.AsSpan(4));
        fileBlock[16] = 1;
        frame.CopyTo(fileBlock, 17);
        BinaryPrimitives.WriteUInt32LittleEndian(fileBlock.AsSpan(17 + frame.Length), (uint)db.Length);
        var fileLen = fileBlock.Length;

        var ms = new MemoryStream();
        var ranges = system.Select(s => (Start: s.Start, End: s.End, Sys: true))
            .Append((Start: blockStart, End: blockEnd, Sys: false)).OrderBy(r => r.Start).ToList();
        foreach (var r in ranges)
        {
            if (r.Sys) ms.Write(data, (int)r.Start, (int)(r.End - r.Start));
            else ms.Write(fileBlock);
        }
        long at = 0;
        foreach (var r in ranges)
        {
            if (r.Sys) _shifted[(r.Start, r.End)] = (at, r.End - r.Start);
            at += r.Sys ? r.End - r.Start : fileLen;
        }
        return ms.ToArray();
    }

    byte[] RepairIndex(byte[] index, List<Entry> entries)
    {
        foreach (var e in entries)
            if (_shifted.TryGetValue((e.B, e.C), out var moved) && e.A == e.C - e.B)
            {
                BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(e.Pos + 40), moved.Total);
                BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(e.Pos + 48), moved.At);
                BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(e.Pos + 56), moved.At + moved.Total);
            }
        return index;
    }

    static bool ApplyTargetRefuses(out string message)
    {
        message = "";
        foreach (var name in new[] { "nhl24", "nhl24_new", "nhl24pc", "shadps4" })
            if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0)
            {
                message = $"{name}.exe is running. Close the game before writing a save.";
                return true;
            }
        return false;
    }

    static void Replace(string path, byte[] bytes)
    {
        var tmp = path + ".cims-tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, true);
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
