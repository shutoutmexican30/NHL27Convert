using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4.Streams;
using Suite.Core;
using Suite.Rosters;
using Xunit;

namespace Suite.Rosters.Tests;

/// <summary>The current (fork 0.9+) savedata INDEX layout: 80-byte records, __SaveLoad* system blocks carve
/// regions out of DATA, and the file region is found by validation, never by the file record's own numbers.
/// Also a read-only check against the real saves of the configured build, when one is present.</summary>
public class SavedRosterFormatTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cims-savefmt-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static byte[] FakeDb(int size, byte fill)
    {
        var db = Enumerable.Repeat(fill, size).ToArray();
        "DB\0\b"u8.CopyTo(db);
        BinaryPrimitives.WriteUInt32LittleEndian(db.AsSpan(8), (uint)size);
        return db;
    }

    static byte[] Frame(byte[] db)
    {
        var ms = new MemoryStream();
        using (var enc = LZ4Stream.Encode(ms, leaveOpen: true)) enc.Write(db);
        return ms.ToArray();
    }

    static byte[] Block(byte[] payload, byte stamp)
    {
        var b = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)payload.Length);
        b.AsSpan(4, 12).Fill(stamp);
        payload.CopyTo(b, 16);
        return b;
    }

    static byte[] RosterPayload(byte[] db)
    {
        var frame = Frame(db); // fast default encode, like the game's own writer
        var payload = new byte[1 + frame.Length + 4];
        payload[0] = 1;
        frame.CopyTo(payload, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1 + frame.Length), (uint)db.Length);
        return payload;
    }

    /// <summary>Lays out a save the way the current emulator does: system blocks before and after the file
    /// block, INDEX system triples (total, start, end), and a file record whose numbers are guest metadata.</summary>
    string MakeCurrentSave(string name, byte[] db, bool fileAfterSystem)
    {
        var dir = Path.Combine(_dir, "savedata", name);
        Directory.CreateDirectory(dir);
        var info = Block(Encoding.ASCII.GetBytes("{\"IsBackup\":false}"), 3);
        var ver = Block(new byte[72], 7);
        var meta = Block(new byte[236], 8);
        var file = Block(RosterPayload(db), 9);
        var data = new MemoryStream();
        var index = new byte[80 * 4];
        void Put(int rec, string recName, long a, long b, long c)
        {
            Encoding.ASCII.GetBytes(recName).CopyTo(index, rec * 80);
            index[rec * 80 + 32] = 1;
            BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(rec * 80 + 40), a);
            BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(rec * 80 + 48), b);
            BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(rec * 80 + 56), c);
        }
        var ranges = new List<(string Name, byte[] Bytes)>();
        if (fileAfterSystem) ranges.Add((name, file));
        ranges.Add(("__SaveloadVer", ver));
        ranges.Add(("__SaveLoadMetaData", meta));
        if (!fileAfterSystem) ranges.Add((name, file));
        ranges.Insert(0, ("__SaveLoadFileInfo", info));

        int slot = 0;
        foreach (var (n, bytes) in ranges)
        {
            long start = data.Length;
            data.Write(bytes);
            if (n == name) Put(slot, n, 0xDEAD, 0xBEEF, 0xF00D); // guest metadata, must be ignored
            else Put(slot, n, bytes.Length, start, data.Length);
            slot++;
        }
        File.WriteAllBytes(Path.Combine(dir, "DATA"), data.ToArray());
        File.WriteAllBytes(Path.Combine(dir, "INDEX"), index);
        File.WriteAllText(Path.Combine(dir, "NAME"), name);
        return dir;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Current_layout_reads_and_rewrites(bool fileAfterSystem)
    {
        var db = FakeDb(300_000, 0x11);
        var dir = MakeCurrentSave("RosterCustom1", db, fileAfterSystem);
        var save = Assert.Single(SavedRoster.Find(Path.Combine(_dir, "savedata")));
        Assert.Equal(db, save.ReadDb());

        var newDb = FakeDb(300_000, 0x22);
        save.WriteDb(newDb, Path.Combine(_dir, "backup"));
        Assert.Equal(newDb, save.ReadDb());
        var restored = Directory.GetDirectories(Path.Combine(_dir, "backup")).Single();
        Assert.Equal(db, new SavedRosterBacked(restored).ReadDb());
    }

    [Fact]
    public void When_the_reencode_is_larger_the_save_is_rebuilt_and_system_blocks_move()
    {
        var db = FakeDb(300_000, 0x11);
        var dir = MakeCurrentSave("RosterCustom1", db, fileAfterSystem: true);
        var save = Assert.Single(SavedRoster.Find(Path.Combine(_dir, "savedata")));
        var indexBefore = File.ReadAllBytes(Path.Combine(dir, "INDEX"));

        // Incompressible new database: the packed frame cannot fit the old block, so DATA is rebuilt.
        var rng = new Random(7);
        var big = FakeDb(300_000, 0);
        rng.NextBytes(big);
        "DB\0\b"u8.CopyTo(big);
        BinaryPrimitives.WriteUInt32LittleEndian(big.AsSpan(8), (uint)big.Length);
        save.WriteDb(big, Path.Combine(_dir, "backup"));
        Assert.Equal(big, save.ReadDb());
        var indexAfter = File.ReadAllBytes(Path.Combine(dir, "INDEX"));
        Assert.NotEqual(indexBefore, indexAfter); // the system triples moved
    }

    [Fact]
    public void Real_saves_of_the_active_build_read_without_throwing()
    {
        var game = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "NHL24PC_V3.2", "NHL24PC", "game");
        if (!File.Exists(Path.Combine(game, "package.mft"))) return;
        foreach (var root in SavedRoster.SavedataRoots(game))
        {
            var saves = SavedRoster.Find(root);          // must not throw on any folder
            foreach (var save in saves) Assert.True(save.ReadDb().Length > 1000); // and any roster save decodes
        }
    }
}

/// <summary>Test-only accessor for a backed-up save folder (SavedRoster's constructor is private).</summary>
public sealed class SavedRosterBacked
{
    readonly SavedRoster _inner;
    public SavedRosterBacked(string dir)
    {
        var ctor = typeof(SavedRoster).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single();
        _inner = (SavedRoster)ctor.Invoke(new object[] { dir, Path.GetFileName(dir) });
    }
    public byte[] ReadDb() => _inner.ReadDb();
}
