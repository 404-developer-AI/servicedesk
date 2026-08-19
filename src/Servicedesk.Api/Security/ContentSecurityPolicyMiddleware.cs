using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Servicedesk.Api.Security;

/// <summary>
/// Generates a per-request CSP nonce and emits the header. Production is strict
/// (nonce-based script/style, no unsafe-inline, no unsafe-eval, no external origins).
/// Development relaxes <c>connect-src</c> for Vite HMR websockets and adds
/// <c>'unsafe-eval'</c> for the Vite client.
/// </summary>
/// <remarks>
/// The nonce is placed in <c>HttpContext.Items["csp-nonce"]</c> for anything
/// rendered server-side. The SPA's <c>index.html</c> is served as a verbatim
/// static file, so its inline scripts are allowed by SHA-256 hash instead
/// (computed once at startup from the built file, v0.0.92) — nonce injection
/// into the static HTML was scaffolded early on but never built, which left
/// the inline theme bootstrap blocked in production.
/// </remarks>
public sealed class ContentSecurityPolicyMiddleware
{
    public const string NonceItemKey = "csp-nonce";

    private readonly RequestDelegate _next;
    private readonly bool _isDevelopment;
    private readonly string _reportUri;
    private readonly IReadOnlyList<string> _inlineScriptHashes;

    public ContentSecurityPolicyMiddleware(RequestDelegate next, IWebHostEnvironment env, IConfiguration configuration)
    {
        _next = next;
        _isDevelopment = env.IsDevelopment();
        _reportUri = configuration["Security:Csp:ReportUri"] ?? "/api/security/csp-report";
        _inlineScriptHashes = LoadIndexHtmlScriptHashes(env);
    }

    public Task InvokeAsync(HttpContext context)
    {
        var nonce = GenerateNonce();
        context.Items[NonceItemKey] = nonce;

        // Attachment downloads are framed by our own origin for the PDF
        // preview lightbox; everything else keeps the stricter 'none'.
        var allowSameOriginFrame = IsAttachmentDownload(context.Request.Path);
        // Customer-portal pages (SPA documents under /portal) additionally
        // allow the Cloudflare Turnstile script + challenge frame. Scoped to
        // the document response of those routes only: an agent page never
        // carries the relaxation, and the CSP is per-document so a portal
        // document loaded at /portal/* keeps it across client-side navigation
        // within the portal.
        var portalPage = IsPortalPage(context.Request);
        var policy = BuildPolicy(nonce, _isDevelopment, _reportUri, allowSameOriginFrame, _inlineScriptHashes, portalPage);
        context.Response.OnStarting(state =>
        {
            var ctx = (HttpContext)state!;
            ctx.Response.Headers["Content-Security-Policy"] = policy;
            return Task.CompletedTask;
        }, context);

        return _next(context);
    }

    // Matches every ticket attachment download — both inbound-mail attachments
    // (/api/tickets/{id}/mail/{mailId}/attachments/{id}) and user-uploaded
    // note/reply attachments (/api/tickets/{id}/attachments/{id}). Both are
    // framed by our own origin in the PDF preview lightbox, so both need
    // frame-ancestors 'self'. Kept in lock-step with the identical check in
    // SecurityHeadersMiddleware (which sets X-Frame-Options: SAMEORIGIN);
    // before, this required '/mail/' too, so uploaded PDFs were refused
    // ("frame-ancestors 'none'") while images (rendered via <img>) worked.
    private static bool IsAttachmentDownload(PathString path)
    {
        if (!path.HasValue) return false;
        var v = path.Value!;
        return v.StartsWith("/api/tickets/", StringComparison.OrdinalIgnoreCase)
            && v.Contains("/attachments/", StringComparison.OrdinalIgnoreCase);
    }

