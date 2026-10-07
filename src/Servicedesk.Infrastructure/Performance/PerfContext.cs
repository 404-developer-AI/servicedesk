using System.Collections.Concurrent;
using System.Diagnostics;

namespace Servicedesk.Infrastructure.Performance;

/// Per-request measurement scope, carried through the async flow with an
/// <see cref="AsyncLocal{T}"/>. The database and external-HTTP listeners run
/// synchronously inside the awaiting request's execution context, so they
/// can add their time here without any lookup — that is what turns one
/// request's total into "app vs database vs external".
public sealed class PerfRequestScope
{
    public PerfRequestScope(string method, string route, bool diagnose, long startTimestamp)
    {
        Method = method;
        Route = route;
        Diagnose = diagnose;
        StartTimestamp = startTimestamp;
        if (diagnose) Fingerprints = new ConcurrentDictionary<string, FingerprintCounter>();
    }

    public string Method { get; }
    public string Route { get; }
    public bool Diagnose { get; }
    public long StartTimestamp { get; }

    /// "METHOD route" — the DB-query source label for this request.
    public string Source => Method + " " + Route;

    public long DbCount;
    public long DbMicro;
    public long ExtCount;
    public long ExtMicro;
    /// Time spent before the endpoint ran (rate limiter, auth, CSRF, gates); 0 until marked.
    public long PipelineMicro;

    /// Diagnose only: executions and time per query fingerprint.
    public ConcurrentDictionary<string, FingerprintCounter>? Fingerprints { get; }

    public void AddDb(double ms)
    {
        Interlocked.Increment(ref DbCount);
        Interlocked.Add(ref DbMicro, (long)(ms * 1000));
    }

    public void AddExternal(double ms)
    {
        Interlocked.Increment(ref ExtCount);
        Interlocked.Add(ref ExtMicro, (long)(ms * 1000));
    }

    public void MarkPipelineDone()
    {
        if (Volatile.Read(ref PipelineMicro) != 0) return;
        var elapsed = Stopwatch.GetElapsedTime(StartTimestamp).Ticks / 10; // ticks → µs
        Interlocked.CompareExchange(ref PipelineMicro, Math.Max(1, elapsed), 0);
    }

    public double ElapsedMs => Stopwatch.GetElapsedTime(StartTimestamp).TotalMilliseconds;
}

public sealed class FingerprintCounter
{
    public long Count;
    public long Micro;
}

/// Ambient measurement context: the current request scope, or the label of
/// the background worker currently running on this async flow.
public static class PerfContext
{
    private static readonly AsyncLocal<PerfRequestScope?> RequestScope = new();
    private static readonly AsyncLocal<string?> WorkerName = new();
    private static readonly AsyncLocal<bool> ExpectedFailures = new();

    public static PerfRequestScope? Request
    {
        get => RequestScope.Value;
        set => RequestScope.Value = value;
    }

    public static string? Worker
    {
        get => WorkerName.Value;
        set => WorkerName.Value = value;
    }

    /// v0.1.27 — set on a flow whose failures are an expected outcome (the
    /// toolbox's EXPLAIN attempts: many captured shapes cannot be planned
    /// generically). Exceptions and failed DB commands on that flow are not
    /// counted, so the monitor doesn't report its own probing as errors.
    /// Set it inside an async method: the value then ends with that call.
    public static bool ExpectedFailuresOnly
    {
        get => ExpectedFailures.Value;
        set => ExpectedFailures.Value = value;
    }

    /// Who issued the current query: the request ("GET /api/tickets/{id:guid}"),
    /// a worker ("worker:mail-polling") or neither ("other").
    public static string CurrentSource()
    {
        var request = RequestScope.Value;
        if (request is not null) return request.Source;
        var worker = WorkerName.Value;
        return worker is null ? "other" : "worker:" + worker;
    }

    /// The coarse bucket used for Basic-mode DB totals: request / worker / other.
    public static string CurrentSourceKind()
    {
        if (RequestScope.Value is not null) return "request";
        var worker = WorkerName.Value;
        return worker is null ? "other" : "worker:" + worker;
    }
}
