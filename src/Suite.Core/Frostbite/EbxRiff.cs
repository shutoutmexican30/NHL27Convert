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
        uint stringsOffset = Count();
        long nameAt = dataStart + (long)stringsOffset;
        if (nameAt < dataStart || nameAt >= ebxdEnd) throw new InvalidDataException("Root-name string is outside EBXD.");
        int zero = Array.IndexOf(b, (byte)0, (int)nameAt, ebxdEnd - (int)nameAt);
        if (zero < 0) throw new InvalidDataException("Root-name string is not terminated inside EBXD.");
        return new EbxRiff(b, 16, ebxdEnd, efix, fileGuidAt, (int)nameAt, zero - (int)nameAt, imports, dataStart, pointerOffsets,
            [dataOffsets, pointerOffsets, resourceOffsets, importOffsets, typeInfoOffsets], [(arraysAt, arrays), (boxedAt, boxed)]);
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
        if (delta != 0) FixOffsets(patched, output, delta, newEfix - _efixAt);

        var check = Parse(output);
        if (check.Name != newName || check.FileGuid != newFileGuid) throw new InvalidOperationException("Rewritten EBX did not read back.");
        return output;
    }

    /// <summary>
    /// A longer or shorter root name moves everything stored after it (material presets keep their parameter names there).
    /// Self-relative pointers and EFIX offsets that cross the name are corrected, and each pointer is checked against the original.
    /// </summary>
    void FixOffsets(byte[] before, byte[] output, int delta, int efixShift)
    {
        int nameEnd = _nameAt + _nameLength;
        int Moved(int position) => position > nameEnd ? position + delta : position;
        for (int i = 0; i < _pointerOffsets.Count; i++)
        {
            int field = _dataStart + BitConverter.ToInt32(before, _pointerOffsets.At + i * 4);
            long value = BitConverter.ToInt64(before, field);
            if (value == 0) continue;
            long target = field + value;
            int newField = Moved(field);
            long newTarget = target > nameEnd ? target + delta : target;
            BitConverter.TryWriteBytes(output.AsSpan(newField), newTarget - newField);
            if (target > nameEnd && target < _ebxdEnd && CString(before, (int)target) != CString(output, (int)newTarget))
                throw new InvalidOperationException($"Rewritten EBX pointer no longer reaches its string: {CString(before, (int)target)}");
        }
        int nameOffset = _nameAt - _dataStart;
        foreach (var (at, count) in _offsetTables)
            for (int i = 0; i < count; i++)
            {
                int entry = at + i * 4;
                int offset = BitConverter.ToInt32(before, entry);
                if (offset > nameOffset) BitConverter.TryWriteBytes(output.AsSpan(entry + efixShift), offset + delta);
            }
        foreach (var (at, value) in _sectionOffsets)
            if (value > nameOffset) BitConverter.TryWriteBytes(output.AsSpan(at + efixShift), (uint)(value + delta));
    }

    static string CString(byte[] bytes, int at)
    {
        int zero = Array.IndexOf(bytes, (byte)0, at);
        return Encoding.ASCII.GetString(bytes, at, zero - at);
    }

    static int Align(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}
