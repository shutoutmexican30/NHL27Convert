// TextureChunk verify <res> <chunk> [referencePng]                  structure + codec round trip, prints numbers
// TextureChunk png    <res> <chunk> <out.png>                       decode mip 0
// TextureChunk build  <res> <template> <png> <outChunk>             encode a full BC5 chain, same byte length
// TextureChunk apply  <game> <res> <newChunk> [--expect <liveChunk>] [--restore-check]
//                                                                   append + repoint one TOC chunk, read back, check
using Suite.Core;
using Suite.Core.Frostbite;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: TextureChunk verify|png|build ... | apply <game> <res> <newChunk> [--expect <liveChunk>] [--restore-check]");
    return 2;
}

string mode = args[0];

// ------------------------------------------------------------------ apply (writes; test copy only)
if (mode == "apply")
{
    if (args.Length < 4) { Console.Error.WriteLine("usage: TextureChunk apply <game> <res> <newChunk> [--expect <liveChunk>] [--restore-check]"); return 2; }
    var game = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar);
    var res = TextureRes.Parse(File.ReadAllBytes(args[2]));
    var newRaw = File.ReadAllBytes(args[3]);

    var settings = SuiteSettings.Load();
    foreach (var original in new[] { settings.Nhl24GameDir, settings.Nhl24BaseDir, settings.Nhl24UpdateDir, settings.Nhl24ReadView })
        if (!string.IsNullOrEmpty(original) && string.Equals(Path.GetFullPath(original).TrimEnd(Path.DirectorySeparatorChar), game, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("REFUSED: that is the original game or its read view. Use the test copy.");
            return 1;
        }
    var problems = ApplyTarget.Problems(game);
    if (problems.Count > 0) { Console.Error.WriteLine("REFUSED: " + string.Join("; ", problems)); return 1; }
    if (newRaw.Length != res.PayloadSize)
    {
        Console.Error.WriteLine($"new chunk is {newRaw.Length:N0} bytes, the slot requires {res.PayloadSize:N0}");
        return 1;
    }
    byte[]? expected = null;
    int expectAt = Array.IndexOf(args, "--expect");
    if (expectAt > 0 && expectAt + 1 < args.Length) expected = File.ReadAllBytes(args[expectAt + 1]);
    bool restoreCheck = args.Contains("--restore-check");

    var tocsBefore = TocChunkWriter.LoadTocs(game);
    var before = TocChunkWriter.FindAll(tocsBefore, res.ChunkGuid);
    Console.WriteLine($"chunk {res.ChunkGuid}: {before.Count} TOC entr{(before.Count == 1 ? "y" : "ies")} before the write");
    foreach (var b in before) Console.WriteLine($"   {Path.GetFileName(b.Where.Toc)}  patch={b.Where.Patch} cas={b.Where.Cas:D2} offset={b.Where.Offset:N0} size={b.Where.Size:N0}");

    var backup = ApplyTarget.Backup(game);
    Console.WriteLine($"backup: {backup}");

    bool Verify(byte[] want)
    {
        var now = TocChunkWriter.FindAll(TocChunkWriter.LoadTocs(game), res.ChunkGuid);
        foreach (var r in now)
        {
            try
            {
                var raw = TocChunkWriter.ReadRaw(game, r.Where);
                bool same = raw.AsSpan().SequenceEqual(want);
                Console.WriteLine($"   read back {Path.GetFileName(r.Where.Toc)}: {(same ? "identical to the new payload" : "DIFFERENT")} ({raw.Length:N0} raw bytes)");
                if (same) return true;
            }
            catch (Exception ex) { Console.WriteLine($"   read back {Path.GetFileName(r.Where.Toc)}: {ex.Message}"); }
        }
        return false;
    }

    TocChunkWriter.WriteResult write;
    try
    {
        write = TocChunkWriter.Replace(game, res.ChunkGuid, newRaw, expected);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("write refused: " + ex.Message);
        return 1;
    }
    foreach (var line in write.Log) Console.WriteLine("   " + line);

    if (!Verify(newRaw)) { Console.Error.WriteLine("FAILED read-back; restoring from the backup."); ApplyTarget.RestoreFromBackup(game, backup); return 1; }
    var diffs = ApplyTarget.Compare(game, backup);
    Console.WriteLine($"changes vs backup: {diffs.Count} (CAS lengths + TOC hashes)");
    foreach (var d in diffs.Take(8)) Console.WriteLine("   " + d);

    if (restoreCheck)
    {
        Console.WriteLine("restore from the backup...");
        ApplyTarget.RestoreFromBackup(game, backup);
        var left = ApplyTarget.Compare(game, backup);
        Console.WriteLine($"after restore: {left.Count} differences");
        bool oldHeld = expected is null ? true : Verify(expected);
        if (left.Count > 0 || !oldHeld) { Console.Error.WriteLine("FAILED restore check (game left restored)."); return 1; }
        Console.WriteLine("re-apply...");
        TocChunkWriter.Replace(game, res.ChunkGuid, newRaw, expected);
        if (!Verify(newRaw)) { Console.Error.WriteLine("FAILED re-apply; restoring."); ApplyTarget.RestoreFromBackup(game, backup); return 1; }
        Console.WriteLine("restore check passed: apply -> restore -> apply all verified");
    }
    Console.WriteLine("DONE");
    return 0;
}

// ------------------------------------------------------------------ read-only modes
var res2 = TextureRes.Parse(File.ReadAllBytes(args[1]));
Console.WriteLine($"res   {res2.Width}x{res2.Height}  mips {res2.MipCount}  block {res2.BlockBytes}  payload {res2.PayloadSize:N0} bytes  chunk {res2.ChunkGuid}");
for (int i = 0; i < res2.MipCount; i++) Console.WriteLine($"      mip {i,2}: {res2.MipSizes[i],10:N0}");

