using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// HTTP-side implementation of <see cref="ITelavoxApiClient"/>. Designed
/// to be a much simpler beast than <c>AdsolutHttpInvoker</c>: Telavox tokens
/// are static (no rotation, no refresh-token dance), so each call resolves
/// the relevant token once, attaches Bearer, sends, audits, returns. The
/// 401-retry-with-refresh loop the Adsolut path needs has no analogue here.
///
/// Token-handling rules followed throughout this file:
/// <list type="bullet">
/// <item>Tokens never enter <c>integration_audit</c> payloads (only the
/// truncated body snippet, with response-body, never request-body or
/// headers).</item>
/// <item>Tokens never enter ILogger calls.</item>
/// <item>Tokens are read once per HTTP call from
/// <see cref="IProtectedSecretStore"/> (PAPI) or supplied verbatim by
/// the caller (CAPI), and discarded after the request completes.</item>
/// </list>
public sealed class TelavoxApiClient : ITelavoxApiClient
{
    public const string HttpClientName = "telavox-api";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProtectedSecretStore _secrets;
    private readonly ISettingsService _settings;
    private readonly IIntegrationAuditLogger _audit;
    private readonly ILogger<TelavoxApiClient> _logger;

    public TelavoxApiClient(
        IHttpClientFactory httpClientFactory,
        IProtectedSecretStore secrets,
        ISettingsService settings,
        IIntegrationAuditLogger audit,
        ILogger<TelavoxApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secrets = secrets;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TelavoxCustomer>> ListCustomersAsync(CancellationToken ct)
    {
        var baseUrl = await ResolveBaseUrlAsync(ct);
        var body = await SendWithPartnerTokenAsync(
            eventType: TelavoxEventTypes.CustomersList,
            method: HttpMethod.Get,
            url: $"{baseUrl}/papi/v1/customers",
            jsonBody: null,
            auditPayload: null,
            ct: ct);
        return ParseCustomers(body);
    }

    public async Task<IReadOnlyList<TelavoxExtension>> ListExtensionsAsync(
        string customerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            throw new ArgumentException("customerId is required", nameof(customerId));
        var baseUrl = await ResolveBaseUrlAsync(ct);
        var body = await SendWithPartnerTokenAsync(
            eventType: TelavoxEventTypes.ExtensionsList,
            method: HttpMethod.Get,
            url: $"{baseUrl}/papi/v1/customers/{Uri.EscapeDataString(customerId)}/extensions",
            jsonBody: null,
            auditPayload: new { customerId },
            ct: ct);
        return ParseExtensions(body);
    }

    public async Task<TelavoxCreateApiUserResult> CreateApiUserAsync(
        string customerId,
        string email,
        string description,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            throw new ArgumentException("customerId is required", nameof(customerId));
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("email is required", nameof(email));

        var baseUrl = await ResolveBaseUrlAsync(ct);
        var payload = JsonSerializer.Serialize(new
        {
            email = email.Trim(),
            description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
        });

        var body = await SendWithPartnerTokenAsync(
            eventType: TelavoxEventTypes.ApiUserCreate,
            method: HttpMethod.Post,
            url: $"{baseUrl}/papi/v1/customers/{Uri.EscapeDataString(customerId)}/api-users",
            jsonBody: payload,
            // Email is the Telavox primary-key for the api-user, not a
            // secret. Description is admin-supplied and forensically useful.
            auditPayload: new { customerId, email = email.Trim() },
            ct: ct);

        var result = ParseCreateApiUserResponse(body);
        if (string.IsNullOrEmpty(result.Token))
        {
            throw new TelavoxApiException(
                "Telavox accepted the api-user creation but the response body did not carry a token field.",
                httpStatus: 200);
        }
        return result;
    }

    public async Task DeleteApiUserAsync(
        string customerId, string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            throw new ArgumentException("customerId is required", nameof(customerId));
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("email is required", nameof(email));

        var baseUrl = await ResolveBaseUrlAsync(ct);
        // A 404 from Telavox is treated as success (already gone) so a
        // duplicate revoke or a manual cleanup in Telavox doesn't surface
        // as an admin-facing error. Other non-2xx surface normally.
        try
        {
            await SendWithPartnerTokenAsync(
                eventType: TelavoxEventTypes.ApiUserDelete,
                method: HttpMethod.Delete,
                url: $"{baseUrl}/papi/v1/customers/{Uri.EscapeDataString(customerId)}/api-users/{Uri.EscapeDataString(email)}",
                jsonBody: null,
                auditPayload: new { customerId, email },
                ct: ct);
        }
        catch (TelavoxApiException ex) when (ex.HttpStatus == 404)
        {
            _logger.LogInformation(
                "Telavox api-user {Email} for customer {CustomerId} was already gone (404); treating revoke as success.",
                email, customerId);
        }
    }

    public async Task<TelavoxCall?> GetCurrentCallAsync(
        string capiToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(capiToken))
            throw new ArgumentException("capiToken is required", nameof(capiToken));

        var baseUrl = await ResolveBaseUrlAsync(ct);
        var body = await SendAsync(
            eventType: TelavoxEventTypes.AgentCallsPoll,
            method: HttpMethod.Get,
            url: $"{baseUrl}/origo/api/v1/me/calls?state=active",
            jsonBody: null,
            bearer: capiToken,
            auditPayload: null,
            ct: ct);
        return ParseCurrentCall(body);
    }

