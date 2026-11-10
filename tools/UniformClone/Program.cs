using System.Buffers.Binary;
using System.Text;
using Suite.Core;
using Suite.Core.Frostbite;

// Read-only donor structure probe while the cloner is being built.
if (args.Length < 2) { Console.Error.WriteLine("usage: UniformClone <game> <folder-prefix>"); return 2; }
Oodle.Ensure(SuiteSettings.Load());
var index = BundleIndex.Build(args[0]);
string prefix = args[1].TrimEnd('/') + "/";
var rows = index.ResByName.Values.SelectMany(x => x).Where(r => r.Name.StartsWith(prefix, StringComparison.Ordinal))
    .GroupBy(r => r.Bundle).OrderBy(g => g.Key.FirstName).ToList();
Console.WriteLine($"{rows.Count} donor bundle(s)");
foreach (var group in rows)
{
    var b = group.Key;
    var toc = File.ReadAllBytes(b.Toc);
    const int H = TocFile.HeaderSize;
    uint F(int i) => BinaryPrimitives.ReadUInt32BigEndian(toc.AsSpan(H + 4 * i));
    long table = H + F(1); int count = (int)F(2);
    uint? hash = null;
    for (int i = 0; i < count; i++)
    {
        long rec = H + (long)BinaryPrimitives.ReadUInt64BigEndian(toc.AsSpan((int)table + i * 16 + 8));
        if (rec == b.RecordAt) { hash = BinaryPrimitives.ReadUInt32BigEndian(toc.AsSpan((int)table + i * 16)); break; }
    }
    Console.WriteLine($"bundle {b.FirstName}\n  toc={Path.GetFileName(b.Toc)} record=0x{b.RecordAt:X} hash=0x{hash:X8} fnv1=0x{Fnv(b.FirstName, false):X8} fnv1a=0x{Fnv(b.FirstName, true):X8}");
    foreach (var r in group) Console.WriteLine($"  RES {r.Name}");
    foreach (var e in index.EbxByName.Values.SelectMany(x => x).Where(e => e.Bundle == b)) Console.WriteLine($"  EBX {e.Name}");
}
return 0;

static uint Fnv(string text, bool a)
{
    uint h = 2166136261;
    foreach (var c in Encoding.ASCII.GetBytes(text)) { if (a) h ^= c; h *= 16777619; if (!a) h ^= c; }
    return h;
}
