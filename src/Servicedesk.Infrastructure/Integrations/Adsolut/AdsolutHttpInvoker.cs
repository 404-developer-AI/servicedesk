using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Shared HTTP execution path for the Adsolut API clients (Administrations,
/// Customers, …). Centralises three concerns the per-endpoint clients
/// shouldn't each reinvent:
/// <list type="bullet">
/// <item>Bearer-token attach (resolved via <see cref="IAdsolutAccessTokenProvider"/>)</item>
/// <item>Once-on-401 retry: a stale cached access token is discarded and
/// the call is retried with a freshly minted one. Beyond that one retry
/// we treat 401 as terminal and surface it to the caller.</item>
/// <item><c>integration_audit</c> row per attempt, with latency, http
/// status and a sanitized payload describing the call. The audit row
/// always lands; logging failures inside the audit logger never escalate
/// into an integration outage.</item>
/// </list>
public sealed class AdsolutHttpInvoker
{
    public const string HttpClientName = "adsolut-api";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAdsolutAccessTokenProvider _tokens;
    private readonly IIntegrationAuditLogger _audit;
    private readonly ISettingsService _settings;
    private readonly ILogger<AdsolutHttpInvoker> _logger;

    public AdsolutHttpInvoker(
        IHttpClientFactory httpClientFactory,
        IAdsolutAccessTokenProvider tokens,
        IIntegrationAuditLogger audit,
        ISettingsService settings,
        ILogger<AdsolutHttpInvoker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _audit = audit;
        _settings = settings;
        _logger = logger;
    }

    public async Task<string> ResolveBaseUrlAsync(CancellationToken ct)
    {
        var raw = (await _settings.GetAsync<string>(SettingKeys.Adsolut.ApiBaseUrl, ct) ?? string.Empty).Trim();
        if (raw.Length == 0) raw = "https://api.adsolut.com";
        return raw.TrimEnd('/');
    }

    /// Invokes <paramref name="buildRequest"/> with a current bearer token,
    /// retries exactly once on a 401 with a fresh token, parses the success
    /// body via <paramref name="parseSuccess"/>, and records every attempt
    /// in <c>integration_audit</c>. Returns whatever <paramref name="parseSuccess"/>
    /// produces, or throws <see cref="AdsolutApiException"/> on a final non-2xx.
    public async Task<T> SendAsync<T>(
        string eventType,
        Func<HttpRequestMessage> buildRequest,
        Func<HttpResponseMessage, CancellationToken, Task<T>> parseSuccess,
        object? auditPayload = null,
        CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var request = buildRequest();
            string? endpoint = request.RequestUri?.ToString();

            string token;
            try
            {
                token = await _tokens.GetAccessTokenAsync(ct);
            }
            catch (AdsolutRefreshException ex)
            {
                await _audit.LogAsync(new IntegrationAuditEvent(
                    Integration: AdsolutEventTypes.Integration,
                    EventType: eventType,
                    Outcome: IntegrationAuditOutcome.Error,
                    Endpoint: endpoint,
                    HttpStatus: null,
                    LatencyMs: 0,
                    ErrorCode: ex.UpstreamErrorCode ?? "refresh_failed",
                    Payload: new { auditPayload, message = ex.Message, requiresReconnect = ex.RequiresReconnect }), ct);
                throw new AdsolutApiException(
                    "Adsolut refresh failed before API call: " + ex.Message,
                    ex.RequiresReconnect ? (int?)401 : null,
                    ex.UpstreamErrorCode);
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/json");

            var http = _httpClientFactory.CreateClient(HttpClientName);
            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                stopwatch.Stop();
                await _audit.LogAsync(new IntegrationAuditEvent(
                    Integration: AdsolutEventTypes.Integration,
                    EventType: eventType,
                    Outcome: IntegrationAuditOutcome.Warn,
                    Endpoint: endpoint,
                    HttpStatus: null,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    ErrorCode: "transport_error",
                    Payload: new { auditPayload, message = ex.Message }), ct);
                throw new AdsolutApiException("Transport error talking to Adsolut: " + ex.Message, ex);
            }
            stopwatch.Stop();
            try
            {
                var status = (int)response.StatusCode;

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
                {
                    // Cached token may have been minted just before WK rotated
                    // it (concurrent refresh) or revoked; force-refresh once.
                    _tokens.Invalidate();
                    await _audit.LogAsync(new IntegrationAuditEvent(
                        Integration: AdsolutEventTypes.Integration,
                        EventType: eventType,
                        Outcome: IntegrationAuditOutcome.Warn,
                        Endpoint: endpoint,
                        HttpStatus: status,
                        LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                        ErrorCode: "unauthorized_retry",
                        Payload: new { auditPayload, attempt }), ct);
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    var value = await parseSuccess(response, ct);
                    await _audit.LogAsync(new IntegrationAuditEvent(
                        Integration: AdsolutEventTypes.Integration,
                        EventType: eventType,
                        Outcome: IntegrationAuditOutcome.Ok,
                        Endpoint: endpoint,
                        HttpStatus: status,
                        LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                        Payload: auditPayload), ct);
                    return value;
                }

                // Non-2xx, terminal at this layer. Try to lift the upstream
                // error code; sometimes it's `{ "error": "..." }`, sometimes
                // a plain text body.
                var body = await SafeReadBodyAsync(response, ct);
                var upstreamCode = TryParseErrorCode(body);
                var outcome = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? IntegrationAuditOutcome.Warn
                    : (status >= 500 ? IntegrationAuditOutcome.Warn : IntegrationAuditOutcome.Error);
                await _audit.LogAsync(new IntegrationAuditEvent(
                    Integration: AdsolutEventTypes.Integration,
                    EventType: eventType,
                    Outcome: outcome,
                    Endpoint: endpoint,
                    HttpStatus: status,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    ErrorCode: upstreamCode ?? $"http_{status}",
                    Payload: new { auditPayload, snippet = Truncate(body, 256) }), ct);
                throw new AdsolutApiException(
                    $"Adsolut returned {status}{(upstreamCode is null ? string.Empty : ": " + upstreamCode)}",
                    httpStatus: status,
                    upstreamErrorCode: upstreamCode);
            }
            finally
            {
                response.Dispose();
            }
        }

        // Two attempts both 401 — the second-attempt 401 is logged above as
        // an Error row; here we just translate it into the typed exception
        // for the caller. Reach this branch only when both loop iterations
        // saw 401; any other path returns or throws inside the loop.
        throw new AdsolutApiException(
            "Adsolut rejected the access token twice in a row.",
            httpStatus: 401);
    }

    /// Diagnostic variant of <see cref="SendAsync"/> for the admin debug
    /// surface — returns the raw response (status + url + body) on every
    /// outcome instead of throwing on non-2xx, so an admin can inspect
    /// what Adsolut actually returned even for 4xx/5xx. Still does the
    /// bearer-attach + once-on-401 retry + audit row, just with the body
    /// surfaced instead of swallowed. Refresh failures still bubble up
    /// because there is no useful body to show in that case.
    public async Task<AdsolutRawResponse> SendRawAsync(
        string eventType,
        Func<HttpRequestMessage> buildRequest,
        object? auditPayload = null,
        CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var request = buildRequest();
            string endpoint = request.RequestUri?.ToString() ?? string.Empty;

            string token;
            try
            {
                token = await _tokens.GetAccessTokenAsync(ct);
            }
            catch (AdsolutRefreshException ex)
            {
                await _audit.LogAsync(new IntegrationAuditEvent(
                    Integration: AdsolutEventTypes.Integration,
                    EventType: eventType,
                    Outcome: IntegrationAuditOutcome.Error,
                    Endpoint: endpoint,
                    HttpStatus: null,
                    LatencyMs: 0,
                    ErrorCode: ex.UpstreamErrorCode ?? "refresh_failed",
                    Payload: new { auditPayload, message = ex.Message, requiresReconnect = ex.RequiresReconnect }), ct);
                throw new AdsolutApiException(
                    "Adsolut refresh failed before API call: " + ex.Message,
                    ex.RequiresReconnect ? (int?)401 : null,
                    ex.UpstreamErrorCode);
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/json");

            var http = _httpClientFactory.CreateClient(HttpClientName);
            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                stopwatch.Stop();
                await _audit.LogAsync(new IntegrationAuditEvent(
                    Integration: AdsolutEventTypes.Integration,
                    EventType: eventType,
                    Outcome: IntegrationAuditOutcome.Warn,
                    Endpoint: endpoint,
                    HttpStatus: null,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    ErrorCode: "transport_error",
                    Payload: new { auditPayload, message = ex.Message }), ct);
                throw new AdsolutApiException("Transport error talking to Adsolut: " + ex.Message, ex);
            }
            stopwatch.Stop();
            try
            {
                var status = (int)response.StatusCode;

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
                {
                    _tokens.Invalidate();
                    await _audit.LogAsync(new IntegrationAuditEvent(
                        Integration: AdsolutEventTypes.Integration,
                        EventType: eventType,
                        Outcome: IntegrationAuditOutcome.Warn,
                        Endpoint: endpoint,
                        HttpStatus: status,
                        LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                        ErrorCode: "unauthorized_retry",
                        Payload: new { auditPayload, attempt }), ct);
                    continue;
                }

                var body = await SafeReadBodyAsync(response, ct);
                var outcome = response.IsSuccessStatusCode
                    ? IntegrationAuditOutcome.Ok
                    : (response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500
                        ? IntegrationAuditOutcome.Warn
                        : IntegrationAuditOutcome.Error);
                var upstreamCode = response.IsSuccessStatusCode ? null : TryParseErrorCode(body);
                await _audit.LogAsync(new IntegrationAuditEvent(
                    Integration: AdsolutEventTypes.Integration,
                    EventType: eventType,
                    Outcome: outcome,
                    Endpoint: endpoint,
                    HttpStatus: status,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    ErrorCode: response.IsSuccessStatusCode ? null : (upstreamCode ?? $"http_{status}"),
                    Payload: new { auditPayload, snippet = Truncate(body, 256) }), ct);
                return new AdsolutRawResponse(status, endpoint, body, upstreamCode);
            }
            finally
            {
                response.Dispose();
            }
        }

        throw new AdsolutApiException(
            "Adsolut rejected the access token twice in a row.",
            httpStatus: 401);
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch (Exception) { return string.Empty; }
    }

    private static string? TryParseErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString();
                if (err.ValueKind == JsonValueKind.Object &&
                    err.TryGetProperty("code", out var code) &&
                    code.ValueKind == JsonValueKind.String)
                {
                    return code.GetString();
                }
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}

/// Wrapper for the diagnostic <see cref="AdsolutHttpInvoker.SendRawAsync"/>
/// path. Carries the upstream HTTP status, the URL that was actually called,
/// the raw response body verbatim and (when the body parsed as a JSON error
/// envelope) the lifted error code. The body is intentionally a string and
/// not a parsed JsonDocument so the admin sees exactly what WK sent — bytes,
/// formatting and all.
public sealed record AdsolutRawResponse(int Status, string RequestUrl, string Body, string? UpstreamErrorCode);
