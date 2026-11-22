using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);

var names = new[]
{
    "content/worlds/rink/ice/components/rink_ice_nhl_mesh",
    "content/character/skater/player/skate/bauer/vapor/components/player_skate_bauer_vapor_hyperlite_guard_half_mesh",
    "content/character/crowd/prop/sign/anaheim/textures/crowd_sign_anaheim_nohotspot_default_0_color",
    "content/character/crowd/prop/sign/anaheim/components/crowd_sign_anaheim_mesh",
    "content/worlds/rink/outdoorthrees/components/rink_outdoorthrees_powerring_mesh",
    "content/character/skater/player/stick/bauer/vapor/components/player_stick_bauer_vapor_mesh",
};

foreach (var name in names)
{
    if (!index.ResByName.TryGetValue(name, out var entries)) { Console.WriteLine("MISSING " + name); continue; }
    var stored = BundleIndex.ReadBytes(entries[0].Data);
    byte[] r = stored; try { r = ChunkBlocks.Parse(stored).Decode(); } catch { }
    uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(at));
    ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(at));
    if (r.Length < 0x200) { Console.WriteLine("SKIP (not a mesh res) " + name); continue; }
    uint meshType = U32(0x6C);
    long lod0 = (long)U64(0x20);
    Console.WriteLine($"{name.Split('/')[^1]}: meshType {meshType} lod0 0x{lod0:X} resLen {r.Length}");
    Console.WriteLine("  u16s 0x88..0xB0: " + string.Join(" ", Enumerable.Range(0, (0xB0 - 0x88) / 2).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(0x88 + i * 2)).ToString())));

    // scan candidate lodCount/sectionCount u16 pairs
    for (int p = 0x88; p < 0x100; p += 2)
    {
        int lc = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(p));
        int sc = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(p + 2));
        if (lc is < 1 or > 6 || sc is < 1 or > 300) continue;
        // validate LOD0 at lod0: type, secCount == sc, sections parse
        if (lod0 + 0x150 > r.Length) continue;
        int t = (int)U32((int)lod0);
        int lsc = (int)U32((int)lod0 + 8);
        long secOff = (long)U64((int)lod0 + 0xC);
        if (lsc != sc) continue;
        if (secOff <= 0 || secOff + (long)sc * 0xE0 > r.Length) continue;
        // section names validate? name table offset is a u32 at +0x08
        int good = 0;
        for (int i = 0; i < Math.Min(sc, 4); i++)
        {
            int s = (int)secOff + i * 0xE0;
            int so = (int)U32(s + 0x08);
            if (so <= 0 || so >= r.Length) continue;
            int end = Array.IndexOf(r, (byte)0, so);
            if (end > so && end - so < 60 && r[so] >= 0x20 && r[so] < 0x7f) good++;
        }
        Console.WriteLine($"  pair@0x{p:X}: lodCount {lc} sectionCount {sc} -> LOD0 type {t} secCount {lsc} secOff 0x{secOff:X} names {good}/4 {((good >= Math.Min(sc, 4) - 1) ? "VALID" : "")}");
    }
    Console.WriteLine();
}
