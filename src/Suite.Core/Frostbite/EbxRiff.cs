using System.Text;

namespace Suite.Core.Frostbite;

/// <summary>
/// The small RIFF EBX assets used by NHL 24 branding slots. Their root resource path is the last string in EBXD, while
/// EFIX carries the file GUID and imported file GUIDs. Rewriting those fields does not require game type descriptors.
/// </summary>
public sealed class EbxRiff
{
    public Guid FileGuid { get; }
    public string Name { get; }
    public IReadOnlyList<Guid> ImportedFileGuids => _importFileGuids.Select(at => new Guid(_bytes.AsSpan(at, 16))).ToArray();

    readonly byte[] _bytes;
    readonly int _ebxdSizeAt, _ebxdEnd, _efixAt, _fileGuidAt, _nameAt, _nameLength;
    readonly int[] _importFileGuids;
    readonly int _dataStart;
    readonly (int At, int Count) _pointerOffsets;
    readonly (int At, int Count)[] _offsetTables;
    readonly (int At, uint Value)[] _sectionOffsets;

    EbxRiff(byte[] bytes, int ebxdSizeAt, int ebxdEnd, int efixAt, int fileGuidAt, int nameAt, int nameLength, int[] importFileGuids,
        int dataStart, (int At, int Count) pointerOffsets, (int At, int Count)[] offsetTables, (int At, uint Value)[] sectionOffsets)
    {
        _bytes = bytes; _ebxdSizeAt = ebxdSizeAt; _ebxdEnd = ebxdEnd; _efixAt = efixAt;
        _dataStart = dataStart; _pointerOffsets = pointerOffsets; _offsetTables = offsetTables; _sectionOffsets = sectionOffsets;
        _fileGuidAt = fileGuidAt; _nameAt = nameAt; _nameLength = nameLength; _importFileGuids = importFileGuids;
        FileGuid = new Guid(bytes.AsSpan(fileGuidAt, 16));
        Name = Encoding.ASCII.GetString(bytes, nameAt, nameLength);
    }

    public static EbxRiff Parse(ReadOnlySpan<byte> source)
    {
        var b = source.ToArray();
        if (b.Length < 40 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(b, 8, 4) is not ("EBX\0" or "EBXS") || Encoding.ASCII.GetString(b, 12, 4) != "EBXD")
            throw new InvalidDataException("Not a RIFF EBX asset.");
        if (BitConverter.ToUInt32(b, 4) != b.Length - 8) throw new InvalidDataException("RIFF size does not match its payload.");
        int ebxdSize = checked((int)BitConverter.ToUInt32(b, 16));
        int ebxdEnd = 20 + ebxdSize;
        if (ebxdEnd < 20 || ebxdEnd > b.Length) throw new InvalidDataException("EBXD runs past the asset.");
        int dataStart = Align(20, 16);
        int efix = Align(ebxdEnd, 2);
        if (efix + 24 > b.Length || Encoding.ASCII.GetString(b, efix, 4) != "EFIX")
            throw new InvalidDataException($"EBXD is not followed by EFIX (EBXD end {ebxdEnd}, EFIX offset {efix}).");
        int efixSize = checked((int)BitConverter.ToUInt32(b, efix + 4));
        int p = efix + 8, efixEnd = p + efixSize;
        if (efixEnd > b.Length) throw new InvalidDataException("EFIX runs past the asset.");
        int fileGuidAt = p;
        p += 16;

        uint Count()
        {
            if (p + 4 > efixEnd) throw new InvalidDataException("Truncated EFIX table.");
            uint value = BitConverter.ToUInt32(b, p); p += 4; return value;
        }
        void Skip(uint count, int stride)
        {
            long end = p + count * (long)stride;
            if (end > efixEnd) throw new InvalidDataException("EFIX table runs past its chunk.");
            p = (int)end;
        }
        Skip(Count(), 16);                    // class GUIDs
        Skip(Count(), 4);                     // signatures
        _ = Count();                          // exported instances
        (int At, int Count) Table()
        {
            uint count = Count();
            int at = p;
            Skip(count, 4);
            return (at, checked((int)count));
        }
        var dataOffsets = Table();
        var pointerOffsets = Table();
        var resourceOffsets = Table();
        uint importCount = Count();
        var imports = new int[importCount];
        for (int i = 0; i < imports.Length; i++) { imports[i] = p; Skip(1, 32); }
        var importOffsets = Table();
        var typeInfoOffsets = Table();
        int arraysAt = p; uint arrays = Count();
        int boxedAt = p; uint boxed = Count();
        int stringsAt = p;
        uint stringsOffset = Count();
        long nameAt = dataStart + (long)stringsOffset;
        if (nameAt < dataStart || nameAt >= ebxdEnd) throw new InvalidDataException("Root-name string is outside EBXD.");
        int zero = Array.IndexOf(b, (byte)0, (int)nameAt, ebxdEnd - (int)nameAt);
        if (zero < 0) throw new InvalidDataException("Root-name string is not terminated inside EBXD.");
        return new EbxRiff(b, 16, ebxdEnd, efix, fileGuidAt, (int)nameAt, zero - (int)nameAt, imports, dataStart, pointerOffsets,
            [dataOffsets, pointerOffsets, resourceOffsets, importOffsets, typeInfoOffsets], [(arraysAt, arrays), (boxedAt, boxed)])
            { _stringsOffsetAt = stringsAt, _efixEnd = efixEnd };
    }

