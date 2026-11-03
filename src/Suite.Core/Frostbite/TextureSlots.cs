namespace Suite.Core.Frostbite;

/// <summary>
/// Any NHL 24 texture slot by name (jersey sheets, branding arrays, logos): read it, and write <see cref="TextureReplacement"/>s
/// with the suite's own writer - every TOC chunk-table entry and bundle copy (<see cref="ChunkReplace"/>), full mip chains,
/// PS4 tiling, array layers replaced one by one with the rest kept byte for byte. Backup first, read-back after, and any
/// failure restores the backup. Replaces the NHL24Kit Worker's apply.
/// Rules kept here because they hold whatever the source: base coeff sheets stay stock; Alpha = "auto" (or any value) on
/// a sheet keeps NHL 24's own alpha (the fabric mask) under the new colour.
/// </summary>
public static class TextureSlots
{
    public sealed record Slot(string Name, TextureRes Res, Guid Chunk, byte[] Stock);

    public static Slot Read(string game, BundleIndex index, string name)
    {
        var res = index.ResByName.GetValueOrDefault(name)?.FirstOrDefault() ?? throw new InvalidOperationException($"No texture named {name} in this game.");
        var stored = BundleIndex.ReadBytes(res.Data);
        byte[] header; try { header = ChunkBlocks.Parse(stored).Decode(); } catch { header = stored; }
        var tr = TextureRes.Parse(header);
        long size = tr.PayloadSize * Ps4Codec.Slices(header);
        var full = ChunkReplace.ReadAll(game, index, tr.ChunkGuid).FirstOrDefault(r => r.LogicalOffset == 0 && r.Raw.Length == size).Raw
            ?? throw new InvalidOperationException($"{name}: no full copy of its texture data.");
        return new Slot(name, tr, tr.ChunkGuid, full);
    }

    public static string? Refusal(TextureReplacement r)
    {
        if (r.Slot.EndsWith("_base_coeff", StringComparison.OrdinalIgnoreCase)) return $"{r.Slot}: base coeff sheets stay stock.";
        if (string.IsNullOrEmpty(r.Source) && (r.Layers is null || r.Layers.Count == 0)) return $"{r.Slot}: nothing to write.";
        return null;
    }

    /// <summary>The new chunk for one replacement (pure; no game writes).</summary>
    public static byte[] Build(Slot slot, TextureReplacement r)
    {
        var images = new Dictionary<int, Rgba>();
        if (!string.IsNullOrEmpty(r.Source)) images[0] = ImageIO.Load(r.Source);
        foreach (var (layer, path) in r.Layers ?? []) images[layer] = ImageIO.Load(path);
        if (r.Alpha is not null)
        {
            var stock = Ps4Codec.DecodeSlices(slot.Res, slot.Stock);
            foreach (var k in images.Keys.ToList())
            {
                var img = images[k].Width == slot.Res.Width && images[k].Height == slot.Res.Height ? images[k] : ImageIO.ResizeSmooth(images[k], slot.Res.Width, slot.Res.Height);
                var px = (byte[])img.Pixels.Clone();
                for (int i = 3; i < px.Length; i += 4) px[i] = stock[k].Pixels[i];
                images[k] = new Rgba(img.Width, img.Height, px);
            }
        }
        return Ps4Codec.Build(slot.Res, slot.Stock, images);
    }

    /// <summary>Writes the replacements into <paramref name="game"/>. Returns (ok, message, backup).</summary>
    public static (bool Ok, string Message, string? Backup) Apply(string game, IReadOnlyList<TextureReplacement> replacements, Action<string>? progress = null)
    {
        if (ApplyTarget.RunningGame() is { } running) return (false, running + " is running. Close the game first.", null);
        var problems = ApplyTarget.Problems(game);
        if (problems.Count > 0) return (false, "Apply refused:\n" + string.Join("\n", problems), null);
        var refused = replacements.Select(Refusal).OfType<string>().ToList();
        if (refused.Count > 0) return (false, "Apply refused:\n" + string.Join("\n", refused), null);
        progress?.Invoke("Reading the game's bundles...");
        var index = BundleIndex.Build(game);
        var jobs = new List<(string Name, Guid Chunk, byte[] Data)>();
        foreach (var r in replacements.GroupBy(r => r.Slot).Select(g => g.Last()))
        {
            progress?.Invoke($"Encoding {r.Slot.Split('/')[^1]}...");
            var slot = Read(game, index, r.Slot);
            jobs.Add((r.Slot, slot.Chunk, Build(slot, r)));
        }
        progress?.Invoke("Backing up TOCs and CAS lengths...");
        var backup = ApplyTarget.Backup(game);
        try
        {
            foreach (var (name, id, data) in jobs)
            {
                progress?.Invoke($"Writing {name.Split('/')[^1]}...");
                foreach (var line in ChunkReplace.Replace(game, index, id, data)) SuiteLog.Write("texture apply: " + line);
                index = BundleIndex.Build(game);
            }
            progress?.Invoke("Reading them back...");
            foreach (var (name, id, data) in jobs)
                foreach (var rb in ChunkReplace.ReadAll(game, index, id))
                    if (rb.LogicalOffset + (long)rb.Raw.Length > data.Length || !rb.Raw.AsSpan().SequenceEqual(data.AsSpan((int)rb.LogicalOffset, rb.Raw.Length)))
                        throw new InvalidOperationException($"Read-back differs: {name} via {rb.Where}.");
            return (true, $"{jobs.Count} texture(s) written and read back identical. Backup: {backup}", backup);
        }
        catch (Exception ex)
        {
            SuiteLog.Write("texture apply failed, restoring: " + ex);
            ApplyTarget.RestoreFromBackup(game, backup);
            return (false, $"Apply failed ({ex.Message}); the game was put back from the backup.", backup);
        }
    }
}
