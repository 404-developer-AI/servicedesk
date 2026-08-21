using System.Net;
using Servicedesk.Api.Security;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Portal;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.0 — smaller portal building blocks: token minting/lookup, the
/// customer HTML sanitizer, input normalisation, and the CSP/COEP scoping
/// of the Turnstile allowance to /portal documents.
public sealed class PortalUnitTests
{
    // v0.1.1 — Portal.RegistrationTicketPriorityId: the override wins only
    // when it points at an existing *active* priority; empty, garbage and
    // inactive ids all fall back to the default (registration tickets bypass
    // triggers, so this setting is the only priority knob they have).
    [Fact]
    public void Registration_priority_override_picks_only_active_matches()
    {
        static Servicedesk.Domain.Taxonomy.Priority P(Guid id, bool active) => new(
            id, "High", "high", 3, "#f00", "flame", 1,
            IsActive: active, IsSystem: false, IsDefault: false,
            CreatedUtc: DateTime.UnixEpoch, UpdatedUtc: DateTime.UnixEpoch);

        var active = Guid.NewGuid();
        var inactive = Guid.NewGuid();
        var priorities = new[] { P(active, true), P(inactive, false) };

        Assert.Equal(active, PortalAccountService.PickRegistrationPriority(priorities, active.ToString()));
        Assert.Null(PortalAccountService.PickRegistrationPriority(priorities, inactive.ToString()));
        Assert.Null(PortalAccountService.PickRegistrationPriority(priorities, Guid.NewGuid().ToString()));
        Assert.Null(PortalAccountService.PickRegistrationPriority(priorities, ""));
        Assert.Null(PortalAccountService.PickRegistrationPriority(priorities, null));
        Assert.Null(PortalAccountService.PickRegistrationPriority(priorities, "not-a-guid"));
    }

    [Fact]
    public void Token_roundtrip_hashes_match_and_garbage_is_rejected()
    {
        var svc = new PortalTokenService();
        var (raw, hash) = svc.Mint();

        Assert.Equal(43, raw.Length); // 32 bytes base64url, no padding
        Assert.DoesNotContain('+', raw);
        Assert.DoesNotContain('/', raw);
        Assert.Equal(hash, svc.HashForLookup(raw));
        Assert.Equal(hash, svc.HashForLookup(" " + raw + " "));

        Assert.Null(svc.HashForLookup(""));
        Assert.Null(svc.HashForLookup("not*base64url"));
        Assert.Null(svc.HashForLookup(raw[..20]));              // wrong length
        Assert.Null(svc.HashForLookup(new string('A', 100)));    // too long
        Assert.NotEqual(hash, svc.Mint().Hash);
    }

    [Fact]
    public void Html_sanitizer_strips_scripts_images_and_styles_but_keeps_marks()
    {
        var dirty = """
            <p onclick="x()">Hello <strong>world</strong> <img src="http://evil/x.png"> <a href="javascript:alert(1)">bad</a>
            <a href="https://example.com/docs">ok</a></p><script>alert(1)</script><style>p{}</style>
            <table><tr><td>cell</td></tr></table><span style="color:red">styled</span>
            """;
        var clean = PortalHtmlSanitizer.Sanitize(dirty);

        Assert.DoesNotContain("<script", clean);
        Assert.DoesNotContain("<style", clean);
        Assert.DoesNotContain("<img", clean);
        Assert.DoesNotContain("javascript:", clean);
        Assert.DoesNotContain("onclick", clean);
        Assert.DoesNotContain("<table", clean);
        Assert.DoesNotContain("style=", clean);
        Assert.Contains("<strong>world</strong>", clean);
        Assert.Contains("href=\"https://example.com/docs\"", clean);
        Assert.Contains("rel=\"noopener noreferrer nofollow\"", clean);
        Assert.Contains("cell", clean); // text survives, table chrome doesn't
    }

