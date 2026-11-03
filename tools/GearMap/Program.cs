// GearMap - which goalie gear model each DB id means, in both games.  Read-only.
// The id in stockgoalieequipment / exhibitiongoalieequipment (pads, blocker, trapper; stick) is not an index: it is
// looked up in content/common/configs/mappings/goalie_equipment_mapping (goalie_stick_mapping for sticks), an EBX
// holding {name, id} records - u32 count at 0x84, then 16-byte records from 0x88: i64 pointer to the name relative to
// the record, i64 id.  NHL 24 is read through the bundle index; NHL 27's PS5 CAS files store EBX uncompressed, so its
// copy is found by the asset path string (Patch before Data).  Shared models keep the same id in both games
// (110 brians_gnetik_4, 118 true_l12_2, 122 bauer_supreme_mach, 126 true_catalyst_px3).
// Writes %APPDATA%\CenterIceModdingSuite\data\goalie_<equipment|stick>_<nhl24|nhl27>.tsv (id, model).
using System.Buffers.Binary;
using System.Text;
using Suite.Core;
using Suite.Core.Frostbite;

var settings = SuiteSettings.Load();
string[] maps = ["goalie_equipment_mapping", "goalie_stick_mapping"];
const string Folder = "content/common/configs/mappings/";

static List<(long Id, string Name)> Parse(byte[] e)
{
    if (e.Length < 0x88 || Encoding.ASCII.GetString(e, 0, 4) != "RIFF") throw new InvalidDataException("not an EBX");
    int n = BinaryPrimitives.ReadInt32LittleEndian(e.AsSpan(0x84));
    var list = new List<(long, string)>();
    for (int k = 0; k < n; k++)
    {
        int r = 0x88 + 16 * k;
        int at = r + (int)BinaryPrimitives.ReadInt64LittleEndian(e.AsSpan(r));
        int end = Array.IndexOf(e, (byte)0, at);
        var name = Encoding.ASCII.GetString(e, at, end - at);
        if (name.Length == 0 || name.Any(c => c is < ' ' or > '~')) throw new InvalidDataException($"record {k}: not a name");
        list.Add((BinaryPrimitives.ReadInt64LittleEndian(e.AsSpan(r + 8)), name));
    }
    return list;
}

void Write(string game, string map, List<(long Id, string Name)> rows)
{
    var file = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Roaming, "data")), $"{map.Replace("_mapping", "")}_{game}.tsv");
    File.WriteAllLines(file, rows.Select(r => $"{r.Id}\t{r.Name}"));
    Console.WriteLine($"{game} {map}: {rows.Count} models -> {file}");
}

// NHL 24, through the read view's bundle index.  The mapping sits in many bundles and the base game's copies stop
// short of the ids the update added (129-131), so every copy is read and the fullest one wins.
var index = BundleIndex.Build(settings.Nhl24ReadView);
foreach (var map in maps)
{
    var copies = new List<(string Where, List<(long, string)> Rows)>();
    foreach (var e in index.EbxByName[Folder + map])
    {
        var stored = BundleIndex.ReadBytes(e.Data);
        byte[] ebx; try { ebx = ChunkBlocks.Parse(stored).Decode(); } catch { ebx = stored; }
        try { copies.Add((Path.GetRelativePath(settings.Nhl24ReadView, e.Data.Cas), Parse(ebx))); } catch (InvalidDataException) { }
    }
    var best = copies.OrderByDescending(c => c.Rows.Count).ThenByDescending(c => c.Where.StartsWith("Patch")).First();
    Console.WriteLine($"  nhl24 {map}: {copies.Count} copies, sizes {string.Join(",", copies.Select(c => c.Rows.Count).Distinct())}; using {best.Where}");
    Write("nhl24", map, best.Rows);
}

// NHL 27: the EBX begins with RIFF a little before its own path string.
var root = settings.Nhl27GameRoot;
foreach (var map in maps)
{
    var needle = Encoding.ASCII.GetBytes(Folder + map + "\0");
    List<(long, string)>? found = null;
    foreach (var cas in new[] { "Patch", "Data" }.Select(t => Path.Combine(root, t)).Where(Directory.Exists)
                 .SelectMany(d => Directory.EnumerateFiles(d, "*.cas", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)))
    {
        if (Find(cas, needle) is not { } at) continue;
        using var fs = File.OpenRead(cas);
        long from = Math.Max(0, at - 0x10000);
        var buf = new byte[at - from];
        fs.Position = from; fs.ReadExactly(buf);
        int riff = buf.AsSpan().LastIndexOf("RIFF"u8);
        int size = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(riff + 4)) + 8;
        var ebx = new byte[size];
        fs.Position = from + riff; fs.ReadExactly(ebx);
        found = Parse(ebx);
        Console.WriteLine($"  nhl27 {map} in {Path.GetRelativePath(root, cas)} @{from + riff}");
        break;
    }
    if (found is null) { Console.WriteLine($"nhl27 {map}: not found"); continue; }
    Write("nhl27", map, found);
}
return 0;

static long? Find(string path, byte[] needle)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
    var buf = new byte[(8 << 20) + needle.Length];
    long pos = 0; int carry = 0, read;
    while ((read = fs.Read(buf, carry, buf.Length - carry)) > 0)
    {
        int len = carry + read;
        int i = buf.AsSpan(0, len).IndexOf(needle);
        if (i >= 0) return pos - carry + i;
        carry = Math.Min(needle.Length - 1, len);
        Array.Copy(buf, len - carry, buf, 0, carry);
        pos += read;
    }
    return null;
}