    private async Task<string> ResolveBaseUrlAsync(CancellationToken ct)
    {
        var raw = (await _settings.GetAsync<string>(SettingKeys.Telavox.ApiBaseUrl, ct) ?? string.Empty).Trim();
        if (raw.Length == 0) raw = "https://api.telavox.se";
        return raw.TrimEnd('/');
    }

    private async Task<string> SendWithPartnerTokenAsync(
        string eventType,
        HttpMethod method,
        string url,
        string? jsonBody,
        object? auditPayload,
        CancellationToken ct)
    {
        var token = await _secrets.GetAsync(ProtectedSecretKeys.TelavoxPartnerToken, ct);
        if (string.IsNullOrEmpty(token))
        {
            // No partner-token set — still write one audit row so an admin
            // staring at the integration page sees the configuration-error
            // attempts in integration_audit instead of a silent void. No
            // HTTP call happens, so status/latency are zero.
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: TelavoxEventTypes.Integration,
                EventType: eventType,
                Outcome: IntegrationAuditOutcome.Warn,
                Endpoint: url,
                HttpStatus: null,
                LatencyMs: 0,
                ErrorCode: "partner_token_missing",
                Payload: auditPayload), ct);
            throw new TelavoxApiException(
                "Telavox partner-token is not configured. Set it on the integration page before calling PAPI.",
                httpStatus: null,
                upstreamErrorCode: "partner_token_missing");
        }
        return await SendAsync(eventType, method, url, jsonBody, token, auditPayload, ct);
    }

    private async Task<string> SendAsync(
        string eventType,
        HttpMethod method,
        string url,
        string? jsonBody,
        string bearer,
        object? auditPayload,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.ParseAdd("application/json");
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

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
                Integration: TelavoxEventTypes.Integration,
                EventType: eventType,
                Outcome: IntegrationAuditOutcome.Warn,
                Endpoint: url,
                HttpStatus: null,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: "transport_error",
                Payload: new { auditPayload, message = ex.Message }), ct);
            throw new TelavoxApiException("Transport error talking to Telavox: " + ex.Message, ex);
        }
        stopwatch.Stop();
        try
        {
            var status = (int)response.StatusCode;
            var responseBody = await SafeReadBodyAsync(response, ct);

            if (response.IsSuccessStatusCode)
            {
                await _audit.LogAsync(new IntegrationAuditEvent(
                    Integration: TelavoxEventTypes.Integration,
                    EventType: eventType,
                    Outcome: IntegrationAuditOutcome.Ok,
                    Endpoint: url,
                    HttpStatus: status,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    Payload: auditPayload), ct);
                return responseBody;
            }

            var upstreamCode = TryParseErrorCode(responseBody);
            // 429 and 5xx are transient-warn, 4xx are caller errors. 401 is
            // a real auth problem (bad token) and admin-actionable.
            var outcome = response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500
                ? IntegrationAuditOutcome.Warn
                : IntegrationAuditOutcome.Error;
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: TelavoxEventTypes.Integration,
                EventType: eventType,
                Outcome: outcome,
                Endpoint: url,
                HttpStatus: status,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: upstreamCode ?? $"http_{status}",
                Payload: new { auditPayload, snippet = Truncate(responseBody, 256) }), ct);
            throw new TelavoxApiException(
                $"Telavox returned {status}{(upstreamCode is null ? string.Empty : ": " + upstreamCode)}",
                httpStatus: status,
                upstreamErrorCode: upstreamCode);
        }
        finally
        {
            response.Dispose();
        }
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
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            // Telavox error envelopes seen empirically: { error: "..." } or
            // { error: { code: "..." } } or { message: "..." }. Try each in
            // order without assuming one specific shape.
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString();
                if (err.ValueKind == JsonValueKind.Object &&
                    err.TryGetProperty("code", out var code) &&
                    code.ValueKind == JsonValueKind.String)
                {
                    return code.GetString();
                }
            }
            if (doc.RootElement.TryGetProperty("code", out var topCode) &&
                topCode.ValueKind == JsonValueKind.String)
            {
                return topCode.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    // ---- parsers (internal for tests via InternalsVisibleTo) -----------
    //
    // Parsing is deliberately defensive: every Telavox response is allowed
    // to be either a bare array (some endpoints) or a paged-result wrapper
    // (`{ items: [...] }`). Missing fields become empty strings / null so
    // a new optional field upstream doesn't break SD; missing primary keys
    // (id, callId) skip the row rather than throw — same discipline as the
    // Adsolut parsers.

    internal static IReadOnlyList<TelavoxCustomer> ParseCustomers(string body)
    {
        var array = TryRootArray(body);
        if (array is null) return Array.Empty<TelavoxCustomer>();
        var list = new List<TelavoxCustomer>(array.Value.GetArrayLength());
        foreach (var el in array.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetStringId(el, "id");
            if (string.IsNullOrEmpty(id)) continue;
            var name = TryGetString(el, "name") ?? string.Empty;
            list.Add(new TelavoxCustomer(id, name));
        }
        return list;
    }

    internal static IReadOnlyList<TelavoxExtension> ParseExtensions(string body)
    {
        var array = TryRootArray(body);
        if (array is null) return Array.Empty<TelavoxExtension>();
        var list = new List<TelavoxExtension>(array.Value.GetArrayLength());
        foreach (var el in array.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetStringId(el, "id");
            if (string.IsNullOrEmpty(id)) continue;
            // Telavox extensions can be referenced either by an opaque id or
            // a human-readable number. Both are kept so the SD link table can
            // store the number (what an admin sees) and the underlying id
            // separately if Telavox ever splits the two.
            var number = TryGetString(el, "number")
                ?? TryGetString(el, "extension")
                ?? string.Empty;
            var name = TryGetString(el, "name");
            // User-email may live under "user.email" or "userEmail".
            var userEmail = TryGetString(el, "userEmail");
            if (userEmail is null && el.TryGetProperty("user", out var user)
                && user.ValueKind == JsonValueKind.Object)
            {
                userEmail = TryGetString(user, "email");
            }
            list.Add(new TelavoxExtension(id, number, name, userEmail));
        }
        return list;
    }

    /// Internal parser for POST /api-users responses. Public for tests.
    /// Telavox responses observed in partner-docs carry `email`, `userId`
    /// (or `id`) and `token`; we accept the variants without forcing one
    /// specific shape.
    internal static TelavoxCreateApiUserResult ParseCreateApiUserResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new TelavoxCreateApiUserResult(string.Empty, string.Empty, string.Empty);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return new TelavoxCreateApiUserResult(string.Empty, string.Empty, string.Empty);
        var root = doc.RootElement;
        var email = TryGetString(root, "email") ?? string.Empty;
        var userId = TryGetString(root, "userId")
            ?? TryGetString(root, "id")
            ?? string.Empty;
        var token = TryGetString(root, "token")
            ?? TryGetString(root, "apiToken")
            ?? TryGetString(root, "accessToken")
            ?? string.Empty;
        return new TelavoxCreateApiUserResult(email, userId, token);
    }

    /// Parser for GET /me/calls?state=active. CAPI shape observed:
    /// either a bare array (zero or many active calls) or an object
    /// wrapping `calls`/`items`. We return the first active call (the UI
    /// only ever pops one popup per agent at a time anyway).
    internal static TelavoxCall? ParseCurrentCall(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var doc = JsonDocument.Parse(body);
        JsonElement? array = doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => doc.RootElement,
            JsonValueKind.Object when doc.RootElement.TryGetProperty("calls", out var calls)
                && calls.ValueKind == JsonValueKind.Array => calls,
            JsonValueKind.Object when doc.RootElement.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array => items,
            _ => (JsonElement?)null,
        };
        if (array is null) return null;
        foreach (var el in array.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetString(el, "id")
                ?? TryGetString(el, "callId")
                ?? string.Empty;
            if (string.IsNullOrEmpty(id)) continue;
            var state = (TryGetString(el, "state") ?? string.Empty).ToUpperInvariant();
            var fromNumber = TryGetString(el, "from")
                ?? TryGetString(el, "fromNumber")
                ?? TryGetString(el, "caller");
            var toNumber = TryGetString(el, "to")
                ?? TryGetString(el, "toNumber")
                ?? TryGetString(el, "callee");
            var startUtc = TryGetDateTimeOffset(el, "startTime")
                ?? TryGetDateTimeOffset(el, "start")
                ?? TryGetDateTimeOffset(el, "started");
            return new TelavoxCall(id, state, fromNumber, toNumber, startUtc);
        }
        return null;
    }

    private static JsonElement? TryRootArray(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            switch (doc.RootElement.ValueKind)
            {
                case JsonValueKind.Array:
                    // Clone so the JsonDocument can be safely disposed at
                    // method exit while callers still walk the array. The
                    // alternative — returning the live JsonElement — keeps a
                    // disposed doc alive and faults on EnumerateArray.
                    return doc.RootElement.Clone();
                case JsonValueKind.Object:
                    if (doc.RootElement.TryGetProperty("items", out var items)
                        && items.ValueKind == JsonValueKind.Array)
                        return items.Clone();
                    if (doc.RootElement.TryGetProperty("data", out var data)
                        && data.ValueKind == JsonValueKind.Array)
                        return data.Clone();
                    return null;
                default:
                    return null;
            }
        }
        catch (JsonException) { return null; }
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.ToString(),
            JsonValueKind.Null => null,
            _ => null,
        };
    }

    /// Id-friendly read: accepts both string and integer ids (some Telavox
    /// endpoints return numeric customer-ids). Returns the value as a
    /// trimmed string so callers never juggle two types.
    private static string TryGetStringId(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return string.Empty;
        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString() ?? string.Empty,
            JsonValueKind.Number => prop.ToString(),
            _ => string.Empty,
        };
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        var raw = prop.GetString();
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var v)
            ? v
            : (DateTimeOffset?)null;
    }
}
