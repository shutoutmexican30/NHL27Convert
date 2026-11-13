using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace Suite.Core;

/// <summary>
/// shadPS4 runtime memory patches (<c>user\patches\nhl24.xml</c>, applied by the emulator's MemoryPatcher when the
/// game loads) for NHL 24's 1.60 <c>game-UPDATE\eboot.bin</c>.
///
/// Address convention, checked against the live emulator: Address = 0x400000 + virtual address, and in the 1.60 eboot
/// the code+rodata segment starts at file offset 0x51D80 = vaddr 0, so Address = 0x400000 + file offset - 0x51D80.
/// Proof: the working replay-compression entry (Address 0x068E0770) is the je at eboot file offset 0x065324F0, and
/// the emulator log reports it applied.
/// </summary>
public static class EbootPatches
{
    public const long PatchBase = 0x400000;
    public const long CodeFileOffset160 = 0x51D80;

    public static long RuntimeAddress(long fileOffset, long codeFileOffset = CodeFileOffset160) =>
        fileOffset < codeFileOffset
            ? throw new ArgumentOutOfRangeException(nameof(fileOffset), $"0x{fileOffset:X} is in the SELF header, not the image.")
            : PatchBase + fileOffset - codeFileOffset;

    public static long FileOffset(long runtimeAddress, long codeFileOffset = CodeFileOffset160) =>
        runtimeAddress - PatchBase + codeFileOffset;

    public static string Hex(long value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);

    public static long ParseHex(string text) =>
        long.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>Replaces every &lt;Metadata&gt; with the given entries' names (or adds them at the end); keeps the rest.</summary>
    public static XDocument Upsert(XDocument doc, IEnumerable<XElement> entries)
    {
        var root = doc.Root ?? throw new InvalidDataException("Patch file has no root element.");
        foreach (var entry in entries)
        {
            string name = (string?)entry.Attribute("Name") ?? throw new InvalidDataException("Entry without a Name.");
            string ver = (string?)entry.Attribute("AppVer") ?? "";
            var old = root.Elements("Metadata").Where(m => (string?)m.Attribute("Name") == name && ((string?)m.Attribute("AppVer") ?? "") == ver).ToList();
            if (old.Count > 0)
            {
                old[0].ReplaceWith(entry);
                foreach (var extra in old.Skip(1)) extra.Remove();
            }
            else root.Add(entry);
        }
        return doc;
    }
}

/// <summary>
/// The salary-cap patch: editable values (floor, cap, min and max salary) and the eboot sites that hold them, read
/// from <c>data\salary_cap.json</c>. Every site stores a little-endian int32; its stock value is verified in the
/// eboot before an entry is built, so the patch only ever replaces the exact original bytes.
/// </summary>
public sealed class SalaryCapPatch
{
    public sealed record Site(string Group, string Field, long FileOffset, int Stock);

    public required string Name { get; init; }
    public required string AppVer { get; init; }
    public required long EbootSize { get; init; }
    public required long CodeFileOffset { get; init; }
    public required IReadOnlyDictionary<string, int> Values { get; init; }
    public required IReadOnlyList<Site> Sites { get; init; }
    public required IReadOnlyDictionary<string, string> GroupComments { get; init; }

    public static string ShippedPath => Path.Combine(AppContext.BaseDirectory, "data", "salary_cap.json");

