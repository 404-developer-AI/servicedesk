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
        var papiBase = await ResolvePapiBaseAsync(ct);
        var body = await SendWithPartnerTokenAsync(
            eventType: TelavoxEventTypes.CustomersList,
            method: HttpMethod.Get,
            url: $"{papiBase}/partner2/api/papi/v1/customers",
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
        var papiBase = await ResolvePapiBaseAsync(ct);
        // PAPI surprise: /v1/customers/{customer}/extensions is a HATEOAS
        // link-discovery stub that returns only a `links[]` array, never
        // the actual extensions. The real list-endpoint is
        // /extensions/users (returns ExtensionDto[]). Pre-D the worker hit
        // the stub and silently got zero extensions in the dropdown.
        var body = await SendWithPartnerTokenAsync(
            eventType: TelavoxEventTypes.ExtensionsList,
            method: HttpMethod.Get,
            url: $"{papiBase}/partner2/api/papi/v1/customers/{Uri.EscapeDataString(customerId)}/extensions/users",
            jsonBody: null,
            auditPayload: new { customerId },
            ct: ct);
        return ParseExtensions(body);
    }

    public async Task<TelavoxCreateApiUserResult> CreateApiUserAsync(
        string customerId,
        string name,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            throw new ArgumentException("customerId is required", nameof(customerId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name is required", nameof(name));

        var papiBase = await ResolvePapiBaseAsync(ct);
        var trimmedName = name.Trim();
        // PAPI expects the api-user name as a QUERY param, not a body —
        // mismatch with v0.0.34 commit B which sent a JSON body and got
        // a 404 even with a valid partner-token. The endpoint accepts no
        // body; we still send the audit-payload so an admin can correlate
        // the create-call with the audit row.
        var createUrl = $"{papiBase}/partner2/api/papi/v1/customers/{Uri.EscapeDataString(customerId)}/api-users?name={Uri.EscapeDataString(trimmedName)}";
        var createBody = await SendWithPartnerTokenAsync(
            eventType: TelavoxEventTypes.ApiUserCreate,
            method: HttpMethod.Post,
            url: createUrl,
            jsonBody: null,
            auditPayload: new { customerId, name = trimmedName },
            ct: ct);

        var apiUserKey = ParseApiUserKey(createBody);
        if (string.IsNullOrEmpty(apiUserKey))
        {
            throw new TelavoxApiException(
                "Telavox accepted the api-user creation but the response body did not carry a key field.",
                httpStatus: 200);
        }

        // Step 2: mint a CAPI bearer-token bound to the api-user just
        // created. If this fails, best-effort delete the partial upstream
        // api-user so Telavox isn't left with an orphan, then surface the
        // original exception to the caller.
        string tokenBody;
        try
        {
            tokenBody = await SendWithPartnerTokenAsync(
                eventType: TelavoxEventTypes.ApiUserTokenCreate,
                method: HttpMethod.Post,
                url: $"{papiBase}/partner2/api/papi/v1/customers/{Uri.EscapeDataString(customerId)}/api-users/{Uri.EscapeDataString(apiUserKey)}/tokens",
                jsonBody: null,
                auditPayload: new { customerId, apiUserKey },
                ct: ct);
        }
        catch
        {
            try { await DeleteApiUserAsync(customerId, apiUserKey, CancellationToken.None); }
            catch (Exception rbEx)
            {
                _logger.LogWarning(rbEx,
                    "Token-mint failure rollback of api-user {ApiUserKey} for customer {CustomerId} failed; admin must manually revoke it.",
                    apiUserKey, customerId);
            }
            throw;
        }

        var bearer = ParseCapiTokenBearer(tokenBody);
        if (string.IsNullOrEmpty(bearer))
        {
            // Same orphan-cleanup as the catch above: a 200 with an empty
            // token body still leaves an api-user we can never bind to a
            // bearer, so kill it before propagating.
            try { await DeleteApiUserAsync(customerId, apiUserKey, CancellationToken.None); }
            catch (Exception rbEx)
            {
                _logger.LogWarning(rbEx,
                    "Empty-token-body rollback of api-user {ApiUserKey} for customer {CustomerId} failed.",
                    apiUserKey, customerId);
            }
            throw new TelavoxApiException(
                "Telavox accepted the token creation but the response body did not carry a bearerToken field.",
                httpStatus: 200);
        }

        return new TelavoxCreateApiUserResult(Name: trimmedName, UserId: apiUserKey, Token: bearer);
    }

    public async Task DeleteApiUserAsync(
        string customerId, string apiUserKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            throw new ArgumentException("customerId is required", nameof(customerId));
        if (string.IsNullOrWhiteSpace(apiUserKey))
            throw new ArgumentException("apiUserKey is required", nameof(apiUserKey));

        var papiBase = await ResolvePapiBaseAsync(ct);
        // A 404 from Telavox is treated as success (already gone) so a
        // duplicate revoke or a manual cleanup in Telavox doesn't surface
        // as an admin-facing error. Other non-2xx surface normally.
        try
        {
            await SendWithPartnerTokenAsync(
                eventType: TelavoxEventTypes.ApiUserDelete,
                method: HttpMethod.Delete,
                url: $"{papiBase}/partner2/api/papi/v1/customers/{Uri.EscapeDataString(customerId)}/api-users/{Uri.EscapeDataString(apiUserKey)}",
                jsonBody: null,
                auditPayload: new { customerId, apiUserKey },
                ct: ct);
        }
        catch (TelavoxApiException ex) when (ex.HttpStatus == 404)
        {
            _logger.LogInformation(
                "Telavox api-user {ApiUserKey} for customer {CustomerId} was already gone (404); treating revoke as success.",
                apiUserKey, customerId);
        }
    }

    public async Task<TelavoxCall?> GetCurrentCallAsync(
        string extension, string capiToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(extension))
            throw new ArgumentException("extension is required", nameof(extension));
        if (string.IsNullOrWhiteSpace(capiToken))
            throw new ArgumentException("capiToken is required", nameof(capiToken));

        var capiBase = await ResolveCapiBaseAsync(ct);
        // suppressSuccessAudit: true — successful CAPI polls fire on the
        // PollIntervalSeconds cadence (default 2s per agent). Writing a row
        // per success would dwarf every other table in the install within
        // a day. The polling worker already writes a coalesced tick-summary
        // row carrying the aggregate counts; here we only keep failure rows
        // so an admin can still spot auth/transport problems forensically.
        var body = await SendAsync(
            eventType: TelavoxEventTypes.AgentCallsPoll,
            method: HttpMethod.Get,
            url: $"{capiBase}/api/capi/v1/extensions/{Uri.EscapeDataString(extension)}/calls",
            jsonBody: null,
            bearer: capiToken,
            auditPayload: new { extension },
            suppressSuccessAudit: true,
            ct: ct);
        return ParseCurrentCall(body);
    }

    private async Task<string> ResolvePapiBaseAsync(CancellationToken ct)
    {
        var raw = (await _settings.GetAsync<string>(SettingKeys.Telavox.PapiBaseUrl, ct) ?? string.Empty).Trim();
        if (raw.Length == 0) raw = "https://partner.telavox.se";
        return raw.TrimEnd('/');
    }

    private async Task<string> ResolveCapiBaseAsync(CancellationToken ct)
    {
        var raw = (await _settings.GetAsync<string>(SettingKeys.Telavox.CapiBaseUrl, ct) ?? string.Empty).Trim();
        if (raw.Length == 0) raw = "https://home.telavox.se";
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
        return await SendAsync(eventType, method, url, jsonBody, token, auditPayload, suppressSuccessAudit: false, ct);
    }

    private async Task<string> SendAsync(
        string eventType,
        HttpMethod method,
        string url,
        string? jsonBody,
        string bearer,
        object? auditPayload,
        bool suppressSuccessAudit,
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
                if (!suppressSuccessAudit)
                {
                    await _audit.LogAsync(new IntegrationAuditEvent(
                        Integration: TelavoxEventTypes.Integration,
                        EventType: eventType,
                        Outcome: IntegrationAuditOutcome.Ok,
                        Endpoint: url,
                        HttpStatus: status,
                        LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                        Payload: auditPayload), ct);
                }
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
        // PAPI swagger: CustomerDto carries the customer-id under `key`
        // (example "customer-123"), with `name` separate. Pre-D this parser
        // looked for `id` and skipped every row because no row had it.
        var array = TryRootArray(body);
        if (array is null) return Array.Empty<TelavoxCustomer>();
        var list = new List<TelavoxCustomer>(array.Value.GetArrayLength());
        foreach (var el in array.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetStringId(el, "key");
            // Defensive fallback: accept `id` too, so a future PAPI schema
            // rename or a partner-environment variant doesn't silently
            // empty the dropdown the way v0.0.34 commit B did.
            if (string.IsNullOrEmpty(id)) id = TryGetStringId(el, "id");
            if (string.IsNullOrEmpty(id)) continue;
            var name = TryGetString(el, "name") ?? string.Empty;
            list.Add(new TelavoxCustomer(id, name));
        }
        return list;
    }

    internal static IReadOnlyList<TelavoxExtension> ParseExtensions(string body)
    {
        // PAPI swagger: ExtensionDto.key is the opaque extension identifier
        // (example "extension-123") — that's what the CAPI path-param
        // /api/capi/v1/extensions/{extension}/calls expects, not the
        // dialable number. The dialable number lives under
        // `fixedNumber.e164Number` (or `mobileNumber.e164Number`); we
        // surface that as the "number" display so the admin recognises the
        // row, but the value the link table stores is the key.
        var array = TryRootArray(body);
        if (array is null) return Array.Empty<TelavoxExtension>();
        var list = new List<TelavoxExtension>(array.Value.GetArrayLength());
        foreach (var el in array.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetStringId(el, "key");
            if (string.IsNullOrEmpty(id)) id = TryGetStringId(el, "id");
            if (string.IsNullOrEmpty(id)) continue;

            var number = TryReadE164(el, "fixedNumber")
                ?? TryReadE164(el, "mobileNumber")
                ?? TryGetString(el, "number")
                ?? TryGetString(el, "extension")
                ?? string.Empty;
            var name = TryGetString(el, "name");
            var userEmail = TryGetString(el, "email")
                ?? TryGetString(el, "userEmail");
            // Legacy nested shape (pre-PAPI swagger update): user.email.
            if (userEmail is null && el.TryGetProperty("user", out var user)
                && user.ValueKind == JsonValueKind.Object)
            {
                userEmail = TryGetString(user, "email");
            }
            list.Add(new TelavoxExtension(id, number, name, userEmail));
        }
        return list;
    }

    private static string? TryReadE164(JsonElement el, string propName)
    {
        if (!el.TryGetProperty(propName, out var sub)) return null;
        if (sub.ValueKind != JsonValueKind.Object) return null;
        return TryGetString(sub, "e164Number");
    }

    /// Parses the response of POST /v1/customers/{customer}/api-users —
    /// per PAPI swagger this is an <c>ApiUserDto</c> with a <c>key</c>
    /// field (example "apiUser-123"). Empty string when the body is
    /// missing the field or unparseable so the caller can surface a
    /// structured error.
    internal static string ParseApiUserKey(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;
            return TryGetString(doc.RootElement, "key") ?? string.Empty;
        }
        catch (JsonException) { return string.Empty; }
    }

    /// Parses the response of POST /v1/customers/{customer}/api-users/{key}/tokens
    /// — per PAPI swagger this is a <c>CapiTokenDto</c> with a
    /// <c>bearerToken</c> field. Empty when missing / unparseable so the
    /// caller can surface a structured error.
    internal static string ParseCapiTokenBearer(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;
            return TryGetString(doc.RootElement, "bearerToken") ?? string.Empty;
        }
        catch (JsonException) { return string.Empty; }
    }

    /// Parser for GET /api/capi/v1/extensions/{extension}/calls. The
    /// real CAPI returns a bare JSON array of <c>OngoingCallDto</c>:
    /// <c>[ { callerId, callDirection, lineStatus }, … ]</c>. No callId,
    /// no startTime, no toNumber — the wire shape is intentionally narrow.
    /// Empirically the same call surfaces multiple times during ringing
    /// (one row per terminal/device); we pick the first row that isn't
    /// already terminated, in <i>either</i> direction. The captured
    /// <c>callDirection</c> rides along on the returned call so the worker
    /// can keep the popup inbound-only while still flipping the dashboard
    /// call-state indicator for an agent who is dialling out.
    ///
    /// Because there is no real callId, the synthetic <see cref="TelavoxCall.CallId"/>
    /// is the <c>callerId</c> itself; the same caller is treated as the
    /// same call for the duration of a single ring/answer cycle, which is
    /// the granularity the state-machine actually needs. Two back-to-back
    /// calls from the same number within seconds would dedupe — accepted
    /// trade-off for v0.0.34, can be replaced with a server-side
    /// (callerId + first-observed-utc) compound key in a follow-up if it
    /// matters in practice.
    internal static TelavoxCall? ParseCurrentCall(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            // CAPI returns a bare array. Defensive: also accept an envelope
            // shape (`{ items: [] }` / `{ data: [] }`) so a future API
            // wrapping doesn't break the parser silently.
            JsonElement? array = doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement,
                JsonValueKind.Object when doc.RootElement.TryGetProperty("items", out var items)
                    && items.ValueKind == JsonValueKind.Array => items,
                JsonValueKind.Object when doc.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Array => data,
                _ => (JsonElement?)null,
            };
            if (array is null) return null;
            foreach (var el in array.Value.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                var callerId = TryGetString(el, "callerId");
                if (string.IsNullOrEmpty(callerId)) continue;

                var direction = (TryGetString(el, "callDirection") ?? string.Empty)
                    .ToLowerInvariant();

                var lineStatus = (TryGetString(el, "lineStatus") ?? string.Empty)
                    .ToLowerInvariant();
                // Terminal states clear the baseline — same behaviour as a
                // null `current` in the state-machine.
                if (lineStatus == "down") continue;

                // Outbound calls are kept (not skipped here): the transition
                // module gates the popup on direction so the agent never gets
                // a popup for a call they placed, while the dashboard
                // call-state indicator still tracks the outbound call.
                return new TelavoxCall(
                    CallId: callerId,
                    State: lineStatus,
                    FromNumber: callerId,
                    ToNumber: null,
                    StartUtc: null,
                    Direction: direction);
            }
            return null;
        }
        catch (JsonException) { return null; }
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
