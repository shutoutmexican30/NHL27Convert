using System.Buffers.Binary;
using System.Text;
using Suite.Rosters;

namespace Suite.Rosters.Tests;

public class SavedRosterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "cims-save-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static byte[] FakeDb(int size, byte fill)
    {
        var db = Enumerable.Repeat(fill, size).ToArray();
        "DB\0\b"u8.CopyTo(db);
        BinaryPrimitives.WriteUInt32LittleEndian(db.AsSpan(8), (uint)size);
        return db;
    }

    static byte[] Record(byte[] payload, byte stamp)
    {
        var rec = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(rec, (uint)payload.Length);
        rec.AsSpan(4, 12).Fill(stamp);
        payload.CopyTo(rec, 16);
        return rec;
    }

    /// <summary>A save folder laid out like the game's: two small records, then the roster.</summary>
    string MakeSave(string name, byte[] db)
    {
        var dir = Path.Combine(_dir, "savedata", name);
        Directory.CreateDirectory(dir);
        var ms = new MemoryStream();
        using (var enc = K4os.Compression.LZ4.Streams.LZ4Stream.Encode(ms, leaveOpen: true)) enc.Write(db);
        var payload = new byte[] { 1 }.Concat(ms.ToArray()).Concat(BitConverter.GetBytes((uint)db.Length)).ToArray();
        var records = new (string Name, byte[] Raw)[] { ("__SaveloadVer", Record(new byte[72], 7)), ("__SaveLoadMetaData", Record(new byte[236], 8)), (name, Record(payload, 9)) };
        var data = new MemoryStream();
        var index = new byte[80 * records.Length];
        for (int i = 0; i < records.Length; i++)
        {
            Encoding.ASCII.GetBytes(records[i].Name).CopyTo(index, i * 80);
            index[i * 80 + 32] = 1;
            long start = data.Length;
            data.Write(records[i].Raw);
            BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(i * 80 + 40), records[i].Raw.Length);
            BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(i * 80 + 48), start);
            BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(i * 80 + 56), data.Length);
        }
        File.WriteAllBytes(Path.Combine(dir, "DATA"), data.ToArray());
        File.WriteAllBytes(Path.Combine(dir, "INDEX"), index);
        File.WriteAllText(Path.Combine(dir, "NAME"), name);
        return dir;
    }

    [Fact]
    public void Finds_reads_and_rewrites_a_roster_save_keeping_other_records()
    {
        var db = FakeDb(300_000, 0x11);
        MakeSave("RosterCustom1", db);
        Directory.CreateDirectory(Path.Combine(_dir, "savedata", "Profile0"));
        var saves = SavedRoster.Find(Path.Combine(_dir, "savedata"));
        var save = Assert.Single(saves);
        Assert.Equal(db, save.ReadDb());

        var before = File.ReadAllBytes(Path.Combine(save.Dir, "DATA"));
        var newDb = FakeDb(300_000, 0x22);
        save.WriteDb(newDb, Path.Combine(_dir, "backup"));
        Assert.Equal(newDb, save.ReadDb());
        // The two small records come first and are untouched; the backup holds the old save.
        Assert.Equal(before.AsSpan(0, 88 + 252).ToArray(), File.ReadAllBytes(Path.Combine(save.Dir, "DATA")).AsSpan(0, 88 + 252).ToArray());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_dir, "backup", "RosterCustom1", "DATA")));
        // A second write keeps the first backup (the stock save).
        save.WriteDb(db, Path.Combine(_dir, "backup"));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_dir, "backup", "RosterCustom1", "DATA")));
    }

    /// <summary>Optional: CIMS_SAVE_SAMPLE = a copy of a real roster save folder; read, rewrite unchanged, read again.</summary>
    [Fact]
    public void Real_save_round_trips_when_a_sample_is_given()
    {
        var sample = Environment.GetEnvironmentVariable("CIMS_SAVE_SAMPLE");
        if (string.IsNullOrEmpty(sample) || !Directory.Exists(sample)) return;
        var copy = Path.Combine(_dir, "savedata", Path.GetFileName(sample));
        Directory.CreateDirectory(copy);
        foreach (var f in Directory.GetFiles(sample)) File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));
        var save = Assert.Single(SavedRoster.Find(Path.Combine(_dir, "savedata")));
        var db = save.ReadDb();
        save.WriteDb(db, Path.Combine(_dir, "backup"));
        Assert.Equal(db, save.ReadDb());
        var outDb = Environment.GetEnvironmentVariable("CIMS_SAVE_SAMPLE_OUT");
        if (!string.IsNullOrEmpty(outDb)) File.WriteAllBytes(outDb, db);
    }
}
