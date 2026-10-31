using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// Matches numbered logo layers (branding crests, collar shields, helmet logos) between the two games by their
/// alpha silhouette. Layer order differs between games — NHL 24's layer 0 can be the NHL shield while NHL 27's
/// layer 0 is the team crest — but the same logo has the same shape, so the closest silhouette is the right source.
/// Recoloured logos still match because only the alpha (shape) is compared.
/// </summary>
public static class LogoMatcher
{
    const int N = 32;

    /// <summary>Mean absolute alpha difference (0..255) a candidate must beat to be accepted.</summary>
    public const double AcceptMad = 24;

    /// <summary>32×32 alpha silhouette of an image file (transparent = 0), or null when it cannot be read.</summary>
    public static double[]? Silhouette(string path)
    {
        try
        {
            var img = ImageIO.Load(path);
            var result = new double[N * N];
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                int sx0 = x * img.Width / N, sx1 = Math.Max(sx0 + 1, (x + 1) * img.Width / N);
                int sy0 = y * img.Height / N, sy1 = Math.Max(sy0 + 1, (y + 1) * img.Height / N);
                long sum = 0;
                int count = 0;
                for (int sy = sy0; sy < sy1 && sy < img.Height; sy++)
                for (int sx = sx0; sx < sx1 && sx < img.Width; sx++)
                {
                    sum += img.Pixels[(sy * img.Width + sx) * 4 + 3];
                    count++;
                }
                result[y * N + x] = count == 0 ? 0 : (double)sum / count;
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    public static double Difference(double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }

    /// <summary>Index of the candidate whose silhouette is closest to the stock one, or null when none is close enough.</summary>
    public static int? Best(double[] stock, IReadOnlyList<double[]> candidates)
    {
        int best = -1;
        double bestScore = double.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            var score = Difference(stock, candidates[i]);
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best >= 0 && bestScore <= AcceptMad ? best : null;
    }
}
