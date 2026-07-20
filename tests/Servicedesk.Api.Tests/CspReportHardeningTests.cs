using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Servicedesk.Api.Security;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Health.SecurityActivity;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.92 — CSP-report noise hardening: inline index.html scripts allowed by
/// startup-computed hash, CSP-report rate-limit rejections split into their
/// own security-activity category, and identical violation reports deduped.
public sealed class CspReportHardeningTests
{
    // ----- Inline-script hashing (root cause of the report flood) -----

    [Fact]
    public void ComputeInlineScriptHashes_HashesExactBodyOfInlineScripts()
    {
        const string body = "(function(){var x=1;})();";
        var html = $"<html><head><script>{body}</script></head>" +
                   "<body><script type=\"module\" src=\"/src/main.tsx\"></script></body></html>";

        var hashes = ContentSecurityPolicyMiddleware.ComputeInlineScriptHashes(html);

        // CSP-3 spec: base64(SHA-256) over the raw text between the tags.
        var expected = $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}'";
        Assert.Equal(new[] { expected }, hashes);
    }

    [Fact]
    public void ComputeInlineScriptHashes_PreservesWhitespaceExactly()
    {
        // The browser hashes the script text verbatim — trimming or collapsing
        // whitespace on our side would produce a token the browser rejects.
        const string body = "\n      alert(1);\n    ";
        var html = $"<script>{body}</script>";

        var hashes = ContentSecurityPolicyMiddleware.ComputeInlineScriptHashes(html);

        var expected = $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}'";
        Assert.Equal(new[] { expected }, hashes);
    }

    [Fact]
    public void ComputeInlineScriptHashes_SkipsSrcScriptsAndEmptyBodies()
    {
        const string html =
            "<script src=\"/a.js\"></script>" +
            "<script type=\"module\" src=\"/b.js\"></script>" +
            "<script></script>";

        Assert.Empty(ContentSecurityPolicyMiddleware.ComputeInlineScriptHashes(html));
    }

    [Fact]
    public void ComputeInlineScriptHashes_HandlesMultipleInlineScripts()
    {
        const string html = "<script>one();</script><p>x</p><script>two();</script>";

        var hashes = ContentSecurityPolicyMiddleware.ComputeInlineScriptHashes(html);

        Assert.Equal(2, hashes.Count);
        Assert.NotEqual(hashes[0], hashes[1]);
    }

    [Fact]
    public void BuildPolicy_EmitsInlineScriptHashesInScriptSrcOnly()
    {
        var hashes = new[] { "'sha256-abc123='" };
        var csp = ContentSecurityPolicyMiddleware.BuildPolicy(
            "nonce-value", development: false, reportUri: "/api/security/csp-report",
            allowSameOriginFrame: false, inlineScriptHashes: hashes);

        var scriptSrc = ExtractDirective(csp, "script-src");
        Assert.Contains("'nonce-nonce-value'", scriptSrc);
        Assert.Contains("'sha256-abc123='", scriptSrc);
        Assert.DoesNotContain("'unsafe-eval'", scriptSrc);
        Assert.DoesNotContain("'sha256-", ExtractDirective(csp, "style-src"));
    }

    [Fact]
    public void BuildPolicy_Development_KeepsUnsafeEvalNextToHashes()
    {
        var csp = ContentSecurityPolicyMiddleware.BuildPolicy(
            "n", development: true, reportUri: "/r",
            allowSameOriginFrame: false, inlineScriptHashes: new[] { "'sha256-x='" });

        var scriptSrc = ExtractDirective(csp, "script-src");
        Assert.Contains("'sha256-x='", scriptSrc);
        Assert.Contains("'unsafe-eval'", scriptSrc);
    }

    [Fact]
    public void BuildPolicy_NoHashes_IsUnchanged()
    {
        var withEmpty = ContentSecurityPolicyMiddleware.BuildPolicy(
            "n", development: false, reportUri: "/r",
            allowSameOriginFrame: false, inlineScriptHashes: Array.Empty<string>());
        var legacyOverload = ContentSecurityPolicyMiddleware.BuildPolicy(
            "n", development: false, reportUri: "/r", allowSameOriginFrame: false);

        Assert.Equal(legacyOverload, withEmpty);
    }

    [Fact]
    public void RepoIndexHtml_ThemeBootstrap_IsCoveredByAHash()
    {
        // Guards the actual shipped file: the anti-FOUC theme script in
        // index.html must yield at least one hash, otherwise production
        // regresses to blocking it on every page load.
        var indexPath = FindRepoFile(Path.Combine("src", "Servicedesk.Web", "index.html"));
        var hashes = ContentSecurityPolicyMiddleware.ComputeInlineScriptHashes(File.ReadAllText(indexPath));

        Assert.NotEmpty(hashes);
        Assert.All(hashes, h => Assert.Matches("^'sha256-[A-Za-z0-9+/]+={0,2}'$", h));
    }

    // ----- Rate-limit rejection event-type split -----

    [Theory]
    [InlineData("/api/security/csp-report", "rate_limited_csp_report")]
    [InlineData("/api/tickets", "rate_limited")]
    [InlineData("/api/auth/login", "rate_limited")]
    [InlineData("/api/security/csp-report/extra", "rate_limited_csp_report")]
    public void ResolveEventType_SplitsCspReportRejections(string path, string expected)
    {
        Assert.Equal(expected, AuditRateLimiterEvents.ResolveEventType(path));
    }

    [Fact]
    public void SecurityActivity_HasDedicatedCspReportCategory()
    {
        var category = Assert.Single(
            SecurityActivityCategories.All, c => c.Key == "rate_limited_csp_report");
        Assert.Equal(new[] { AuditRateLimiterEvents.EventTypeRateLimitedCspReport }, category.EventTypes);
        Assert.Contains(AuditRateLimiterEvents.EventTypeRateLimitedCspReport,
            SecurityActivityCategories.AllEventTypes);

        // The generic bucket must no longer absorb CSP-report rejections.
        var generic = Assert.Single(SecurityActivityCategories.All, c => c.Key == "rate_limited");
        Assert.DoesNotContain(AuditRateLimiterEvents.EventTypeRateLimitedCspReport, generic.EventTypes);
    }

    [Fact]
    public void SecurityActivity_EveryCategoryThreshold_HasARegisteredDefault()
    {
        // A category whose threshold key is missing from SettingDefaults.All
        // makes the monitor's settings read throw at runtime.
        foreach (var category in SecurityActivityCategories.All)
        {
            Assert.Contains(SettingDefaults.All, d => d.Key == category.ThresholdSettingKey);
        }
    }

    // ----- Report dedup fingerprint -----

    [Fact]
    public void DedupFingerprint_SameViolationOnDifferentPages_Matches()
    {
        var a = Fingerprint("1.2.3.4", Report(directive: "script-src-elem", blockedUri: "inline", documentUri: "https://sd.example/tickets"));
        var b = Fingerprint("1.2.3.4", Report(directive: "script-src-elem", blockedUri: "inline", documentUri: "https://sd.example/dashboard"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void DedupFingerprint_DifferentViolationOrIp_Differs()
    {
        var baseline = Fingerprint("1.2.3.4", Report("script-src-elem", "inline", "https://sd.example/"));

        Assert.NotEqual(baseline, Fingerprint("1.2.3.4", Report("style-src", "inline", "https://sd.example/")));
        Assert.NotEqual(baseline, Fingerprint("1.2.3.4", Report("script-src-elem", "https://evil.example/x.js", "https://sd.example/")));
        Assert.NotEqual(baseline, Fingerprint("5.6.7.8", Report("script-src-elem", "inline", "https://sd.example/")));
    }

    [Fact]
    public void DedupFingerprint_MalformedReport_IsStableAndPerIp()
    {
        var a = CspReportEndpoint.BuildDedupFingerprint("1.2.3.4", parsed: null);
        var b = CspReportEndpoint.BuildDedupFingerprint("1.2.3.4", parsed: null);
        var otherIp = CspReportEndpoint.BuildDedupFingerprint("5.6.7.8", parsed: null);

        Assert.Equal(a, b);
        Assert.NotEqual(a, otherIp);
    }

    // ----- Endpoint integration: dedup + audit rows -----

    [Fact]
    public async Task CspReportEndpoint_LogsFirstReport_ThenDedupsIdenticalOnes()
    {
        using var factory = new SecurityBaselineFactory();
        var client = factory.CreateClient();
        var report = Report("script-src-elem", "inline", "https://sd.example/tickets");

        var first = await client.PostAsJsonAsync("/api/security/csp-report", ReportEnvelope(report));
        var duplicate = await client.PostAsJsonAsync("/api/security/csp-report", ReportEnvelope(report));

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, duplicate.StatusCode);
        Assert.Single(factory.Audit.Events, e => e.EventType == "csp_violation");
    }

    [Fact]
    public async Task CspReportEndpoint_DistinctViolations_AreBothLogged()
    {
        using var factory = new SecurityBaselineFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/security/csp-report",
            ReportEnvelope(Report("script-src-elem", "inline", "https://sd.example/")));
        await client.PostAsJsonAsync("/api/security/csp-report",
            ReportEnvelope(Report("connect-src", "https://elsewhere.example/beacon", "https://sd.example/")));

        Assert.Equal(2, factory.Audit.Events.Count(e => e.EventType == "csp_violation"));
    }

    [Fact]
    public async Task CspReportEndpoint_RateLimitRejection_LogsDedicatedEventType()
    {
        using var factory = new SecurityBaselineFactory()
            .WithConfig("Security:RateLimit:CspReport:PermitPerWindow", "1")
            .WithConfig("Security:RateLimit:CspReport:WindowSeconds", "60");
        var client = factory.CreateClient();

        // Distinct violations so the dedup cache doesn't swallow the second
        // request before the limiter sees it (the limiter runs first anyway,
        // but keep the test honest about what trips it).
        await client.PostAsJsonAsync("/api/security/csp-report",
            ReportEnvelope(Report("script-src-elem", "inline", "https://sd.example/")));
        var rejected = await client.PostAsJsonAsync("/api/security/csp-report",
            ReportEnvelope(Report("connect-src", "https://elsewhere.example/beacon", "https://sd.example/")));

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Contains(factory.Audit.Events,
            e => e.EventType == AuditRateLimiterEvents.EventTypeRateLimitedCspReport);
        Assert.DoesNotContain(factory.Audit.Events,
            e => e.EventType == AuditRateLimiterEvents.EventTypeRateLimited);
    }

    // ----- Ignored directives (expected noise, e.g. blocked mail images) -----

    [Fact]
    public async Task CspReportEndpoint_ImgSrcReport_IsAcknowledgedButNotLogged()
    {
        using var factory = new SecurityBaselineFactory();
        var client = factory.CreateClient();

        // External images in inbound mail bodies are blocked by design; their
        // reports must not reach the audit log (default ignore list: img-src).
        var response = await client.PostAsJsonAsync("/api/security/csp-report",
            ReportEnvelope(Report("img-src", "https://elsewhere.example/logo.png", "https://sd.example/tickets/1")));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(factory.Audit.Events, e => e.EventType == "csp_violation");
    }

    [Fact]
    public async Task CspReportEndpoint_ScriptSrcReport_IsStillLogged()
    {
        using var factory = new SecurityBaselineFactory();
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/security/csp-report",
            ReportEnvelope(Report("script-src-elem", "https://evil.example/x.js", "https://sd.example/")));

        Assert.Single(factory.Audit.Events, e => e.EventType == "csp_violation");
    }

    [Fact]
    public void ExtractEffectiveDirective_PrefersEffective_FallsBackToViolatedFirstToken()
    {
        // effective-directive is already bare.
        Assert.Equal("img-src", CspReportEndpoint.ExtractEffectiveDirective(
            ReportEnvelopeElement(Report("img-src", "https://x/", "https://sd.example/"))));

        // Legacy report-uri payloads may only carry violated-directive with
        // the full directive value; the bare name is its first token.
        var legacy = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(
            new Dictionary<string, JsonElement>
            {
                ["csp-report"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(
                    new Dictionary<string, string>
                    {
                        ["violated-directive"] = "img-src 'self' data: blob:",
                        ["blocked-uri"] = "https://x/",
                    })),
            }));
        Assert.Equal("img-src", CspReportEndpoint.ExtractEffectiveDirective(legacy));

        // Malformed → null → the report takes the logging path.
        Assert.Null(CspReportEndpoint.ExtractEffectiveDirective(null));
    }

    [Fact]
    public void IsIgnoredDirective_MatchesCsvEntriesCaseInsensitively()
    {
        Assert.True(CspReportEndpoint.IsIgnoredDirective("img-src", "img-src"));
        Assert.True(CspReportEndpoint.IsIgnoredDirective("IMG-SRC", " img-src , media-src "));
        Assert.False(CspReportEndpoint.IsIgnoredDirective("script-src-elem", "img-src"));
        Assert.False(CspReportEndpoint.IsIgnoredDirective("img-src", ""));
        Assert.False(CspReportEndpoint.IsIgnoredDirective("img-src", null));
    }

    // ----- helpers -----

    private static string Fingerprint(string ip, JsonElement report)
        => CspReportEndpoint.BuildDedupFingerprint(ip, ReportEnvelopeElement(report));

    private static JsonElement Report(string directive, string blockedUri, string documentUri)
        => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["document-uri"] = documentUri,
            ["effective-directive"] = directive,
            ["violated-directive"] = directive,
            ["blocked-uri"] = blockedUri,
        }));

    private static Dictionary<string, JsonElement> ReportEnvelope(JsonElement report)
        => new() { ["csp-report"] = report };

    private static JsonElement ReportEnvelopeElement(JsonElement report)
        => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(ReportEnvelope(report)));

    private static string ExtractDirective(string csp, string name)
    {
        foreach (var part in csp.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith(name + " ", StringComparison.Ordinal)) return trimmed;
        }

        return "";
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath} above {AppContext.BaseDirectory}.");
    }
}
