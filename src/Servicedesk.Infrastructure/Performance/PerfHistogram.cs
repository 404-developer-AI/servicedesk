namespace Servicedesk.Infrastructure.Performance;

/// Fixed log-linear histogram used for every duration in the monitor.
///
/// The bucket set never changes, so histograms are <em>additive</em>: minute
/// rows sum into hour rows and any period's percentiles are computed from
/// the summed counts. Storing raw samples (or pre-computed percentiles, which
/// cannot be combined) would make both rollups and arbitrary periods wrong.
/// Each bucket holds the count of values ≤ its upper bound (and above the
/// previous one); the last bucket is +Inf.
public static class PerfHistogram
{
    /// Upper bounds (inclusive). The unit is whatever the caller records —
    /// milliseconds for durations, kilobytes for payload sizes, CLS × 1000.
    public static readonly double[] Bounds =
    {
        1, 2, 3, 5, 7, 10, 15, 20, 30, 50, 75, 100, 150, 200, 300, 500, 750,
        1_000, 1_500, 2_000, 3_000, 5_000, 7_500, 10_000, 15_000, 30_000, 60_000,
    };

    /// Bounds + the +Inf overflow bucket.
    public static readonly int BucketCount = Bounds.Length + 1;

    public static int IndexOf(double value)
    {
        if (double.IsNaN(value) || value <= Bounds[0]) return 0;
        // Binary search for the first bound >= value.
        int lo = 0, hi = Bounds.Length - 1;
        if (value > Bounds[hi]) return Bounds.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (Bounds[mid] >= value) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }

    /// Percentile (0..100) estimated from bucket counts by linear
    /// interpolation inside the bucket that holds the rank. The +Inf bucket
    /// reports <paramref name="observedMax"/> when known (the true max is
    /// always stored next to the histogram), else its lower bound.
    public static double Percentile(IReadOnlyList<long> counts, double percentile, double? observedMax = null)
    {
        long total = 0;
        for (var i = 0; i < counts.Count; i++) total += counts[i];
        if (total == 0) return 0;

        var rank = Math.Clamp(percentile, 0, 100) / 100.0 * total;
        if (rank < 1) rank = 1;
        long cumulative = 0;
        for (var i = 0; i < counts.Count; i++)
        {
            var c = counts[i];
            if (c == 0) continue;
            if (cumulative + c >= rank)
            {
                var lower = i == 0 ? 0 : Bounds[Math.Min(i - 1, Bounds.Length - 1)];
                double upper;
                if (i >= Bounds.Length)
                {
                    upper = observedMax is { } m && m > lower ? m : lower;
                }
                else
                {
                    upper = Bounds[i];
                }
                var within = (rank - cumulative) / c;
                var estimate = lower + (upper - lower) * within;
                // Never report more than the measured maximum.
                return observedMax is { } max && max > 0 ? Math.Min(estimate, max) : estimate;
            }
            cumulative += c;
        }
        return observedMax ?? Bounds[^1];
    }

    /// Element-wise sum; tolerates arrays of different (older/newer) length.
    public static long[] Merge(IEnumerable<IReadOnlyList<long>> histograms)
    {
        var result = new long[BucketCount];
        foreach (var h in histograms)
        {
            var n = Math.Min(h.Count, result.Length);
            for (var i = 0; i < n; i++) result[i] += h[i];
        }
        return result;
    }

    public static long[] Merge(long[] a, IReadOnlyList<long> b)
    {
        var n = Math.Min(a.Length, b.Count);
        for (var i = 0; i < n; i++) a[i] += b[i];
        return a;
    }
}
