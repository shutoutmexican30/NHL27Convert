using System.Text.RegularExpressions;

namespace Suite.Core.Frostbite;

/// <summary>
/// NHL 24 on-ice sponsor hotspots (content/worlds/rink/textures/hotspot/ice_hotspot_<set>_<n>_color, BC7, mostly
/// 512x512, PS4 tiled): the painted sponsor patches the game places on the ice. Same read/replace machinery as
/// <see cref="CenterIceSlots"/> — decode a hotspot, show it, take a replacement image, rebuild the mip chain and
/// write it everywhere the game reads it (<see cref="ChunkReplace"/>) with a backup and a read-back.
/// The set/index numbers are the game's own slotting (two sets, 0 and 1); which patch shows where is decided by
/// the arena data, the tool only replaces the art of a numbered slot.
/// </summary>
public static class IceHotspots
{
    public const string Folder = "content/worlds/rink/textures/hotspot/";
    public static string Name(int set, int index) => $"{Folder}ice_hotspot_{set}_{index}_color";

    static readonly Regex Rx = new(@"ice_hotspot_(\d+)_(\d+)_color$", RegexOptions.Compiled);

    /// <summary>Every hotspot slot the game ships, grouped by set and sorted by index.</summary>
    public static List<(int Set, int Index)> Slots(BundleIndex index) =>
        index.ResByName.Keys.Select(k => (Name: k, M: Rx.Match(k)))
            .Where(t => t.M.Success)
            .Select(t => (Set: int.Parse(t.M.Groups[1].Value), Index: int.Parse(t.M.Groups[2].Value)))
            .OrderBy(t => t.Set).ThenBy(t => t.Index).ToList();

    public sealed record Slot(int Set, int Index, TextureRes Res, Guid Chunk, Rgba Image);

    public static Slot Read(string game, BundleIndex index, int set, int idx)
    {
        var res = index.ResByName.GetValueOrDefault(Name(set, idx))?.FirstOrDefault()
            ?? throw new InvalidOperationException($"No hotspot {set}/{idx} in this game.");
        var stored = BundleIndex.ReadBytes(res.Data);
        byte[] header = stored; try { header = ChunkBlocks.Parse(stored).Decode(); } catch { }
        var tr = TextureRes.Parse(header);
        if (header[12] != 0x43) throw new InvalidOperationException($"Hotspot {set}/{idx} is not BC7.");
        var full = ChunkReplace.ReadAll(game, index, tr.ChunkGuid).FirstOrDefault(r => r.LogicalOffset == 0 && r.Raw.Length == tr.PayloadSize).Raw
            ?? throw new InvalidOperationException($"Hotspot {set}/{idx}: no full copy of its texture.");
        return new Slot(set, idx, tr, tr.ChunkGuid, Bc7.DecodeTiled(TextureChunk.Parse(full, tr).Mips[0], tr.Width, tr.Height));
    }

    /// <summary>
    /// A replacement without transparency gets its background keyed out (near-white to alpha, feathered); a PNG with
    /// alpha is used as it is. Resized to the slot, mipmapped and encoded BC7 in the slot's exact chunk layout.
    /// </summary>
    public static byte[] Build(TextureRes res, Rgba image)
    {
        if (image.Width != res.Width || image.Height != res.Height) image = ImageIO.ResizeSmooth(image, res.Width, res.Height);
        if (!image.Pixels.Where((_, i) => i % 4 == 3).Any(a => a < 250)) image = KeyWhite(image);
        var mips = new List<byte[]>();
        for (int i = 0; i < res.MipCount; i++)
        {
            mips.Add(Bc7.EncodeTiled(image));
            if (i + 1 < res.MipCount) image = TextureChunk.Halve(image);
        }
        return TextureChunk.Build(res, mips).Data;
    }

