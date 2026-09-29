namespace Servicedesk.Infrastructure.Insights;

/// Headline numbers for a ticket-count report (new / closed). The SPA computes the same
/// figures client-side (queue toggles are instant there); the PDF uses this.
public sealed record TicketCountSummary(
    int Total,
    double AveragePerDay,
    int ElapsedDays,
    TicketCountBucket? PeakBucket,
    int PeakCount,
    InsightsQueue? TopQueue,
    int TopQueueCount)
{
    /// Average per day counts only days up to and including today, so a
    /// half-finished month is not diluted by its future days.
    public static TicketCountSummary Compute(TicketCountReport report)
    {
        var total = 0;
        TicketCountBucket? peak = null;
        var peakCount = 0;
        var perQueue = new int[report.Queues.Count];
        foreach (var b in report.Buckets)
        {
            var sum = 0;
            for (var i = 0; i < b.Counts.Count; i++)
            {
                sum += b.Counts[i];
                perQueue[i] += b.Counts[i];
            }
            total += sum;
            if (sum > peakCount)
            {
                peakCount = sum;
                peak = b;
            }
        }

        var lastCounted = report.Range.To < report.Today ? report.Range.To : report.Today;
        var elapsed = Math.Max(0, lastCounted.DayNumber - report.Range.From.DayNumber + 1);

        InsightsQueue? top = null;
        var topCount = 0;
        for (var i = 0; i < perQueue.Length; i++)
        {
            if (perQueue[i] > topCount)
            {
                topCount = perQueue[i];
                top = report.Queues[i];
            }
        }

        return new TicketCountSummary(
            total,
            elapsed == 0 ? 0 : (double)total / elapsed,
            elapsed,
            peak,
            peakCount,
            top,
            topCount);
    }
}