    int _stringsOffsetAt, _efixEnd;

    /// <summary>
    /// Replaces the contents of one byte array stored in EBXD (a stamp keeps its hotspot XML in one), growing or shrinking
    /// it. The array's count, its EBXX descriptor, and every pointer, offset and section that lies after it move with it;
    /// the next section keeps its 16-byte alignment. Identity, imports and all other bytes are checked unchanged.
    /// </summary>
    public byte[] ReplaceArray(int dataAt, ReadOnlySpan<byte> content)
    {
        int arrayOffset = dataAt - _dataStart;
        if (dataAt < _dataStart + 4 || dataAt >= _ebxdEnd) throw new ArgumentOutOfRangeException(nameof(dataAt), "The array is outside EBXD.");
        int oldCount = BitConverter.ToInt32(_bytes, dataAt - 4);
        int ebxx = _bytes.AsSpan(_efixEnd).IndexOf("EBXX"u8);
        if (ebxx < 0) throw new InvalidDataException("The asset has no EBXX array table.");
        ebxx += _efixEnd;
        int descriptors = BitConverter.ToInt32(_bytes, ebxx + 8);
        int descriptorAt = -1;
        for (int i = 0; i < descriptors; i++)
        {
            int at = ebxx + 16 + i * 16;
            if (BitConverter.ToInt32(_bytes, at) == arrayOffset && BitConverter.ToInt32(_bytes, at + 4) == oldCount) descriptorAt = at;
        }
        if (descriptorAt < 0) throw new InvalidDataException("No EBXX descriptor matches the array's offset and count.");
        // The next section starts at the first 16-byte boundary (from the data start) after the array.
        int oldEnd = _dataStart + Align(arrayOffset + oldCount, 16);
        int newEnd = _dataStart + Align(arrayOffset + content.Length, 16);
        int delta = newEnd - oldEnd;
        if (oldEnd > _ebxdEnd) throw new InvalidDataException("The array runs past EBXD.");
        int newEbxdEnd = _ebxdEnd + delta, newEfix = Align(newEbxdEnd, 2), efixShift = newEfix - _efixAt;
        var output = new byte[_bytes.Length + delta + (newEfix - newEbxdEnd) - (_efixAt - _ebxdEnd)];
        _bytes.AsSpan(0, dataAt).CopyTo(output);
        content.CopyTo(output.AsSpan(dataAt));
        _bytes.AsSpan(oldEnd, _ebxdEnd - oldEnd).CopyTo(output.AsSpan(newEnd));
        _bytes.AsSpan(_efixAt).CopyTo(output.AsSpan(newEfix));
        void W(int at, int value) => BitConverter.TryWriteBytes(output.AsSpan(at), value);
        W(dataAt - 4, content.Length);
        W(_ebxdSizeAt, newEbxdEnd - 20);
        W(4, output.Length - 8);
        int Moved(int position) => position >= oldEnd ? position + delta : position;
        for (int i = 0; i < _pointerOffsets.Count; i++)
        {
            int field = _dataStart + BitConverter.ToInt32(_bytes, _pointerOffsets.At + i * 4);
            long value = BitConverter.ToInt64(_bytes, field);
            if (value == 0) continue;
            long target = field + value;
            if (target > dataAt && target < oldEnd) throw new InvalidDataException("A pointer enters the array being replaced.");
            int newField = Moved(field);
            long newTarget = target >= oldEnd ? target + delta : target;
            BitConverter.TryWriteBytes(output.AsSpan(newField), newTarget - newField);
        }
        int oldEndOffset = oldEnd - _dataStart;
        foreach (var (at, count) in _offsetTables)
            for (int i = 0; i < count; i++)
            {
                int offset = BitConverter.ToInt32(_bytes, at + i * 4);
                if (offset >= oldEndOffset) W(at + i * 4 + efixShift, offset + delta);
            }
        foreach (var at in _sectionOffsets.Select(s => s.At).Append(_stringsOffsetAt))
        {
            int value = BitConverter.ToInt32(_bytes, at);
            if (value >= oldEndOffset) W(at + efixShift, value + delta);
        }
        for (int i = 0; i < descriptors; i++)
        {
            int at = ebxx + 16 + i * 16;
            int offset = BitConverter.ToInt32(_bytes, at);
            if (at == descriptorAt) W(at + efixShift, offset);
            else if (offset >= oldEndOffset) W(at + efixShift, offset + delta);
        }
        W(descriptorAt + efixShift + 4, content.Length);
        var check = Parse(output);
        if (check.Name != Name || check.FileGuid != FileGuid || !check.ImportedFileGuids.SequenceEqual(ImportedFileGuids))
            throw new InvalidOperationException("Array replacement changed the asset's identity or imports.");
        if (!output.AsSpan(newEnd, newEbxdEnd - newEnd).SequenceEqual(_bytes.AsSpan(oldEnd, _ebxdEnd - oldEnd)))
            throw new InvalidOperationException("Array replacement changed the data after the array.");
        return output;
    }

