using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth;

namespace Servicedesk.Infrastructure.Security.IpBlocking;

/// Tees abuse-type audit events into the IP threat detector. Every rate-limit
/// rejection, CSRF rejection and failed sign-in already flows through
/// <see cref="IAuditLogger"/> with the client IP, so the detector sees them
/// without touching any call site. Feeding happens after the real write and
/// never throws — the audit row is what matters.
public sealed class IpSignalAuditDecorator : IAuditLogger
{
    private static readonly Dictionary<string, IpSignalKind> SignalByEvent = new(StringComparer.Ordinal)
    {
        // Literal mirrors AuditRateLimiterEvents.EventTypeRateLimited (Api).
        // rate_limited_csp_report is deliberately absent: browsers send those
        // on their own, it is not an abuse signal.
        ["rate_limited"] = IpSignalKind.RateLimited,
        [AuthEventTypes.CsrfRejected] = IpSignalKind.CsrfRejected,
        [AuthEventTypes.LoginFailed] = IpSignalKind.FailedLogin,
        [AuthEventTypes.LoginLockedOut] = IpSignalKind.FailedLogin,
        [AuthEventTypes.TwoFactorChallengeFailed] = IpSignalKind.FailedLogin,
        [AuthEventTypes.MicrosoftLoginRejectedUnknown] = IpSignalKind.FailedLogin,
        [AuthEventTypes.MicrosoftLoginRejectedDisabled] = IpSignalKind.FailedLogin,
        [AuthEventTypes.MicrosoftLoginRejectedCustomer] = IpSignalKind.FailedLogin,
        [AuthEventTypes.MicrosoftLoginRejectedInactive] = IpSignalKind.FailedLogin,
        [AuthEventTypes.MicrosoftLoginFailedCallback] = IpSignalKind.FailedLogin,
        ["portal.login.failed"] = IpSignalKind.FailedLogin,
        ["portal.login.locked_out"] = IpSignalKind.FailedLogin,
    };

    private readonly IAuditLogger _inner;
    private readonly IIpThreatDetector _detector;
    private readonly TimeProvider _clock;

    public IpSignalAuditDecorator(IAuditLogger inner, IIpThreatDetector detector, TimeProvider? clock = null)
    {
        _inner = inner;
        _detector = detector;
        _clock = clock ?? TimeProvider.System;
    }

    internal static bool TryMap(string? eventType, out IpSignalKind kind)
    {
        kind = default;
        return eventType is not null && SignalByEvent.TryGetValue(eventType, out kind);
    }

    public async Task LogAsync(AuditEvent evt, CancellationToken cancellationToken = default)
    {
        await _inner.LogAsync(evt, cancellationToken);

        if (evt?.ClientIp is null || !TryMap(evt.EventType, out var kind)) return;
        try
        {
            // A failed sign-in's Target is the typed username/e-mail — personal
            // data that has no place in block evidence. Only request paths
            // (rate-limit / CSRF targets) are kept as samples.
            var sample = kind == IpSignalKind.FailedLogin ? null : evt.Target;
            _detector.Record(evt.ClientIp, kind, sample, _clock.GetUtcNow().UtcDateTime);
        }
        catch
        {
            // Detection is best-effort; never let it fail an audited request.
        }
    }
}
