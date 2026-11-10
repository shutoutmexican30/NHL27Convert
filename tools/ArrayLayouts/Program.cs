// ArrayLayouts --target <game> [--contains nashville] : read-only jersey-array layout inventory.
using Suite.Core;
using Suite.Core.Frostbite;

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
string target = Path.GetFullPath(Opt("--target") ?? throw new ArgumentException("--target is required"));
string filter = Opt("--contains") ?? "";
string? export = Opt("--export");
var index = BundleIndex.Build(target, s => Console.WriteLine(s));
foreach (string name in index.ResByName.Keys.Where(n => n.Contains("branding_", StringComparison.Ordinal)
             && (n.EndsWith("_color_array", StringComparison.Ordinal) || n.EndsWith("_normal_array", StringComparison.Ordinal)
                 || n.EndsWith("_coeff_array", StringComparison.Ordinal))
             && n.Contains(filter, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
{
    var slots = TextureSlots.ReadAll(target, index, name);
    Console.WriteLine(name);
    if (export is not null && name.EndsWith("_color_array", StringComparison.Ordinal))
    {
        string folder = Path.Combine(export, name.Split('/')[^2]);
        Directory.CreateDirectory(folder);
        var images = Ps4Codec.DecodeSlices(slots[0].Res, slots[0].Stock);
        for (int layer = 0; layer < images.Count; layer++)
            ImageIO.SavePng(images[layer], Path.Combine(folder, $"{name.Split('/')[^1]}_{layer}.png"));
    }
    foreach (var slot in slots)
    {
        Console.WriteLine($"  {slot.Chunk}: {slot.Res.Width}x{slot.Res.Height}, {Ps4Codec.Slices(slot.Res.Header)} layers, {slot.Res.MipCount} mips, payload {slot.Res.PayloadSize}");
        foreach (var chunk in index.ChunksById.GetValueOrDefault(slot.Chunk) ?? [])
            Console.WriteLine($"    bundle={chunk.Bundle.FirstName}; logical={chunk.LogicalOffset}+{chunk.LogicalSize}; stored={chunk.Data.Size}; toc={chunk.Data.Toc}");
        foreach (var reference in ChunkReplace.ReadAll(target, index, slot.Chunk))
            Console.WriteLine($"    readable={reference.Where}; logical={reference.LogicalOffset}; decoded={reference.Raw.Length}");
    }
}