    /// True for a GET of a customer-portal SPA document (/portal or /portal/…),
    /// never for API calls. Kept in lock-step with SecurityHeadersMiddleware.
    internal static bool IsPortalPage(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method)) return false;
        var path = request.Path;
        if (!path.HasValue) return false;
        var v = path.Value!;
        return v.Equals("/portal", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("/portal/", StringComparison.OrdinalIgnoreCase);
    }

    /// Origin that serves the Turnstile widget script and challenge frame.
    public const string TurnstileOrigin = "https://challenges.cloudflare.com";

    internal static string GenerateNonce()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Hashes the inline scripts of the SPA's served <c>index.html</c> so
    /// script-src can allow exactly those scripts and nothing else. The SPA
    /// host serves index.html as a verbatim static file (no per-request nonce
    /// injection), so the anti-FOUC theme bootstrap in its head was blocked by
    /// the nonce-only policy on every production page load — breaking the dark
    /// theme's first paint and flooding /api/security/csp-report (v0.0.92).
    /// Hashes are computed once at startup from the built file; the file only
    /// changes on deploy, which restarts the app.
    /// </summary>
    private static IReadOnlyList<string> LoadIndexHtmlScriptHashes(IWebHostEnvironment env)
    {
        try
        {
            var webRoot = env.WebRootPath;
            if (string.IsNullOrEmpty(webRoot)) return Array.Empty<string>();
            var indexPath = Path.Combine(webRoot, "index.html");
            if (!File.Exists(indexPath)) return Array.Empty<string>();
            return ComputeInlineScriptHashes(File.ReadAllText(indexPath));
        }
        catch
        {
            // A missing/unreadable index.html (dev runs serve the SPA from the
            // Vite dev server) simply means no inline scripts to allow.
            return Array.Empty<string>();
        }
    }

    /// Extracts every inline (src-less) script body and returns its CSP-3
    /// source token: base64(SHA-256) over the raw UTF-8 text between the tags,
    /// exactly as served — no trimming, per the CSP hashing spec.
    internal static IReadOnlyList<string> ComputeInlineScriptHashes(string html)
    {
        var hashes = new List<string>();
        foreach (Match m in InlineScriptRegex.Matches(html))
        {
            var body = m.Groups["body"].Value;
            if (body.Length == 0) continue;
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(body));
            hashes.Add($"'sha256-{Convert.ToBase64String(digest)}'");
        }

        return hashes;
    }

    private static readonly Regex InlineScriptRegex = new(
        @"<script\b(?![^>]*\bsrc\s*=)[^>]*>(?<body>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    internal static string BuildPolicy(string nonce, bool development, string reportUri)
        => BuildPolicy(nonce, development, reportUri, allowSameOriginFrame: false);

    internal static string BuildPolicy(string nonce, bool development, string reportUri, bool allowSameOriginFrame)
        => BuildPolicy(nonce, development, reportUri, allowSameOriginFrame, Array.Empty<string>());

    internal static string BuildPolicy(
        string nonce,
        bool development,
        string reportUri,
        bool allowSameOriginFrame,
        IReadOnlyList<string> inlineScriptHashes)
        => BuildPolicy(nonce, development, reportUri, allowSameOriginFrame, inlineScriptHashes, portalPage: false);

    internal static string BuildPolicy(
        string nonce,
        bool development,
        string reportUri,
        bool allowSameOriginFrame,
        IReadOnlyList<string> inlineScriptHashes,
        bool portalPage)
    {
        // The hash tokens ride next to the nonce: the nonce keeps covering
        // anything stamped with it at runtime, the hashes cover the static
        // inline scripts baked into index.html. Everything else stays blocked.
        var hashPart = inlineScriptHashes.Count == 0
            ? string.Empty
            : " " + string.Join(' ', inlineScriptHashes);
        // Portal documents: the Turnstile loader is a cross-origin <script>
        // (host-source, no nonce) and renders its challenge in an <iframe>
        // from the same origin — both allowed only there.
        var turnstilePart = portalPage ? " " + TurnstileOrigin : string.Empty;
        var scriptSrc = development
            ? $"'self' 'nonce-{nonce}'{hashPart}{turnstilePart} 'unsafe-eval'"
            : $"'self' 'nonce-{nonce}'{hashPart}{turnstilePart}";

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

        var directives = new List<string>
        {
            "default-src 'self'",
            $"script-src {scriptSrc}",
            $"style-src {styleSrc}",
            "img-src 'self' data: blob:",
            "font-src 'self' data: https://fonts.gstatic.com",
            $"connect-src {connectSrc}",
        };
        // frame-src is normally governed by default-src 'self'; the portal
        // registration page embeds the Turnstile challenge frame.
        if (portalPage) directives.Add($"frame-src 'self' {TurnstileOrigin}");
        directives.AddRange(new[]
        {
            allowSameOriginFrame ? "frame-ancestors 'self'" : "frame-ancestors 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "object-src 'none'",
            $"report-uri {reportUri}",
        });
        return string.Join("; ", directives);
    }
}