    /// <summary>Clone with a new root path, file GUID and remapped imports. All object data and type information stay intact.</summary>
    public byte[] Rewrite(string newName, Guid newFileGuid, IReadOnlyDictionary<Guid, Guid>? importRemap = null)
    {
        if (newName.Length == 0 || newName.Any(c => c < 0x20 || c > 0x7E))
            throw new ArgumentException("EBX path must be printable ASCII.", nameof(newName));
        byte[] name = Encoding.ASCII.GetBytes(newName);
        var patched = (byte[])_bytes.Clone();
        newFileGuid.TryWriteBytes(patched.AsSpan(_fileGuidAt, 16));
        if (importRemap is not null)
            foreach (int at in _importFileGuids)
            {
                var old = new Guid(patched.AsSpan(at, 16));
                if (importRemap.TryGetValue(old, out var replacement)) replacement.TryWriteBytes(patched.AsSpan(at, 16));
            }

        int delta = name.Length - _nameLength;
        int newEbxdEnd = _ebxdEnd + delta;
        int newEfix = Align(newEbxdEnd, 2);
        var output = new byte[patched.Length + delta + newEfix - newEbxdEnd - (_efixAt - _ebxdEnd)];
        patched.AsSpan(0, _nameAt).CopyTo(output);
        name.CopyTo(output, _nameAt);
        output[_nameAt + name.Length] = 0;
        int afterName = _nameAt + name.Length + 1;
        patched.AsSpan(_nameAt + _nameLength + 1, _ebxdEnd - _nameAt - _nameLength - 1)
            .CopyTo(output.AsSpan(afterName));
        // The new array is zero-initialized, so any required alignment byte is already present.
        patched.AsSpan(_efixAt).CopyTo(output.AsSpan(newEfix));
        BitConverter.TryWriteBytes(output.AsSpan(_ebxdSizeAt), (uint)(newEbxdEnd - 20));
        BitConverter.TryWriteBytes(output.AsSpan(4), (uint)(output.Length - 8));
        if (delta != 0) FixOffsets(patched, output, delta, newEfix - _efixAt,
            _nameAt + _nameLength, _nameAt - _dataStart, rejectInterior: false);

        var check = Parse(output);
        if (check.Name != newName || check.FileGuid != newFileGuid) throw new InvalidOperationException("Rewritten EBX did not read back.");
        return output;
    }

