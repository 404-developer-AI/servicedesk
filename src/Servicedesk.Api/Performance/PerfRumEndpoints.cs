using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Performance;

namespace Servicedesk.Api.Performance;

/// Real-user monitoring intake from the staff SPA.
///
/// <para>Hard limits: signed-in agents/admins only (the customer portal never
/// reports), 32 KB body, at most 300 items per batch, metric names from a
/// fixed whitelist, values range-checked, routes reduced to templates and
/// scrubbed of id-like segments, and a dedicated per-session rate-limit
/// policy. The batch is folded into the in-memory window — timestamps come
/// from the server clock, the client only reports durations.</para>
public static partial class PerfRumEndpoints
{
    public const int MaxBodyBytes = 32 * 1024;
    public const int MaxItems = 300;

    private static readonly HashSet<string> Metrics = new(StringComparer.Ordinal)
    {
        // Core Web Vitals
        "lcp", "inp", "cls", "fcp", "ttfb",
        // Navigation timing of the initial load
        "nav_dns", "nav_tcp", "nav_tls", "nav_ttfb", "nav_download", "nav_dom", "nav_load",
        // API calls (detail = API route): browser total, server part (Server-Timing), network part
        "api_total", "api_server", "api_network",
        // Main-thread blocking
        "long_task", "loaf",
        // SPA navigation
        "route_change", "view", "screen_api_calls", "screen_api_kb",
        // Long-lived tabs
        "js_heap_mb",
        // Realtime + transport
        "signalr_reconnect", "protocol",
    };

    private static readonly HashSet<string> Devices = new(StringComparer.Ordinal) { "desktop", "mobile", "tablet" };
    private static readonly HashSet<string> Connections = new(StringComparer.Ordinal) { "slow-2g", "2g", "3g", "4g" };

    public sealed class RumPayload
    {
        public string? Device { get; set; }
        public string? Conn { get; set; }
        public List<RumItem>? Items { get; set; }
    }

    public sealed class RumItem
    {
        public string? M { get; set; }
        public string? R { get; set; }
        public string? D { get; set; }
        public double V { get; set; }
    }

    public static IEndpointRouteBuilder MapPerfRumEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/perf")
            .WithTags("Performance")
            .RequireAuthorization(AuthorizationPolicies.RequireAgent);

        // What the SPA needs to decide whether (and how much) to report.
        group.MapGet("/config", (IPerfSettings perf) =>
        {
            var options = perf.Options;
            var enabled = perf.IsEnabled(PerfCollector.Frontend);
            var diagnose = enabled && perf.Level == PerfLevel.Diagnose;
            return Results.Ok(new
            {
                enabled,
                samplePercent = diagnose ? 100 : options.RumSamplePercent,
                diagnose,
                flushSeconds = 30,
            });
        }).WithName("GetPerfConfig").WithOpenApi();

        group.MapPost("/rum", ([FromBody] RumPayload payload, IPerfSettings perf, PerfRecorder recorder) =>
        {
            if (!perf.IsEnabled(PerfCollector.Frontend)) return Results.NoContent();
            if (payload.Items is null || payload.Items.Count == 0) return Results.NoContent();
            if (payload.Items.Count > MaxItems) return Results.BadRequest(new { error = "too_many_items" });

            var accepted = Ingest(payload, recorder.Current);
            return Results.Ok(new { accepted });
        })
        .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
        .RequireRateLimiting("perf-rum")
        .WithName("PostPerfRum").WithOpenApi();

        return app;
    }

    /// Validates and records a batch; returns the number of accepted items.
    internal static int Ingest(RumPayload payload, PerfWindow window)
    {
        var device = payload.Device is { } dv && Devices.Contains(dv) ? dv : "other";
        var connection = payload.Conn is { } cn && Connections.Contains(cn) ? cn : "unknown";
        var accepted = 0;
        foreach (var item in payload.Items!)
        {
            if (item.M is null || !Metrics.Contains(item.M)) continue;
            if (!double.IsFinite(item.V) || item.V < 0 || item.V > 600_000) continue;
            var route = PerfRedactor.Route(item.R) ?? "unknown";
            var detail = Detail(item.M, item.D);
            if (detail is null) continue;
            // CLS is unitless (0..~1): stored ×1000 so the shared histogram fits.
            var value = item.M == "cls" ? Math.Min(item.V, 100) * 1000 : item.V;
            window.RumFor(new RumKey(route, item.M, detail, device, connection)).Record(value);
            accepted++;
        }
        return accepted;
    }

    private static string? Detail(string metric, string? raw)
    {
        switch (metric)
        {
            case "api_total":
            case "api_server":
            case "api_network":
                var api = PerfRedactor.Route(raw);
                return api is not null && api.StartsWith("/api/", StringComparison.Ordinal) ? api : null;
            case "loaf":
            case "long_task":
                if (string.IsNullOrEmpty(raw)) return "";
                return ScriptName().IsMatch(raw) ? raw : "other";
            case "protocol":
                return raw is not null && Protocol().IsMatch(raw) ? raw : "other";
            default:
                return "";
        }
    }

    // A chunk file name or "function@chunk" — never a URL with a query string.
    [GeneratedRegex(@"^[A-Za-z0-9_.\-@$/]{1,80}$")]
    private static partial Regex ScriptName();

    [GeneratedRegex(@"^(h2|h3|http/1\.0|http/1\.1|http/2|http/3)$")]
    private static partial Regex Protocol();
}
