using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Security;

public static class CspReportEndpoint
{
    public static IEndpointRouteBuilder MapCspReportEndpoint(this IEndpointRouteBuilder app)
    {
        // 16 KB is an order of magnitude above a legitimate CSP report (the
        // longest field is usually the document-uri). Anything bigger is
        // either a misbehaving browser or an abuse attempt trying to inflate
        // the audit table.
        const int MaxBodyBytes = 16 * 1024;

        app.MapPost("/api/security/csp-report", async (
            HttpContext ctx,
            IAuditLogger audit,
            IMemoryCache cache,
            ISettingsService settings,
            CancellationToken ct) =>
        {
            object? report = null;
            JsonElement? parsed = null;
            try
            {
                using var limited = new LimitedStream(ctx.Request.Body, MaxBodyBytes);
                using var doc = await JsonDocument.ParseAsync(limited, cancellationToken: ct);
                var element = JsonSerializer.Deserialize<JsonElement>(doc.RootElement.GetRawText());
                parsed = element;
                report = element;
            }
            catch
            {
                report = new { malformed = true };
            }

            // Expected-noise filter. Inbound mail bodies routinely reference
            // external images that `img-src 'self'` blocks BY DESIGN (privacy
            // default, like mail clients blocking remote content) — every
            // blocked logo or tracking pixel would otherwise write a
            // csp_violation audit row that says nothing about XSS. Directives
            // on the ignore list are acknowledged but never logged; script/
            // style/connect violations — the actual attack signal — always
            // land in the audit log.
            var ignoredDirectives = await settings.GetAsync<string>(
                SettingKeys.Security.CspIgnoredReportDirectives, ct);
            var directive = ExtractEffectiveDirective(parsed);
            if (directive is not null && IsIgnoredDirective(directive, ignoredDirectives))
            {
                return Results.NoContent();
            }

            // v0.0.92 — one violation, one audit row. A single root cause
            // (e.g. a browser extension injecting the same inline script into
            // every page) repeats the exact same report on every page load;
            // without dedup that floods the audit log and the report rate
            // limit. Identical reports from the same IP inside the window are
            // acknowledged but not re-logged.
            var dedupWindowSeconds = await settings.GetAsync<int>(
                SettingKeys.Security.CspReportDedupWindowSeconds, ct);
            if (dedupWindowSeconds > 0)
            {
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
                var cacheKey = "csp-report-dedup:" + BuildDedupFingerprint(clientIp, parsed);
                if (cache.TryGetValue(cacheKey, out _))
                {
                    return Results.NoContent();
                }

                cache.Set(cacheKey, true, TimeSpan.FromSeconds(dedupWindowSeconds));
            }

            await audit.LogAsync(new AuditEvent(
                EventType: "csp_violation",
                Actor: "browser",
                ActorRole: "anon",
                ClientIp: ctx.Connection.RemoteIpAddress?.ToString(),
                UserAgent: ctx.Request.Headers.UserAgent.ToString(),
                Payload: report), ct);

            return Results.NoContent();
        })
        .WithName("CspReport")
        .WithOpenApi()
        .RequireRateLimiting("csp-report");

        return app;
    }

    /// <summary>
    /// Collapses a report to what identifies the violation itself: the
    /// directive that fired and the resource that was blocked, scoped per IP.
    /// The document-uri is deliberately excluded — the same root cause fires
    /// on every page the user visits and should still count as one violation.
    /// Reports missing those fields (unexpected shape, malformed JSON) fall
    /// back to hashing the raw payload so distinct unknowns don't collapse.
    /// </summary>
    internal static string BuildDedupFingerprint(string clientIp, JsonElement? parsed)
    {
        string discriminator;
        if (parsed is { } root && TryGetReportBody(root, out var body))
        {
            var directive = GetString(body, "effective-directive")
                ?? GetString(body, "violated-directive")
                ?? "-";
            var blockedUri = GetString(body, "blocked-uri") ?? "-";
            discriminator = directive + "|" + blockedUri;
        }
        else
        {
            discriminator = "raw|" + (parsed?.GetRawText() ?? "malformed");
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(clientIp + "|" + discriminator));
        return Convert.ToHexString(digest);
    }

    /// <summary>
    /// The directive that fired, normalized to its bare name. Prefers
    /// `effective-directive` (already bare, e.g. "img-src"); falls back to
    /// the first token of `violated-directive`, which legacy report-uri
    /// payloads send as the full directive value ("img-src 'self' data:").
    /// Null when the report is malformed or carries neither field — those
    /// always take the logging path so unknowns stay visible.
    /// </summary>
    internal static string? ExtractEffectiveDirective(JsonElement? parsed)
    {
        if (parsed is not { } root || !TryGetReportBody(root, out var body)) return null;

        var effective = GetString(body, "effective-directive");
        if (!string.IsNullOrWhiteSpace(effective)) return effective.Trim();

        var violated = GetString(body, "violated-directive");
        if (string.IsNullOrWhiteSpace(violated)) return null;
        violated = violated.Trim();
        var space = violated.IndexOf(' ');
        return space > 0 ? violated[..space] : violated;
    }

    internal static bool IsIgnoredDirective(string directive, string? ignoredCsv)
    {
        if (string.IsNullOrWhiteSpace(ignoredCsv)) return false;
        foreach (var entry in ignoredCsv.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(entry, directive, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool TryGetReportBody(JsonElement root, out JsonElement body)
    {
        // report-uri wraps the fields in a "csp-report" envelope; be lenient
        // and accept a bare object too (report-to style payloads).
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("csp-report", out var wrapped)
                && wrapped.ValueKind == JsonValueKind.Object)
            {
                body = wrapped;
                return true;
            }

            body = root;
            return true;
        }

        body = default;
        return false;
    }

    private static string? GetString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// Read-only stream wrapper that hard-caps the number of bytes a caller
    /// can read from the underlying stream. Reading past the cap throws so
    /// the JSON parser bails out and the endpoint records {malformed:true}.
    private sealed class LimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _max;
        private long _read;

        public LimitedStream(Stream inner, long max)
        {
            _inner = inner;
            _max = max;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var allowed = (int)Math.Min(count, Math.Max(0, _max - _read + 1));
            if (allowed <= 0) throw new InvalidOperationException("CSP report exceeds size limit.");
            var read = _inner.Read(buffer, offset, allowed);
            _read += read;
            if (_read > _max) throw new InvalidOperationException("CSP report exceeds size limit.");
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var allowed = (int)Math.Min(buffer.Length, Math.Max(0, _max - _read + 1));
            if (allowed <= 0) throw new InvalidOperationException("CSP report exceeds size limit.");
            var read = await _inner.ReadAsync(buffer[..allowed], cancellationToken);
            _read += read;
            if (_read > _max) throw new InvalidOperationException("CSP report exceeds size limit.");
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
