using System.Buffers.Binary;
using System.Text;

namespace Suite.Core.Frostbite;

/// <summary>
/// The bundle and chunk lookup tables of a CAS TOC. Rebuilding decodes Huffman bundle names, writes an uncompressed
/// lookup section, and preserves the signed header and every original bundle-record byte. Bundle records are relocated
/// as one opaque tail; their CAS payload locations are not changed.
/// </summary>
public sealed class TocArchive
{
    public const uint HasCompressedNames = 4;

    public sealed record Bundle(string Name, uint SizeAndFlags, ulong RecordOffset);
    public sealed record NewBundle(string Name, uint SizeAndFlags, byte[] Record);
    public sealed record Chunk(Guid Id, uint LocationWord, uint Offset, uint Size, bool Removed = false);

    readonly byte[] _header;
    readonly uint _flags;
    readonly int[] _bundleHashMap;
    readonly int[] _chunkHashMap;

    public IReadOnlyList<Bundle> Bundles { get; }
    public IReadOnlyList<Chunk> Chunks { get; }
    public bool HasInlineBundleRecords => Bundles.Any(b => (b.SizeAndFlags & 0x40000000) != 0);

    TocArchive(byte[] header, uint flags, List<Bundle> bundles, List<Chunk> chunks, int[] bundleHashMap, int[] chunkHashMap)
    {
        _header = header;
        _flags = flags;
        Bundles = bundles;
        Chunks = chunks;
        _bundleHashMap = bundleHashMap;
        _chunkHashMap = chunkHashMap;
    }

    public static TocArchive Parse(ReadOnlySpan<byte> source)
    {
        const int H = TocFile.HeaderSize;
        byte[] bytes = source.ToArray();
        if (bytes.Length < H + 48 || bytes[0] != 0 || bytes[1] != 0xD1 || bytes[2] != 0xCE)
            throw new InvalidDataException("Not a supported CAS TOC.");
        uint F(int i) => Read32(bytes, H + 4 * i);
        uint bundleMapAt = F(0), bundleTableAt = F(1), bundleCount = F(2);
        uint chunkMapAt = F(3), chunkIdsAt = F(4), chunkCount = F(5), chunkDataAt = F(9);
        uint namesAt = F(8), flags = F(11);
        if (bundleCount > 1_000_000 || chunkCount > 1_000_000)
            throw new InvalidDataException("TOC table count is implausible.");
        int bundles = checked((int)bundleCount), chunks = checked((int)chunkCount);
        CheckTable(bytes, bundleMapAt, (long)bundles * 4, "bundle hash map");
        CheckTable(bytes, bundleTableAt, (long)bundles * 16, "bundle table");
        CheckTable(bytes, chunkMapAt, (long)chunks * 4, "chunk hash map");
        CheckTable(bytes, chunkIdsAt, (long)chunks * 20, "chunk ID table");

        HuffmanNames? huffman = null;
        if ((flags & HasCompressedNames) != 0)
        {
            if (bytes.Length < H + 60) throw new InvalidDataException("Compressed TOC header is truncated.");
            uint namesCount = Read32(bytes, H + 48), tableCount = Read32(bytes, H + 52), tableAt = Read32(bytes, H + 56);
            if (namesCount > 16_000_000 || tableCount > 16_000_000)
                throw new InvalidDataException("Compressed-name table is implausibly large.");
            CheckTable(bytes, namesAt, (long)namesCount * 4, "compressed names");
            CheckTable(bytes, tableAt, (long)tableCount * 4, "Huffman table");
            huffman = HuffmanNames.Parse(bytes, Absolute(namesAt), checked((int)namesCount),
                Absolute(tableAt), checked((int)tableCount));
        }

        var bundleList = new List<Bundle>(bundles);
        var bundleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < bundles; i++)
        {
            int at = Absolute(bundleTableAt) + i * 16;
            int nameOffset = checked((int)Read32(bytes, at));
            string name = huffman is null
                ? ReadName(bytes, Absolute(namesAt) + nameOffset)
                : huffman.Decode(nameOffset);
            ValidateName(name);
            if (!bundleNames.Add(name)) throw new InvalidDataException($"Duplicate TOC bundle name: {name}");
            bundleList.Add(new Bundle(name, Read32(bytes, at + 4), Read64(bytes, at + 8)));
        }

