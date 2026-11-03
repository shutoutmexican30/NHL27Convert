// CenterIce png   <res> <chunk> <out.png>       decode mip 0 of a BC7 slot (PS4 tiled)
// CenterIce fetch <thefaceoff.net link>                      download the page's center-ice picture (cached, credited)
// CenterIce convert <res> <chunk> <rink image> <out.png>   rink picture -> decal in the stock slot's layout (CenterIceImage)
// CenterIce build <res> <image> <outChunk>      full BC7 mip chain, same byte length as the slot; resized to the slot if needed
// Writes only the output files; the game write is ChunkWrite's (every reference, read-back, restore check).
using Suite.Core;
using Suite.Core.Frostbite;

if (args.Length == 4 && args[0] == "slotapply")
{
    // slotapply <game> <slot> <rink image> - the page's Apply, from the command line (backup, read-back).
    var game = Path.GetFullPath(args[1]);
    int slot = int.Parse(args[2]);
    var rink = ImageIO.Load(args[3]);
    var index = BundleIndex.Build(game);
    var s = CenterIceSlots.Read(game, index, slot);
    var image = CenterIceImage.ToSlot(rink, s.Image);
    var (ok, message, _) = CenterIceSlots.Apply(game, new Dictionary<int, Rgba> { [slot] = image }, Console.WriteLine);
    Console.WriteLine((ok ? "OK " : "FAILED ") + message);
    return ok ? 0 : 1;
}

if (args.Length == 5 && args[0] == "slotconvert")
{
    // slotconvert <game> <slot> <rink image> <out.png> - the conversion the page performs, previewed as a PNG only.
    var game = Path.GetFullPath(args[1]);
    int slot = int.Parse(args[2]);
    var index = BundleIndex.Build(game);
    var s = CenterIceSlots.Read(game, index, slot);
    var rink = ImageIO.Load(args[3]);
    var outImg = CenterIceImage.ToSlot(rink, s.Image);
    ImageIO.SavePng(outImg, Path.GetFullPath(args[4]));
    Console.WriteLine($"slot {slot}: stock circle {CenterIceImage.FindCircle(s.Image, true)}, rink circle {CenterIceImage.FindCircle(rink)} -> {args[4]}");
    return 0;
}

if (args.Length == 3 && args[0] == "slots")
{
    // Every center-ice slot the game has, as a small PNG each: tells you which slot belongs to which arena.
    var game = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(args[2]);
    var index = BundleIndex.Build(game, s => Console.WriteLine(s));
    for (int slot = 0; slot < 34; slot++)
    {
        try
        {
            var s = CenterIceSlots.Read(game, index, slot);
            var thumb = ImageIO.Shrink(s.Image, 160);
            ImageIO.SavePng(thumb, Path.Combine(args[2], $"slot_{slot:D2}.png"));
            Console.WriteLine($"slot {slot,2}: {s.Res.Width}x{s.Res.Height} -> slot_{slot:D2}.png");
        }
        catch (Exception ex) { Console.WriteLine($"slot {slot,2}: {ex.Message}"); }
    }
    return 0;
}

if (args.Length == 2 && args[0] == "fetch")
{
    // TheFaceoff.net page or image link -> cached file (user-initiated; personal use; credited).
    var file = await Faceoff.FetchAsync(args[1]);
    var pic = ImageIO.Load(file);
    Console.WriteLine($"{file} ({pic.Width}x{pic.Height})");
    Console.WriteLine(Faceoff.Credit);
    return 0;
}
if (args.Length == 5 && args[0] == "convert")
{
    var r = TextureRes.Parse(File.ReadAllBytes(args[1]));
    var stock = Bc7.DecodeTiled(TextureChunk.Parse(File.ReadAllBytes(args[2]), r).Mips[0], r.Width, r.Height);
    var outImg = CenterIceImage.ToSlot(ImageIO.Load(args[3]), stock);
    ImageIO.SavePng(outImg, Path.GetFullPath(args[4]));
    Console.WriteLine($"stock circle {CenterIceImage.FindCircle(stock, true)}; rink circle {CenterIceImage.FindCircle(ImageIO.Load(args[3]))} -> {args[4]}");
    return 0;
}
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
img = CenterIceImage.CutRedLine(img);   // clean red-line gap, as every stock slot has
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
src = CenterIceImage.CutRedLine(src);
double d = 0; for (int i = 0; i < src.Pixels.Length; i++) d += Math.Abs(src.Pixels[i] - back.Pixels[i]);
Console.WriteLine($"built {built.Length} bytes ({res.MipCount} mips); mip 0 round trip mean |d| {d / src.Pixels.Length:F2}");
return 0;
