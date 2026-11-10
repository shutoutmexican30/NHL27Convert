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
        return Read(game, index, res, name);
    }

    /// <summary>
    /// Every copy of a texture slot. An update can carry the same texture again with its own chunk (Detroit away's
    /// branding array is in Data AND in Patch, each a different chunk of a different size), and the game reads the
    /// base Data copy for such pairs — so a write must land in every copy, not only the first found.
    /// </summary>
    public static List<Slot> ReadAll(string game, BundleIndex index, string name)
    {
        var entries = index.ResByName.GetValueOrDefault(name) ?? throw new InvalidOperationException($"No texture named {name} in this game.");
        var slots = new List<Slot>();
        foreach (var res in entries)
        {
            var slot = Read(game, index, res, name);
            if (!slots.Any(s => s.Chunk == slot.Chunk)) slots.Add(slot);
        }
        return slots;
    }

    static Slot Read(string game, BundleIndex index, BundleIndex.Res res, string name)
    {
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
    public static byte[] Build(Slot slot, TextureReplacement r, int workers = 0, IReadOnlyList<Rgba>? stockForAlpha = null)
    {
        var images = new Dictionary<int, Rgba>();
        static Rgba Load(string path) => path.StartsWith("coeff:", StringComparison.Ordinal)
            ? LogoCoefficient.FromColor(ImageIO.Load(path["coeff:".Length..])) : ImageIO.Load(path);
        if (!string.IsNullOrEmpty(r.Source)) images[0] = Load(r.Source);
        foreach (var (layer, path) in r.Layers ?? []) images[layer] = Load(path);
        if (r.Alpha is not null)
        {
            var stock = stockForAlpha ?? Ps4Codec.DecodeSlices(slot.Res, slot.Stock);
            foreach (var k in images.Keys.ToList())
            {
                var img = images[k].Width == slot.Res.Width && images[k].Height == slot.Res.Height ? images[k] : ImageIO.ResizeSmooth(images[k], slot.Res.Width, slot.Res.Height, workers);
                var px = (byte[])img.Pixels.Clone();
                for (int i = 3; i < px.Length; i += 4) px[i] = stock[k].Pixels[i];
                images[k] = new Rgba(img.Width, img.Height, px);
            }
        }
        return Ps4Codec.Build(slot.Res, slot.Stock, images, workers);
    }

    /// <summary>Writes the replacements into <paramref name="game"/>. Returns (ok, message, backup).</summary>
    public static (bool Ok, string Message, string? Backup) Apply(string game, IReadOnlyList<TextureReplacement> replacements,
        Action<string>? progress = null, int workers = 0)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        var stage = System.Diagnostics.Stopwatch.StartNew();
        void Timed(string label)
        {
            var text = $"{label} in {stage.Elapsed:mm\\:ss}";
            progress?.Invoke(text + "...");
            SuiteLog.Write("texture apply timing: " + text);
            stage.Restart();
        }
        if (ApplyTarget.RunningGame() is { } running) return (false, running + " is running. Close the game first.", null);
        var problems = ApplyTarget.Problems(game);
        if (problems.Count > 0) return (false, "Apply refused:\n" + string.Join("\n", problems), null);
        var refused = replacements.Select(Refusal).OfType<string>().ToList();
        if (refused.Count > 0) return (false, "Apply refused:\n" + string.Join("\n", refused), null);
        progress?.Invoke("Reading the game's bundles...");
        var index = BundleIndex.Build(game);
        Timed("Bundles read");
        var cache = new TexturePayloadCache();
        var prepared = TexturePreparer.Prepare(game, index, replacements, workers, cache, progress);
        cache.Save();
        var jobs = prepared.Jobs;
        Timed($"Preparation finished ({prepared.Encoded} encoded, {prepared.CacheHits} cached, {prepared.UpToDate} already current)");
        var conflicts = jobs.GroupBy(j => j.Chunk).Where(g =>
        {
            var first = g.First().Data;
            return g.Any(j => j.Data.Length != first.Length || !j.Data.AsSpan().SequenceEqual(first));
        }).ToList();
        if (conflicts.Count > 0)
            throw new InvalidOperationException("Conflicting data for chunk(s): " + string.Join("; ", conflicts.Select(c =>
                $"{c.Key} from {string.Join(", ", c.Select(j => j.Name).Distinct())}")));
        if (jobs.Count == 0)
            return (true, $"Everything already matches: {prepared.UpToDate} chunk copies verified current. No game files changed. Total: {total.Elapsed:mm\\:ss}.", null);
        progress?.Invoke("Backing up TOCs and CAS lengths...");
        var backup = ApplyTarget.Backup(game);
        Timed("Backup completed");
        try
        {
            progress?.Invoke($"Writing {jobs.Count} chunk copies...");
            foreach (var line in ChunkReplace.ReplaceMany(game, index, jobs.Select(j => (j.Chunk, j.Data)).ToList(), workers))
                SuiteLog.Write("texture apply: " + line);
            Timed("CAS and TOC writes completed");
            progress?.Invoke("Reading them back...");
            index = BundleIndex.Build(game);
            foreach (var job in jobs)
                foreach (var rb in ChunkReplace.ReadAll(game, index, job.Chunk))
                    if (rb.LogicalOffset + (long)rb.Raw.Length > job.Data.Length || !rb.Raw.AsSpan().SequenceEqual(job.Data.AsSpan((int)rb.LogicalOffset, rb.Raw.Length)))
                        throw new InvalidOperationException($"Read-back differs: {job.Name} via {rb.Where}.");
            Timed("Read-back verification completed");
            return (true, $"{jobs.Select(j => j.Name).Distinct().Count()} texture(s) written ({jobs.Count} chunk copies) and read back identical; " +
                $"{prepared.UpToDate} already current. Total: {total.Elapsed:mm\\:ss}. Backup: {backup}", backup);
        }
        catch (Exception ex)
        {
            SuiteLog.Write("texture apply failed, restoring: " + ex);
            ApplyTarget.RestoreFromBackup(game, backup);
            return (false, $"Apply failed ({ex.Message}); the game was put back from the backup.", backup);
        }
    }
}
