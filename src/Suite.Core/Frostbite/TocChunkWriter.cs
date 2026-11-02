namespace Suite.Core.Frostbite;

/// <summary>
/// Replaces one top-level TOC texture chunk: append the new stored blocks to the CAS the entry's location record names,
/// then repoint every TOC entry that pointed at the copy being replaced. Append only - existing bytes are never
/// rewritten, so a restore is just putting the old TOC back. Take <see cref="ApplyTarget.Backup"/> first.
/// </summary>
public static class TocChunkWriter
{
    public sealed record Ref(TocFile Toc, ChunkRef Where);
    public sealed record WriteResult(Guid Id, string Cas, long Offset, int StoredLength, int Repointed, List<string> Log);

    /// <summary>Every TOC under Data and Patch that carries chunk tables.</summary>
    public static List<TocFile> LoadTocs(string game)
    {
        var list = new List<TocFile>();
        foreach (var root in new[] { "Patch", "Data" })
        {
            var dir = Path.Combine(game, root);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.toc", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                list.Add(TocFile.Load(f));
        }
        return list;
    }

    public static string CasPath(string game, ChunkRef r) => Path.Combine(game, r.Patch ? "Patch" : "Data", "Gen4b",
        "superbundlelayout", $"nhl_installpackage_{r.Catalog:D2}", $"cas_{r.Cas:D2}.cas");

    public static byte[] ReadStored(string game, ChunkRef r)
    {
        var cas = CasPath(game, r);
        if (!File.Exists(cas)) throw new FileNotFoundException($"CAS {cas} does not exist.");
        using var fs = File.OpenRead(cas);
        if (r.Offset + (long)r.Size > fs.Length)
            throw new InvalidDataException($"{Path.GetFileName(cas)} is {fs.Length:N0} bytes; the entry wants {r.Offset:N0}+{r.Size:N0}.");
        fs.Position = r.Offset;
        var stored = new byte[r.Size];
        fs.ReadExactly(stored);
        return stored;
    }

    public static byte[] ReadRaw(string game, ChunkRef r) => ChunkBlocks.Parse(ReadStored(game, r)).Decode();

    /// <summary>Every TOC entry for a chunk id, across all TOCs. Duplicate copies exist and only one is live.</summary>
    public static List<Ref> FindAll(IEnumerable<TocFile> tocs, Guid id) =>
        tocs.Select(t => (toc: t, where: t.Find(id))).Where(x => x.where is not null)
            .Select(x => new Ref(x.toc, x.where!)).ToList();

    /// <summary>
    /// Appends <paramref name="raw"/> as new blocks and repoints the live copy. When
    /// <paramref name="expectedCurrentRaw"/> is given, the copy that decodes to it is the one treated as live - so a stale
    /// duplicate is never picked by accident.
    /// </summary>
    public static WriteResult Replace(string game, Guid id, byte[] raw, byte[]? expectedCurrentRaw = null)
    {
        if (!File.Exists(Path.Combine(game, "package.mft")))
            throw new InvalidDataException($"{game} does not look like a game root (no package.mft).");

        var tocs = LoadTocs(game);
        var found = FindAll(tocs, id);
        if (found.Count == 0) throw new InvalidDataException($"No TOC entry for chunk {id}.");

        Ref? target = null;
        var tried = new List<string>();
        foreach (var f in found)
        {
            try
            {
                var current = ReadRaw(game, f.Where);
                bool matches = expectedCurrentRaw is null || current.AsSpan().SequenceEqual(expectedCurrentRaw);
                tried.Add($"{Path.GetFileName(f.Where.Toc)} patch={f.Where.Patch} cas={f.Where.Cas:D2} @{f.Where.Offset:N0}: {current.Length:N0} raw, {(matches ? "expected" : "different")}");
                if (matches && target is null) target = f;
            }
            catch (Exception ex)
            {
                tried.Add($"{Path.GetFileName(f.Where.Toc)}: unreadable ({ex.Message})");
            }
        }
        if (target is null)
            throw new InvalidDataException("No copy of the chunk decodes to the expected current payload; refusing to guess.\n  " + string.Join("\n  ", tried));

        var stored = ChunkBlocks.EncodeBlocks(raw);
        if (!ChunkBlocks.Parse(stored).Decode().AsSpan().SequenceEqual(raw))
            throw new InvalidOperationException("The rebuilt stored blocks do not decode back to the new payload.");

        var cas = CasPath(game, target.Where);
        long offset;
        using (var fs = new FileStream(cas, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            offset = fs.Position;
            if (offset + stored.Length > uint.MaxValue)
                throw new InvalidOperationException($"{cas} would grow past 4 GB.");
            fs.Write(stored);
            fs.Flush(true);
        }

        int repointed = 0;
        var dirty = new HashSet<TocFile>();
        foreach (var toc in tocs)
            if (toc.Find(id) is { } r && r.SameLocation(target.Where))
            {
                toc.Repoint(r.EntryPos, (uint)offset, (uint)stored.Length);
                dirty.Add(toc);
                repointed++;
            }
        if (repointed == 0) throw new InvalidOperationException("No TOC entry points at the chunk being replaced.");
        foreach (var toc in dirty) toc.Save();

        var log = new List<string>
        {
            $"appended {stored.Length:N0} bytes to {Path.GetRelativePath(game, cas)} @ {offset:N0}; {repointed} TOC entr{(repointed == 1 ? "y" : "ies")} repointed",
            $"verified copy: {tried.FirstOrDefault(t => t.Contains("expected")) ?? tried[0]}",
        };
        return new WriteResult(id, cas, offset, stored.Length, repointed, log);
    }
}