    public static SalaryCapPatch Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var values = r.GetProperty("values").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);
        var sites = new List<Site>();
        var comments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in r.GetProperty("groups").EnumerateArray())
        {
            string group = g.GetProperty("name").GetString()!;
            comments[group] = g.TryGetProperty("comment", out var c) ? c.GetString() ?? "" : "";
            foreach (var s in g.GetProperty("sites").EnumerateArray())
            {
                string field = s.GetProperty("field").GetString()!;
                if (!values.ContainsKey(field)) throw new InvalidDataException($"Site field '{field}' has no value in 'values'.");
                sites.Add(new Site(group, field, EbootPatches.ParseHex(s.GetProperty("fileOffset").GetString()!), s.GetProperty("stock").GetInt32()));
            }
        }
        if (sites.Select(s => s.FileOffset).Distinct().Count() != sites.Count) throw new InvalidDataException("Duplicate site offsets.");
        foreach (var (k, v) in values)
            if (v <= 0) throw new InvalidDataException($"{k} must be positive.");
        if (values.TryGetValue("floor", out int f) && values.TryGetValue("cap", out int cap) && f > cap)
            throw new InvalidDataException("floor is above cap.");
        if (values.TryGetValue("minSalary", out int mn) && values.TryGetValue("maxSalary", out int mx) && mn > mx)
            throw new InvalidDataException("minSalary is above maxSalary.");
        return new SalaryCapPatch
        {
            Name = r.GetProperty("name").GetString()!,
            AppVer = r.GetProperty("appVer").GetString()!,
            EbootSize = r.GetProperty("ebootSize").GetInt64(),
            CodeFileOffset = EbootPatches.ParseHex(r.GetProperty("codeFileOffset").GetString()!),
            Values = values,
            Sites = sites,
            GroupComments = comments,
        };
    }

    /// <summary>Problems that make the eboot unsafe to patch: wrong size, or a site not holding its stock value.</summary>
    public IReadOnlyList<string> Verify(ReadOnlySpan<byte> eboot)
    {
        var problems = new List<string>();
        if (eboot.Length != EbootSize) problems.Add($"eboot is {eboot.Length:N0} bytes, expected {EbootSize:N0} (1.60 game-UPDATE eboot).");
        foreach (var s in Sites)
        {
            if (s.FileOffset < CodeFileOffset || s.FileOffset + 4 > eboot.Length) { problems.Add($"{EbootPatches.Hex(s.FileOffset)} is outside the image."); continue; }
            int now = BinaryPrimitives.ReadInt32LittleEndian(eboot.Slice((int)s.FileOffset, 4));
            if (now != s.Stock) problems.Add($"{EbootPatches.Hex(s.FileOffset)} ({s.Field}) holds {now:N0}, expected stock {s.Stock:N0}.");
        }
        return problems;
    }

    /// <summary>One enabled &lt;Metadata&gt; per group, one byte line per changed byte (the format the emulator is known to apply).</summary>
    public IReadOnlyList<(string Comment, XElement Entry)> BuildEntries()
    {
        var list = new List<(string, XElement)>();
        foreach (var group in Sites.GroupBy(s => s.Group))
        {
            var lines = new XElement("PatchList");
            foreach (var s in group)
            {
                int value = Values[s.Field];
                lines.Add(new XComment($" file {EbootPatches.Hex(s.FileOffset)} {s.Field}: {s.Stock:N0} -> {value:N0} "));
                Span<byte> was = stackalloc byte[4], now = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(was, s.Stock);
                BinaryPrimitives.WriteInt32LittleEndian(now, value);
                for (int i = 0; i < 4; i++)
                {
                    if (was[i] == now[i]) continue;
                    lines.Add(new XElement("Line",
                        new XAttribute("Type", "byte"),
                        new XAttribute("Address", EbootPatches.Hex(EbootPatches.RuntimeAddress(s.FileOffset + i, CodeFileOffset))),
                        new XAttribute("Value", "0x" + now[i].ToString("X2", CultureInfo.InvariantCulture))));
                }
            }
            var meta = new XElement("Metadata",
                new XAttribute("Name", $"{Name} - {group.Key}"),
                new XAttribute("isEnabled", "true"),
                new XAttribute("AppVer", AppVer),
                new XAttribute("AppElf", "eboot.bin"),
                new XAttribute("Author", "CIMS"),
                lines);
            list.Add((GroupComments.TryGetValue(group.Key, out var c) ? c : "", meta));
        }
        return list;
    }

    /// <summary>Upserts the entries into a patch document, putting each group's comment just before its entry.</summary>
    public XDocument ApplyTo(XDocument doc)
    {
        var root = doc.Root ?? throw new InvalidDataException("Patch file has no root element.");
        foreach (var (comment, meta) in BuildEntries())
        {
            EbootPatches.Upsert(doc, new[] { meta });
            var placed = root.Elements("Metadata").First(m => (string?)m.Attribute("Name") == (string?)meta.Attribute("Name") && (string?)m.Attribute("AppVer") == AppVer);
            string text = " " + comment + " ";
            // The comment directly above one of these entries is the group comment it was written with: refresh it.
            if (comment.Length > 0)
            {
                if (placed.PreviousNode is XComment pc) pc.Value = text;
                else placed.AddBeforeSelf(new XComment(text));
            }
        }
        return doc;
    }

    /// <summary>Checks a patch document against the eboot: every byte line of these entries lies on a known site and its stock byte.</summary>
    public IReadOnlyList<string> ReadBack(XDocument doc, ReadOnlySpan<byte> eboot)
    {
        var problems = new List<string>(Verify(eboot));
        var siteByte = new Dictionary<long, (Site Site, int Index)>();
        foreach (var s in Sites) for (int i = 0; i < 4; i++) siteByte[s.FileOffset + i] = (s, i);
        var names = Sites.Select(s => $"{Name} - {s.Group}").ToHashSet();
        int lines = 0;
        foreach (var m in doc.Root!.Elements("Metadata").Where(m => names.Contains((string?)m.Attribute("Name") ?? "") && (string?)m.Attribute("AppVer") == AppVer))
        {
            if ((string?)m.Attribute("isEnabled") != "true") problems.Add($"'{m.Attribute("Name")!.Value}' is not enabled.");
            foreach (var l in m.Descendants("Line"))
            {
                lines++;
                long file = EbootPatches.FileOffset(EbootPatches.ParseHex(l.Attribute("Address")!.Value), CodeFileOffset);
                if (!siteByte.TryGetValue(file, out var at)) { problems.Add($"{l.Attribute("Address")!.Value} is not on a cap site."); continue; }
                Span<byte> now = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(now, Values[at.Site.Field]);
                if ((byte)EbootPatches.ParseHex(l.Attribute("Value")!.Value) != now[at.Index]) problems.Add($"{l.Attribute("Address")!.Value} writes the wrong byte.");
                if (file < eboot.Length)
                {
                    Span<byte> was = stackalloc byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(was, at.Site.Stock);
                    if (eboot[(int)file] != was[at.Index]) problems.Add($"{l.Attribute("Address")!.Value}: eboot byte is not the stock byte.");
                }
            }
        }
        if (lines == 0) problems.Add("No salary-cap lines found in the patch file.");
        return problems;
    }
}
