using Microsoft.Extensions.Primitives;

namespace Servicedesk.Api.Security;

/// Writes a fixed set of security headers on every response. Kept separate from
/// the CSP middleware so CSP (dynamic, per-request nonce) can live and evolve
/// without touching the static ones.
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        // Attachment responses must be framable by our own origin so the
        // PDF/image preview lightbox can load them in an <iframe>. Anywhere
        // else stays DENY to prevent clickjacking.
        var allowSameOriginFrame = IsAttachmentDownload(context.Request.Path);

        context.Response.OnStarting(state =>
        {
            var ctx = (HttpContext)state!;
            var headers = ctx.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = allowSameOriginFrame ? "SAMEORIGIN" : "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Cross-Origin-Embedder-Policy"] = "require-corp";
            headers.Remove("Server");
            headers.Remove("X-Powered-By");
            return Task.CompletedTask;
        }, context);

        return _next(context);
    }

    private static bool IsAttachmentDownload(PathString path)
    {
        // Matches both attachment surfaces so the preview-lightbox iframe
        // can load either:
        //   /api/tickets/{ticketId}/attachments/{attachmentId}
        //   /api/tickets/{ticketId}/mail/{mailId}/attachments/{attachmentId}
        if (!path.HasValue) return false;
        var v = path.Value!;
        return v.StartsWith("/api/tickets/", StringComparison.OrdinalIgnoreCase)
            && v.Contains("/attachments/", StringComparison.OrdinalIgnoreCase);
    }
}