if (mode == "verify")
{
    var chunk = TextureChunk.Parse(File.ReadAllBytes(args[2]), res2);
    var rebuilt = TextureChunk.Build(res2, chunk.Mips);
    Console.WriteLine($"rebuild byte-identical      : {rebuilt.Data.AsSpan().SequenceEqual(chunk.Data)}");
    if (res2.BlockBytes != 16)
    {
        var bc1 = Bc1.DecodeTiled(chunk.Mips[0], res2.Width, res2.Height);
        var re = Bc1.DecodeTiled(Bc1.EncodeTiled(bc1), res2.Width, res2.Height);
        var (m1, x1) = Diff(bc1, re, 3);
        Console.WriteLine($"BC1 decode->encode->decode  : mean |d| {m1:F2}  max {x1}");
        if (args.Length > 3 && File.Exists(args[3]))
        {
            var reference = ImageIO.Load(args[3]);
            if (reference.Width != res2.Width || reference.Height != res2.Height) reference = ImageIO.ResizeSmooth(reference, res2.Width, res2.Height);
            var (m2, x2) = Diff(bc1, reference, 3);
            int close = 0, total = res2.Width * res2.Height;
            for (int i = 0; i < total; i++)
            {
                bool ok = true;
                for (int c = 0; c < 3; c++) if (Math.Abs(bc1.Pixels[i * 4 + c] - reference.Pixels[i * 4 + c]) > 8) ok = false;
                if (ok) close++;
            }
            Console.WriteLine($"BC1 vs reference PNG RGB    : mean |d| {m2:F2}  max {x2}   within 8: {100.0 * close / total:F1}%");
        }
        return 0;
    }
    var decoded = Bc5.DecodeTiled(chunk.Mips[0], res2.Width, res2.Height);
    var again = Bc5.DecodeTiled(Bc5.EncodeTiled(decoded), res2.Width, res2.Height);
    var (mean, max) = Diff(decoded, again, 2);
    Console.WriteLine($"decode->encode->decode R,G  : mean |d| {mean:F2}  max {max}");
    var chain = new List<byte[]> { Bc5.EncodeTiled(decoded) };
    var cur = decoded;
    for (int i = 1; i < res2.MipCount; i++) { cur = TextureChunk.Halve(cur); chain.Add(Bc5.EncodeTiled(cur)); }
    Console.WriteLine($"new chain length identical  : {TextureChunk.Build(res2, chain).Data.Length == chunk.Data.Length}");
    if (args.Length > 3 && File.Exists(args[3]))
    {
        var reference = ImageIO.Load(args[3]);
        if (reference.Width != res2.Width || reference.Height != res2.Height) reference = ImageIO.ResizeSmooth(reference, res2.Width, res2.Height);
        var (m2, x2) = Diff(decoded, reference, 2);
        int close = 0, total = res2.Width * res2.Height;
        for (int i = 0; i < total; i++)
        {
            int a = decoded.Pixels[i * 4] - reference.Pixels[i * 4];
            int b = decoded.Pixels[i * 4 + 1] - reference.Pixels[i * 4 + 1];
            if (Math.Abs(a) <= 8 && Math.Abs(b) <= 8) close++;
        }
        Console.WriteLine($"vs reference PNG R,G        : mean |d| {m2:F2}  max {x2}   within 8: {100.0 * close / total:F1}%");
    }
    return 0;
}

if (mode == "png")
{
    var chunk = TextureChunk.Parse(File.ReadAllBytes(args[2]), res2);
    var decodedPng = res2.BlockBytes == 16
        ? Bc5.DecodeTiled(chunk.Mips[0], res2.Width, res2.Height)
        : Bc1.DecodeTiled(chunk.Mips[0], res2.Width, res2.Height);
    ImageIO.SavePng(decodedPng, args[3]);
    Console.WriteLine($"wrote {args[3]}");
    return 0;
}

if (mode == "build" && args.Length >= 5)
{

    var template = TextureChunk.Parse(File.ReadAllBytes(args[2]), res2);
    var source = ImageIO.Load(args[3]);
    if (source.Width != res2.Width || source.Height != res2.Height)
    {
        Console.WriteLine($"resizing input {source.Width}x{source.Height} -> {res2.Width}x{res2.Height}");
        source = ImageIO.ResizeSmooth(source, res2.Width, res2.Height);
    }
    var built = TextureChunk.Build(res2, TextureChunk.EncodeChain(res2, source));
    File.WriteAllBytes(args[4], built.Data);
    var back = res2.BlockBytes == 16
        ? Bc5.DecodeTiled(built.Mips[0], res2.Width, res2.Height)
        : Bc1.DecodeTiled(built.Mips[0], res2.Width, res2.Height);
    var (mean, max) = res2.BlockBytes == 16 ? Diff(source, back, 2) : Diff(source, back, 3);
    Console.WriteLine($"built {args[4]}");
    Console.WriteLine($"length identical to template: {built.Data.Length == template.Data.Length}  ({built.Data.Length:N0})");
    Console.WriteLine($"source vs encoded mip 0 R,G : mean |d| {mean:F2}  max {max}");
    return 0;
}

Console.Error.WriteLine("unknown mode");
return 2;

static (double Mean, int Max) Diff(Rgba a, Rgba b, int channels)
{
    long sum = 0; int max = 0; long n = 0;
    for (int i = 0; i < a.Width * a.Height; i++)
        for (int c = 0; c < channels; c++)
        {
            int d = Math.Abs(a.Pixels[i * 4 + c] - b.Pixels[i * 4 + c]);
            sum += d; n++;
            if (d > max) max = d;
        }
    return (n == 0 ? 0 : (double)sum / n, max);
}