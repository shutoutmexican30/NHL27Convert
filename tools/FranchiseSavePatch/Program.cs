// FranchiseSavePatch v2: read/write NHL 24 franchise saves (BeAGM* folders under
// user\home\<id>\savedata\CUSA37933\<name>\). The DATA file is a SaveLoad container:
// 80-byte INDEX entries (name 32, pad 8, size/start/end u64) pointing at records in
// DATA; the main record is u32(size-16), 12 bytes, 0x01, one LZ4 frame, u32 decoded
// length. That frame decodes to concatenated [0x01 + LZ4 frame] sections; section 3
// of a BeAGM save is the dynasty T3DB ("DB\0\10\0...").
//
//   FranchiseSavePatch scan <save-dir> [more dirs...]     decode + report
//   FranchiseSavePatch set <save-dir> <u32> <u32> [...]   replace value(s), backup first
// Values are matched as little-endian u32 everywhere; every occurrence is replaced.
// The folder is backed up (DATA/INDEX copied next to it as <name>.bak-<time>) before
// writing; the rewritten save is decoded again and checked byte-for-byte.
using System.Buffers.Binary;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;

if (args.Length < 2) { Console.Error.WriteLine("usage: FranchiseSavePatch scan <save-dir>... | set <save-dir> <old> <new> [...]"); return 2; }

var dir = Path.GetFullPath(args[1]);
var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
var dataP = Path.Combine(dir, "DATA");
var indexP = Path.Combine(dir, "INDEX");

byte[] Decode(byte[] frame)
{
    using var input = LZ4Stream.Decode(new MemoryStream(frame));
    using var output = new MemoryStream();
    var buf = new byte[65536];
    try
    {
        int n;
        while ((n = input.Read(buf, 0, buf.Length)) > 0) output.Write(buf, 0, n);
    }
    catch (InvalidDataException) when (output.Length > 0) { /* trailing bytes after the frame end */ }
    return output.ToArray();
}
byte[] Encode(byte[] raw)
{
    var output = new MemoryStream();
    var settings = new LZ4EncoderSettings { BlockSize = 64 * 1024, ChainBlocks = false, ContentChecksum = true, CompressionLevel = LZ4Level.L09_HC };
    using (var enc = LZ4Stream.Encode(output, settings, leaveOpen: true)) enc.Write(raw);
    return output.ToArray();
}

var data = File.ReadAllBytes(dataP);
var index = File.ReadAllBytes(indexP);
var entries = new List<(string Name, long Size, long Start, long End)>();
for (int off = 0; off + 80 <= index.Length; off += 80)
{
    var nm = System.Text.Encoding.ASCII.GetString(index, off, 32).TrimEnd('\0');
    long size = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(off + 40));
    long start = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(off + 48));
    long end = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(off + 56));
    entries.Add((nm, size, start, end));
}
var rec = entries.FirstOrDefault(e => e.Name == name);
if (rec.Name is null) { Console.Error.WriteLine($"no record named {name}"); return 1; }
var raw = data.AsSpan((int)rec.Start, (int)(rec.End - rec.Start)).ToArray();
if (raw[16] != 1) { Console.Error.WriteLine("record is not an LZ4 payload"); return 1; }
var level1 = Decode(raw[17..^4].ToArray());
if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(raw.Length - 4)) != level1.Length)
    Console.WriteLine($"warn: decoded length trailer {BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(raw.Length - 4))} != {level1.Length}");

// split sections
var sections = new List<byte[]>();
int pos = 0;
while (pos < level1.Length)
{
    if (level1[pos] != 1) { Console.Error.WriteLine($"section marker missing at 0x{pos:X}"); return 1; }
    int search = pos + 1;
    while (true)
    {
        int nxt = -1;
        for (int i = search; i + 5 <= level1.Length; i++)
            if (level1[i] == 1 && level1[i + 1] == 0x04 && level1[i + 2] == 0x22 && level1[i + 3] == 0x4D && level1[i + 4] == 0x18) { nxt = i; break; }
        int sliceEnd = nxt < 0 ? level1.Length : nxt;
        try
        {
            sections.Add(Decode(level1[(pos + 1)..sliceEnd].ToArray()));
            if (nxt < 0) break;
            pos = nxt;
            break;
        }
        catch { if (nxt < 0) throw; search = nxt + 1; }
    }
}
Console.WriteLine($"{name}: record {(rec.End - rec.Start)} bytes -> {sections.Count} sections");
for (int i = 0; i < sections.Count; i++)
    Console.WriteLine($"  [{i}] {sections[i].Length,9} bytes {(sections[i].Length >= 4 && sections[i][0] == 'D' && sections[i][1] == 'B' ? "T3DB" : sections[i].Length >= 4 ? sections[i][..4].ToString() : "")}");

