using System.Security.Cryptography;

namespace Suite.Core.Frostbite;

public sealed record TextureJob(string Name, Guid Chunk, byte[] Data);
public sealed record TexturePreparation(List<TextureJob> Jobs, int UpToDate, int CacheHits, int Encoded, int SlotCopies);

/// <summary>Read-only, bounded parallel preparation. No game writes occur here.</summary>
static class TexturePreparer
{
    public static TexturePreparation Prepare(string game, BundleIndex index, IReadOnlyList<TextureReplacement> replacements,
        int workers, TexturePayloadCache cache, Action<string>? progress)
    {
        var rows = replacements.GroupBy(r => r.Slot).Select(g => g.Last()).ToList();
        var (jobsAtOnce, codecThreads) = CpuParallel.TextureBudget(workers);
        var lines = new Line[rows.Count];
        int done = 0;
        Parallel.For(0, rows.Count, new ParallelOptions { MaxDegreeOfParallelism = jobsAtOnce }, i =>
        {
            var replacement = rows[i];
            var line = new Line();
            try
            {
                foreach (var slot in TextureSlots.ReadAll(game, index, replacement.Slot))
                {
                    line.Copies++;
                    var stockHash = Convert.ToHexString(SHA256.HashData(slot.Stock));
                    var key = TexturePayloadCache.Key(slot, replacement, out var stockForAlpha);
                    if (cache.HashOf(key) == stockHash)
                    {
                        line.UpToDate++;
                        continue;
                    }

                    var data = cache.Load(key);
                    if (data is not null) line.CacheHits++;
                    else
                    {
                        data = TextureSlots.Build(slot, replacement, codecThreads, stockForAlpha);
                        line.Encoded++;
                    }

                    if (Convert.ToHexString(SHA256.HashData(data)) == stockHash)
                    {
                        cache.Store(key, data, keepPayload: false);
                        line.UpToDate++;
                        continue;
                    }
                    cache.Store(key, data, keepPayload: true);
                    line.Jobs.Add(new TextureJob(replacement.Slot, slot.Chunk, data));
                }
            }
            catch (Exception ex) { throw new InvalidOperationException($"Preparing {replacement.Slot} failed: {ex.Message}", ex); }
            lines[i] = line;
            int n = Interlocked.Increment(ref done);
            if (n % 8 == 0 || n == rows.Count)
                progress?.Invoke($"Prepared {n}/{rows.Count} textures ({jobsAtOnce} jobs, {codecThreads} codec threads each)...");
        });

        var result = new TexturePreparation([], lines.Sum(l => l.UpToDate), lines.Sum(l => l.CacheHits),
            lines.Sum(l => l.Encoded), lines.Sum(l => l.Copies));
        foreach (var line in lines) result.Jobs.AddRange(line.Jobs);
        return result;
    }

    sealed class Line
    {
        public List<TextureJob> Jobs { get; } = [];
        public int UpToDate { get; set; }
        public int CacheHits { get; set; }
        public int Encoded { get; set; }
        public int Copies { get; set; }
    }
}
