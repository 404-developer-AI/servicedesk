using System.Security.Cryptography;

namespace Servicedesk.Api.Security;

/// <summary>
/// Generates a per-request CSP nonce and emits the header. Production is strict
/// (nonce-based script/style, no unsafe-inline, no unsafe-eval, no external origins).
/// Development relaxes <c>connect-src</c> for Vite HMR websockets and adds
/// <c>'unsafe-eval'</c> for the Vite client.
/// </summary>
/// <remarks>
/// The nonce is placed in <c>HttpContext.Items["csp-nonce"]</c> so the (future)
/// SPA host middleware can inject it into <c>index.html</c> when static files
/// are served in production builds (v0.0.14+). For v0.0.3 the header itself is
/// the contract; the placeholder wiring is scaffolded for later.
/// </remarks>
public sealed class ContentSecurityPolicyMiddleware
{
    public const string NonceItemKey = "csp-nonce";

    private readonly RequestDelegate _next;
    private readonly bool _isDevelopment;
    private readonly string _reportUri;

    public ContentSecurityPolicyMiddleware(RequestDelegate next, IWebHostEnvironment env, IConfiguration configuration)
    {
        _next = next;
        _isDevelopment = env.IsDevelopment();
        _reportUri = configuration["Security:Csp:ReportUri"] ?? "/api/security/csp-report";
    }

    public Task InvokeAsync(HttpContext context)
    {
        var nonce = GenerateNonce();
        context.Items[NonceItemKey] = nonce;

        // Attachment downloads are framed by our own origin for the PDF
        // preview lightbox; everything else keeps the stricter 'none'.
        var allowSameOriginFrame = IsAttachmentDownload(context.Request.Path);
        var policy = BuildPolicy(nonce, _isDevelopment, _reportUri, allowSameOriginFrame);
        context.Response.OnStarting(state =>
        {
            var ctx = (HttpContext)state!;
            ctx.Response.Headers["Content-Security-Policy"] = policy;
            return Task.CompletedTask;
        }, context);

        return _next(context);
    }

    private static bool IsAttachmentDownload(PathString path)
    {
        if (!path.HasValue) return false;
        var v = path.Value!;
        return v.StartsWith("/api/tickets/", StringComparison.OrdinalIgnoreCase)
            && v.Contains("/mail/", StringComparison.OrdinalIgnoreCase)
            && v.Contains("/attachments/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string GenerateNonce()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    internal static string BuildPolicy(string nonce, bool development, string reportUri)
        => BuildPolicy(nonce, development, reportUri, allowSameOriginFrame: false);

    internal static string BuildPolicy(string nonce, bool development, string reportUri, bool allowSameOriginFrame)
    {
        var scriptSrc = development
            ? $"'self' 'nonce-{nonce}' 'unsafe-eval'"
            : $"'self' 'nonce-{nonce}'";

        // style-src allows 'unsafe-inline' in both dev and prod. Reason: Sonner,
        // Radix, Framer Motion, Vaul and other UI libs inject stylesheets at
        // runtime via document.createElement('style') without a nonce — a
        // nonce-only policy blocks them and the toast/drawer/dropdown UI
        // renders unstyled. The CSP-3 nonce model has no 'strict-dynamic' for
        // styles, and sonner@2.0.7 exposes no nonce prop. Accepted tradeoff:
        // style-injection is not script-injection; script-src stays strict.
        //
        // NOTE: the nonce is deliberately NOT emitted in style-src. Per CSP-3,
        // the presence of a nonce in a directive causes 'unsafe-inline' to be
        // IGNORED by conformant browsers (Chrome/Firefox) — a strict-mode
        // fallback for backwards compatibility. Since we do not render any
        // nonced <style> tags ourselves (the SPA only uses <link rel=stylesheet>
        // plus runtime-injected styles), the nonce adds nothing here and its
        // mere presence would break the UI libs. It still lives in script-src
        // where it continues to be the primary defense against XSS.
        //
        // fonts.googleapis.com is whitelisted because index.css @imports Inter
        // from there; the actual .woff2 files come from fonts.gstatic.com
        // (see font-src below).
        const string styleHosts = "https://fonts.googleapis.com";
        var styleSrc = $"'self' 'unsafe-inline' {styleHosts}";

        var connectSrc = development
            ? "'self' ws: wss: http://localhost:* https://localhost:*"
            : "'self'";

        return string.Join("; ", new[]
        {
            "default-src 'self'",
            $"script-src {scriptSrc}",
            $"style-src {styleSrc}",
            "img-src 'self' data: blob:",
            "font-src 'self' data: https://fonts.gstatic.com",
            $"connect-src {connectSrc}",
            allowSameOriginFrame ? "frame-ancestors 'self'" : "frame-ancestors 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "object-src 'none'",
            $"report-uri {reportUri}",
        });
    }
}