    /// <summary>
    /// Replaces an unreferenced string/XML region after the root name and repairs every EBX offset that crosses it.
    /// This permits a reviewed hotspot layout to outgrow a legacy stamp without changing its identity or object data.
    /// </summary>
    public byte[] ReplaceEmbedded(int at, int length, ReadOnlySpan<byte> replacement)
    {
        int nameEnd = _nameAt + _nameLength;
        if (at <= nameEnd || length < 1 || at + length > _ebxdEnd)
            throw new ArgumentOutOfRangeException(nameof(at), "Embedded content must be a nonempty region after the root name inside EBXD.");
        for (int i = 0; i < _pointerOffsets.Count; i++)
        {
            int field = _dataStart + BitConverter.ToInt32(_bytes, _pointerOffsets.At + i * 4);
            long target = field + BitConverter.ToInt64(_bytes, field);
            if ((field >= at && field < at + length) || (target > at && target < at + length))
                throw new InvalidDataException("An EBX pointer enters the embedded content being replaced; its relocation needs explicit review.");
        }
        var patched = (byte[])_bytes.Clone();
        int delta = replacement.Length - length;
        int newEbxdEnd = _ebxdEnd + delta;
        int newEfix = Align(newEbxdEnd, 2);
        var output = new byte[patched.Length + delta + newEfix - newEbxdEnd - (_efixAt - _ebxdEnd)];
        patched.AsSpan(0, at).CopyTo(output);
        replacement.CopyTo(output.AsSpan(at));
        patched.AsSpan(at + length, _ebxdEnd - at - length).CopyTo(output.AsSpan(at + replacement.Length));
        patched.AsSpan(_efixAt).CopyTo(output.AsSpan(newEfix));
        BitConverter.TryWriteBytes(output.AsSpan(_ebxdSizeAt), (uint)(newEbxdEnd - 20));
        BitConverter.TryWriteBytes(output.AsSpan(4), (uint)(output.Length - 8));
        if (delta != 0) FixOffsets(patched, output, delta, newEfix - _efixAt,
            at + length - 1, at - _dataStart, rejectInterior: true);
        var check = Parse(output);
        if (check.Name != Name || check.FileGuid != FileGuid || !check.ImportedFileGuids.SequenceEqual(ImportedFileGuids))
            throw new InvalidOperationException("Embedded EBX replacement changed its identity or imports.");
        return output;
    }

    /// <summary>Moves self-relative pointers and EFIX offsets that cross replaced root-name or embedded content.</summary>
    void FixOffsets(byte[] before, byte[] output, int delta, int efixShift,
        int contentEnd, int contentOffset, bool rejectInterior)
    {
        int Moved(int position) => position > contentEnd ? position + delta : position;
        for (int i = 0; i < _pointerOffsets.Count; i++)
        {
            int field = _dataStart + BitConverter.ToInt32(before, _pointerOffsets.At + i * 4);
            long value = BitConverter.ToInt64(before, field);
            if (value == 0) continue;
            long target = field + value;
            int newField = Moved(field);
            long newTarget = target > contentEnd ? target + delta : target;
            BitConverter.TryWriteBytes(output.AsSpan(newField), newTarget - newField);
            if (target > contentEnd && target < _ebxdEnd && CString(before, (int)target) != CString(output, (int)newTarget))
                throw new InvalidOperationException($"Rewritten EBX pointer no longer reaches its string: {CString(before, (int)target)}");
        }
        foreach (var (at, count) in _offsetTables)
            for (int i = 0; i < count; i++)
            {
                int entry = at + i * 4;
                int offset = BitConverter.ToInt32(before, entry);
                if (offset > contentOffset) BitConverter.TryWriteBytes(output.AsSpan(entry + efixShift), offset + delta);
                else if (rejectInterior && offset >= contentOffset)
                    throw new InvalidDataException("An EBX offset table enters the embedded content being replaced.");
            }
        foreach (var (at, value) in _sectionOffsets)
        {
            if (value > contentOffset) BitConverter.TryWriteBytes(output.AsSpan(at + efixShift), (uint)(value + delta));
            else if (rejectInterior && value >= contentOffset)
                throw new InvalidDataException("An EBX section offset enters the embedded content being replaced.");
        }
    }

    static string CString(byte[] bytes, int at)
    {
        int zero = Array.IndexOf(bytes, (byte)0, at);
        return Encoding.ASCII.GetString(bytes, at, zero - at);
    }

    static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}