if (args[0] == "scan")
{
    (string label, uint v)[] probes =
    [
        ("cap 83500000", 83500000), ("floor 61700000", 61700000), ("min 775000", 775000),
        ("max 16700000", 16700000), ("rookie 950000", 950000), ("cap/5k 16700", 16700), ("floor/5k 12340", 12340),
        ("min/5k 155", 155), ("max/5k 3340", 3340), ("year 2023", 2023), ("yyyymmdd 20231012", 20231012),
    ];
    for (int i = 0; i < sections.Count; i++)
    {
        foreach (var (label, v) in probes)
        {
            var pat = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(pat, v);
            int n = 0; var first = new List<string>();
            for (int p = 0; p + 4 <= sections[i].Length; p++)
                if (sections[i].AsSpan(p, 4).SequenceEqual(pat))
                {
                    n++; if (first.Count < 6) first.Add($"0x{p:X}");
                }
            if (n > 0) Console.WriteLine($"  [{i}] {label}: x{n} [{string.Join(",", first)}]");
        }
    }
    return 0;
}

if (args[0] == "set")
{
    var pairs = new List<(uint Old, uint New)>();
    for (int i = 2; i + 1 < args.Length; i += 2) pairs.Add((uint.Parse(args[i]), uint.Parse(args[i + 1])));
    int total = 0;
    for (int i = 0; i < sections.Count; i++)
    {
        var b = sections[i];
        foreach (var (o, n) in pairs)
        {
            var pat = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(pat, o);
            var rep = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(rep, n);
            for (int p = 0; p + 4 <= b.Length; p++)
                if (b.AsSpan(p, 4).SequenceEqual(pat)) { rep.CopyTo(b, p); total++; }
        }
    }
    if (total == 0) { Console.WriteLine("no occurrences; nothing to write"); return 0; }
    Console.WriteLine($"replacing {total} occurrence(s)");

    var backup = dir + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
    Directory.CreateDirectory(backup);
    File.Copy(dataP, Path.Combine(backup, "DATA"));
    File.Copy(indexP, Path.Combine(backup, "INDEX"));
    Console.WriteLine("backup: " + backup);

    var ms = new MemoryStream();
    foreach (var s in sections) { ms.WriteByte(1); ms.Write(Encode(s)); }
    var inner = ms.ToArray();
    var frame = Encode(inner);
    var newRaw = new List<byte> { };
    newRaw.AddRange(raw[..16]);
    newRaw.Add(1);
    newRaw.AddRange(frame);
    newRaw.AddRange(BitConverter.GetBytes((uint)inner.Length));

    var newData = new List<byte>(data);
    if (newRaw.Count > raw.Length)
    {
        // append at end of DATA, repoint the record
        long newStart = newData.Count;
        newData.AddRange(newRaw);
        BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(entries.IndexOf(rec) * 80 + 40), newRaw.Count);
        BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(entries.IndexOf(rec) * 80 + 48), newStart);
        BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(entries.IndexOf(rec) * 80 + 56), newStart + newRaw.Count);
        // size-16 field
        for (int k = 0; k < 4; k++) newData[(int)newStart + k] = (byte)(newRaw.Count - 16 >> k * 8);
    }
    else
    {
        for (int k = 0; k < newRaw.Count; k++) newData[(int)rec.Start + k] = newRaw[k];
        BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(entries.IndexOf(rec) * 80 + 40), newRaw.Count);
    }
    File.WriteAllBytes(dataP, newData.ToArray());
    File.WriteAllBytes(indexP, index);
    Console.WriteLine($"wrote DATA ({newData.Count} bytes) + INDEX");

    // verify: decode again
    var check = Decode(File.ReadAllBytes(dataP).AsSpan((int)BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(entries.IndexOf(rec) * 80 + 48)) + 17, (int)newRaw.Count - 17 - 4).ToArray());
    Console.WriteLine(check.Length == inner.Length && check.SequenceEqual(inner) ? "VERIFY OK" : "VERIFY FAILED");
    return 0;
}
return 2;
