using System;
using System.IO;
using System.Linq;
using FMT.Hash;
using Suite.Core;
using Suite.Core.Frostbite;

var ok = true;
void Check(string what, bool good) { ok &= good; Console.WriteLine((good ? "PASS  " : "FAIL  ") + what); }

// FMT.Hash's own vectors
Check("Fnv64 buttonData.ini", Fnv64.FNV64_String8_Lower("buttonData.ini") == 9263688441353523713ul);
Check("Sha256 AttribSchema_gp_kickshot_shooting", (uint)Sha256.SHA256Hash32_Lowercase("AttribSchema_gp_kickshot_shooting", "1028") == 1303288196u);
Check("Sha256 SHOT_ErrorScalarVsAimInsidePostSmallGoal", (uint)Sha256.SHA256Hash32_Lowercase("SHOT_ErrorScalarVsAimInsidePostSmallGoal", "1028") == 2841633721u);

// Real NHL24 data: Frostbite EBX file GUIDs are FNV-1a 128 of the asset's lower-case path.
// Verify against names the suite resolved from its own TOC name tables.
var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var index = BundleIndex.Build(game);
var name = index.EbxByName.Keys.First(k => k.Contains("/default", StringComparison.Ordinal));
var binName = index.EbxByName.Keys.First(k => k.StartsWith("content/character/skater/player/skate/", StringComparison.Ordinal));
Console.WriteLine("asset: " + name);
var ebx = index.EbxByName[name][0];
byte[] stored = BundleIndex.ReadBytes(ebx.Data);
byte[] payload = stored; try { payload = ChunkBlocks.Parse(stored).Decode(); } catch { }
if (System.Text.Encoding.ASCII.GetString(payload.AsSpan(0, 4)) == "RIFF")
{
    var riff = EbxRiff.Parse(payload);
    var lower2 = System.Text.Encoding.ASCII.GetBytes(riff.Name.ToLowerInvariant());
    var g2 = Fnv1a.ComputeHash128(lower2, lower2.Length);
    var expect = new Guid(g2);
    Console.WriteLine($"riff name '{riff.Name}' guid {riff.FileGuid} expect {expect}");
    Check("RIFF EBX FileGuid == FNV1a128(its name)", riff.FileGuid == expect);
}
else
{
// EBX payload is usually prefixed by a small header before the EFIX/EBXD parts; scan for the RIFF-less binary EBX guid
// which sits at a known offset in the EBX header (big-endian FNV1a128 of the name).
Console.WriteLine($"ebx stored {stored.Length} decoded {payload.Length} first16 {Convert.ToHexString(payload.AsSpan(0, Math.Min(16, payload.Length)))}");
var lower = System.Text.Encoding.ASCII.GetBytes(name.ToLowerInvariant());
var guid = Fnv1a.ComputeHash128(lower, lower.Length);
Console.WriteLine("fnv1a128 of name: " + Convert.ToHexString(guid));
bool found = Contains(payload, guid);
Check("EBX header contains FNV1a128(name)", found);
var guidBE = guid.Reverse().ToArray();
Check("EBX header contains FNV1a128(name) BE", Contains(payload, guidBE));
Console.WriteLine($"fnv64: {Fnv64.FNV64_String8_Lower(name):x16}");
}

static bool Contains(byte[] hay, byte[] needle)
{
    for (int i = 0; i + needle.Length <= hay.Length; i++)
    {
        bool good = true;
        for (int k = 0; k < needle.Length; k++) if (hay[i + k] != needle[k]) { good = false; break; }
        if (good) return true;
    }
    return false;
}

// Binary EBX: the fileGuid sits in the EBX header; scan for the name hash.
{
    var bebx = index.EbxByName[binName][0];
    byte[] bstored = BundleIndex.ReadBytes(bebx.Data);
    byte[] bpayload = bstored; try { bpayload = ChunkBlocks.Parse(bstored).Decode(); } catch { }
    Console.WriteLine($"binary asset: {binName}  payload {bpayload.Length} first8 {Convert.ToHexString(bpayload.AsSpan(0, 8))}");
    var lower3 = System.Text.Encoding.ASCII.GetBytes(binName.ToLowerInvariant());
    var g3 = Fnv1a.ComputeHash128(lower3, lower3.Length);
    Console.WriteLine("fnv1a128: " + Convert.ToHexString(g3));
    Check("binary EBX contains FNV1a128(name)", Contains(bpayload, g3));
    Check("binary EBX contains FNV1a128(name) BE", Contains(bpayload, g3.Reverse().ToArray()));
}
Console.WriteLine(ok ? "ALL PASS" : "SOME FAILED");

static class Fnv1a
{
    // Frostbite's FNV1a 128 (as in every Frostbite tool): two parallel FNV-1a runs with different seeds/offsets.
    public static byte[] ComputeHash128(byte[] data, int length)
    {
        uint sa = 0x811c9dc5, sb = 0x811c9dc5;
        const uint prime = 0x01000193;
        int i;
        for (i = 0; i < length - 1; i += 2)
        {
            sa = (sa ^ data[i]) * prime;
            sb = (sb ^ data[i + 1]) * prime;
        }
        if (i < length) sa = (sa ^ data[i]) * prime;
        return new byte[] { (byte)(sb & 0xff), (byte)((sb >> 8) & 0xff), (byte)((sb >> 16) & 0xff), (byte)((sb >> 24) & 0xff),
                       (byte)(sa & 0xff), (byte)((sa >> 8) & 0xff), (byte)((sa >> 16) & 0xff), (byte)((sa >> 24) & 0xff),
                       0, 0, 0, 0, 0, 0, 0, 0 };
    }
}
