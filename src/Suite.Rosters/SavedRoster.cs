using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;

namespace Suite.Rosters;

/// <summary>
/// A roster saved in game (the sixth roster location): an emulator save folder
/// (user\home\&lt;id&gt;\savedata\CUSA37933\&lt;name&gt;\ with DATA, INDEX, NAME) whose record named like the folder holds the
/// roster database as an LZ4 frame. INDEX is 80-byte entries: name (32 bytes), 8 bytes, then size, start, end (u64) in DATA.
/// A record is u32 (size - 16), 12 bytes kept as they are, then the payload; the roster payload is 0x01, the LZ4 frame and
/// the decoded length (u32). Every other record and every INDEX byte besides size/start/end is kept.
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

    /// <summary>Every home's save folder for NHL 24 next to a game folder (…\NHL24PC-CIMS\game → …\NHL24PC-CIMS\user\home\*).</summary>
    public static IEnumerable<string> SavedataRoots(string gameDir)
    {
        var home = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(gameDir.TrimEnd('\\', '/')))!, "user", "home");
        if (!Directory.Exists(home)) yield break;
        foreach (var user in Directory.GetDirectories(home))
        {
            var root = Path.Combine(user, "savedata", "CUSA37933");
            if (Directory.Exists(root)) yield return root;
        }
    }

    sealed record Entry(int Pos, string Name, long Size, long Start, long End);

    static List<Entry> Entries(byte[] index)
    {
        if (index.Length % 80 != 0) throw new InvalidDataException("INDEX is not a list of 80-byte entries.");
        var list = new List<Entry>();
        for (int pos = 0; pos < index.Length; pos += 80)
        {
            var name = Encoding.ASCII.GetString(index, pos, 32).Split('\0')[0];
            long size = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(pos + 40)),
                 start = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(pos + 48)),
                 end = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(pos + 56));
            if (end != start + size) throw new InvalidDataException($"INDEX entry {name}: end != start + size.");
            list.Add(new Entry(pos, name, size, start, end));
        }
        return list;
    }

    (byte[] Data, byte[] Index, List<Entry> Entries, Entry Roster) Load()
    {
        var data = File.ReadAllBytes(Path.Combine(Dir, "DATA"));
        var index = File.ReadAllBytes(Path.Combine(Dir, "INDEX"));
        var entries = Entries(index);
        foreach (var e in entries)
            if (e.End > data.Length || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)e.Start)) + 16 != e.Size)
                throw new InvalidDataException($"Record {e.Name} does not fit its INDEX entry.");
        var roster = entries.SingleOrDefault(e => e.Name == Name) ?? throw new InvalidDataException($"No record named {Name}.");
        return (data, index, entries, roster);
    }

    public byte[] ReadDb()
    {
        var (data, _, _, r) = Load();
        var raw = data.AsSpan((int)r.Start, (int)r.Size);
        if (raw[16] != 1) throw new InvalidDataException("The roster record is not an LZ4 payload.");
        var db = Decode(raw[17..^4].ToArray());
        if (BinaryPrimitives.ReadUInt32LittleEndian(raw[^4..]) != db.Length) throw new InvalidDataException("Decoded length does not match the record.");
        if (db.Length < 12 || db[0] != 'D' || db[1] != 'B' || DbLength(db) > db.Length || db.Length - DbLength(db) > 64)
            throw new InvalidDataException("The roster record is not a T3DB database.");
        return db;
    }

    /// <summary>
    /// The saved roster is a T3DB database followed by a short save trailer (18 bytes in the saves seen so far); the
    /// database's own size field says where the trailer starts. The trailer is kept as it is.
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
        // Same frame settings the game's own saves decode: 64 KB independent blocks, content checksum.
        var settings = new LZ4EncoderSettings { BlockSize = 64 * 1024, ChainBlocks = false, ContentChecksum = true, CompressionLevel = LZ4Level.L09_HC };
        using (var enc = LZ4Stream.Encode(output, settings, leaveOpen: true)) enc.Write(db);
        return output.ToArray();
    }

    /// <summary>
    /// Replaces the roster in this save with <paramref name="db"/> (database + trailer, as <see cref="ReadDb"/> returns it). The folder is copied to
    /// <paramref name="backupDir"/> first (once per folder: an existing backup is kept). DATA and INDEX are rebuilt in
    /// record order, written through temp files, then read back and checked.
    /// </summary>
    public void WriteDb(byte[] db, string backupDir)
    {
        if (db.Length < 12 || DbLength(db) > db.Length || db.Length - DbLength(db) > 64) throw new ArgumentException("Not a saved roster (T3DB database + trailer).", nameof(db));
        var (data, index, entries, roster) = Load();
        var backup = Path.Combine(backupDir, Name);
        if (!Directory.Exists(backup)) CopyDir(Dir, backup);

        var frame = Encode(db);
        if (!Decode(frame).AsSpan().SequenceEqual(db)) throw new InvalidOperationException("LZ4 round trip failed.");
        var newIndex = (byte[])index.Clone();
        var newData = new MemoryStream();
        foreach (var e in entries.OrderBy(e => e.Start))
        {
            byte[] raw = data.AsSpan((int)e.Start, (int)e.Size).ToArray();
            if (e == roster)
            {
                var payload = new byte[1 + frame.Length + 4];
                payload[0] = 1;
                frame.CopyTo(payload, 1);
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1 + frame.Length), (uint)db.Length);
                var rec = new byte[16 + payload.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(rec, (uint)payload.Length);
                raw.AsSpan(4, 12).CopyTo(rec.AsSpan(4));
                payload.CopyTo(rec, 16);
                raw = rec;
            }
            long start = newData.Length;
            newData.Write(raw);
            BinaryPrimitives.WriteInt64LittleEndian(newIndex.AsSpan(e.Pos + 40), raw.Length);
            BinaryPrimitives.WriteInt64LittleEndian(newIndex.AsSpan(e.Pos + 48), start);
            BinaryPrimitives.WriteInt64LittleEndian(newIndex.AsSpan(e.Pos + 56), start + raw.Length);
        }
        Replace(Path.Combine(Dir, "DATA"), newData.ToArray());
        Replace(Path.Combine(Dir, "INDEX"), newIndex);
        if (!ReadDb().AsSpan().SequenceEqual(db)) throw new InvalidOperationException("The rewritten save does not read back the same roster.");
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
