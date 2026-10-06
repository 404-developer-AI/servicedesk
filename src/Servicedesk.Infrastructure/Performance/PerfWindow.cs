using System.Collections.Concurrent;

namespace Servicedesk.Infrastructure.Performance;

public readonly record struct HttpKey(string Method, string Route, string StatusClass);
public readonly record struct DbKey(string Fingerprint, string Source);
public readonly record struct SpanKey(string Kind, string Name, string Detail);
public readonly record struct RumKey(string Route, string Metric, string Detail, string Device, string Connection);
public readonly record struct NPlusOneKey(string Route, string Fingerprint);

/// One slow request kept individually (with its breakdown in Diagnose).
public sealed record SlowRequestRecord(
    DateTimeOffset TimestampUtc,
    string Method,
    string Route,
    int Status,
    double TotalMs,
    double DbMs,
    long DbCount,
    double ExtMs,
    double PipelineMs,
    int GcCount,
    long Bytes,
    string? BreakdownJson,
    string TraceId);

public sealed record WorkerRunRecord(
    DateTimeOffset StartedUtc,
    string Worker,
    double DurationMs,
    bool Success,
    long Items,
    string? ErrorKind);

/// Everything collected during one flush window (normally one minute).
/// Collectors write into <see cref="PerfRecorder.Current"/>; the flush
/// service swaps in a fresh window and persists the old one in one batch.
/// Every map is capped: a key beyond the cap lands in an <c>__other__</c>
/// bucket, so a cardinality bug can never grow memory without bound.
public sealed class PerfWindow
{
    public const string OtherKey = "__other__";

    public const int MaxHttpKeys = 1500;
    public const int MaxDbKeys = 4000;
    public const int MaxSpanKeys = 1500;
    public const int MaxRumKeys = 3000;
    public const int MaxNPlusOneKeys = 500;
    public const int MaxSlowRequests = 200;
    public const int MaxWorkerRuns = 2000;
    public const int MaxActiveUsers = 1000;

    // Extra-sum slots on HTTP aggregates.
    public const int HttpDbCount = 0;
    public const int HttpDbMicro = 1;
    public const int HttpExtMicro = 2;
    public const int HttpPipelineMicro = 3;
    public const int HttpBytes = 4;
    public const int HttpExtraCount = 5;

    public PerfWindow(DateTimeOffset startedUtc) => StartedUtc = startedUtc;

    public DateTimeOffset StartedUtc { get; }

    public readonly ConcurrentDictionary<HttpKey, PerfAggregate> Http = new();
    public readonly ConcurrentDictionary<DbKey, PerfAggregate> Db = new();
    public readonly ConcurrentDictionary<SpanKey, PerfAggregate> Spans = new();
    public readonly ConcurrentDictionary<RumKey, PerfAggregate> Rum = new();
    public readonly ConcurrentDictionary<NPlusOneKey, PerfAggregate> NPlusOne = new();
    public readonly ConcurrentQueue<SlowRequestRecord> SlowRequests = new();
    public readonly ConcurrentQueue<WorkerRunRecord> WorkerRuns = new();
    public readonly ConcurrentDictionary<int, byte> ActiveUsers = new();

    /// Fingerprint → (normalized SQL, first caller seen). Filled in Diagnose.
    public readonly ConcurrentDictionary<string, FingerprintInfo> Fingerprints = new();

    internal int HttpKeyCount;
    internal int DbKeyCount;
    internal int SpanKeyCount;
    internal int RumKeyCount;
    internal int NPlusOneKeyCount;
    internal int SlowCount;
    internal int WorkerRunCount;
    internal int ActiveUserCount;

    public long OverheadTicks;
    public long Requests;
    public long InFlightMax;

    private readonly List<RuntimeSample> _runtimeSamples = new();

    public void AddRuntimeSample(RuntimeSample sample)
    {
        lock (_runtimeSamples) _runtimeSamples.Add(sample);
    }

    public IReadOnlyList<RuntimeSample> RuntimeSamples
    {
        get { lock (_runtimeSamples) return _runtimeSamples.ToArray(); }
    }

    public PerfAggregate HttpFor(HttpKey key) =>
        GetOrAdd(Http, ref HttpKeyCount, MaxHttpKeys, key, key with { Route = OtherKey },
            () => new PerfAggregate(HttpExtraCount, secondHistogram: true));

    public PerfAggregate DbFor(DbKey key) =>
        GetOrAdd(Db, ref DbKeyCount, MaxDbKeys, key, new DbKey(OtherKey, key.Source), () => new PerfAggregate());

    public PerfAggregate SpanFor(SpanKey key) =>
        GetOrAdd(Spans, ref SpanKeyCount, MaxSpanKeys, key, key with { Name = OtherKey, Detail = "" }, () => new PerfAggregate());

    public PerfAggregate RumFor(RumKey key) =>
        GetOrAdd(Rum, ref RumKeyCount, MaxRumKeys, key, key with { Route = OtherKey, Detail = "" }, () => new PerfAggregate());

    public PerfAggregate NPlusOneFor(NPlusOneKey key) =>
        GetOrAdd(NPlusOne, ref NPlusOneKeyCount, MaxNPlusOneKeys, key, new NPlusOneKey(OtherKey, OtherKey), () => new PerfAggregate());

    public void AddSlowRequest(SlowRequestRecord record)
    {
        if (Interlocked.Increment(ref SlowCount) <= MaxSlowRequests) SlowRequests.Enqueue(record);
    }

    public void AddWorkerRun(WorkerRunRecord record)
    {
        if (Interlocked.Increment(ref WorkerRunCount) <= MaxWorkerRuns) WorkerRuns.Enqueue(record);
    }

    public void AddActiveUser(int userHash)
    {
        if (ActiveUsers.ContainsKey(userHash)) return;
        if (Interlocked.Increment(ref ActiveUserCount) <= MaxActiveUsers) ActiveUsers.TryAdd(userHash, 0);
    }

    private static PerfAggregate GetOrAdd<TKey>(
        ConcurrentDictionary<TKey, PerfAggregate> map,
        ref int keyCount,
        int cap,
        TKey key,
        TKey overflowKey,
        Func<PerfAggregate> factory)
        where TKey : notnull
    {
        if (map.TryGetValue(key, out var existing)) return existing;
        if (Volatile.Read(ref keyCount) >= cap)
        {
            key = overflowKey;
            if (map.TryGetValue(key, out existing)) return existing;
        }
        var created = factory();
        var actual = map.GetOrAdd(key, created);
        if (ReferenceEquals(actual, created)) Interlocked.Increment(ref keyCount);
        return actual;
    }
}

public sealed record FingerprintInfo(string Sql, string? Caller);