    [Theory]
    [InlineData("Someone@Example.COM", "someone@example.com")]
    [InlineData("  a.b+c@sub.example.org ", "a.b+c@sub.example.org")]
    [InlineData("no-at-sign", null)]
    [InlineData("two@@example.com", null)]
    [InlineData("spaces in@example.com", null)]
    [InlineData("user@localhost", null)]
    [InlineData("", null)]
    public void Email_normalisation(string input, string? expected)
    {
        Assert.Equal(expected, PortalAccountService.NormalizeEmail(input));
    }

    [Theory]
    [InlineData("  Jane   Doe ", "Jane Doe")]
    [InlineData("J", null)]
    [InlineData("<script>", null)]
    [InlineData("", null)]
    public void Name_normalisation(string input, string? expected)
    {
        Assert.Equal(expected, PortalAccountService.NormalizeName(input));
    }

    [Fact]
    public void Validity_description_is_human()
    {
        Assert.Equal("24 hours", PortalMailService.DescribeValidity(TimeSpan.FromHours(24)));
        Assert.Equal("7 days", PortalMailService.DescribeValidity(TimeSpan.FromHours(168)));
        Assert.Equal("60 minutes", PortalMailService.DescribeValidity(TimeSpan.FromMinutes(60).Subtract(TimeSpan.FromTicks(1))));
        Assert.Equal("1 hour", PortalMailService.DescribeValidity(TimeSpan.FromHours(1)));
        Assert.Equal(string.Empty, PortalMailService.DescribeValidity(null));
    }

    [Fact]
    public void Mail_template_tokens_are_substituted_and_subject_is_plain_text()
    {
        var tokens = new Dictionary<string, string>
        {
            ["{{name}}"] = WebUtility.HtmlEncode("Tom & Jerry"),
            ["{{link}}"] = WebUtility.HtmlEncode("https://sd.example.com/portal/verify-email?token=a&b"),
        };
        var html = PortalMailService.ApplyTokens("<p>Hi {{name}} <a href=\"{{link}}\">go</a></p>", tokens, html: true);
        var subject = PortalMailService.ApplyTokens("Welcome {{name}}", tokens, html: false);

        Assert.Contains("Tom &amp; Jerry", html);
        Assert.Contains("token=a&amp;b", html);
        Assert.Equal("Welcome Tom & Jerry", subject);
    }

    [Fact]
    public void Csp_allows_turnstile_only_on_portal_documents()
    {
        var strict = ContentSecurityPolicyMiddleware.BuildPolicy("n", false, "/r", false, Array.Empty<string>(), portalPage: false);
        var portal = ContentSecurityPolicyMiddleware.BuildPolicy("n", false, "/r", false, Array.Empty<string>(), portalPage: true);

        Assert.DoesNotContain(ContentSecurityPolicyMiddleware.TurnstileOrigin, strict);
        Assert.DoesNotContain("frame-src", strict);
        Assert.Contains($"script-src 'self' 'nonce-n' {ContentSecurityPolicyMiddleware.TurnstileOrigin}", portal);
        Assert.Contains($"frame-src 'self' {ContentSecurityPolicyMiddleware.TurnstileOrigin}", portal);
        // Everything else stays as strict as before.
        Assert.Contains("frame-ancestors 'none'", portal);
        Assert.Contains("object-src 'none'", portal);
        Assert.DoesNotContain("'unsafe-eval'", portal);
    }

    [Fact]
    public async Task Portal_documents_drop_coep_but_keep_the_other_headers()
    {
        using var factory = new SecurityBaselineFactory();
        var client = factory.CreateClient();

        var portal = await client.GetAsync("/portal/register");
        var agent = await client.GetAsync("/api/system/version");

        Assert.False(portal.Headers.Contains("Cross-Origin-Embedder-Policy"));
        Assert.Equal("require-corp", agent.Headers.GetValues("Cross-Origin-Embedder-Policy").Single());
        Assert.Equal("DENY", portal.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("same-origin", portal.Headers.GetValues("Cross-Origin-Opener-Policy").Single());
        var csp = portal.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains(ContentSecurityPolicyMiddleware.TurnstileOrigin, csp);
        var agentCsp = agent.Headers.GetValues("Content-Security-Policy").Single();
        Assert.DoesNotContain(ContentSecurityPolicyMiddleware.TurnstileOrigin, agentCsp);
    }
}
