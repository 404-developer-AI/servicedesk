using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;

namespace Servicedesk.Infrastructure.Performance;

/// Latest values of the gauges the runtime sampler reads every tick.
public sealed class PerfGauges
{
    public long PoolIdle;
    public long PoolUsed;
    public long PoolMax;
    public long PoolPending;
    public long PoolTimeouts;
    public long KestrelActive;
    public long KestrelQueued;
    public long KestrelRejected;
    public long SignalRConnections;
}

/// Hooks the framework's own instrumentation instead of wrapping our code:
///
/// <list type="bullet">
/// <item><b>Npgsql meter</b> — every command's duration (Basic: per-request
///   query count and DB time, plus per-source totals), the connection-pool
///   gauges and pool timeouts. Covers every Dapper call without touching a
///   repository.</item>
/// <item><b>Npgsql ActivitySource</b> — Diagnose only: the statement text per
///   command → fingerprint, per-request N+1 counting and the calling method.
///   Sampling returns <c>None</c> outside Diagnose, so no Activity is even
///   allocated in Basic.</item>
/// <item><b>System.Net.Http meter</b> — every outgoing HTTP call (Graph,
///   Adsolut, TRMM…) per host, whichever HttpClient made it.</item>
/// <item><b>Kestrel / SignalR meters</b> — connection gauges.</item>
/// </list>
///
/// The callbacks run synchronously in the caller's execution context, which
/// is what lets them add to the current request's <see cref="PerfRequestScope"/>.
/// Every callback is wrapped so a monitoring bug can never fail the query or
/// the HTTP call it observes.
public sealed class PerfInstrumentation : IHostedService, IDisposable
{
    private enum Instrument
    {
        DbCommandDuration,
        DbCommandFailed,
        DbConnectionUsage,
        DbConnectionMax,
        DbPendingRequests,
        DbConnectionTimeouts,
        HttpClientDuration,
        KestrelActive,
        KestrelQueued,
        KestrelRejected,
        SignalRActive,
    }

    private const string CallerProperty = "sd.perf.caller";
    private const int CallerCapturesPerSecond = 50;

    private readonly PerfRecorder _recorder;
    private readonly IPerfSettings _settings;
    private MeterListener? _meters;
    private ActivityListener? _activities;

    // Observable accumulators (reset before each poll).
    private long _pollIdle;
    private long _pollUsed;
    private long _pollMax;

    private long _captureSecond;
    private int _capturesThisSecond;

    public PerfInstrumentation(PerfRecorder recorder, IPerfSettings settings)
    {
        _recorder = recorder;
        _settings = settings;
    }

