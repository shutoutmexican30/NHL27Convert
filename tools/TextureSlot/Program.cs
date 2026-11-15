// TextureSlot export <game> <texture name> <out-dir>                 every layer's mip 0 as PNG (read only)
// TextureSlot apply  <game> <texture name> <png> [--layer N] [--alpha] [--restore] --apply   (dry run without --apply)
//     writes with TextureSlots.Apply (backup, all references, read-back); --restore puts the backup back afterwards
//     and checks the game is byte-identical to it again (test copies only: the original game is refused).
using Suite.Core;
using Suite.Core.Frostbite;

if (args.Length < 4) { Console.Error.WriteLine("usage: TextureSlot export|apply <game> <name> <out-dir|png> [--layer N] [--alpha] [--restore]"); return 2; }
var game = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar);
var settings = SuiteSettings.Load();
foreach (var original in new[] { settings.Nhl24GameDir, settings.Nhl24BaseDir, settings.Nhl24UpdateDir, settings.Nhl24ReadView })
    if (args[0] == "apply" && !string.IsNullOrWhiteSpace(original) && string.Equals(Path.GetFullPath(original).TrimEnd(Path.DirectorySeparatorChar), game, StringComparison.OrdinalIgnoreCase))
        { Console.Error.WriteLine("REFUSED: the original game."); return 1; }
if (args[0] == "compare")
{
    // compare <gameA> <gameB> <names.txt> : per line "tex:<texture>" | "legacy:<file>" | "chunk:<guid>", same/different (read only).
    var gb = Path.GetFullPath(args[2]).TrimEnd(Path.DirectorySeparatorChar);
    var ia = BundleIndex.Build(game); var ib = BundleIndex.Build(gb);
    var la = new LegacyFiles(game, settings); var lb = new LegacyFiles(gb, settings);
    string H(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b))[..10];
    foreach (var line in File.ReadAllLines(args[3]).Where(l => l.Contains(':')))
    {
        var (kind, name) = (line[..line.IndexOf(':')], line[(line.IndexOf(':') + 1)..]);
        try
        {
            byte[] A, B;
            if (kind == "tex") { A = TextureSlots.Read(game, ia, name).Stock; B = TextureSlots.Read(gb, ib, name).Stock; }
            else if (kind == "legacy") { A = la.Read(name); B = lb.Read(name); }
            else { var id = Guid.Parse(name); A = ChunkReplace.ReadAll(game, ia, id).First(r => r.LogicalOffset == 0).Raw; B = ChunkReplace.ReadAll(gb, ib, id).First(r => r.LogicalOffset == 0).Raw; }
            Console.WriteLine($"{(A.AsSpan().SequenceEqual(B) ? "SAME" : "DIFF")}  {line}  ({H(A)} / {H(B)})");
        }
        catch (Exception ex) { Console.WriteLine($"ERR   {line}: {ex.Message}"); }
    }
    return 0;
}
if (args[0] == "applymany")
{
    // applymany <game> <list.tsv> --apply : one TextureSlots.Apply (one backup, one read-back) for every "slot<TAB>png" line.
    if (!args.Contains("--apply")) { Console.WriteLine("dry run (add --apply to write)"); return 0; }
    var list = File.ReadAllLines(args[2]).Where(l => l.Contains('\t')).Select(l => l.Split('\t'))
        .Select(p => new TextureReplacement(p[0], Path.GetFullPath(p[1]), null, null)).ToList();
    var (okMany, msgMany, _) = TextureSlots.Apply(game, list, Console.WriteLine);
    Console.WriteLine(msgMany);
    return okMany ? 0 : 1;
}
var index = BundleIndex.Build(game);
var slot = TextureSlots.Read(game, index, args[2]);
Console.WriteLine($"{args[2]}: {slot.Res.Width}x{slot.Res.Height}, {slot.Res.MipCount} mips, {Ps4Codec.Slices(slot.Res.Header)} layer(s), {Ps4Codec.Of(slot.Res.Header, slot.Res.BlockBytes)}");
if (args[0] == "info")
{
    Console.WriteLine("mips: " + string.Join(",", slot.Res.MipSizes) + " payload " + slot.Res.PayloadSize + " fmtbyte " + slot.Res.Header[12] + " firstmip? " + slot.Res.Header[28] + "," + slot.Res.Header[29] + "," + slot.Res.Header[31]);
    foreach (var c in ChunkReplace.ReadAll(game, index, slot.Chunk)) Console.WriteLine($"copy logical {c.LogicalOffset} raw {c.Raw.Length}");
    return 0;
}
if (args[0] == "export")
{
    Directory.CreateDirectory(args[3]);
    var layers = Ps4Codec.DecodeSlices(slot.Res, slot.Stock);
    for (int i = 0; i < layers.Count; i++) ImageIO.SavePng(layers[i], Path.Combine(Path.GetFullPath(args[3]), $"{args[2].Split('/')[^1]}_{i}.png"));
    Console.WriteLine($"{layers.Count} PNG(s) -> {args[3]}");
    return 0;
}
if (!WriteGate.Allowed(args.Skip(3).Prepend(args[1]).Prepend("--target").ToArray()) || !args.Contains("--apply")) { Console.WriteLine("dry run (add --apply to write)"); return 0; }
int li = Array.IndexOf(args, "--layer");
var r = li >= 0
    ? new TextureReplacement(args[2], "", new Dictionary<int, string> { [int.Parse(args[li + 1])] = Path.GetFullPath(args[3]) }, args.Contains("--alpha") ? "auto" : null)
    : new TextureReplacement(args[2], Path.GetFullPath(args[3]), null, args.Contains("--alpha") ? "auto" : null);
var (ok, message, backup) = TextureSlots.Apply(game, [r], Console.WriteLine);
Console.WriteLine(message);
if (!ok) return 1;
// Show what the game now reads (layer or sheet) next to the input.
var after = TextureSlots.Read(game, BundleIndex.Build(game), args[2]);
var shown = Ps4Codec.DecodeSlices(after.Res, after.Stock)[li >= 0 ? int.Parse(args[li + 1]) : 0];
ImageIO.SavePng(shown, Path.ChangeExtension(Path.GetFullPath(args[3]), ".ingame.png"));
if (args.Contains("--restore"))
{
    ApplyTarget.RestoreFromBackup(game, backup!);
    var left = ApplyTarget.Compare(game, backup!);
    Console.WriteLine(left.Count == 0 ? "restored: identical to the backup" : "RESTORE DIFFERS: " + string.Join("; ", left.Take(5)));
    if (left.Count > 0) return 1;
}
return 0;

