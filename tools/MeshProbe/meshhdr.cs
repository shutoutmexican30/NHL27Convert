using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);

foreach (var name in new[]
{
    "content/worlds/rink/ice/components/rink_ice_nhl_mesh",
    "content/character/skater/player/skate/bauer/vapor/components/player_skate_bauer_vapor_hyperlite_guard_half_mesh",
    "content/character/crowd/prop/sign/anaheim/components/crowd_sign_anaheim_mesh",
    "content/worlds/stadiums/nhl/anaheim/components/anaheim_boards_0_mesh",
})
{
    if (!index.ResByName.TryGetValue(name, out var entries) || entries.Count == 0) { Console.WriteLine("MISSING " + name); continue; }
    var e = entries[0];
    var stored = BundleIndex.ReadBytes(e.Data);
    byte[] res = stored; try { res = ChunkBlocks.Parse(stored).Decode(); } catch { }
    Console.WriteLine($"{name.Split('/')[^1]}: res {res.Length} bytes");
    // u32 dump of first 0x160 bytes, look for small counts
    for (int at = 0; at < 0x150; at += 4)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(res.AsSpan(at));
        if (v >= 1 && v <= 64) Console.WriteLine($"  +0x{at:X3}: {v}");
    }
    // guess section count candidates and validate
    foreach (int count in new[] { 4, 8, 12, 16, 20, 24, 32, 40 })
    {
        int okNames = 0;
        for (int i = 0; i < count; i++)
        {
            int r = 0x150 + i * 0xE0;
            if (r + 0xE0 > res.Length) break;
            int strOff = BinaryPrimitives.ReadInt32LittleEndian(res.AsSpan(r + 0x08));
            if (strOff > 0 && strOff + 8 < res.Length && res[strOff] >= 0x20 && res[strOff] < 0x7f)
            {
                int end = Array.IndexOf(res, (byte)0, strOff);
                if (end > strOff && end - strOff < 64) okNames++;
            }
        }
        if (okNames > 0) Console.WriteLine($"  count {count}: {okNames} valid names");
    }
    Console.WriteLine();
}
