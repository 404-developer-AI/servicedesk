using System.Collections.Concurrent;

namespace Servicedesk.Infrastructure.Performance;

/// Lock-free accumulator for one key in one minute window (count, sum, max,
/// histogram, plus a few named extra sums). Every field is updated with
/// <see cref="Interlocked"/>, so collectors on many threads can record into
/// the same instance without a lock. Values are stored as micro-units
/// (value × 1000) so fractional milliseconds survive the integer math.
public sealed class PerfAggregate
{
    public long Count;
    public long ErrorCount;
    public long SumMicro;
    public long MaxMicro;
    public readonly long[] Buckets = new long[PerfHistogram.BucketCount];

    /// Caller-defined extra sums (see the Extra* constants on the window).
    public readonly long[] Extras;

    /// Optional second histogram (payload size in KB for HTTP rows).
    public readonly long[]? Buckets2;

    /// Distinct-user sketch: hashed user ids, capped so it stays bounded.
    private ConcurrentDictionary<int, byte>? _users;
    private const int MaxTrackedUsers = 64;

    public PerfAggregate(int extras = 0, bool secondHistogram = false)
    {
        Extras = extras == 0 ? Array.Empty<long>() : new long[extras];
        Buckets2 = secondHistogram ? new long[PerfHistogram.BucketCount] : null;
    }

    public void Record(double value, bool error = false)
    {
        if (double.IsNaN(value) || value < 0) value = 0;
        var micro = (long)(value * 1000);
        Interlocked.Increment(ref Count);
        if (error) Interlocked.Increment(ref ErrorCount);
        Interlocked.Add(ref SumMicro, micro);
        UpdateMax(ref MaxMicro, micro);
        Interlocked.Increment(ref Buckets[PerfHistogram.IndexOf(value)]);
    }

    /// Extras hold raw values (counts, microseconds, bytes — slot-defined).
    public void AddExtra(int index, long value) => Interlocked.Add(ref Extras[index], value);

    public void RecordSecond(double value)
    {
        if (Buckets2 is null) return;
        Interlocked.Increment(ref Buckets2[PerfHistogram.IndexOf(value)]);
    }

    public void AddUser(int userHash)
    {
        var users = Volatile.Read(ref _users);
        if (users is null)
        {
            Interlocked.CompareExchange(ref _users, new ConcurrentDictionary<int, byte>(), null);
            users = _users!;
        }
        if (users.Count < MaxTrackedUsers) users.TryAdd(userHash, 0);
    }

    public int DistinctUsers => _users?.Count ?? 0;

    public static void UpdateMax(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current) return;
            current = seen;
        }
    }

    public double SumValue => SumMicro / 1000.0;
    public double MaxValue => MaxMicro / 1000.0;
}
