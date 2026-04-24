using System.Net;
using Servicedesk.Api.Tests.TestInfrastructure;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class SecurityHeadersTests : IClassFixture<SecurityBaselineFactory>
{
    private readonly SecurityBaselineFactory _factory;

    public SecurityHeadersTests(SecurityBaselineFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AllStaticSecurityHeadersArePresent()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/system/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Single(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Single(response, "Referrer-Policy"));
        Assert.Equal("same-origin", Single(response, "Cross-Origin-Opener-Policy"));
        Assert.Equal("same-origin", Single(response, "Cross-Origin-Resource-Policy"));
        Assert.Equal("require-corp", Single(response, "Cross-Origin-Embedder-Policy"));
        Assert.Contains("camera=()", Single(response, "Permissions-Policy"));
    }

    [Fact]
    public async Task ContentSecurityPolicy_ProductionHasStrictNonce()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/system/version");

        var csp = Single(response, "Content-Security-Policy");
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self' 'nonce-", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("report-uri /api/security/csp-report", csp);
        Assert.DoesNotContain("'unsafe-eval'", csp);

        // script-src stays strict — no 'unsafe-inline', nonce enforced.
        var scriptDirective = ExtractDirective(csp, "script-src");
        Assert.DoesNotContain("'unsafe-inline'", scriptDirective);
        Assert.Contains("'nonce-", scriptDirective);

        // style-src intentionally allows 'unsafe-inline' because
        // Sonner/Radix/Framer/Vaul inject stylesheets at runtime without a
        // nonce. CRITICAL: no nonce in style-src — browsers ignore
        // 'unsafe-inline' when a nonce is present in the same directive.
        var styleDirective = ExtractDirective(csp, "style-src");
        Assert.Contains("'unsafe-inline'", styleDirective);
        Assert.DoesNotContain("'nonce-", styleDirective);
        Assert.Contains("https://fonts.googleapis.com", styleDirective);
        Assert.Contains("https://fonts.gstatic.com", ExtractDirective(csp, "font-src"));
    }

    [Fact]
    public async Task ContentSecurityPolicy_NoncesDifferPerRequest()
    {
        var client = _factory.CreateClient();
        var r1 = await client.GetAsync("/api/system/version");
        var r2 = await client.GetAsync("/api/system/version");

        var n1 = ExtractNonce(Single(r1, "Content-Security-Policy"));
        var n2 = ExtractNonce(Single(r2, "Content-Security-Policy"));
        Assert.False(string.IsNullOrEmpty(n1));
        Assert.NotEqual(n1, n2);
    }

    private static string Single(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";

    private static string ExtractNonce(string csp)
    {
        const string marker = "'nonce-";
        var i = csp.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return "";
        var start = i + marker.Length;
        var end = csp.IndexOf('\'', start);
        return end < 0 ? "" : csp[start..end];
    }

    private static string ExtractDirective(string csp, string name)
    {
        foreach (var part in csp.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith(name + " ", StringComparison.Ordinal) ||
                trimmed.Equals(name, StringComparison.Ordinal))
            {
                return trimmed;
            }
        }
        return "";
    }
}
