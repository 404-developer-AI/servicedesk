using Servicedesk.Infrastructure.Security.IpBlocking;

namespace Servicedesk.Api.Security;

/// First stop after forwarded-headers resolution (so RemoteIpAddress is the
/// real client, not nginx). A blocked IP gets a bare 403 before routing,
/// static files, logging or auth spend anything on it — and is never audited
/// per request (a scanner would flood the log); hits are counted in memory
/// and flushed to the rule row. Non-blocked requests for scanner-only paths
/// are reported to the detector.
///
/// Break-glass: `Security:IpBlocking:Disabled=true` (env
/// SERVICEDESK_Security__IpBlocking__Disabled=true + restart) turns the whole
/// middleware into a pass-through without touching the database.
public sealed class IpBlockMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IIpBlockList _list;
    private readonly IIpThreatDetector _detector;
    private readonly TimeProvider _clock;
    private readonly bool _disabled;

    public IpBlockMiddleware(RequestDelegate next, IIpBlockList list, IIpThreatDetector detector,
        IConfiguration configuration, TimeProvider? clock = null)
    {
        _next = next;
        _list = list;
        _detector = detector;
        _clock = clock ?? TimeProvider.System;
        _disabled = configuration.GetValue<bool>("Security:IpBlocking:Disabled");
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (_disabled) return _next(context);

        var address = context.Connection.RemoteIpAddress;
        if (address is null || _list.IsProtected(address)) return _next(context);

        var now = _clock.GetUtcNow().UtcDateTime;
        var ip = IpAddressText.Normalize(address);

        if (_list.IsBlocked(ip, now))
        {
            _list.RecordBlockedHit(ip, now);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            return context.Response.WriteAsync("Forbidden");
        }

        var path = context.Request.Path.Value;
        if (_detector.IsScannerPath(path))
        {
            _detector.Record(address, IpSignalKind.ScannerPath, path, now);
        }

        return _next(context);
    }
}
