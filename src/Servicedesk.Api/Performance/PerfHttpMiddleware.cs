using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Performance;

namespace Servicedesk.Api.Performance;

/// Measures every routed request (placed right after UseRouting so the
/// route template is known) and splits its time into pipeline (rate
/// limiter, auth, CSRF, version gate), database, external HTTP and own code.
///
/// <para>Labels are always the route <em>template</em> ("/api/tickets/{id:guid}"),
/// never the concrete URL: no ids or values ever reach a metric row, and the
/// key set stays small. Requests without an endpoint are "unmatched"; the SPA
/// fallback is "spa-fallback"; SignalR traffic (/hubs) is measured by the
/// hub filter instead; the monitor's own endpoints are not measured at all,
/// so looking at the dashboard never skews it.</para>
///
/// <para>Server-Timing: added for signed-in staff only (Agent/Admin with a
/// completed MFA step), never on sign-in endpoints, the customer portal or
/// anonymous calls — response timing on an auth endpoint is an
/// account-enumeration side channel, and customers have no use for it.</para>
public sealed class PerfHttpMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PerfRecorder _recorder;
    private readonly IPerfSettings _settings;

    public PerfHttpMiddleware(RequestDelegate next, PerfRecorder recorder, IPerfSettings settings)
    {
        _next = next;
        _recorder = recorder;
        _settings = settings;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsExcluded(context.Request.Path))
        {
            // The dashboard's own queries are attributed to "perf-dashboard"
            // instead of blending into "other" — the monitor shows its cost.
            if (context.Request.Path.StartsWithSegments("/api/admin/performance", StringComparison.OrdinalIgnoreCase))
            {
                PerfContext.Worker = "perf-dashboard";
            }
            await _next(context);
            return;
        }
        if (!_settings.IsEnabled(PerfCollector.Http))
        {
            await _next(context);
            return;
        }

        var overheadStart = Stopwatch.GetTimestamp();
        var start = overheadStart;
        var method = context.Request.Method;
        var route = RouteLabel(context);
        var diagnose = _settings.Level == PerfLevel.Diagnose;
        var scope = new PerfRequestScope(method, route, diagnose, start);
        PerfContext.Request = scope;
        var gcBefore = GC.CollectionCount(0);
        _recorder.RequestStarted();

        var counting = CountingResponseBody.Install(context);
        var options = _settings.Options;
        if (options.ServerTimingEnabled && route.StartsWith("/api/", StringComparison.Ordinal) && !IsSensitive(context.Request.Path))
        {
            context.Response.OnStarting(static state =>
            {
                var (ctx, s) = ((HttpContext, PerfRequestScope))state;
                if (IsStaff(ctx.User)) ctx.Response.Headers.Append("Server-Timing", ServerTimingValue(s));
                return Task.CompletedTask;
            }, (context, scope));
        }
        _recorder.AddOverhead(Stopwatch.GetTimestamp() - overheadStart);

        var failed = false;
        try
        {
            await _next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            var endStart = Stopwatch.GetTimestamp();
            try
            {
                Record(context, scope, counting, failed, gcBefore, options);
            }
            catch
            {
                // Monitoring must never turn a good response into an error.
            }
            finally
            {
                _recorder.RequestEnded();
                PerfContext.Request = null;
                _recorder.AddOverhead(Stopwatch.GetTimestamp() - endStart);
            }
        }
    }

    private void Record(HttpContext context, PerfRequestScope scope, CountingResponseBody? counting, bool failed,
        int gcBefore, PerfOptions options)
    {
        var totalMs = scope.ElapsedMs;
        var status = failed && !context.Response.HasStarted ? 500 : context.Response.StatusCode;
        var statusClass = StatusClass(status);
        var dbMicro = Interlocked.Read(ref scope.DbMicro);
        var extMicro = Interlocked.Read(ref scope.ExtMicro);
        var dbCount = Interlocked.Read(ref scope.DbCount);
        var pipelineMicro = Interlocked.Read(ref scope.PipelineMicro);
        if (pipelineMicro == 0) pipelineMicro = (long)(totalMs * 1000); // short-circuited before the endpoint
        var bytes = counting?.BytesWritten ?? context.Response.ContentLength ?? 0;

        var window = _recorder.Current;
        var agg = window.HttpFor(new HttpKey(scope.Method, scope.Route, statusClass));
        agg.Record(totalMs, status >= 500);
        agg.AddExtra(PerfWindow.HttpDbCount, dbCount);
        agg.AddExtra(PerfWindow.HttpDbMicro, dbMicro);
        agg.AddExtra(PerfWindow.HttpExtMicro, extMicro);
        agg.AddExtra(PerfWindow.HttpPipelineMicro, pipelineMicro);
        agg.AddExtra(PerfWindow.HttpBytes, bytes);
        agg.RecordSecond(bytes / 1024.0);

        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId is not null)
        {
            var hash = userId.GetHashCode(StringComparison.Ordinal);
            agg.AddUser(hash);
            window.AddActiveUser(hash);
        }

        // N+1: the same query shape many times inside this one request.
        List<object>? nPlusOne = null;
        if (scope.Fingerprints is { IsEmpty: false } fingerprints)
        {
            foreach (var (fingerprint, counter) in fingerprints)
            {
                var n = Interlocked.Read(ref counter.Count);
                if (n < options.NPlusOneThreshold) continue;
                window.NPlusOneFor(new NPlusOneKey(scope.Source, fingerprint)).Record(n);
                (nPlusOne ??= new List<object>()).Add(new { fingerprint, count = n });
            }
        }

        if (totalMs >= options.SlowRequestThresholdMs)
        {
            object? breakdown = null;
            if (scope.Fingerprints is { IsEmpty: false } fps)
            {
                breakdown = new
                {
                    queries = fps
                        .Select(kv => new
                        {
                            fingerprint = kv.Key,
                            count = Interlocked.Read(ref kv.Value.Count),
                            ms = Math.Round(Interlocked.Read(ref kv.Value.Micro) / 1000.0, 2),
                        })
                        .OrderByDescending(q => q.ms)
                        .Take(15)
                        .ToArray(),
                    nPlusOne,
                };
            }
            window.AddSlowRequest(new SlowRequestRecord(
                _settings.Time.GetUtcNow(),
                scope.Method,
                scope.Route,
                status,
                Math.Round(totalMs, 2),
                Math.Round(dbMicro / 1000.0, 2),
                dbCount,
                Math.Round(extMicro / 1000.0, 2),
                Math.Round(pipelineMicro / 1000.0, 2),
                GC.CollectionCount(0) - gcBefore,
                bytes,
                PerfWriter.SerializeBreakdown(breakdown),
                context.TraceIdentifier));
        }
    }

    internal static string ServerTimingValue(PerfRequestScope scope)
    {
        var total = scope.ElapsedMs;
        var db = Interlocked.Read(ref scope.DbMicro) / 1000.0;
        var ext = Interlocked.Read(ref scope.ExtMicro) / 1000.0;
        var app = Math.Max(0, total - db - ext);
        return string.Create(CultureInfo.InvariantCulture,
            $"total;dur={total:0.0}, db;dur={db:0.0}, ext;dur={ext:0.0}, app;dur={app:0.0}");
    }

    internal static bool IsStaff(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return false;
        if (!(user.IsInRole("Admin") || user.IsInRole("Agent"))) return false;
        var amr = user.FindFirst(SessionAuthenticationHandler.AmrClaimType)?.Value;
        return amr is not null
               && amr != SessionAuthenticationHandler.AmrPending
               && amr != SessionAuthenticationHandler.AmrImpersonated;
    }

    /// Paths that never get a Server-Timing header: everything involved in
    /// signing in, and the whole customer-portal surface.
    internal static bool IsSensitive(PathString path) =>
        path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/portal", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/public", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/intake-forms", StringComparison.OrdinalIgnoreCase);

    /// Never measured: SignalR (own collector), the monitor itself.
    internal static bool IsExcluded(PathString path) =>
        path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/admin/performance", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/perf", StringComparison.OrdinalIgnoreCase);

    internal static string StatusClass(int status) => status switch
    {
        429 => "429",
        >= 500 => "5xx",
        >= 400 => "4xx",
        >= 300 => "3xx",
        _ => "2xx",
    };

    internal static string RouteLabel(HttpContext context)
    {
        if (context.GetEndpoint() is not RouteEndpoint endpoint) return "unmatched";
        var raw = endpoint.RoutePattern.RawText;
        if (string.IsNullOrEmpty(raw)) return "unmatched";
        if (raw.Contains("{*path", StringComparison.Ordinal)) return "spa-fallback";
        return NormalizeTemplate(raw);
    }

    internal static string NormalizeTemplate(string raw)
    {
        var t = raw.StartsWith('/') ? raw : "/" + raw;
        if (t.Length > 1 && t.EndsWith('/')) t = t.TrimEnd('/');
        return t.Length > 200 ? t[..200] : t;
    }
}

/// Marks the end of the middleware pipeline (placed just before the
/// endpoints run) so "pipeline" time — limiter, authentication, CSRF and
/// the version gate — can be told apart from the handler itself.
public sealed class PerfPipelineMarkMiddleware
{
    private readonly RequestDelegate _next;

    public PerfPipelineMarkMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        PerfContext.Request?.MarkPipelineDone();
        return _next(context);
    }
}
