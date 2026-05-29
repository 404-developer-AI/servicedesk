using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Eol;

/// HTTP-side implementation of <see cref="IEolDataClient"/>. Anonymous
/// public API — no auth, no per-tenant config beyond the base URL.
/// Audit-log writes mirror the TRMM client so the same UI components
/// can render both feeds.
public sealed class EolDataClient : IEolDataClient
{
    public const string HttpClientName = "eol-api";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsService _settings;
    private readonly IIntegrationAuditLogger _audit;
    private readonly ILogger<EolDataClient> _logger;

    public EolDataClient(
        IHttpClientFactory httpClientFactory,
        ISettingsService settings,
        IIntegrationAuditLogger audit,
        ILogger<EolDataClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    public async Task<IReadOnlyList<EolReleaseRow>> FetchWindowsAsync(CancellationToken ct)
    {
        var body = await SendAsync(EolEventTypes.FetchWindows, "/api/windows.json", ct);
        return ParseReleases(body, product: "windows");
    }

    public async Task<IReadOnlyList<EolReleaseRow>> FetchWindowsServerAsync(CancellationToken ct)
    {
        var body = await SendAsync(EolEventTypes.FetchWindowsServer, "/api/windows-server.json", ct);
        return ParseReleases(body, product: "windows-server");
    }

    // ---- HTTP ----------------------------------------------------------

    private async Task<string> SendAsync(string eventType, string path, CancellationToken ct)
    {
        var baseUrl = await ResolveBaseUrlAsync(ct);
        if (string.IsNullOrEmpty(baseUrl))
        {
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: EolEventTypes.Integration,
                EventType: eventType,
                Outcome: IntegrationAuditOutcome.Warn,
                Endpoint: path,
                HttpStatus: null,
                LatencyMs: 0,
                ErrorCode: "base_url_missing"), ct);
            throw new EolApiException(
                "EOL data base URL is not configured.",
                upstreamErrorCode: "base_url_missing");
        }

        var url = baseUrl + path;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");

        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeoutCts.Token);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: EolEventTypes.Integration,
                EventType: eventType,
                Outcome: IntegrationAuditOutcome.Warn,
                Endpoint: path,
                HttpStatus: null,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: "transport_error",
                Payload: new { message = ex.Message }), ct);
            throw new EolApiException("Transport error talking to endoflife.date: " + ex.Message, ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: EolEventTypes.Integration,
                EventType: eventType,
                Outcome: IntegrationAuditOutcome.Warn,
                Endpoint: path,
                HttpStatus: null,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: "timeout"), ct);
            throw new EolApiException("Timed out talking to endoflife.date.");
        }
        stopwatch.Stop();

        try
        {
            var status = (int)response.StatusCode;
            var body = await SafeReadBodyAsync(response, ct);
            if (response.IsSuccessStatusCode)
            {
                await _audit.LogAsync(new IntegrationAuditEvent(
                    Integration: EolEventTypes.Integration,
                    EventType: eventType,
                    Outcome: IntegrationAuditOutcome.Ok,
                    Endpoint: path,
                    HttpStatus: status,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds), ct);
                return body;
            }

            var code = status == 404 ? "not_found" : $"http_{status}";
            var outcome = status >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests
                ? IntegrationAuditOutcome.Warn
                : IntegrationAuditOutcome.Error;
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: EolEventTypes.Integration,
                EventType: eventType,
                Outcome: outcome,
                Endpoint: path,
                HttpStatus: status,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: code), ct);
            throw new EolApiException(
                $"endoflife.date returned HTTP {status} for {path}.",
                httpStatus: status,
                upstreamErrorCode: code);
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<string> ResolveBaseUrlAsync(CancellationToken ct)
    {
        var raw = (await _settings.GetAsync<string>(SettingKeys.Eol.BaseUrl, ct) ?? string.Empty).Trim();
        return raw.TrimEnd('/');
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    // ---- Parsing -------------------------------------------------------

    /// Parses the top-level array of release rows. Each row has at least
    /// <c>cycle</c>, <c>eol</c>, optionally <c>releaseLabel</c> and
    /// <c>lts</c>. <c>eol</c> can be a date string, the literal boolean
    /// <c>false</c> (= not yet end-of-lifed), or a future flag — we treat
    /// non-date values as "no known EOL date".
    private static IReadOnlyList<EolReleaseRow> ParseReleases(string body, string product)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<EolReleaseRow>();

        var list = new List<EolReleaseRow>(doc.RootElement.GetArrayLength());
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var cycle = ReadString(el, "cycle");
            if (string.IsNullOrWhiteSpace(cycle)) continue;
            var releaseLabel = ReadString(el, "releaseLabel");
            var eolUtc = ReadEolDate(el);
            var lts = ReadBool(el, "lts") ?? false;
            list.Add(new EolReleaseRow(
                Product: product,
                Cycle: cycle.Trim(),
                ReleaseLabel: releaseLabel,
                EolUtc: eolUtc,
                Lts: lts));
        }
        return list;
    }

    private static DateTime? ReadEolDate(JsonElement el)
    {
        if (!el.TryGetProperty("eol", out var v)) return null;
        if (v.ValueKind == JsonValueKind.String
            && DateTime.TryParse(v.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }
        // True/False or other non-date markers: no concrete EOL date known.
        return null;
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? ReadBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : null;
}
