using System.Security.Cryptography;
using System.Text;

namespace Servicedesk.Api.Security;

/// v0.1.4 — partition-key logic for the global rate limiter, extracted from
/// Program.cs so it reads as one piece and can be unit-tested.
///
/// The global limiter is a <em>chain</em> of two partitioned limiters,
/// evaluated in this order:
///
///   1. <b>Per-session budget</b> — signed-in callers are keyed on a hash of
///      their session cookie, anonymous callers on their IP. This is the
///      knob the Settings UI shows (<c>Security.RateLimit.Global.PermitPerWindow</c>).
///   2. <b>Per-IP ceiling</b> — every request that passed its session budget
///      from one address, whatever cookies it presents, shares this bucket.
///      Sized as <c>PermitPerWindow × IpCeilingMultiplier</c>. It is the
///      abuse bound: a forged-cookie flood cannot buy itself unlimited budget.
///
/// Session-first is deliberate: a chain consumes a permit from every link
/// before the rejecting one, so a session-rejected request never eats into
/// the address's ceiling and one runaway session cannot 429 its colleagues.
///
/// Before v0.1.4 there was only a per-IP bucket, so six agents behind one
/// office NAT shared a single 240/min budget and normal ticket work tripped
/// it several times a day (diagnosed from the audit log, Sept 2026).
///
/// The cookie is hashed, never validated, at this layer — the limiter runs
/// before authentication on purpose (cheap rejection first). A random cookie
/// therefore gets its own per-session bucket, but stays inside the per-IP
/// ceiling, and the request still fails authentication downstream.
public static class GlobalRateLimitPartitioner
{
    public const string SystemPollKey = "system-poll";
    public const string InfraKey = "infra";

    /// Always-on lightweight polls that must never be throttled anywhere in
    /// the chain: the live server clock and the dashboard status pill. They
    /// drive globally-visible UI, are cheap, read-only, and carry no attack
    /// surface worth limiting.
    public static bool IsSystemPoll(PathString path)
    {
        var p = path.Value ?? string.Empty;
        return p.StartsWith("/api/system/time", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/api/system/health", StringComparison.OrdinalIgnoreCase);
    }

    /// Connection plumbing a page needs to (re)establish itself: the SignalR
    /// negotiate handshakes, the version probe and the maintenance banner.
    /// These are exempt from the per-session budget (a refresh while the
    /// budget is exhausted must still be able to reconnect its hubs) but do
    /// count toward the per-IP ceiling.
    public static bool IsInfra(HttpContext ctx)
    {
        var p = ctx.Request.Path.Value ?? string.Empty;
        if (p.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase)
            && p.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HttpMethods.IsGet(ctx.Request.Method)
            && (p.StartsWith("/api/system/version", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("/api/system/maintenance", StringComparison.OrdinalIgnoreCase));
    }

    /// Inline-image / attachment downloads (GET …/attachments/{id}). They run
    /// on their own generous bucket so an image-heavy ticket (150+ inline
    /// images, each a separate authenticated GET) doesn't exhaust the API
    /// budget and 429 the rest of the page.
    public static bool IsAttachmentGet(HttpContext ctx)
    {
        var p = ctx.Request.Path.Value ?? string.Empty;
        return HttpMethods.IsGet(ctx.Request.Method)
            && p.Contains("/attachments/", StringComparison.OrdinalIgnoreCase);
    }

    public static string IpKey(HttpContext ctx)
        => ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";

    /// Session-cookie hash for signed-in callers, IP for anonymous ones. The
    /// staff cookie wins when both are present (an admin in shadow-login
    /// carries both). Only a truncated SHA-256 of the cookie is kept as the
    /// partition key — the raw token never sits in the limiter's dictionary.
    public static string SessionOrIpKey(HttpContext ctx, string sessionCookieName, string portalCookieName)
    {
        var cookies = ctx.Request.Cookies;
        if (cookies.TryGetValue(sessionCookieName, out var staff) && !string.IsNullOrEmpty(staff))
        {
            return "s:" + HashCookie(staff);
        }

        if (cookies.TryGetValue(portalCookieName, out var portal) && !string.IsNullOrEmpty(portal))
        {
            return "p:" + HashCookie(portal);
        }

        return "ip:" + IpKey(ctx);
    }

    private static string HashCookie(string value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Convert.ToHexString(hash[..16]);
    }
}
