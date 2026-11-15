using System.Buffers.Binary;

namespace Suite.Core.Frostbite;

/// <summary>Where a TOC says a chunk is stored, and where that entry sits in the TOC file.</summary>
public sealed record ChunkRef(string Toc, int EntryPos, bool Patch, int Catalog, int Cas, uint Offset, uint Size)
{
    public bool SameLocation(ChunkRef o) => Patch == o.Patch && Catalog == o.Catalog && Cas == o.Cas && Offset == o.Offset && Size == o.Size;
}

/// <summary>
/// The chunk tables of an NHL 24 TOC file. The file starts with a 0x22C-byte signed header (magic 00 D1 CE 01); after it
/// come big-endian u32 fields. Field 4 = offset of the chunk id table, field 5 = its record count, field 6 = offset of
/// the chunk location table (all relative to 0x22C). A chunk id record is 20 bytes: the id (Guid.ToByteArray() reversed)
/// and a u32 whose low 24 bits index u32s in the location table. A location is 12 bytes: u32 (byte 1 = patch,
/// byte 2 = catalog, byte 3 = cas), u32 offset, u32 stored size. Only the offset and size are ever rewritten.
/// </summary>
public sealed class TocFile
{
    public const int HeaderSize = 0x22C;
    public string Path { get; }
    public byte[] Bytes { get; }
    readonly Dictionary<Guid, int> _entryOf = [];

    TocFile(string path, byte[] bytes) { Path = path; Bytes = bytes; }

    public static TocFile Load(string path)
    {
        var b = File.ReadAllBytes(path);
        var t = new TocFile(path, b);
        if (b.Length < HeaderSize + 64 || b[0] != 0x00 || b[1] != 0xD1 || b[2] != 0xCE) return t; // not a chunk-table TOC
        uint F(int i) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(HeaderSize + 4 * i));
        long ids = HeaderSize + F(4), count = F(5), locations = HeaderSize + F(6);
        if (count == 0 || ids + count * 20 > b.Length) return t;
        for (int k = 0; k < count; k++)
        {
            int p = (int)ids + k * 20;
            var raw = b.AsSpan(p, 16).ToArray();
            Array.Reverse(raw);
            int index = (int)(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p + 16)) & 0xFFFFFF);
            long entry = locations + index * 4L;
            if (entry + 12 > b.Length) continue;
            t._entryOf.TryAdd(new Guid(raw), (int)entry);
        }
        return t;
    }

    public ChunkRef? Find(Guid id)
    {
        if (!_entryOf.TryGetValue(id, out var e)) return null;
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(e));
        return new ChunkRef(Path, e, ((flags >> 16) & 0xFF) != 0, (int)((flags >> 8) & 0xFF), (int)(flags & 0xFF),
            BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(e + 4)), BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(e + 8)));
    }

    public IEnumerable<Guid> Chunks => _entryOf.Keys;

    public void Repoint(int entryPos, uint offset, uint size)
    {
        BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan(entryPos + 4), offset);
        BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan(entryPos + 8), size);
    }

    /// <summary>Repoints an entry into a different CAS: rewrites the location's patch/catalog/cas bytes (byte 0 kept).</summary>
    public void RepointTo(int entryPos, bool patch, int catalog, int cas, uint offset, uint size)
    {
        if (catalog is < 0 or > 255 || cas is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(cas));
        Bytes[entryPos + 1] = (byte)(patch ? 1 : 0);
        Bytes[entryPos + 2] = (byte)catalog;
        Bytes[entryPos + 3] = (byte)cas;
        Repoint(entryPos, offset, size);
    }

    public void Save()
    {
        var tmp = Path + ".cims-tmp";
        File.WriteAllBytes(tmp, Bytes);
        File.Move(tmp, Path, true);
    }
}
