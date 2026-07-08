namespace ConventionSense.Core;

/// <summary>
/// Wilson score interval lower bound — the confidence score used for all rules.
/// Penalizes small samples, so 3/3 scores far below 30/30.
/// </summary>
public static class Wilson
{
    /// <summary>
    /// Lower bound of the Wilson score interval for the proportion <paramref name="positives"/>/<paramref name="n"/>.
    /// </summary>
    /// <param name="positives">Number of successes (co-changes).</param>
    /// <param name="n">Number of trials (total changes of the trigger entity).</param>
    /// <param name="z">Normal quantile; 1.96 for a 95% interval.</param>
    public static double LowerBound(long positives, long n, double z = 1.96)
    {
        if (n <= 0)
        {
            return 0.0;
        }

        double p = (double)positives / n;
        double z2 = z * z;
        double centre = p + z2 / (2.0 * n);
        double margin = z * Math.Sqrt(p * (1.0 - p) / n + z2 / (4.0 * n * n));
        return (centre - margin) / (1.0 + z2 / n);
    }
}
