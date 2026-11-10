namespace Suite.Core;

/// <summary>Explicit CPU budgets prevent nested image operations from oversubscribing the machine.</summary>
static class CpuParallel
{
    public static void For(int fromInclusive, int toExclusive, int workers, Action<int> body)
    {
        if (workers == 1)
        {
            for (int i = fromInclusive; i < toExclusive; i++) body(i);
            return;
        }
        if (workers <= 0) Parallel.For(fromInclusive, toExclusive, body);
        else Parallel.For(fromInclusive, toExclusive, new ParallelOptions { MaxDegreeOfParallelism = workers }, body);
    }

    /// <summary>A bounded number of large images in flight, with the remaining CPU budget partitioned inside each codec.</summary>
    public static (int Jobs, int CodecThreads) TextureBudget(int workers)
    {
        if (workers <= 0) return (1, 0);
        int jobs = Math.Clamp(workers / 6, 1, 4);
        return (jobs, Math.Max(1, workers / jobs));
    }
}