    /// <summary>
    /// Background → alpha for a replacement picture that has no transparency of its own. The background colour is the
    /// median of the border pixels; a pixel's alpha is its distance from that colour relative to the most ink-like
    /// pixel within 2 px (so ice shading near the colour stays transparent and anti-aliased logo edges get partial
    /// alpha with their ink colour instead of a white halo — the same keying the center-logo conversion uses).
    /// </summary>
    static Rgba KeyWhite(Rgba img)
    {
        int w = img.Width, h = img.Height;
        var border = new List<int>();
        for (int x = 0; x < w; x++) { border.Add(x); border.Add((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { border.Add(y * w); border.Add(y * w + w - 1); }
        double[] bg = new double[3];
        for (int k = 0; k < 3; k++)
        {
            var vals = border.Select(i => img.Pixels[i * 4 + k]).OrderBy(v => v).ToList();
            bg[k] = vals[vals.Count / 2];
        }
        var d = new float[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int p = i * 4;
            double r = img.Pixels[p] - bg[0], g = img.Pixels[p + 1] - bg[1], b = img.Pixels[p + 2] - bg[2];
            d[i] = (float)Math.Sqrt(r * r + g * g + b * b);
        }
        var px = new byte[w * h * 4];
        const double floor = 22;   // background grain and shading stay transparent
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, p = i * 4;
                if (d[i] <= floor) continue;          // background: alpha 0
                int best = i; float bd = d[i];
                for (int yy = Math.Max(0, y - 2); yy <= Math.Min(h - 1, y + 2); yy++)
                    for (int xx = Math.Max(0, x - 2); xx <= Math.Min(w - 1, x + 2); xx++)
                        if (d[yy * w + xx] > bd) { bd = d[yy * w + xx]; best = yy * w + xx; }
                double a = Math.Clamp((d[i] - floor) / Math.Max(bd - floor, 40), 0, 1);
                for (int k = 0; k < 3; k++)
                {
                    // Un-mix the background from the edge pixel; fall back to the neighbour's ink colour where that overshoots.
                    double c = a > 0.02 ? (img.Pixels[p + k] - (1 - a) * bg[k]) / a : img.Pixels[best * 4 + k];
                    px[p + k] = (byte)Math.Clamp(Math.Round(c), 0, 255);
                }
                px[p + 3] = (byte)Math.Round(a * 255);
            }
        });
        return new Rgba(w, h, px);
    }

    /// <summary>Writes finished chunk bytes into the named hotspot slots of <paramref name="game"/> (backup + read-back).</summary>
    public static (bool Ok, string Message, string? Backup) Apply(string game, IReadOnlyDictionary<(int Set, int Index), byte[]> data, Action<string>? progress = null)
    {
        if (ApplyTarget.RunningGame() is { } running) return (false, running + " is running. Close the game first.", null);
        var problems = ApplyTarget.Problems(game);
        if (problems.Count > 0) return (false, "Apply refused:\n" + string.Join("\n", problems), null);
        progress?.Invoke("Backing up TOCs and CAS lengths...");
        var backup = ApplyTarget.Backup(game);
        try
        {
            var index = BundleIndex.Shared(game);
            var jobs = data.Select(kv => { var s = Read(game, index, kv.Key.Set, kv.Key.Index); return (s.Chunk, Data: kv.Value); }).ToList();
            foreach (var (id, bytes) in jobs)
            {
                foreach (var line in ChunkReplace.Replace(game, index, id, bytes)) SuiteLog.Write("hotspot: " + line);
                index = BundleIndex.Build(game);
            }
            progress?.Invoke("Reading them back...");
            foreach (var (id, bytes) in jobs)
                foreach (var r in ChunkReplace.ReadAll(game, index, id))
                    if (!r.Raw.AsSpan().SequenceEqual(bytes.AsSpan((int)r.LogicalOffset, r.Raw.Length)))
                        throw new InvalidOperationException($"Read-back differs ({r.Where}).");
            return (true, $"{jobs.Count} hotspot slot(s) written and read back identical. Backup: {backup}", backup);
        }
        catch (Exception ex)
        {
            SuiteLog.Write("hotspot apply failed, restoring: " + ex);
            ApplyTarget.RestoreFromBackup(game, backup);
            return (false, $"Apply failed ({ex.Message}); the game was put back from the backup.", backup);
        }
    }
}