    public PerfGauges Gauges { get; } = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    internal void Start()
    {
        if (_meters is not null) return;

        _meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                var id = Classify(instrument);
                if (id is not null) listener.EnableMeasurementEvents(instrument, id.Value);
            },
        };
        _meters.SetMeasurementEventCallback<double>(OnDouble);
        _meters.SetMeasurementEventCallback<int>((i, v, t, s) => OnLong(i, v, t, s));
        _meters.SetMeasurementEventCallback<long>(OnLong);
        _meters.Start();

        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                _settings.IsDiagnose(PerfCollector.Database)
                    ? ActivitySamplingResult.AllData
                    : ActivitySamplingResult.None,
            ActivityStarted = OnActivityStarted,
            ActivityStopped = OnActivityStopped,
        };
        ActivitySource.AddActivityListener(_activities);
    }

    /// Pulls the observable instruments (pool usage) into <see cref="Gauges"/>.
    public void PollObservables()
    {
        if (_meters is null) return;
        Interlocked.Exchange(ref _pollIdle, 0);
        Interlocked.Exchange(ref _pollUsed, 0);
        Interlocked.Exchange(ref _pollMax, 0);
        _meters.RecordObservableInstruments();
        Volatile.Write(ref Gauges.PoolIdle, Interlocked.Read(ref _pollIdle));
        Volatile.Write(ref Gauges.PoolUsed, Interlocked.Read(ref _pollUsed));
        Volatile.Write(ref Gauges.PoolMax, Interlocked.Read(ref _pollMax));
    }

    private static Instrument? Classify(System.Diagnostics.Metrics.Instrument instrument)
    {
        return (instrument.Meter.Name, instrument.Name) switch
        {
            ("Npgsql", "db.client.commands.duration") => Instrument.DbCommandDuration,
            ("Npgsql", "db.client.commands.failed") => Instrument.DbCommandFailed,
            ("Npgsql", "db.client.connections.usage") => Instrument.DbConnectionUsage,
            ("Npgsql", "db.client.connections.max") => Instrument.DbConnectionMax,
            ("Npgsql", "db.client.connections.pending_requests") => Instrument.DbPendingRequests,
            ("Npgsql", "db.client.connections.timeouts") => Instrument.DbConnectionTimeouts,
            ("System.Net.Http", "http.client.request.duration") => Instrument.HttpClientDuration,
            ("Microsoft.AspNetCore.Server.Kestrel", "kestrel.active_connections") => Instrument.KestrelActive,
            ("Microsoft.AspNetCore.Server.Kestrel", "kestrel.queued_connections") => Instrument.KestrelQueued,
            ("Microsoft.AspNetCore.Server.Kestrel", "kestrel.rejected_connections") => Instrument.KestrelRejected,
            ("Microsoft.AspNetCore.Http.Connections", "signalr.server.active_connections") => Instrument.SignalRActive,
            _ => null,
        };
    }

    private void OnDouble(System.Diagnostics.Metrics.Instrument instrument, double value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (state is not Instrument id) return;
        var start = Stopwatch.GetTimestamp();
        try
        {
            switch (id)
            {
                case Instrument.DbCommandDuration:
                    OnDbCommand(value * 1000);
                    break;
                case Instrument.HttpClientDuration:
                    OnHttpClient(value * 1000, tags);
                    break;
            }
        }
        catch
        {
            // Never let monitoring fail the observed operation.
        }
        finally
        {
            _recorder.AddOverhead(Stopwatch.GetTimestamp() - start);
        }
    }

    private void OnLong(System.Diagnostics.Metrics.Instrument instrument, long value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (state is not Instrument id) return;
        try
        {
            switch (id)
            {
                case Instrument.DbConnectionUsage:
                    var used = false;
                    foreach (var tag in tags)
                    {
                        if (tag.Key == "state" && tag.Value is string st) used = st == "used";
                    }
                    if (used) Interlocked.Add(ref _pollUsed, value);
                    else Interlocked.Add(ref _pollIdle, value);
                    break;
                case Instrument.DbConnectionMax:
                    Interlocked.Add(ref _pollMax, value);
                    break;
                case Instrument.DbPendingRequests:
                    Interlocked.Add(ref Gauges.PoolPending, value);
                    break;
                case Instrument.DbConnectionTimeouts:
                    Interlocked.Add(ref Gauges.PoolTimeouts, value);
                    break;
                case Instrument.DbCommandFailed:
                    if (_settings.IsEnabled(PerfCollector.Database) && !PerfContext.ExpectedFailuresOnly)
                    {
                        var agg = _recorder.Current.SpanFor(new SpanKey("db", PerfContext.CurrentSourceKind(), ""));
                        Interlocked.Add(ref agg.ErrorCount, value);
                    }
                    break;
                case Instrument.KestrelActive:
                    Interlocked.Add(ref Gauges.KestrelActive, value);
                    break;
                case Instrument.KestrelQueued:
                    Interlocked.Add(ref Gauges.KestrelQueued, value);
                    break;
                case Instrument.KestrelRejected:
                    Interlocked.Add(ref Gauges.KestrelRejected, value);
                    break;
                case Instrument.SignalRActive:
                    Interlocked.Add(ref Gauges.SignalRConnections, value);
                    break;
            }
        }
        catch
        {
            // Never let monitoring fail the observed operation.
        }
    }

    private void OnDbCommand(double ms)
    {
        if (!_settings.IsEnabled(PerfCollector.Database)) return;
        PerfContext.Request?.AddDb(ms);
        _recorder.Current.SpanFor(new SpanKey("db", PerfContext.CurrentSourceKind(), "")).Record(ms);
    }

    private void OnHttpClient(double ms, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (!_settings.IsEnabled(PerfCollector.External)) return;
        string host = "unknown", method = "GET";
        var status = 0;
        var failed = false;
        foreach (var tag in tags)
        {
            switch (tag.Key)
            {
                case "server.address": host = tag.Value as string ?? host; break;
                case "http.request.method": method = tag.Value as string ?? method; break;
                case "http.response.status_code": status = tag.Value is int code ? code : 0; break;
                case "error.type": failed = true; break;
            }
        }
        PerfContext.Request?.AddExternal(ms);
        var statusClass = status switch
        {
            0 => "error",
            429 => "429",
            >= 500 => "5xx",
            >= 400 => "4xx",
            >= 300 => "3xx",
            _ => "2xx",
        };
        var error = failed || status == 0 || status == 429 || status >= 500;
        _recorder.Current.SpanFor(new SpanKey("external", host.ToLowerInvariant(), method + " " + statusClass)).Record(ms, error);
    }

    private void OnActivityStarted(System.Diagnostics.Activity activity)
    {
        // Capture the calling method while the repository frame is still on
        // the synchronous stack (it is gone by the time the command ends).
        // Rate-limited: a stack walk is the only non-trivial cost here.
        var start = Stopwatch.GetTimestamp();
        try
        {
            if (!TryTakeCaptureBudget()) return;
            var caller = CallerFromStack(new StackTrace(2, false));
            if (caller is not null) activity.SetCustomProperty(CallerProperty, caller);
        }
        catch
        {
            // ignore
        }
        finally
        {
            _recorder.AddOverhead(Stopwatch.GetTimestamp() - start);
        }
    }

    private void OnActivityStopped(System.Diagnostics.Activity activity)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            if (activity.GetTagItem("db.statement") is not string statement || statement.Length == 0) return;
            var (fingerprint, normalized) = SqlFingerprint.Get(statement);
            var ms = activity.Duration.TotalMilliseconds;
            var error = activity.Status == ActivityStatusCode.Error
                        || activity.GetTagItem("otel.status_code") as string == "ERROR";
            var window = _recorder.Current;
            window.DbFor(new DbKey(fingerprint, PerfContext.CurrentSource())).Record(ms, error);

            var caller = activity.GetCustomProperty(CallerProperty) as string;
            if (!window.Fingerprints.TryGetValue(fingerprint, out var info))
            {
                window.Fingerprints.TryAdd(fingerprint, new FingerprintInfo(normalized, caller));
            }
            else if (info.Caller is null && caller is not null)
            {
                window.Fingerprints.TryUpdate(fingerprint, info with { Caller = caller }, info);
            }

            var scope = PerfContext.Request;
            if (scope?.Fingerprints is { } perRequest)
            {
                var counter = perRequest.GetOrAdd(fingerprint, static _ => new FingerprintCounter());
                Interlocked.Increment(ref counter.Count);
                Interlocked.Add(ref counter.Micro, (long)(ms * 1000));
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            _recorder.AddOverhead(Stopwatch.GetTimestamp() - start);
        }
    }

    private bool TryTakeCaptureBudget()
    {
        var second = Environment.TickCount64 / 1000;
        if (Interlocked.Exchange(ref _captureSecond, second) != second)
        {
            Interlocked.Exchange(ref _capturesThisSecond, 0);
        }
        return Interlocked.Increment(ref _capturesThisSecond) <= CallerCapturesPerSecond;
    }

    /// First frame in our own code outside the monitoring namespace, as
    /// "Type.Method" (async state machines and lambdas folded back onto the
    /// method that declared them).
    internal static string? CallerFromStack(StackTrace trace)
    {
        foreach (var frame in trace.GetFrames())
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            if (type is null) continue;

            var name = method!.Name;
            var owner = type;
            // <MethodName>d__12 (async) / <>c__DisplayClass (closure) / <>c (lambda cache)
            while (owner.Name.StartsWith('<') && owner.DeclaringType is not null)
            {
                var close = owner.Name.IndexOf('>');
                if (close > 1) name = owner.Name[1..close];
                owner = owner.DeclaringType;
            }
            if (name.StartsWith('<'))
            {
                var close = name.IndexOf('>');
                if (close > 1) name = name[1..close];
            }

            var full = owner.FullName;
            if (full is null || !full.StartsWith("Servicedesk.", StringComparison.Ordinal)) continue;
            if (full.StartsWith("Servicedesk.Infrastructure.Performance", StringComparison.Ordinal)) continue;
            if (full.StartsWith("Servicedesk.Api.Performance", StringComparison.Ordinal)) continue;
            return owner.Name + "." + name;
        }
        return null;
    }

    public void Dispose()
    {
        _activities?.Dispose();
        _activities = null;
        _meters?.Dispose();
        _meters = null;
    }
}
