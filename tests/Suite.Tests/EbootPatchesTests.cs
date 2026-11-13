using System.Buffers.Binary;
using System.Xml.Linq;
using Suite.Core;

namespace Suite.Tests;

public sealed class EbootPatchesTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "data", name);

    [Fact]
    public void Runtime_address_matches_the_entry_the_emulator_applies()
    {
        // Live user\patches\nhl24.xml: 0x068E0770 is the je at 1.60 eboot file offset 0x065324F0, applied per shad log.
        Assert.Equal(0x068E0770, EbootPatches.RuntimeAddress(0x065324F0));
        Assert.Equal(0x065324F0, EbootPatches.FileOffset(0x068E0770));
        Assert.Throws<ArgumentOutOfRangeException>(() => EbootPatches.RuntimeAddress(0x100));
    }

    [Fact]
    public void Repo_patch_file_addresses_all_land_inside_the_160_code_segment()
    {
        var doc = XDocument.Load(Data("nhl24-patches.xml"));
        var addresses = doc.Descendants("Line").Select(l => EbootPatches.ParseHex(l.Attribute("Address")!.Value)).ToList();
        Assert.NotEmpty(addresses);
        Assert.All(addresses, a => Assert.InRange(a, 0x400000, 0x400000 + 0xA289EA4 - 1));
        Assert.Contains(0x068E0770L, addresses); // Repack A is the same je as the upstream replay fix.
    }

    [Fact]
    public void Shipped_salary_cap_has_floor_cap_eleven_min_max_pairs_and_the_table()
    {
        var p = SalaryCapPatch.Load(File.ReadAllText(Data("salary_cap.json")));
        Assert.Equal("01.60", p.AppVer);
        Assert.Equal(76_900_000, p.Values["floor"]);
        Assert.Equal(104_000_000, p.Values["cap"]);
        Assert.Equal(850_000, p.Values["minSalary"]);
        Assert.Equal(20_800_000, p.Values["maxSalary"]);
        Assert.Equal(29, p.Sites.Count);
        Assert.Equal(950_000, p.Values["maxRookieSalary"]);
        Assert.Equal(11, p.Sites.Count(s => s.Group == "min-max fallbacks" && s.Field == "maxSalary"));
        Assert.Equal(11, p.Sites.Count(s => s.Group == "min-max fallbacks" && s.Field == "minSalary"));
        Assert.All(p.Sites, s => Assert.Equal(s.Field switch { "floor" => 61_700_000, "cap" => 83_500_000, "minSalary" => 775_000, "maxRookieSalary" => 950_000, _ => 16_700_000 }, s.Stock));
    }

    private const string SmallJson = """
        { "name": "Salary cap 2099-00", "appVer": "01.60", "ebootSize": 1048576, "codeFileOffset": "0x51D80",
          "values": { "floor": 76900000, "cap": 104000000 },
          "groups": [ { "name": "defaults", "comment": "c", "sites": [
            { "field": "floor", "fileOffset": "0x60000", "stock": 61700000 },
            { "field": "cap", "fileOffset": "0x60004", "stock": 83500000 } ] } ] }
        """;

    private static byte[] StockEboot()
    {
        var e = new byte[1048576];
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(0x60000), 61_700_000);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(0x60004), 83_500_000);
        return e;
    }

    [Fact]
    public void Verify_refuses_an_eboot_whose_site_is_not_stock()
    {
        var p = SalaryCapPatch.Load(SmallJson);
        Assert.Empty(p.Verify(StockEboot()));
        var e = StockEboot();
        e[0x60004] ^= 1;
        Assert.Single(p.Verify(e));
        Assert.NotEmpty(p.Verify(new byte[16]));
    }

    [Fact]
    public void Entries_write_only_changed_bytes_at_runtime_addresses_and_upsert_is_idempotent()
    {
        var p = SalaryCapPatch.Load(SmallJson);
        var doc = XDocument.Parse("""<Patch><Metadata Name="Other" isEnabled="true" AppVer="01.60" AppElf="eboot.bin"><PatchList/></Metadata></Patch>""");
        p.ApplyTo(doc);
        string once = doc.ToString();
        p.ApplyTo(doc);
        Assert.Equal(once, doc.ToString());
        Assert.Equal(2, doc.Root!.Elements("Metadata").Count());
        Assert.Empty(p.ReadBack(doc, StockEboot()));

        // 61,700,000 = A0 77 AD 03 -> 76,900,000 = A0 66 95 04: the first byte is unchanged and gets no line.
        var lines = doc.Descendants("Line").ToList();
        long floorAddr = EbootPatches.RuntimeAddress(0x60000);
        string? At(long a) => lines.SingleOrDefault(l => EbootPatches.ParseHex(l.Attribute("Address")!.Value) == a)?.Attribute("Value")!.Value;
        Assert.Null(At(floorAddr));
        Assert.Equal("0x66", At(floorAddr + 1));
        Assert.Equal("0x95", At(floorAddr + 2));
        Assert.Equal("0x04", At(floorAddr + 3));
        Assert.All(lines, l => Assert.Equal("byte", l.Attribute("Type")!.Value));
    }

    [Fact]
    public void Load_rejects_floor_above_cap()
    {
        Assert.Throws<InvalidDataException>(() => SalaryCapPatch.Load(SmallJson.Replace("\"floor\": 76900000", "\"floor\": 200000000")));
    }
}
