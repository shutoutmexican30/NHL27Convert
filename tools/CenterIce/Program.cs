// CenterIce png   <res> <chunk> <out.png>       decode mip 0 of a BC7 slot (PS4 tiled)
// CenterIce build <res> <image> <outChunk>      full BC7 mip chain, same byte length as the slot; resized to the slot if needed
// Writes only the output files; the game write is ChunkWrite's (every reference, read-back, restore check).
using Suite.Core;
using Suite.Core.Frostbite;

if (args.Length != 4) { Console.Error.WriteLine("usage: CenterIce png|build <res> <chunk|image> <out>"); return 2; }
var header = File.ReadAllBytes(args[1]);
var res = TextureRes.Parse(header);
if (header[12] != 0x43) { Console.Error.WriteLine($"format 0x{header[12]:X2} is not BC7 (0x43)"); return 1; }
if (args[0] == "png")
{
    var chunk = TextureChunk.Parse(File.ReadAllBytes(args[2]), res);
    ImageIO.SavePng(Bc7.DecodeTiled(chunk.Mips[0], res.Width, res.Height), Path.GetFullPath(args[3]));
    Console.WriteLine($"{res.Width}x{res.Height}, {res.MipCount} mips -> {args[3]}");
    return 0;
}
var img = ImageIO.Load(args[2]);
if (img.Width != res.Width || img.Height != res.Height) img = ImageIO.ResizeSmooth(img, res.Width, res.Height);
var mips = new List<byte[]>();
for (int i = 0, w = res.Width, h = res.Height; i < res.MipCount; i++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
{
    var m = Bc7.EncodeTiled(img);
    if (m.Length != res.MipSizes[i]) { Console.Error.WriteLine($"mip {i}: {m.Length} bytes, slot has {res.MipSizes[i]}"); return 1; }
    mips.Add(m);
    if (i + 1 < res.MipCount) img = TextureChunk.Halve(img);
}
var built = TextureChunk.Build(res, mips).Data;
File.WriteAllBytes(args[3], built);
var back = Bc7.DecodeTiled(mips[0], res.Width, res.Height);
var src = ImageIO.Load(args[2]);
if (src.Width != res.Width) src = ImageIO.ResizeSmooth(src, res.Width, res.Height);
double d = 0; for (int i = 0; i < src.Pixels.Length; i++) d += Math.Abs(src.Pixels[i] - back.Pixels[i]);
Console.WriteLine($"built {built.Length} bytes ({res.MipCount} mips); mip 0 round trip mean |d| {d / src.Pixels.Length:F2}");
return 0;
