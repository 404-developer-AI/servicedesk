using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Zammad;

/// HTTP-side implementation of <see cref="IZammadApiClient"/>. Single
/// install-wide token (read once per call from <see cref="IProtectedSecretStore"/>),
/// no per-agent rotation, no refresh dance — Zammad personal-access tokens
/// are long-lived and the admin revokes them in the source Zammad webapp.
///
/// Token-handling rules (mirrors Telavox / Adsolut):
/// <list type="bullet">
/// <item>Token never enters <c>integration_audit</c> payloads (only
/// the truncated response-body snippet on failure, never request
/// headers or body).</item>
/// <item>Token never enters <see cref="ILogger"/> calls.</item>
/// <item>Token is read per HTTP call and discarded after the request
/// completes.</item>
/// </list>
public sealed class ZammadApiClient : IZammadApiClient
{
    public const string HttpClientName = "zammad-api";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProtectedSecretStore _secrets;
    private readonly ISettingsService _settings;
    private readonly IIntegrationAuditLogger _audit;
    private readonly ILogger<ZammadApiClient> _logger;

    public ZammadApiClient(
        IHttpClientFactory httpClientFactory,
        IProtectedSecretStore secrets,
        ISettingsService settings,
        IIntegrationAuditLogger audit,
        ILogger<ZammadApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secrets = secrets;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ZammadGroup>> ListGroupsAsync(CancellationToken ct)
    {
        var body = await SendAsync(
            eventType: ZammadEventTypes.GroupsList,
            method: HttpMethod.Get,
            path: "/api/v1/groups",
            ct: ct);
        return ParseGroups(body);
    }

    public async Task<IReadOnlyList<ZammadState>> ListStatesAsync(CancellationToken ct)
    {
        var body = await SendAsync(
            eventType: ZammadEventTypes.StatesList,
            method: HttpMethod.Get,
            path: "/api/v1/ticket_states",
            ct: ct);
        return ParseStates(body);
    }

    public async Task<IReadOnlyList<ZammadPriority>> ListPrioritiesAsync(CancellationToken ct)
    {
        var body = await SendAsync(
            eventType: ZammadEventTypes.PrioritiesList,
            method: HttpMethod.Get,
            path: "/api/v1/ticket_priorities",
            ct: ct);
        return ParsePriorities(body);
    }

    public async Task<ZammadTicket?> GetTicketAsync(long ticketId, CancellationToken ct)
    {
        try
        {
            // expand=true resolves customer/organization/group/state/priority
            // names alongside the *_id fields so the dry-run worker can
            // build a mapping snapshot without a second roundtrip per
            // related entity.
            var body = await SendAsync(
                eventType: ZammadEventTypes.TicketGet,
                method: HttpMethod.Get,
                path: $"/api/v1/tickets/{ticketId}?expand=true",
                ct: ct);
            return ParseTicket(body);
        }
        catch (ZammadApiException ex) when (ex.HttpStatus == 404)
        {
            return null;
        }
    }

    public async Task<ZammadUser?> GetUserAsync(long userId, CancellationToken ct)
    {
        try
        {
            var body = await SendAsync(
                eventType: ZammadEventTypes.UserGet,
                method: HttpMethod.Get,
                path: $"/api/v1/users/{userId}",
                ct: ct);
            return ParseUser(body);
        }
        catch (ZammadApiException ex) when (ex.HttpStatus == 404)
        {
            return null;
        }
    }

    public async Task<ZammadTicketSearchPage> SearchTicketsAsync(
        ZammadTicketSearchQuery query, CancellationToken ct)
    {
        var perPage = Math.Clamp(query.PerPage, 1, 200);
        var page = Math.Max(query.Page, 1);
        var esQuery = BuildSearchQuery(query.FreeText, query.GroupIds, query.StateIds);

        // No expand=true: we stay on Zammad's assets-envelope mode which
        // is more predictable across versions and gives us a total via
        // `tickets_count`. Order DESC on updated_at so freshly-touched
        // tickets float to the top of the picker — agents almost always
        // migrate by recency, not by creation order.
        var qs = new System.Collections.Generic.List<string>(8)
        {
            $"query={Uri.EscapeDataString(esQuery)}",
            $"page={page}",
            $"per_page={perPage}",
            "with_total_count=true",
            "sort_by=updated_at",
            "order_by=desc",
        };
        var path = "/api/v1/tickets/search?" + string.Join("&", qs);

        var body = await SendAsync(
            eventType: ZammadEventTypes.TicketsSearch,
            method: HttpMethod.Get,
            path: path,
            ct: ct,
            // Fase 2 — keep a snippet of the raw response on the success
            // row so an admin can verify the upstream shape if results
            // come up empty. Reviewable from the audit-log payload view.
            includeSuccessBodySnippet: true);
        var items = ParseSearchItems(body, out var total);
        return new ZammadTicketSearchPage(items, total, page, perPage);
    }

    /// Composes Zammad's Elasticsearch-style query string. Multi-value
    /// filters are <c>(field:a OR field:b)</c> wrapped, distinct clauses
    /// space-joined (Zammad ES-mode defaults to AND between top-level
    /// clauses). Free-text is appended verbatim — the caller is expected
    /// to feed plain user input; this method intentionally does not
    /// quote-wrap it because Zammad supports the full Lucene syntax
    /// (wildcards, boosting, range queries) and an admin migrating with
    /// targeted queries would lose that ability behind a hard quote.
    internal static string BuildSearchQuery(
        string? freeText,
        IReadOnlyList<long> groupIds,
        IReadOnlyList<long> stateIds)
    {
        var clauses = new System.Collections.Generic.List<string>(3);

        if (groupIds is { Count: > 0 })
        {
            clauses.Add(JoinIdClause("group_id", groupIds));
        }
        if (stateIds is { Count: > 0 })
        {
            clauses.Add(JoinIdClause("state_id", stateIds));
        }

        var trimmedText = (freeText ?? string.Empty).Trim();
        if (trimmedText.Length > 0)
        {
            clauses.Add(trimmedText);
        }

        // Empty query — Zammad's /tickets/search requires at least one
        // clause. Using `*` matches everything which is what an admin
        // running an unfiltered "show me anything" expects.
        return clauses.Count == 0 ? "*" : string.Join(" ", clauses);
    }

    private static string JoinIdClause(string field, IReadOnlyList<long> ids)
    {
        if (ids.Count == 1) return $"{field}:{ids[0]}";
        var parts = ids.Select(id => $"{field}:{id}");
        return "(" + string.Join(" OR ", parts) + ")";
    }

    public async Task<ZammadTestConnectionResult> TestConnectionAsync(CancellationToken ct)
    {
        // Outer stopwatch wraps both probe calls so the SPA can show a
        // single end-to-end latency number — matches what the admin
        // actually waited for.
        var outerStopwatch = Stopwatch.StartNew();
        try
        {
            var meBody = await SendAsync(
                eventType: ZammadEventTypes.UsersMe,
                method: HttpMethod.Get,
                path: "/api/v1/users/me",
                ct: ct);
            var me = ParseMe(meBody);

            // /version is permissive — a 401/403 here does not fail the
            // whole test. Catch the api-exception and surface a null
            // version so the page renders "version unknown" rather than
            // treating /users/me success as a connection failure.
            string? version = null;
            try
            {
                var versionBody = await SendAsync(
                    eventType: ZammadEventTypes.VersionGet,
                    method: HttpMethod.Get,
                    path: "/api/v1/version",
                    ct: ct);
                version = ParseVersion(versionBody);
            }
            catch (ZammadApiException ex)
            {
                _logger.LogInformation(
                    "Zammad /version probe failed with status {HttpStatus}; continuing with /users/me success.",
                    ex.HttpStatus);
            }

            outerStopwatch.Stop();

            // Composite-row in integration_audit summarising the click.
            // Individual /users/me + /version rows already landed via
            // SendAsync; this composite gives admins one line per click
            // to scan against.
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: ZammadEventTypes.Integration,
                EventType: ZammadEventTypes.TestConnection,
                Outcome: IntegrationAuditOutcome.Ok,
                Endpoint: null,
                HttpStatus: 200,
                LatencyMs: (int)outerStopwatch.ElapsedMilliseconds,
                Payload: new
                {
                    me = new { id = me.Id, email = me.Email },
                    version,
                }), ct);

            return new ZammadTestConnectionResult(
                Me: me,
                ZammadVersion: version,
                LatencyMs: (int)outerStopwatch.ElapsedMilliseconds);
        }
        catch (ZammadApiException)
        {
            outerStopwatch.Stop();
            // Composite-row already captured by SendAsync as the failing
            // /users/me row; don't double-log here.
            throw;
        }
    }

    // ---- HTTP --------------------------------------------------------

    private async Task<string> SendAsync(
        string eventType,
        HttpMethod method,
        string path,
        CancellationToken ct,
        bool includeSuccessBodySnippet = false)
    {
        var baseUrl = await ResolveBaseUrlAsync(ct);
        var token = await _secrets.GetAsync(ProtectedSecretKeys.ZammadToken, ct);

        if (string.IsNullOrEmpty(token))
        {
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: ZammadEventTypes.Integration,
                EventType: eventType,
                Outcome: IntegrationAuditOutcome.Warn,
                Endpoint: path,
                HttpStatus: null,
                LatencyMs: 0,
                ErrorCode: "token_missing"), ct);
            throw new ZammadApiException(
                "Zammad API token is not configured. Save one under Settings → Integrations → Zammad before testing the connection.",
                upstreamErrorCode: "token_missing");
        }

        if (string.IsNullOrEmpty(baseUrl))
        {
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: ZammadEventTypes.Integration,
                EventType: eventType,
                Outcome: IntegrationAuditOutcome.Warn,
                Endpoint: path,
                HttpStatus: null,
                LatencyMs: 0,
                ErrorCode: "base_url_missing"), ct);
            throw new ZammadApiException(
                "Zammad base URL is not configured. Save one under Settings → Integrations → Zammad before testing the connection.",
                upstreamErrorCode: "base_url_missing");
        }

        var url = baseUrl + path;
        using var request = new HttpRequestMessage(method, url);
        // Zammad personal-access tokens use the "Token token=<value>"
        // Authorization scheme (the same dialect Rails-based APIs use).
        // Bearer also works on modern Zammad but the Token scheme matches
        // the official docs and avoids surprising OAuth-flow rejections.
        request.Headers.TryAddWithoutValidation("Authorization", $"Token token={token}");
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
                Integration: ZammadEventTypes.Integration,
                EventType: eventType,
                Outcome: IntegrationAuditOutcome.Warn,
                Endpoint: path,
                HttpStatus: null,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: "transport_error",
                Payload: new { message = ex.Message }), ct);
            throw new ZammadApiException("Transport error talking to Zammad: " + ex.Message, ex);
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient timeout surfaces as TaskCanceledException with
            // an inner TimeoutException. Surface it as a transport
            // failure so the admin sees "timed out" instead of a
            // confusing "task cancelled".
            stopwatch.Stop();
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: ZammadEventTypes.Integration,
                EventType: eventType,
                Outcome: IntegrationAuditOutcome.Warn,
                Endpoint: path,
                HttpStatus: null,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: "timeout"), ct);
            throw new ZammadApiException("Timed out talking to Zammad.", ex);
        }
        stopwatch.Stop();

        try
        {
            var status = (int)response.StatusCode;
            var responseBody = await SafeReadBodyAsync(response, ct);

            if (response.IsSuccessStatusCode)
            {
                // For diagnostic-flagged calls we keep a body snippet on
                // the success row too — admin can click the row open in
                // the audit log to inspect the upstream shape. Used by
                // the search-call during fase 2 until we're confident
                // the parser handles every Zammad version's variant.
                object? successPayload = includeSuccessBodySnippet
                    ? new { snippet = Truncate(responseBody, 2048) }
                    : null;
                await _audit.LogAsync(new IntegrationAuditEvent(
                    Integration: ZammadEventTypes.Integration,
                    EventType: eventType,
                    Outcome: IntegrationAuditOutcome.Ok,
                    Endpoint: path,
                    HttpStatus: status,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    Payload: successPayload), ct);
                return responseBody;
            }

            var upstreamCode = TryParseErrorCode(responseBody, status);
            var outcome = response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500
                ? IntegrationAuditOutcome.Warn
                : IntegrationAuditOutcome.Error;
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: ZammadEventTypes.Integration,
                EventType: eventType,
                Outcome: outcome,
                Endpoint: path,
                HttpStatus: status,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: upstreamCode,
                Payload: new { snippet = Truncate(responseBody, 256) }), ct);
            throw new ZammadApiException(
                BuildHttpMessage(status, upstreamCode),
                httpStatus: status,
                upstreamErrorCode: upstreamCode);
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<string> ResolveBaseUrlAsync(CancellationToken ct)
    {
        var raw = (await _settings.GetAsync<string>(SettingKeys.Zammad.BaseUrl, ct) ?? string.Empty).Trim();
        return raw.TrimEnd('/');
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    /// Normalises Zammad's various error-envelope shapes into a single
    /// upstream-error-code string. Empirically Zammad returns
    /// <c>{ error: "..." }</c> for most failures plus an additional
    /// <c>error_human: "..."</c> on 4xx. 401 gets a synthetic code so the
    /// SPA can render a specific "invalid token" hint.
    private static string? TryParseErrorCode(string body, int httpStatus)
    {
        if (httpStatus == 401) return "invalid_credentials";
        if (string.IsNullOrWhiteSpace(body)) return $"http_{httpStatus}";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.String)
                    {
                        var s = err.GetString();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
                if (doc.RootElement.TryGetProperty("error_human", out var errHuman)
                    && errHuman.ValueKind == JsonValueKind.String)
                {
                    var s = errHuman.GetString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
        }
        catch (JsonException) { }
        return $"http_{httpStatus}";
    }

    private static string BuildHttpMessage(int status, string? upstreamCode) =>
        upstreamCode is null
            ? $"Zammad returned HTTP {status}."
            : $"Zammad returned HTTP {status}: {upstreamCode}";

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    // ---- parsers (internal for tests via InternalsVisibleTo) ----------

    internal static ZammadMe ParseMe(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ZammadApiException("Zammad /users/me returned an empty body.", httpStatus: 200);
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ZammadApiException("Zammad /users/me did not return a JSON object.", httpStatus: 200);
            }
            var id = TryGetLong(doc.RootElement, "id");
            if (id is null)
            {
                throw new ZammadApiException("Zammad /users/me response is missing the 'id' field.", httpStatus: 200);
            }
            return new ZammadMe(
                Id: id.Value,
                Email: TryGetString(doc.RootElement, "email"),
                FirstName: TryGetString(doc.RootElement, "firstname"),
                LastName: TryGetString(doc.RootElement, "lastname"),
                Login: TryGetString(doc.RootElement, "login"),
                OrganizationId: TryGetLong(doc.RootElement, "organization_id"));
        }
        catch (JsonException ex)
        {
            throw new ZammadApiException("Zammad /users/me response was not valid JSON.", ex);
        }
    }

    /// /api/v1/version returns either <c>{ "version": "6.3.1" }</c> on
    /// modern Zammad or a bare string on very old installs. Both shapes
    /// are accepted; anything else returns null and the UI falls back to
    /// "version unknown".
    internal static string? ParseVersion(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            switch (doc.RootElement.ValueKind)
            {
                case JsonValueKind.String:
                    return doc.RootElement.GetString();
                case JsonValueKind.Object:
                    return TryGetString(doc.RootElement, "version");
                default:
                    return null;
            }
        }
        catch (JsonException) { return null; }
    }

    // ---- groups / states parsers --------------------------------------

    internal static IReadOnlyList<ZammadGroup> ParseGroups(string body)
    {
        var array = TryRootArray(body);
        if (array is null) return Array.Empty<ZammadGroup>();
        var list = new List<ZammadGroup>(array.Value.GetArrayLength());
        foreach (var el in array.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetLong(el, "id");
            if (id is null) continue;
            var name = TryGetString(el, "name") ?? string.Empty;
            var active = TryGetBool(el, "active") ?? true;
            list.Add(new ZammadGroup(id.Value, name, active));
        }
        return list;
    }

    internal static IReadOnlyList<ZammadState> ParseStates(string body)
    {
        var array = TryRootArray(body);
        if (array is null) return Array.Empty<ZammadState>();
        var list = new List<ZammadState>(array.Value.GetArrayLength());
        foreach (var el in array.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetLong(el, "id");
            if (id is null) continue;
            var name = TryGetString(el, "name") ?? string.Empty;
            var stateTypeId = TryGetLong(el, "state_type_id");
            var active = TryGetBool(el, "active") ?? true;
            list.Add(new ZammadState(id.Value, name, stateTypeId, active));
        }
        return list;
    }

    internal static ZammadUser? ParseUser(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var id = TryGetLong(root, "id");
            if (id is null) return null;
            return new ZammadUser(
                Id: id.Value,
                Email: TryGetString(root, "email"),
                FirstName: TryGetString(root, "firstname"),
                LastName: TryGetString(root, "lastname"),
                Login: TryGetString(root, "login"),
                OrganizationId: TryGetLong(root, "organization_id"),
                Active: TryGetBool(root, "active") ?? true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<ZammadPriority> ParsePriorities(string body)
    {
        var array = TryRootArray(body);
        if (array is null) return Array.Empty<ZammadPriority>();
        var list = new List<ZammadPriority>(array.Value.GetArrayLength());
        foreach (var el in array.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetLong(el, "id");
            if (id is null) continue;
            var name = TryGetString(el, "name") ?? string.Empty;
            var active = TryGetBool(el, "active") ?? true;
            list.Add(new ZammadPriority(id.Value, name, active));
        }
        return list;
    }

    /// Parses the expand=true ticket-object Zammad returns from
    /// <c>GET /api/v1/tickets/{id}</c>. With <c>expand=true</c> the
    /// numeric *_id fields are mirrored by resolved string forms —
    /// <c>group</c>, <c>state</c>, <c>priority</c>, <c>organization</c>.
    ///
    /// Customer-email parsing has two paths: Zammad 6 puts the email
    /// in <c>customer_email</c> and uses <c>customer</c> for the display
    /// name; Zammad 7 dropped <c>customer_email</c> entirely and put the
    /// email string in <c>customer</c>. The parser tolerates both: it
    /// first reads <c>customer_email</c>; if that is null or doesn't
    /// look like an email, it tries <c>customer</c> and accepts it only
    /// when it contains <c>@</c> (otherwise it is a display name and
    /// stays out). The remaining string-relations are surfaced as-is so
    /// the worker can persist them in the snapshot for the records-page
    /// readability win.
    internal static ZammadTicket? ParseTicket(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var id = TryGetLong(root, "id");
            if (id is null) return null;

            // Email resolution: Zammad 6's customer_email first, then
            // Zammad 7's customer-as-email fallback. The @-check keeps
            // a Zammad 6 display name from being mistaken for an email.
            var customerEmail = TryGetString(root, "customer_email");
            if (string.IsNullOrWhiteSpace(customerEmail) || !customerEmail.Contains('@'))
            {
                var customerField = TryGetString(root, "customer");
                if (!string.IsNullOrWhiteSpace(customerField) && customerField.Contains('@'))
                {
                    customerEmail = customerField;
                }
                else if (string.IsNullOrWhiteSpace(customerEmail))
                {
                    customerEmail = null;
                }
            }

            return new ZammadTicket(
                Id: id.Value,
                Number: TryGetLongOrParse(root, "number"),
                Title: TryGetString(root, "title") ?? string.Empty,
                CustomerId: TryGetLong(root, "customer_id"),
                CustomerEmail: customerEmail,
                CustomerFirstName: TryGetString(root, "customer_firstname"),
                CustomerLastName: TryGetString(root, "customer_lastname"),
                OrganizationId: TryGetLong(root, "organization_id"),
                OrganizationName: TryGetString(root, "organization"),
                GroupId: TryGetLong(root, "group_id"),
                GroupName: TryGetString(root, "group"),
                StateId: TryGetLong(root, "state_id"),
                StateName: TryGetString(root, "state"),
                PriorityId: TryGetLong(root, "priority_id"),
                PriorityName: TryGetString(root, "priority"),
                ArticleCount: TryGetInt(root, "article_count"),
                CreatedAt: TryGetDateTimeOffset(root, "created_at"),
                UpdatedAt: TryGetDateTimeOffset(root, "updated_at"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// Zammad serializes <c>number</c> as a string ("12345") on some
    /// versions and a number on others. Both are valid; tolerate.
    private static long? TryGetLongOrParse(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(prop.GetString(), out var n) => n,
            _ => null,
        };
    }

    // ---- ticket search parser -----------------------------------------

    /// Zammad's <c>/tickets/search</c> uses two response shapes in
    /// practice. The default assets-envelope (no <c>expand</c>) returns
    /// <c>{ tickets: [&lt;id1&gt;, …], tickets_count: N, assets: { Ticket, User,
    /// Group, TicketState, … } }</c> — denormalised lookups across
    /// related objects. With <c>expand=true</c> the response can be a
    /// bare array of expanded ticket objects (relations rendered as
    /// flat string fields next to their <c>_id</c> counterparts). This
    /// parser handles both so an installer-side preference can't break
    /// the picker.
    internal static IReadOnlyList<ZammadTicketSearchItem> ParseSearchItems(
        string body, out int? total)
    {
        total = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            return Array.Empty<ZammadTicketSearchItem>();
        }
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Bare-array path (expand=true mode). Each entry is a full
            // ticket-object with flattened relations.
            if (root.ValueKind == JsonValueKind.Array)
            {
                return BuildItemsFromFlat(root, assets: null);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<ZammadTicketSearchItem>();
            }

            // Pull total: Zammad reports it under different keys across
            // versions. Zammad 6 + assets-mode = `tickets_count`. Zammad
            // 7 + records-mode = `total_count`. Some intermediate variants
            // expose `total`. Try them in order — first non-null wins.
            total = TryGetInt(root, "tickets_count")
                ?? TryGetInt(root, "total_count")
                ?? TryGetInt(root, "total");

            // Locate the ticket payload array. Zammad 7 dropped the
            // legacy `tickets` key in favour of `records` (undocumented
            // breaking change observed against 7.0). Older installs still
            // expose `tickets`. We accept either so the picker works
            // across versions without configuration.
            JsonElement ticketsEl = default;
            var foundArray = false;
            foreach (var key in new[] { "tickets", "records" })
            {
                if (root.TryGetProperty(key, out var probe)
                    && probe.ValueKind == JsonValueKind.Array)
                {
                    ticketsEl = probe;
                    foundArray = true;
                    break;
                }
            }
            if (!foundArray)
            {
                return Array.Empty<ZammadTicketSearchItem>();
            }

            JsonElement? assets = null;
            if (root.TryGetProperty("assets", out var assetsEl)
                && assetsEl.ValueKind == JsonValueKind.Object)
            {
                assets = assetsEl.Clone();
            }

            // Distinguish "array of ids" from "array of objects". A
            // single peek is enough — Zammad never mixes the two.
            var firstEntry = ticketsEl.GetArrayLength() == 0
                ? (JsonElement?)null
                : ticketsEl[0];
            if (firstEntry is { ValueKind: JsonValueKind.Number })
            {
                return BuildItemsFromIds(ticketsEl, assets);
            }
            return BuildItemsFromFlat(ticketsEl, assets);
        }
        catch (JsonException)
        {
            return Array.Empty<ZammadTicketSearchItem>();
        }
        finally
        {
            doc?.Dispose();
        }
    }

    /// Builds picker items from a list of bare ticket-objects (each row
    /// already carries the customer/group/state fields flat alongside
    /// the *_id integers). <paramref name="assets"/> is consulted only
    /// when a flat string is missing — handles "expand=false but Zammad
    /// returns full objects" installations gracefully.
    private static IReadOnlyList<ZammadTicketSearchItem> BuildItemsFromFlat(
        JsonElement array,
        JsonElement? assets)
    {
        var list = new List<ZammadTicketSearchItem>(array.GetArrayLength());
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetLong(el, "id");
            if (id is null) continue;
            list.Add(BuildItem(el, assets));
        }
        return list;
    }

    /// Builds picker items by walking an id-only ticket list and
    /// dereferencing each id into <c>assets.Ticket</c>. Assets-mode is
    /// the default Zammad shape and what the WebUI uses.
    private static IReadOnlyList<ZammadTicketSearchItem> BuildItemsFromIds(
        JsonElement idsArray,
        JsonElement? assets)
    {
        if (assets is null || !assets.Value.TryGetProperty("Ticket", out var tickets)
            || tickets.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<ZammadTicketSearchItem>();
        }
        var list = new List<ZammadTicketSearchItem>(idsArray.GetArrayLength());
        foreach (var idEl in idsArray.EnumerateArray())
        {
            if (idEl.ValueKind != JsonValueKind.Number) continue;
            if (!idEl.TryGetInt64(out var id)) continue;
            // assets.Ticket is keyed by the id-as-string.
            if (!tickets.TryGetProperty(id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var ticket))
                continue;
            if (ticket.ValueKind != JsonValueKind.Object) continue;
            list.Add(BuildItem(ticket, assets));
        }
        return list;
    }

    private static ZammadTicketSearchItem BuildItem(JsonElement ticket, JsonElement? assets)
    {
        var id = TryGetLong(ticket, "id") ?? 0;
        // Zammad's `number` is a STRING field carrying the human-readable
        // ticket number (often padded with leading zeros). We parse it
        // into a long for ordering, but stick with nullable so a future
        // alphanumeric numbering scheme doesn't crash the parser.
        long? number = null;
        if (ticket.TryGetProperty("number", out var numProp))
        {
            switch (numProp.ValueKind)
            {
                case JsonValueKind.Number when numProp.TryGetInt64(out var n):
                    number = n;
                    break;
                case JsonValueKind.String:
                    var s = numProp.GetString();
                    if (!string.IsNullOrEmpty(s)
                        && long.TryParse(s, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    {
                        number = parsed;
                    }
                    break;
            }
        }
        var title = TryGetString(ticket, "title") ?? string.Empty;
        var customerId = TryGetLong(ticket, "customer_id");
        var groupId = TryGetLong(ticket, "group_id");
        var stateId = TryGetLong(ticket, "state_id");
        var articleCount = TryGetInt(ticket, "article_count");
        var createdAt = TryGetDateTimeOffset(ticket, "created_at");
        var updatedAt = TryGetDateTimeOffset(ticket, "updated_at");

        // Relation strings: present when expand=true was set OR Zammad
        // returns full objects without flattening. Fall back to an
        // assets lookup when missing.
        var groupName = TryGetString(ticket, "group");
        if (string.IsNullOrEmpty(groupName) && groupId is not null && assets is not null)
        {
            groupName = LookupAssetField(assets.Value, "Group", groupId.Value, "name");
        }
        var stateName = TryGetString(ticket, "state");
        if (string.IsNullOrEmpty(stateName) && stateId is not null && assets is not null)
        {
            stateName = LookupAssetField(assets.Value, "TicketState", stateId.Value, "name");
        }

        string? customerName = null;
        string? customerEmail = null;
        var customerFlat = TryGetString(ticket, "customer");
        if (!string.IsNullOrEmpty(customerFlat))
        {
            // Zammad's flat customer field is rendered as
            // "Firstname Lastname <email@host>". Split it on the angle
            // brackets so we surface both the display label and the
            // pure email — the picker shows the email column.
            ParseCustomerLabel(customerFlat, out customerName, out customerEmail);
        }
        if ((string.IsNullOrEmpty(customerEmail) || string.IsNullOrEmpty(customerName))
            && customerId is not null && assets is not null
            && TryGetAssetObject(assets.Value, "User", customerId.Value, out var userEl))
        {
            customerEmail ??= TryGetString(userEl, "email");
            if (string.IsNullOrEmpty(customerName))
            {
                var fn = TryGetString(userEl, "firstname");
                var ln = TryGetString(userEl, "lastname");
                var joined = string.Join(" ", new[] { fn, ln }.Where(s => !string.IsNullOrEmpty(s)));
                if (!string.IsNullOrEmpty(joined)) customerName = joined;
            }
        }

        return new ZammadTicketSearchItem(
            Id: id,
            Number: number,
            Title: title,
            CustomerId: customerId,
            CustomerEmail: customerEmail,
            CustomerName: customerName,
            GroupId: groupId,
            GroupName: groupName,
            StateId: stateId,
            StateName: stateName,
            ArticleCount: articleCount,
            CreatedAt: createdAt,
            UpdatedAt: updatedAt);
    }

    /// Parses Zammad's "Firstname Lastname <email>" customer label. Both
    /// halves are optional — a label without an email leaves
    /// <paramref name="email"/> null, an email-only label leaves
    /// <paramref name="name"/> null.
    internal static void ParseCustomerLabel(string label, out string? name, out string? email)
    {
        name = null;
        email = null;
        var open = label.IndexOf('<');
        var close = label.IndexOf('>');
        if (open >= 0 && close > open)
        {
            var emailPart = label.Substring(open + 1, close - open - 1).Trim();
            if (emailPart.Length > 0) email = emailPart;
            var namePart = label[..open].Trim();
            if (namePart.Length > 0) name = namePart;
            return;
        }
        // No angle brackets — if it looks like an email keep it as
        // email, otherwise treat the whole string as a display name.
        if (label.Contains('@')) email = label.Trim();
        else name = label.Trim();
    }

    private static bool TryGetAssetObject(JsonElement assets, string typeName, long id, out JsonElement result)
    {
        result = default;
        if (!assets.TryGetProperty(typeName, out var bucket)
            || bucket.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!bucket.TryGetProperty(key, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        result = obj;
        return true;
    }

    private static string? LookupAssetField(JsonElement assets, string typeName, long id, string field)
    {
        if (!TryGetAssetObject(assets, typeName, id, out var obj)) return null;
        return TryGetString(obj, field);
    }

    private static JsonElement? TryRootArray(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.Clone()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool? TryGetBool(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static int? TryGetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var v) => v,
            JsonValueKind.String when int.TryParse(prop.GetString(), out var v) => v,
            _ => null,
        };
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        var raw = prop.GetString();
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var v)
            ? v
            : (DateTimeOffset?)null;
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

    private static long? TryGetLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt64(out var v) => v,
            JsonValueKind.String when long.TryParse(prop.GetString(), out var v) => v,
            _ => null,
        };
    }
}
