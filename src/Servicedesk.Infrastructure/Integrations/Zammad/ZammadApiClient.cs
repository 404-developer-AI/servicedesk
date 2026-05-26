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

    public async Task<IReadOnlyList<ZammadArticle>> ListArticlesAsync(long ticketId, CancellationToken ct)
    {
        try
        {
            var body = await SendAsync(
                eventType: ZammadEventTypes.ArticlesList,
                method: HttpMethod.Get,
                path: $"/api/v1/ticket_articles/by_ticket/{ticketId}",
                ct: ct);
            return ParseArticles(body);
        }
        catch (ZammadApiException ex) when (ex.HttpStatus == 404)
        {
            return Array.Empty<ZammadArticle>();
        }
    }

    public async Task<Stream> FetchAttachmentBytesAsync(
        long ticketId,
        long articleId,
        long attachmentId,
        CancellationToken ct)
    {
        var path = $"/api/v1/ticket_attachment/{ticketId}/{articleId}/{attachmentId}";
        return await SendBinaryAsync(
            eventType: ZammadEventTypes.AttachmentFetch,
            path: path,
            ct: ct);
    }

    // ---- KB import (v0.0.43) ------------------------------------------

    public async Task<IReadOnlyList<ZammadKnowledgeBase>> ListKnowledgeBasesAsync(CancellationToken ct)
    {
        // Zammad's bulk-init endpoint is `POST /api/v1/knowledge_bases/init`
        // (confirmed by the official API docs + the upstream routes file).
        // A GET on this path falls through to `GET /:id` and gets
        // misinterpreted as `:id="init"`. The endpoint returns the full
        // admin graph in one call — we project the KB headers for the
        // picker step and discard the rest. The Build-proposal phase hits
        // the same endpoint again for the full bundle; for typical KBs
        // (<5K answers) the cost is sub-second.
        //
        // includeSuccessBodySnippet=true captures the response payload on
        // the audit row so an admin can inspect the exact shape when the
        // picker comes up empty (helps when Zammad version changes the
        // assets-collection class names).
        var body = await SendAsync(
            eventType: ZammadEventTypes.KbList,
            method: HttpMethod.Post,
            path: "/api/v1/knowledge_bases/init",
            ct: ct,
            includeSuccessBodySnippet: true,
            jsonBody: "{}");
        var init = ParseKnowledgeBaseInit(body);
        return init.KnowledgeBases;
    }

    public async Task<ZammadKbInit> GetKnowledgeBaseInitAsync(CancellationToken ct)
    {
        var body = await SendAsync(
            eventType: ZammadEventTypes.KbInit,
            method: HttpMethod.Post,
            path: "/api/v1/knowledge_bases/init",
            ct: ct,
            includeSuccessBodySnippet: true,
            jsonBody: "{}");
        return ParseKnowledgeBaseInit(body);
    }

    public async Task<ZammadKbAnswerDetail?> GetKnowledgeBaseAnswerWithContentAsync(
        long knowledgeBaseId,
        long answerId,
        long translationId,
        CancellationToken ct)
    {
        try
        {
            // ?include_contents={tid} tells Zammad to inline the
            // translation_content row alongside the answer. The exact
            // response shape is assets-mode like /init, so the parser
            // walks the same KnowledgeBase* keys it already understands.
            //
            // The kb-id prefix on the path is required — the un-prefixed
            // `/knowledge_bases/answers/{id}` route 404s on real installs
            // (only documented as a routes-file entry, not actually
            // exposed).
            var path = $"/api/v1/knowledge_bases/{knowledgeBaseId}/answers/{answerId}?include_contents={translationId}";
            var body = await SendAsync(
                eventType: ZammadEventTypes.KbAnswerGet,
                method: HttpMethod.Get,
                path: path,
                ct: ct,
                includeSuccessBodySnippet: true);
            return ParseKnowledgeBaseAnswerDetail(body, answerId, translationId);
        }
        catch (ZammadApiException ex) when (ex.HttpStatus == 404)
        {
            return null;
        }
    }

    public async Task<Stream> FetchKnowledgeBaseAttachmentBytesAsync(
        long knowledgeBaseId,
        long answerId,
        long attachmentId,
        CancellationToken ct)
    {
        // KB attachments are served via the polymorphic Zammad attachment
        // endpoint `/api/v1/attachments/{id}` — the `url` field on each
        // answer's attachment entry points there directly. There is no
        // nested `/knowledge_bases/{kb}/answers/{a}/attachments/{id}`
        // download route in current Zammad. knowledgeBaseId + answerId
        // are kept on the signature for audit-row context (a future
        // version could include them in the audit payload), but the
        // download itself only needs the attachment id.
        var path = $"/api/v1/attachments/{attachmentId}";
        return await SendBinaryAsync(
            eventType: ZammadEventTypes.KbAttachmentFetch,
            path: path,
            ct: ct);
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
            ct: ct);
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
        bool includeSuccessBodySnippet = false,
        string? jsonBody = null)
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
        if (jsonBody is not null)
        {
            // Rails endpoints that route via :id-style patterns expect a
            // POST body even when the action takes no parameters; an empty
            // "{}" satisfies the JSON parser without polluting the call.
            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
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
                // the search-call during fase 2 + KB-import for the
                // first install. Adds a `shape` summary (top-level keys
                // + array-lengths) so the response structure is visible
                // even when the body is too large for the snippet.
                // 16K snippet cap accommodates per-answer payloads where
                // body HTML follows ~3K of asset metadata.
                object? successPayload = includeSuccessBodySnippet
                    ? new { snippet = Truncate(responseBody, 16384), shape = DescribeJsonShape(responseBody) }
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

    /// Binary sibling of <see cref="SendAsync"/>. Used by the attachment
    /// fetcher: we don't want to materialise the full body as a string for
    /// audit-log truncation (megabyte responses are common), and we want a
    /// stream the caller can hand straight to <c>IBlobStore</c>. Success
    /// rows still land in <c>integration_audit</c> — payload carries the
    /// content-length + content-type Zammad advertised, never the bytes.
    /// On non-2xx we drain the body to a string (cheap because errors are
    /// small) so the audit-row gets the same snippet treatment.
    private async Task<Stream> SendBinaryAsync(
        string eventType,
        string path,
        CancellationToken ct)
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
                "Zammad API token is not configured.",
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
                "Zammad base URL is not configured.",
                upstreamErrorCode: "base_url_missing");
        }

        var url = baseUrl + path;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Token token={token}");
        // Don't restrict Accept — Zammad returns the original
        // Content-Type of the file (image/png, application/pdf, …).

        var http = _httpClientFactory.CreateClient(HttpClientName);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            // ResponseHeadersRead so we stream the body instead of buffering.
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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

        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            string errorBody;
            try { errorBody = await response.Content.ReadAsStringAsync(ct); }
            catch { errorBody = string.Empty; }
            response.Dispose();
            var upstreamCode = TryParseErrorCode(errorBody, status);
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
                Payload: new { snippet = Truncate(errorBody, 256) }), ct);
            throw new ZammadApiException(
                BuildHttpMessage(status, upstreamCode),
                httpStatus: status,
                upstreamErrorCode: upstreamCode);
        }

        // Success — open the stream and pass it back. Wrap it so disposing
        // the stream also disposes the underlying HttpResponseMessage so
        // the connection returns to the pool.
        await _audit.LogAsync(new IntegrationAuditEvent(
            Integration: ZammadEventTypes.Integration,
            EventType: eventType,
            Outcome: IntegrationAuditOutcome.Ok,
            Endpoint: path,
            HttpStatus: status,
            LatencyMs: (int)stopwatch.ElapsedMilliseconds,
            Payload: new
            {
                contentLength = response.Content.Headers.ContentLength,
                contentType = response.Content.Headers.ContentType?.MediaType,
            }), ct);
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return new ResponseDisposingStream(stream, response);
    }

    /// Stream wrapper that disposes both the underlying response message
    /// and the content stream when the consumer is done. Needed so the
    /// importer can <c>await using var stream = …</c> and the pooled
    /// HTTP connection still returns to the pool.
    private sealed class ResponseDisposingStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;
        public ResponseDisposingStream(Stream inner, HttpResponseMessage response)
        { _inner = inner; _response = response; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => _inner.ReadAsync(buffer, ct);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            _response.Dispose();
            await base.DisposeAsync();
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

    /// Diagnostic helper: produces a one-line summary of a JSON
    /// response's top-level shape so the audit log carries the structure
    /// even when the snippet truncates before the interesting parts.
    /// Output looks like:
    ///   "object{knowledge_bases[3],assets{KnowledgeBase[1],...},kb_locales[2]}"
    ///   "array[42]"
    /// Returns "non-json" when the body isn't parseable JSON.
    private static string DescribeJsonShape(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "empty";
        try
        {
            using var doc = JsonDocument.Parse(body);
            return DescribeElement(doc.RootElement, depth: 0);
        }
        catch (JsonException)
        {
            return "non-json";
        }
    }

    private static string DescribeElement(JsonElement el, int depth)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Object => DescribeObject(el, depth),
            JsonValueKind.Array  => $"array[{el.GetArrayLength()}]",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "bool",
            JsonValueKind.Null   => "null",
            _ => "?",
        };
    }

    private static string DescribeObject(JsonElement obj, int depth)
    {
        // Cap recursion at 2 levels so the assets envelope's inner-most
        // keys don't blow up the summary string.
        if (depth >= 2) return "object{...}";
        var sb = new System.Text.StringBuilder();
        sb.Append("object{");
        var first = true;
        foreach (var prop in obj.EnumerateObject())
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(prop.Name);
            sb.Append(':');
            sb.Append(DescribeElement(prop.Value, depth + 1));
        }
        sb.Append('}');
        return sb.ToString();
    }

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

    /// Parses the article list returned by
    /// <c>GET /api/v1/ticket_articles/by_ticket/{id}</c>. Each entry is
    /// a flat object — Zammad has no assets-mode for this endpoint. We
    /// keep the body as the raw upstream string; mapping the HTML/text
    /// distinction onto our own <c>body_text</c> / <c>body_html</c>
    /// happens in the writer because the decision is consumer-side.
    internal static IReadOnlyList<ZammadArticle> ParseArticles(string body)
    {
        var array = TryRootArray(body);
        if (array is null) return Array.Empty<ZammadArticle>();
        var list = new List<ZammadArticle>(array.Value.GetArrayLength());
        foreach (var el in array.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetLong(el, "id");
            if (id is null) continue;
            list.Add(new ZammadArticle(
                Id: id.Value,
                TicketId: TryGetLong(el, "ticket_id") ?? 0,
                Type: TryGetString(el, "type"),
                Sender: TryGetString(el, "sender"),
                From: TryGetString(el, "from"),
                To: TryGetString(el, "to"),
                Subject: TryGetString(el, "subject"),
                Body: TryGetString(el, "body"),
                ContentType: TryGetString(el, "content_type"),
                Internal: TryGetBool(el, "internal") ?? false,
                CreatedById: TryGetLong(el, "created_by_id"),
                CreatedByEmail: TryGetString(el, "created_by"),
                CreatedAt: TryGetDateTimeOffset(el, "created_at"),
                MessageId: TryGetString(el, "message_id"),
                Attachments: ParseArticleAttachments(el)));
        }
        return list;
    }

    /// Reads the <c>attachments[]</c> array off one article-list entry.
    /// Zammad returns each attachment as
    /// <c>{ id, filename, size, preferences: { "Mime-Type", "Content-Type",
    ///       "Content-ID", "Content-Disposition" } }</c>. Size is a string
    /// of bytes on some versions and a JSON number on others, so we accept
    /// both. Mime-type is sourced from preferences (Zammad's canonical
    /// location); missing → application/octet-stream so the local row has
    /// a non-empty MIME column (which is NOT NULL).
    ///
    /// Inline disposition is encoded two ways across versions:
    /// <list type="bullet">
    /// <item><c>preferences["Content-Disposition"]</c> = <c>"inline"</c>
    /// — modern Zammad.</item>
    /// <item>Presence of a non-empty <c>Content-ID</c> alone — older
    /// versions sometimes omit Content-Disposition for inline images.</item>
    /// </list>
    /// We treat either as inline. The cid token itself is normalised
    /// (stripped of angle brackets) so it matches the html body's
    /// <c>cid:&lt;token&gt;</c> form after the local rewriter.
    internal static IReadOnlyList<ZammadArticleAttachment> ParseArticleAttachments(JsonElement article)
    {
        if (!article.TryGetProperty("attachments", out var arr)
            || arr.ValueKind != JsonValueKind.Array
            || arr.GetArrayLength() == 0)
        {
            return Array.Empty<ZammadArticleAttachment>();
        }
        var list = new List<ZammadArticleAttachment>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetLong(el, "id");
            if (id is null) continue;

            var filename = TryGetString(el, "filename") ?? string.Empty;
            var size = TryGetLong(el, "size") ?? 0;

            string? mime = null;
            string? contentId = null;
            string? disposition = null;
            if (el.TryGetProperty("preferences", out var prefs)
                && prefs.ValueKind == JsonValueKind.Object)
            {
                mime = TryGetString(prefs, "Mime-Type")
                    ?? TryGetString(prefs, "Content-Type");
                contentId = TryGetString(prefs, "Content-ID");
                disposition = TryGetString(prefs, "Content-Disposition");
            }
            if (string.IsNullOrWhiteSpace(mime))
            {
                mime = "application/octet-stream";
            }

            var normalisedCid = string.IsNullOrWhiteSpace(contentId)
                ? null
                : contentId.Trim().Trim('<', '>');
            var isInline = string.Equals(disposition, "inline", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(normalisedCid);

            list.Add(new ZammadArticleAttachment(
                Id: id.Value,
                Filename: filename,
                SizeBytes: size,
                MimeType: mime,
                IsInline: isInline,
                ContentId: normalisedCid));
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
                UpdatedAt: TryGetDateTimeOffset(root, "updated_at"),
                // pending_time is only set when state_type is pending_*;
                // null on regular tickets. Import keeps it so a Zammad
                // pending-reminder lands as a local pending-till.
                PendingTime: TryGetDateTimeOffset(root, "pending_time"));
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

    // ---- KB parsers (v0.0.43) -----------------------------------------

    /// Parses Zammad's <c>/api/v1/knowledge_bases</c> response. The
    /// endpoint returns a bare array of KB-objects. Zammad doesn't
    /// expose a top-level "name" field — the display label comes from
    /// the first locale's title or, as a final fallback, "Knowledge
    /// base #&lt;id&gt;" so the picker always has something to render.
    /// Category/answer counts are best-effort: not every Zammad version
    /// includes them on the list-endpoint, in which case they read as 0
    /// and the UI shows "—".
    internal static IReadOnlyList<ZammadKnowledgeBase> ParseKnowledgeBases(string body)
    {
        var array = TryRootArray(body);
        if (array is null) return Array.Empty<ZammadKnowledgeBase>();
        var list = new List<ZammadKnowledgeBase>(array.Value.GetArrayLength());
        foreach (var el in array.Value.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetLong(el, "id");
            if (id is null) continue;
            // Active flag — Zammad uses "active" or "kb_active" depending on version.
            var active = TryGetBool(el, "active") ?? TryGetBool(el, "kb_active") ?? true;
            // Name: try several common fallback fields; many installs
            // surface a translation-derived label only.
            var name = TryGetString(el, "name")
                ?? TryGetString(el, "title")
                ?? TryGetString(el, "url_prefix")
                ?? TryReadFirstLocaleTitle(el)
                ?? $"Knowledge base #{id.Value}";
            var defaultLocale = TryReadDefaultLocaleCode(el);
            var categoryCount = TryGetInt(el, "category_count") ?? 0;
            var answerCount = TryGetInt(el, "answer_count") ?? 0;
            list.Add(new ZammadKnowledgeBase(
                Id: id.Value,
                Name: name,
                Active: active,
                DefaultLocale: defaultLocale,
                CategoryCount: categoryCount,
                AnswerCount: answerCount));
        }
        return list;
    }

    private static string? TryReadFirstLocaleTitle(JsonElement kbEl)
    {
        if (!kbEl.TryGetProperty("kb_locales", out var arr)
            || arr.ValueKind != JsonValueKind.Array
            || arr.GetArrayLength() == 0)
        {
            return null;
        }
        foreach (var loc in arr.EnumerateArray())
        {
            if (loc.ValueKind != JsonValueKind.Object) continue;
            var label = TryGetString(loc, "title")
                ?? TryGetString(loc, "name");
            if (!string.IsNullOrWhiteSpace(label)) return label;
        }
        return null;
    }

    private static string? TryReadDefaultLocaleCode(JsonElement kbEl)
    {
        // Zammad surfaces the primary locale either as a top-level
        // primary_locale_id (numeric, requires lookup) or as a flag on
        // the kb_locales[] entries (primary: true). Take whichever is
        // present and return its bcp-47 string.
        if (kbEl.TryGetProperty("kb_locales", out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var loc in arr.EnumerateArray())
            {
                if (loc.ValueKind != JsonValueKind.Object) continue;
                var isPrimary = TryGetBool(loc, "primary") ?? false;
                if (!isPrimary) continue;
                var code = TryGetString(loc, "system_locale")
                    ?? TryGetString(loc, "locale")
                    ?? TryGetString(loc, "name");
                if (!string.IsNullOrWhiteSpace(code)) return code;
            }
        }
        return null;
    }

    /// Parses the bundle returned by
    /// <c>GET /api/v1/knowledge_bases/init</c>. Zammad's response shape
    /// varies across versions — we accept both the flat-collection layout
    /// and the assets-envelope layout, tolerating missing collections so
    /// a partial response still produces a usable bundle (the importer
    /// will surface missing pieces via audit + records).
    ///
    /// Collections we look for:
    /// <list type="bullet">
    /// <item><c>knowledge_bases[]</c> — KB headers</item>
    /// <item><c>knowledge_base_categories[]</c> (or <c>categories[]</c>)</item>
    /// <item><c>knowledge_base_answers[]</c> (or <c>answers[]</c>)</item>
    /// <item><c>knowledge_base_answer_translations[]</c> (or
    /// <c>answer_translations[]</c>)</item>
    /// <item><c>knowledge_base_answer_translation_contents[]</c> (or
    /// <c>answer_translation_contents[]</c>)</item>
    /// <item><c>knowledge_base_category_translations[]</c> (or
    /// <c>category_translations[]</c>)</item>
    /// <item><c>knowledge_base_locales[]</c> (or <c>kb_locales[]</c>) —
    /// drives the locale-list surfaced to the SPA</item>
    /// </list>
    internal static ZammadKbInit ParseKnowledgeBaseInit(string body)
    {
        var empty = new ZammadKbInit(
            Array.Empty<ZammadKnowledgeBase>(),
            Array.Empty<ZammadKbCategory>(),
            Array.Empty<ZammadKbAnswer>(),
            Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(body)) return empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return empty;

            // POST /knowledge_bases/init returns assets-mode directly at
            // root level (no `assets` wrapper). The keys are
            // <ClassName>:object{<id>:object{...}} for every KB
            // collection — KnowledgeBase, KnowledgeBaseTranslation,
            // KnowledgeBaseLocale, KnowledgeBaseCategory, etc.
            //
            // We accept three shapes for robustness:
            //   (a) flat top-level arrays (`knowledge_bases[]`, …)
            //   (b) wrapped assets (`root.assets.KnowledgeBase`)
            //   (c) flat assets (root itself acts as the assets envelope —
            //       what manage/init returns)
            JsonElement? assets;
            if (root.TryGetProperty("assets", out var assetsEl)
                && assetsEl.ValueKind == JsonValueKind.Object)
            {
                assets = assetsEl;
            }
            else if (root.TryGetProperty("KnowledgeBase", out _))
            {
                assets = root;
            }
            else
            {
                assets = null;
            }

            // Lookups consumed by KB + category + answer parsing. Built
            // up-front from translation/locale collections so the row
            // parsers don't re-walk the bundle per element.
            var kbTitleByKbId = BuildKbTitleLookup(
                EnumerateAssetCollection(assets,
                    "KnowledgeBaseTranslation", "KnowledgeBase::Translation"));
            var primaryKbLocaleId = FindPrimaryKbLocaleId(
                EnumerateAssetCollection(assets,
                    "KnowledgeBaseLocale", "KnowledgeBase::Locale"));

            var kbArray = TryGetArrayProperty(root, "knowledge_bases");
            var kbs = kbArray is not null
                ? ParseKnowledgeBaseObjects(kbArray.Value)
                : ParseKnowledgeBasesFromAssets(assets, kbTitleByKbId);

            // Category translations may live at the flat top-level or in
            // assets under either ::-name or PascalCase-without-:: name.
            var categoryTranslationsArray = TryGetArrayProperty(root,
                "knowledge_base_category_translations", "category_translations");
            IEnumerable<JsonElement> categoryTranslations = categoryTranslationsArray is not null
                ? categoryTranslationsArray.Value.EnumerateArray()
                : EnumerateAssetCollection(assets,
                    "KnowledgeBase::Category::Translation", "KnowledgeBaseCategoryTranslation");
            var categoryTitles = BuildCategoryTitleLookup(categoryTranslations, primaryKbLocaleId);

            var categoriesArray = TryGetArrayProperty(root,
                "knowledge_base_categories", "categories");
            IEnumerable<JsonElement> categoryEls = categoriesArray is not null
                ? categoriesArray.Value.EnumerateArray()
                : EnumerateAssetCollection(assets,
                    "KnowledgeBase::Category", "KnowledgeBaseCategory");
            var categories = ParseCategoriesFromElements(categoryEls, categoryTitles);

            // Answer translations + their content texts.
            var translationContentsArray = TryGetArrayProperty(root,
                "knowledge_base_answer_translation_contents", "answer_translation_contents");
            IEnumerable<JsonElement> contentEls = translationContentsArray is not null
                ? translationContentsArray.Value.EnumerateArray()
                : EnumerateAssetCollection(assets,
                    "KnowledgeBase::Answer::Translation::Content",
                    "KnowledgeBaseAnswerTranslationContent");
            var contentsByTranslationId = BuildContentLookupFromElements(contentEls);

            var translationsArray = TryGetArrayProperty(root,
                "knowledge_base_answer_translations", "answer_translations");
            IEnumerable<JsonElement> translationEls = translationsArray is not null
                ? translationsArray.Value.EnumerateArray()
                : EnumerateAssetCollection(assets,
                    "KnowledgeBase::Answer::Translation", "KnowledgeBaseAnswerTranslation");
            var translationsByAnswerId = BuildAnswerTranslationLookupFromElements(
                translationEls, contentsByTranslationId, primaryKbLocaleId);

            var answersArray = TryGetArrayProperty(root,
                "knowledge_base_answers", "answers");
            IEnumerable<JsonElement> answerEls = answersArray is not null
                ? answersArray.Value.EnumerateArray()
                : EnumerateAssetCollection(assets,
                    "KnowledgeBase::Answer", "KnowledgeBaseAnswer");
            var answers = ParseAnswersFromElements(answerEls, translationsByAnswerId);

            var localesArray = TryGetArrayProperty(root,
                "knowledge_base_locales", "kb_locales", "locales");
            IEnumerable<JsonElement> localeEls = localesArray is not null
                ? localesArray.Value.EnumerateArray()
                : EnumerateAssetCollection(assets,
                    "KnowledgeBase::Locale", "KnowledgeBaseLocale");
            var locales = ParseLocalesFromElements(localeEls);

            return new ZammadKbInit(kbs, categories, answers, locales);
        }
        catch (JsonException)
        {
            return empty;
        }
    }

    /// Walks an assets-mode collection (object keyed by id-as-string)
    /// trying each candidate key in order — Zammad serializes the
    /// inner ::-namespaced class either as "KnowledgeBase::Category"
    /// or "KnowledgeBaseCategory" depending on jbuilder template.
    private static IEnumerable<JsonElement> EnumerateAssetCollection(
        JsonElement? assets, params string[] candidateKeys)
    {
        if (assets is null) yield break;
        foreach (var key in candidateKeys)
        {
            if (!assets.Value.TryGetProperty(key, out var bucket)) continue;
            if (bucket.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in bucket.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    yield return prop.Value;
                }
            }
            yield break;
        }
    }

    private static IReadOnlyList<ZammadKnowledgeBase> ParseKnowledgeBasesFromAssets(
        JsonElement? assets,
        IReadOnlyDictionary<long, string> kbTitleByKbId)
    {
        var list = new List<ZammadKnowledgeBase>();
        foreach (var el in EnumerateAssetCollection(assets, "KnowledgeBase"))
        {
            var id = TryGetLong(el, "id");
            if (id is null) continue;
            // Zammad's KB object carries no direct name/title — the
            // display label lives on a `KnowledgeBaseTranslation` row
            // joined via knowledge_base_id. Fall back to url_prefix /
            // first-locale-title only when no translation matched.
            string? title = kbTitleByKbId.TryGetValue(id.Value, out var t) ? t : null;
            // category_ids[].length + answer_ids[].length give us
            // counts without scanning the full bundle.
            var categoryCount = CountArrayProperty(el, "category_ids")
                ?? TryGetInt(el, "category_count") ?? 0;
            var answerCount = CountArrayProperty(el, "answer_ids")
                ?? TryGetInt(el, "answer_count") ?? 0;
            list.Add(new ZammadKnowledgeBase(
                Id: id.Value,
                Name: title
                    ?? TryGetString(el, "name")
                    ?? TryGetString(el, "title")
                    ?? TryGetString(el, "url_prefix")
                    ?? TryReadFirstLocaleTitle(el)
                    ?? $"Knowledge base #{id.Value}",
                Active: TryGetBool(el, "active") ?? TryGetBool(el, "kb_active") ?? true,
                DefaultLocale: TryReadDefaultLocaleCode(el),
                CategoryCount: categoryCount,
                AnswerCount: answerCount));
        }
        return list;
    }

    private static int? CountArrayProperty(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.Array)
        {
            return prop.GetArrayLength();
        }
        return null;
    }

    /// Builds the knowledge_base_id → title map from a stream of
    /// KnowledgeBaseTranslation rows. Each translation row carries
    /// `knowledge_base_id`, `kb_locale_id`, and `title`. We only keep
    /// the first non-empty title per KB — primary locale wins because
    /// Zammad's translations are returned in primary-first order, but
    /// even if they weren't a non-empty fallback is better than no
    /// label.
    private static IReadOnlyDictionary<long, string> BuildKbTitleLookup(
        IEnumerable<JsonElement> translations)
    {
        var result = new Dictionary<long, string>();
        foreach (var el in translations)
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var kbId = TryGetLong(el, "knowledge_base_id");
            var title = TryGetString(el, "title");
            if (kbId is null || string.IsNullOrWhiteSpace(title)) continue;
            if (!result.ContainsKey(kbId.Value)) result[kbId.Value] = title;
        }
        return result;
    }

    /// Scans KnowledgeBaseLocale rows for the primary entry and
    /// returns its `id` — that id is what every translation row's
    /// `kb_locale_id` field references. Returns null when no row is
    /// flagged primary (Zammad always seeds one, so this should not
    /// happen on a real install — but the importer treats null as
    /// "any locale matches" so the picker still renders).
    private static long? FindPrimaryKbLocaleId(IEnumerable<JsonElement> locales)
    {
        foreach (var el in locales)
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (TryGetBool(el, "primary") == true)
            {
                var id = TryGetLong(el, "id");
                if (id is not null) return id.Value;
            }
        }
        return null;
    }

    /// Local synthetic locale code emitted for primary-locale
    /// translation rows. The user picked nl-BE as the only import
    /// locale in the v0.0.43 scope decisions, and the parser is
    /// Zammad-aware enough to treat the source's primary as our
    /// default. Stored as a constant so test expectations can
    /// reference it.
    internal const string PrimaryLocaleEmitCode = "nl-BE";

    private static JsonElement? TryGetArrayProperty(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop)
                && prop.ValueKind == JsonValueKind.Array)
            {
                return prop;
            }
        }
        return null;
    }

    private static IReadOnlyList<ZammadKnowledgeBase> ParseKnowledgeBaseObjects(JsonElement array)
    {
        var list = new List<ZammadKnowledgeBase>(array.GetArrayLength());
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetLong(el, "id");
            if (id is null) continue;
            list.Add(new ZammadKnowledgeBase(
                Id: id.Value,
                Name: TryGetString(el, "name")
                    ?? TryGetString(el, "title")
                    ?? TryGetString(el, "url_prefix")
                    ?? TryReadFirstLocaleTitle(el)
                    ?? $"Knowledge base #{id.Value}",
                Active: TryGetBool(el, "active") ?? TryGetBool(el, "kb_active") ?? true,
                DefaultLocale: TryReadDefaultLocaleCode(el),
                CategoryCount: TryGetInt(el, "category_count") ?? 0,
                AnswerCount: TryGetInt(el, "answer_count") ?? 0));
        }
        return list;
    }

    private static IReadOnlyDictionary<long, Dictionary<string, string>> BuildCategoryTitleLookup(
        IEnumerable<JsonElement> translations,
        long? primaryKbLocaleId)
    {
        var result = new Dictionary<long, Dictionary<string, string>>();
        foreach (var el in translations)
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var categoryId = TryGetLong(el, "category_id");
            var title = TryGetString(el, "title");
            if (categoryId is null || string.IsNullOrWhiteSpace(title)) continue;
            // Determine the locale-code to emit under. When the row's
            // numeric kb_locale_id matches the source primary, we emit
            // under our import locale (nl-BE) so the picker filter hits.
            var locale = ResolveEmitLocale(el, primaryKbLocaleId);
            if (string.IsNullOrWhiteSpace(locale)) continue;
            if (!result.TryGetValue(categoryId.Value, out var perLocale))
            {
                perLocale = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[categoryId.Value] = perLocale;
            }
            perLocale[locale] = title;
        }
        return result;
    }

    /// Resolves the locale code to emit a translation row under. The
    /// row carries either a numeric `kb_locale_id` (assets-mode) or a
    /// string locale field (`system_locale` / `locale`). When the
    /// numeric id matches the source primary, we collapse onto our
    /// import-locale constant so consumers don't need to know Zammad's
    /// per-install id mapping.
    private static string? ResolveEmitLocale(JsonElement el, long? primaryKbLocaleId)
    {
        var localeId = TryGetLong(el, "kb_locale_id");
        if (localeId is not null
            && primaryKbLocaleId is not null
            && localeId.Value == primaryKbLocaleId.Value)
        {
            return PrimaryLocaleEmitCode;
        }
        // Fallback to whatever string Zammad surfaced — older flat-mode
        // responses carry `system_locale: "nl-BE"`.
        return TryReadTranslationLocaleCode(el);
    }

    /// Zammad encodes locale on translation rows either as a string
    /// field (<c>kb_locale_system</c> or <c>locale</c>) or via a numeric
    /// <c>kb_locale_id</c> that needs a lookup. We accept either and
    /// fall back to <c>locale</c> when needed. Numeric ids that can't be
    /// resolved at parse-time are surfaced as the bare integer string —
    /// the importer treats unrecognised locales as a soft skip.
    private static string? TryReadTranslationLocaleCode(JsonElement el)
    {
        var s = TryGetString(el, "kb_locale_system")
            ?? TryGetString(el, "system_locale")
            ?? TryGetString(el, "locale_code")
            ?? TryGetString(el, "locale");
        if (!string.IsNullOrWhiteSpace(s)) return s;
        var localeId = TryGetLong(el, "kb_locale_id");
        return localeId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<ZammadKbCategory> ParseCategoriesFromElements(
        IEnumerable<JsonElement> elements,
        IReadOnlyDictionary<long, Dictionary<string, string>> categoryTitles)
    {
        var list = new List<ZammadKbCategory>();
        foreach (var el in elements)
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetLong(el, "id");
            if (id is null) continue;
            var translations = new List<ZammadKbCategoryTranslation>();
            if (categoryTitles.TryGetValue(id.Value, out var perLocale))
            {
                foreach (var kv in perLocale)
                {
                    translations.Add(new ZammadKbCategoryTranslation(kv.Key, kv.Value));
                }
            }
            list.Add(new ZammadKbCategory(
                Id: id.Value,
                KnowledgeBaseId: TryGetLong(el, "knowledge_base_id") ?? 0,
                ParentId: TryGetLong(el, "parent_id"),
                Position: TryGetInt(el, "position") ?? 0,
                Translations: translations));
        }
        return list;
    }

    private static IReadOnlyDictionary<long, string> BuildContentLookupFromElements(
        IEnumerable<JsonElement> contents)
    {
        // translation_id → body_html. Zammad's translation_content row
        // carries either body (HTML) or body_text alongside a
        // content_type discriminator. We prefer the HTML form because
        // it preserves formatting; if Zammad returned plain text only,
        // we wrap it minimally so the local sanitizer accepts it.
        var result = new Dictionary<long, string>();
        foreach (var el in contents)
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetLong(el, "id");
            if (id is null) continue;
            // Zammad uses translation_id on some versions and a flat
            // 1:1 join on others — fall back to id when explicit
            // translation_id is missing.
            var translationId = TryGetLong(el, "translation_id") ?? id.Value;
            var body = TryGetString(el, "body");
            if (string.IsNullOrEmpty(body))
            {
                var bodyText = TryGetString(el, "body_text");
                if (!string.IsNullOrEmpty(bodyText))
                {
                    body = "<p>" + System.Net.WebUtility.HtmlEncode(bodyText).Replace("\n", "<br/>") + "</p>";
                }
            }
            result[translationId] = body ?? string.Empty;
        }
        return result;
    }

    private static IReadOnlyDictionary<long, List<ZammadKbAnswerTranslation>> BuildAnswerTranslationLookupFromElements(
        IEnumerable<JsonElement> translations,
        IReadOnlyDictionary<long, string> contentsByTranslationId,
        long? primaryKbLocaleId)
    {
        var result = new Dictionary<long, List<ZammadKbAnswerTranslation>>();
        foreach (var el in translations)
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetLong(el, "id");
            var answerId = TryGetLong(el, "answer_id");
            if (id is null || answerId is null) continue;
            var locale = ResolveEmitLocale(el, primaryKbLocaleId) ?? string.Empty;
            var title = TryGetString(el, "title") ?? string.Empty;
            // content_id points at the contents-row OR Zammad inlines
            // the body directly (some versions). Try inline first, then
            // resolved via content_id, then via the translation's own id.
            var bodyHtml = TryGetString(el, "body");
            if (string.IsNullOrEmpty(bodyHtml))
            {
                var contentId = TryGetLong(el, "content_id");
                if (contentId is not null && contentsByTranslationId.TryGetValue(contentId.Value, out var byContent))
                {
                    bodyHtml = byContent;
                }
                else if (contentsByTranslationId.TryGetValue(id.Value, out var byTranslation))
                {
                    bodyHtml = byTranslation;
                }
            }
            if (!result.TryGetValue(answerId.Value, out var bucket))
            {
                bucket = new List<ZammadKbAnswerTranslation>();
                result[answerId.Value] = bucket;
            }
            bucket.Add(new ZammadKbAnswerTranslation(
                Id: id.Value,
                LocaleCode: locale,
                Title: title,
                BodyHtml: bodyHtml ?? string.Empty));
        }
        return result;
    }

    private static IReadOnlyList<ZammadKbAnswer> ParseAnswersFromElements(
        IEnumerable<JsonElement> elements,
        IReadOnlyDictionary<long, List<ZammadKbAnswerTranslation>> translationsByAnswerId)
    {
        var list = new List<ZammadKbAnswer>();
        foreach (var el in elements)
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = TryGetLong(el, "id");
            if (id is null) continue;
            var translations = translationsByAnswerId.TryGetValue(id.Value, out var t)
                ? (IReadOnlyList<ZammadKbAnswerTranslation>)t
                : Array.Empty<ZammadKbAnswerTranslation>();
            list.Add(new ZammadKbAnswer(
                Id: id.Value,
                KnowledgeBaseId: TryGetLong(el, "knowledge_base_id") ?? 0,
                CategoryId: TryGetLong(el, "category_id") ?? 0,
                Position: TryGetInt(el, "position") ?? 0,
                Promoted: TryGetBool(el, "promoted") ?? false,
                InternalAt: TryGetDateTimeOffset(el, "internal_at"),
                PublishedAt: TryGetDateTimeOffset(el, "published_at"),
                ArchivedAt: TryGetDateTimeOffset(el, "archived_at"),
                CreatedById: TryGetLong(el, "created_by_id"),
                InternalNote: TryGetString(el, "internal_note"),
                Translations: translations,
                Attachments: ParseKbAnswerAttachments(el)));
        }
        return list;
    }

    private static IReadOnlyList<ZammadKbAnswerAttachment> ParseKbAnswerAttachments(JsonElement answer)
    {
        if (!answer.TryGetProperty("attachments", out var arr)
            || arr.ValueKind != JsonValueKind.Array
            || arr.GetArrayLength() == 0)
        {
            return Array.Empty<ZammadKbAnswerAttachment>();
        }
        var list = new List<ZammadKbAnswerAttachment>(arr.GetArrayLength());
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var recordId = TryGetLong(el, "id");
            string? mime = null;
            string? contentId = null;
            string? disposition = null;
            if (el.TryGetProperty("preferences", out var prefs)
                && prefs.ValueKind == JsonValueKind.Object)
            {
                mime = TryGetString(prefs, "Mime-Type")
                    ?? TryGetString(prefs, "Content-Type");
                contentId = TryGetString(prefs, "Content-ID");
                disposition = TryGetString(prefs, "Content-Disposition");
            }
            mime ??= TryGetString(el, "type") ?? "application/octet-stream";
            var previewUrl = TryGetString(el, "preview_url")
                ?? TryGetString(el, "url");

            // The numeric id in Zammad's body HTML img-src is the
            // Store::File id (e.g. `/api/v1/attachments/253306`), which
            // is what we extract from preview_url/url here. The
            // attachment's own `id` field is a different identifier
            // (the KnowledgeBase::Answer::Attachment record id) and
            // does NOT match the body's reference. We use the URL-
            // derived id everywhere downstream — both as the fetch
            // path id and as the rewriter map key — so the body's
            // `<img src="/api/v1/attachments/X">` finds X in the map.
            // Falls back to the record id when no URL is present (no
            // known Zammad version emits attachments without a url,
            // but the import treats it as a soft fallback).
            var urlId = ExtractAttachmentIdFromUrl(previewUrl);
            var effectiveId = urlId ?? recordId;
            if (effectiveId is null) continue;

            var normalisedCid = string.IsNullOrWhiteSpace(contentId)
                ? null
                : contentId.Trim().Trim('<', '>');
            var isInline = string.Equals(disposition, "inline", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(normalisedCid)
                || mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

            list.Add(new ZammadKbAnswerAttachment(
                Id: effectiveId.Value,
                Filename: TryGetString(el, "filename") ?? string.Empty,
                SizeBytes: TryGetLong(el, "size") ?? 0,
                MimeType: mime,
                IsInline: isInline,
                ContentId: normalisedCid,
                PreviewUrl: previewUrl));
        }
        return list;
    }

    /// Pulls the numeric id from a Zammad attachment URL like
    /// <c>/api/v1/attachments/253306</c> or <c>.../253306?preview=1</c>.
    /// Returns null when the input doesn't match the pattern.
    internal static long? ExtractAttachmentIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            url, @"/attachments/(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return long.TryParse(m.Groups[1].Value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var id) ? id : null;
    }

    /// Parses the per-answer detail response. Expected shape (assets-
    /// mode at root level, same as /init):
    ///   { "KnowledgeBaseAnswer":      { "36": {...} },
    ///     "KnowledgeBaseAnswerTranslation": { "37": {...} },
    ///     "KnowledgeBaseAnswerTranslationContent": { "37": { "body": "..." } } }
    /// We look up the content for <paramref name="translationId"/> in
    /// the content collection (keyed by translation id) and pull the
    /// attachments off the answer row.
    internal static ZammadKbAnswerDetail ParseKnowledgeBaseAnswerDetail(
        string body, long answerId, long translationId)
    {
        var emptyDetail = new ZammadKbAnswerDetail(
            answerId, translationId, BodyHtml: null,
            Attachments: Array.Empty<ZammadKbAnswerAttachment>());
        if (string.IsNullOrWhiteSpace(body)) return emptyDetail;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return emptyDetail;

            // Same root-or-assets detection as /init.
            JsonElement? assets;
            if (root.TryGetProperty("assets", out var assetsEl)
                && assetsEl.ValueKind == JsonValueKind.Object)
            {
                assets = assetsEl;
            }
            else if (root.TryGetProperty("KnowledgeBaseAnswer", out _)
                || root.TryGetProperty("KnowledgeBaseAnswerTranslationContent", out _))
            {
                assets = root;
            }
            else
            {
                assets = null;
            }

            // Body content: look it up by translation id in the content
            // collection. Some Zammad versions also inline `body` on
            // the translation row itself when include_contents was set,
            // so we accept either source.
            string? bodyHtml = null;
            foreach (var el in EnumerateAssetCollection(assets,
                "KnowledgeBaseAnswerTranslationContent",
                "KnowledgeBase::Answer::Translation::Content"))
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var contentTranslationId = TryGetLong(el, "translation_id") ?? TryGetLong(el, "id");
                if (contentTranslationId is null) continue;
                if (contentTranslationId.Value != translationId) continue;
                bodyHtml = TryGetString(el, "body");
                if (string.IsNullOrEmpty(bodyHtml))
                {
                    var bodyText = TryGetString(el, "body_text");
                    if (!string.IsNullOrEmpty(bodyText))
                    {
                        bodyHtml = "<p>" + System.Net.WebUtility.HtmlEncode(bodyText).Replace("\n", "<br/>") + "</p>";
                    }
                }
                break;
            }
            if (bodyHtml is null)
            {
                // Fallback: scan translation rows for an inlined body.
                foreach (var el in EnumerateAssetCollection(assets,
                    "KnowledgeBaseAnswerTranslation", "KnowledgeBase::Answer::Translation"))
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var tid = TryGetLong(el, "id");
                    if (tid is null || tid.Value != translationId) continue;
                    bodyHtml = TryGetString(el, "body");
                    if (!string.IsNullOrEmpty(bodyHtml)) break;
                }
            }

            // Attachments: live on the answer row. Pull the answer whose
            // id matches the requested answerId and reuse the existing
            // attachment parser so the rewriter sees the same shape it
            // does during the /init walk.
            IReadOnlyList<ZammadKbAnswerAttachment> attachments = Array.Empty<ZammadKbAnswerAttachment>();
            foreach (var el in EnumerateAssetCollection(assets,
                "KnowledgeBaseAnswer", "KnowledgeBase::Answer"))
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var aid = TryGetLong(el, "id");
                if (aid is null || aid.Value != answerId) continue;
                attachments = ParseKbAnswerAttachments(el);
                break;
            }

            return new ZammadKbAnswerDetail(answerId, translationId, bodyHtml, attachments);
        }
        catch (JsonException)
        {
            return emptyDetail;
        }
    }

    private static IReadOnlyList<string> ParseLocalesFromElements(IEnumerable<JsonElement> elements)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in elements)
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var locale = TryGetString(el, "system_locale")
                ?? TryGetString(el, "locale")
                ?? TryGetString(el, "name");
            if (!string.IsNullOrWhiteSpace(locale)) set.Add(locale);
        }
        return set.ToArray();
    }
}