        var chunkList = new List<Chunk>(chunks);
        var chunkIds = new HashSet<Guid>();
        for (int i = 0; i < chunks; i++)
        {
            int at = Absolute(chunkIdsAt) + i * 20;
            byte[] raw = bytes.AsSpan(at, 16).ToArray();
            Array.Reverse(raw);
            var id = new Guid(raw);
            if (!chunkIds.Add(id)) throw new InvalidDataException($"Duplicate TOC chunk ID: {id}");
            uint link = Read32(bytes, at + 16);
            if (link == uint.MaxValue)
            {
                chunkList.Add(new Chunk(id, 0, 0, 0, true));
                continue;
            }
            long location = (long)Absolute(chunkDataAt) + (link & 0xFFFFFF) * 4L;
            CheckTable(bytes, (uint)(location - H), 12, "chunk location");
            chunkList.Add(new Chunk(id, Read32(bytes, (int)location), Read32(bytes, (int)location + 4),
                Read32(bytes, (int)location + 8)));
        }

        int[] ReadMap(uint offset, int count)
        {
            var map = new int[count];
            for (int i = 0; i < count; i++) map[i] = (int)Read32(bytes, Absolute(offset) + i * 4);
            return map;
        }
        var result = new TocArchive(bytes.AsSpan(0, H).ToArray(), flags, bundleList, chunkList,
            ReadMap(bundleMapAt, bundles), ReadMap(chunkMapAt, chunks));
        result._originalBytes = bytes;
        result._originalLength = bytes.Length;
        return result;
    }

    public Bundle? FindBundle(string name)
    {
        if (Bundles.Count == 0) return null;
        int index = LookupIndex(Encoding.ASCII.GetBytes(name.ToLowerInvariant()), _bundleHashMap);
        if ((uint)index >= Bundles.Count) return null;
        var result = Bundles[index];
        return result.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? result : null;
    }

    public Chunk? FindChunk(Guid id)
    {
        if (Chunks.Count == 0) return null;
        int index = LookupIndex(id.ToByteArray(), _chunkHashMap);
        if ((uint)index >= Chunks.Count) return null;
        var result = Chunks[index];
        return result.Id == id ? result : null;
    }

    /// <summary>Rebuilds the lookup section, optionally appending self-contained bundle records and chunk locations.</summary>
    /// <param name="removeBundles">Existing bundles to drop from the table (a broken earlier install). Their records stay
    /// as unreferenced bytes in the opaque record area, so no other record moves.</param>
    /// <param name="removeChunks">Existing chunk ids to drop with them.</param>
    public byte[] Rebuild(IEnumerable<NewBundle>? newBundles = null, IEnumerable<Chunk>? newChunks = null,
        IReadOnlySet<string>? removeBundles = null, IReadOnlySet<Guid>? removeChunks = null)
    {
        const int H = TocFile.HeaderSize;
        var additions = newBundles?.ToList() ?? [];
        var extraChunks = newChunks?.ToList() ?? [];
        removeBundles ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        removeChunks ??= new HashSet<Guid>();
        foreach (var name in removeBundles)
            if (FindBundle(name) is null) throw new InvalidDataException($"Cannot remove a bundle the TOC does not have: {name}");
        foreach (var id in removeChunks)
            if (FindChunk(id) is null) throw new InvalidDataException($"Cannot remove a chunk the TOC does not have: {id}");
        var keptBundles = Bundles.Where(b => !removeBundles.Contains(b.Name)).ToList();
        var keptChunks = Chunks.Where(c => !removeChunks.Contains(c.Id)).ToList();
        var names = new HashSet<string>(keptBundles.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>(keptChunks.Select(c => c.Id));
        foreach (var bundle in additions)
        {
            ValidateName(bundle.Name);
            if (bundle.Record.Length < 32) throw new InvalidDataException($"Bundle record is truncated: {bundle.Name}");
            if (!names.Add(bundle.Name)) throw new InvalidDataException($"Duplicate TOC bundle name: {bundle.Name}");
        }
        foreach (var chunk in extraChunks)
            if (!ids.Add(chunk.Id)) throw new InvalidDataException($"Duplicate TOC chunk ID: {chunk.Id}");

        ulong firstRecord = ulong.MaxValue;
        foreach (var bundle in Bundles)
            if (bundle.RecordOffset != ulong.MaxValue && bundle.RecordOffset <= int.MaxValue)
                firstRecord = Math.Min(firstRecord, bundle.RecordOffset);
        // The opaque record area begins at the first record. Compressed strings and their Huffman table precede it.
        // An empty TOC has no record area to preserve.
        int tailLength = firstRecord == ulong.MaxValue ? 0 : checked((int)(_originalLength - H - (long)firstRecord));
        if (tailLength < 0) throw new InvalidDataException("A TOC bundle record lies beyond the file.");
        int tailSource = firstRecord == ulong.MaxValue ? _originalLength : Absolute(checked((uint)firstRecord));

        var allBundles = keptBundles.Select(b => new PendingBundle(b.Name, b.SizeAndFlags, b.RecordOffset, null))
            .Concat(additions.Select(b => new PendingBundle(b.Name, b.SizeAndFlags, 0, b.Record))).ToList();
        var allChunks = keptChunks.Concat(extraChunks).ToList();
        var bundlePlacement = Place(allBundles, b => Encoding.ASCII.GetBytes(b.Name.ToLowerInvariant()));
        var chunkPlacement = Place(allChunks, c => c.Id.ToByteArray());
        var sortedBundles = bundlePlacement.Items;
        var sortedChunks = chunkPlacement.Items;

        using var output = new MemoryStream();
        output.Write(_header);
        output.Write(new byte[48]); // Uncompressed TOCs have twelve header fields, not fifteen.
        void U32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
            output.Write(b);
        }
        uint Position() => checked((uint)(output.Position - H));
        void Align8() { while ((output.Position - H) % 8 != 0) output.WriteByte(0); }

        uint bundleMapAt = Position();
        foreach (int value in bundlePlacement.Map) U32(unchecked((uint)value));
        Align8();
        uint bundleTableAt = Position();
        long bundleEntries = output.Position;
        output.Write(new byte[checked(sortedBundles.Count * 16)]);
        Align8();
        uint chunkMapAt = Position();
        foreach (int value in chunkPlacement.Map) U32(unchecked((uint)value));
        Align8();
        uint chunkIdsAt = Position();
        int activeChunk = 0;
        for (int i = 0; i < sortedChunks.Count; i++)
        {
            byte[] id = sortedChunks[i].Id.ToByteArray();
            Array.Reverse(id);
            output.Write(id);
            U32(sortedChunks[i].Removed ? uint.MaxValue : checked((uint)(activeChunk++ * 3)) | 0x01000000);
        }
        Align8();
        uint chunkDataAt = Position();
        foreach (var chunk in sortedChunks)
        {
            if (chunk.Removed) continue;
            U32(chunk.LocationWord); U32(chunk.Offset); U32(chunk.Size);
        }
        Align8();
        uint namesAt = Position();
        var nameOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var bundle in sortedBundles)
        {
            if (nameOffsets.ContainsKey(bundle.Name)) continue;
            nameOffsets.Add(bundle.Name, checked((uint)(output.Position - H - namesAt)));
            output.Write(Encoding.ASCII.GetBytes(bundle.Name));
            output.WriteByte(0);
        }
        Align8();
        uint recordsAt = Position();
        if (recordsAt > uint.MaxValue - (uint)tailLength) throw new InvalidDataException("Rebuilt TOC exceeds 4 GB.");
        output.Write(_originalBytes, tailSource, tailLength);
        long recordDelta = recordsAt - (long)firstRecord;

        for (int i = 0; i < sortedBundles.Count; i++)
        {
            var bundle = sortedBundles[i];
            long entry = bundleEntries + i * 16;
            Write32(output, entry, nameOffsets[bundle.Name]);
            Write32(output, entry + 4, bundle.SizeAndFlags);
            ulong recordOffset;
            if (bundle.NewRecord is null)
            {
                if (bundle.RecordOffset == ulong.MaxValue) recordOffset = ulong.MaxValue;
                else
                {
                    long relocated = checked((long)bundle.RecordOffset + recordDelta);
                    if (relocated < 0) throw new InvalidDataException($"Bundle record moved before the TOC: {bundle.Name}");
                    recordOffset = (ulong)relocated;
                }
            }
            else
            {
                Align8();
                recordOffset = checked((ulong)Position());
                output.Write(bundle.NewRecord);
            }
            Write64(output, entry + 8, recordOffset);
        }

        output.Position = H;
        U32(bundleMapAt); U32(bundleTableAt); U32((uint)sortedBundles.Count);
        U32(chunkMapAt); U32(chunkIdsAt); U32((uint)sortedChunks.Count);
        U32(chunkDataAt); U32(chunkDataAt); U32(namesAt); U32(chunkDataAt);
        U32(checked((uint)sortedChunks.Count * 3)); U32(_flags & ~HasCompressedNames);
        return output.ToArray();
    }

    // Parse inputs are retained only so Rebuild can copy the opaque original record area.
    byte[] _originalBytes = [];
    int _originalLength;

    sealed record PendingBundle(string Name, uint SizeAndFlags, ulong RecordOffset, byte[]? NewRecord);
    sealed record Placement<T>(int[] Map, List<T> Items);

    static Placement<T> Place<T>(IReadOnlyList<T> input, Func<T, byte[]> key)
    {
        int size = input.Count;
        if (size == 0) return new Placement<T>([], []);
        var buckets = new List<(byte[] Key, T Value)>[size];
        for (int i = 0; i < size; i++) buckets[i] = [];
        foreach (var item in input)
        {
            byte[] k = key(item);
            buckets[(int)(Hash(k) % size)].Add((k, item));
        }
        Array.Sort(buckets, (a, b) => b.Count.CompareTo(a.Count));
        var items = new T?[size];
        var used = new bool[size];
        var map = Enumerable.Repeat(-1, size).ToArray();
        int bucket = 0;
        for (; bucket < size && buckets[bucket].Count > 1; bucket++)
        {
            var collisions = buckets[bucket];
            var indices = new List<int>(collisions.Count);
            uint seed = 1;
            int i = 0;
            while (i < collisions.Count)
            {
                int index = (int)(Hash(collisions[i].Key, seed) % size);
                if (used[index] || indices.Contains(index)) { seed++; i = 0; indices.Clear(); }
                else { indices.Add(index); i++; }
            }
            map[(int)(Hash(collisions[0].Key) % size)] = checked((int)seed);
            for (i = 0; i < collisions.Count; i++)
            {
                items[indices[i]] = collisions[i].Value;
                used[indices[i]] = true;
            }
        }
        int next = 0;
        for (; bucket < size && buckets[bucket].Count == 1; bucket++)
        {
            while (used[next]) next = (next + 1) % size;
            map[(int)(Hash(buckets[bucket][0].Key) % size)] = -next - 1;
            items[next] = buckets[bucket][0].Value;
            next = (next + 1) % size;
        }
        return new Placement<T>(map, items.Select(v => v!).ToList());
    }

    static int LookupIndex(byte[] key, IReadOnlyList<int> map)
    {
        int index = map[(int)(Hash(key) % map.Count)];
        return index < 0 ? -index - 1 : (int)(Hash(key, (uint)index) % map.Count);
    }

    static uint Hash(byte[] bytes, uint offset = 0x811C9DC5)
    {
        const uint prime = 0x01000193;
        uint hash = offset;
        unchecked { foreach (byte b in bytes) hash = (hash * prime) ^ (uint)(sbyte)b; }
        return hash % prime;
    }

    sealed class HuffmanNames
    {
        sealed class Node
        {
            public uint Value { get; }
            public Node? Left { get; }
            public Node? Right { get; }
            public bool IsLeaf => Left is null && Right is null;
            public char Character => (char)~Value;
            public Node(uint value, Node? left = null, Node? right = null) { Value = value; Left = left; Right = right; }
        }
        readonly Node _root;
        readonly uint[] _data;
        HuffmanNames(Node root, uint[] data) { _root = root; _data = data; }

        public static HuffmanNames Parse(byte[] bytes, int dataAt, int dataCount, int tableAt, int tableCount)
        {
            var data = new uint[dataCount];
            for (int i = 0; i < data.Length; i++) data[i] = Read32(bytes, dataAt + i * 4);
            var nodes = new List<Node>();
            Node? left = null, right = null, root = null;
            uint nodeValue = 0;
            for (int i = 0; i < tableCount; i++)
            {
                var node = new Node(Read32(bytes, tableAt + i * 4));
                int existing = nodes.FindIndex(n => n.Value == node.Value);
                if (existing >= 0) node = nodes[existing];
                if (left is null) left = node;
                else if (right is null)
                {
                    right = node;
                    if (existing < 0) nodes.Add(right);
                    node = new Node(nodeValue++, left, right);
                    root = node;
                    left = right = null;
                    existing = -1;
                }
                if (existing < 0) nodes.Add(node);
            }
            if (root is null) throw new InvalidDataException("TOC Huffman table has no root node.");
            return new HuffmanNames(root, data);
        }

        public string Decode(int bitIndex)
        {
            if (bitIndex < 0 || bitIndex / 32 >= _data.Length) throw new InvalidDataException("Invalid Huffman bit offset.");
            var name = new StringBuilder();
            int remaining = _data.Length * 32 - bitIndex;
            while (remaining-- > 0)
            {
                var node = _root;
                while (!node.IsLeaf)
                {
                    if (bitIndex / 32 >= _data.Length) throw new InvalidDataException("Huffman name is truncated.");
                    int bit = (int)((_data[bitIndex / 32] >> (bitIndex % 32)) & 1);
                    node = bit == 0 ? node.Left! : node.Right!;
                    bitIndex++;
                }
                if (node.Character == 0) return name.ToString();
                name.Append(node.Character);
                if (name.Length > 4096) throw new InvalidDataException("Huffman bundle name is too long.");
            }
            throw new InvalidDataException("Huffman bundle name has no terminator.");
        }
    }

    static int Absolute(uint relative) => checked((int)(TocFile.HeaderSize + relative));
    static uint Read32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at, 4));
    static ulong Read64(byte[] b, int at) => BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at, 8));
    static void Write32(Stream s, long at, uint value)
    {
        long restore = s.Position; s.Position = at;
        Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); s.Write(b);
        s.Position = restore;
    }
    static void Write64(Stream s, long at, ulong value)
    {
        long restore = s.Position; s.Position = at;
        Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, value); s.Write(b);
        s.Position = restore;
    }
    static string ReadName(byte[] bytes, int at)
    {
        if ((uint)at >= bytes.Length) throw new InvalidDataException("TOC name lies outside its string table.");
        int end = Array.IndexOf(bytes, (byte)0, at);
        if (end < 0) throw new InvalidDataException("TOC bundle name is not terminated.");
        return Encoding.ASCII.GetString(bytes, at, end - at);
    }
    static void ValidateName(string name)
    {
        if (name.Length == 0 || name.Length > 4096 || name.Any(c => c < 0x20 || c > 0x7e))
            throw new InvalidDataException("TOC bundle names must be nonempty printable ASCII.");
    }
    static void CheckTable(byte[] bytes, uint relative, long size, string label)
    {
        long at = TocFile.HeaderSize + (long)relative;
        if (at < TocFile.HeaderSize || size < 0 || at + size > bytes.Length)
            throw new InvalidDataException($"TOC {label} lies outside the file.");
    }
}
