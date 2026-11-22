using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Integrations.Http;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.M365;

/// Outcome of reading one customer tenant. <see cref="AuthFailure"/> means the
/// failure is a consent/permission problem (token denied, or the directory /
/// mailbox reads came back 401/403) — the caller flips the link to
/// needs_reconsent. A non-auth failure is transient (network / throttle / 5xx)
/// and the next tick simply retries.
public sealed record M365ReadResult(
    bool Success,
    bool AuthFailure,
    string? Error,
    IReadOnlyList<M365MailboxRow> Mailboxes)
{
    public static M365ReadResult Ok(IReadOnlyList<M365MailboxRow> mailboxes) =>
        new(true, false, null, mailboxes);

    public static M365ReadResult Auth(string error) =>
        new(false, true, error, Array.Empty<M365MailboxRow>());

    public static M365ReadResult Transient(string error) =>
        new(false, false, error, Array.Empty<M365MailboxRow>());
}

public interface IM365GraphReader
{
    /// Read every mailbox (with its real type + licenses) from one customer
    /// tenant, app-only, using the MSP multi-tenant app credentials. Never
    /// throws for an upstream/auth problem — returns a classified result.
    Task<M365ReadResult> ReadMailboxesAsync(string tenantId, CancellationToken ct);
}

/// App-only Microsoft Graph reader for a customer tenant. The C# translation of
/// tools/graph-test/Test-CustomerGraph.ps1: client-credentials token →
/// subscribedSkus map → page /users → mailboxSettings.userPurpose via $batch
/// (20/req) to stay fast on large tenants. Tokens are cached per tenant in a
/// shared BearerTokenCache keyed on tenant + MSP client id + secret, so a
/// rotated secret never reuses a token of the old one; the client id/secret
/// are read from settings + protected_secrets and never logged. Every call
/// goes through the shared HTTP 429 handling (RateLimitRetry); throttled
/// sub-requests inside a $batch are retried separately.
public sealed class M365GraphReader : IM365GraphReader
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const int BatchSize = 20;
    private const int PageGuard = 1000; // ~1M users at $top=999; a safety stop.
    private const string ServiceName = "Microsoft 365";

    private static readonly string[] UserSelect =
    {
        "id", "displayName", "givenName", "surname",
        "userPrincipalName", "mail", "accountEnabled", "assignedLicenses",
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingsService _settings;
    private readonly IProtectedSecretStore _secrets;
    private readonly ILogger<M365GraphReader> _logger;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // One cache per customer tenant: the shared cache holds a single token.
    private readonly ConcurrentDictionary<string, BearerTokenCache> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public M365GraphReader(
        IHttpClientFactory httpFactory,
        ISettingsService settings,
        IProtectedSecretStore secrets,
        ILogger<M365GraphReader> logger)
        : this(httpFactory, settings, secrets, logger, Task.Delay)
    {
    }

    /// Test seam: <paramref name="delay"/> replaces the real back-off waits.
    internal M365GraphReader(
        IHttpClientFactory httpFactory,
        ISettingsService settings,
        IProtectedSecretStore secrets,
        ILogger<M365GraphReader> logger,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _secrets = secrets;
        _logger = logger;
        _delay = delay;
    }

    public async Task<M365ReadResult> ReadMailboxesAsync(string tenantId, CancellationToken ct)
    {
        var clientId = (await _settings.GetAsync<string>(SettingKeys.M365.ClientId, ct) ?? string.Empty).Trim();
        var clientSecret = await _secrets.GetAsync(ProtectedSecretKeys.M365ClientSecret, ct);
        if (clientId.Length == 0 || string.IsNullOrEmpty(clientSecret))
            return M365ReadResult.Transient("MSP app credentials are not configured.");

        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(100);

        string token;
        try
        {
            token = await GetTokenAsync(client, tenantId, clientId, clientSecret, ct);
        }
        catch (M365AuthException ex)
        {
            return M365ReadResult.Auth(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return M365ReadResult.Transient(ex.Message);
        }

        try
        {
            var skuMap = await ReadSkuMapAsync(client, token, ct);
            var users = await ReadUsersAsync(client, token, ct);
            var rows = await ResolveMailboxesAsync(client, token, users, skuMap, ct);
            return M365ReadResult.Ok(rows);
        }
        catch (M365AuthException ex)
        {
            // A 401/403 on a core read means the consented permission set is
            // stale (a permission was added but consent wasn't re-granted, or
            // consent was revoked). Drop the cached token so the next attempt —
            // after the customer re-consents — fetches a fresh one immediately.
            if (_tokens.TryGetValue(tenantId, out var cache)) cache.Invalidate();
            return M365ReadResult.Auth(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return M365ReadResult.Transient(ex.Message);
        }
    }

    // ---- token ----------------------------------------------------------

    private Task<string> GetTokenAsync(
        HttpClient client, string tenantId, string clientId, string clientSecret, CancellationToken ct) =>
        _tokens.GetOrAdd(tenantId, _ => new BearerTokenCache()).GetAsync(
            BearerTokenCache.KeyFor(tenantId, clientId, clientSecret),
            token => FetchTokenAsync(client, tenantId, clientId, clientSecret, token),
            ct);

    private async Task<(string Token, int ExpiresInSeconds)> FetchTokenAsync(
        HttpClient client, string tenantId, string clientId, string clientSecret, CancellationToken ct)
    {
        var url = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token";
        using var resp = await RateLimitRetry.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["client_secret"] = clientSecret,
                ["grant_type"] = "client_credentials",
            }),
        }, _logger, ServiceName, ct, delay: _delay);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            // A denied token is a consent/credential problem, not transient —
            // surface the AADSTS description so the link's last_error explains
            // it. A 429 that outlasted the retries is transient.
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("Token request still rate-limited (429) after retries.");
            var desc = TryReadError(body) ?? body;
            throw new M365AuthException($"Token request failed: {desc}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(accessToken))
            throw new M365AuthException("Token response carried no access_token.");
        var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var sec) ? sec : 3600;
        return (accessToken, expiresIn);
    }

    // ---- subscribedSkus -------------------------------------------------

    private async Task<Dictionary<string, string>> ReadSkuMapAsync(
        HttpClient client, string token, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var resp = await SendAsync(client, token, HttpMethod.Get, $"{GraphBase}/subscribedSkus", ct);
        if (!resp.IsSuccessStatusCode)
        {
            // subscribedSkus only powers readable license NAMES. A failure here —
            // including a 403 when Organization.Read.All has not (yet) been
            // consented — must NOT sink the whole read: fall back to raw SKU
            // GUIDs, exactly like the probe script. The real auth gate is
            // User.Read.All + MailboxSettings.Read below, so a tenant with only
            // those two consented still syncs (licenses just show as GUIDs).
            _logger.LogWarning(
                "M365 subscribedSkus read returned {Status}; license names fall back to SKU GUIDs (grant + re-consent Organization.Read.All to resolve names).",
                (int)resp.StatusCode);
            return map;
        }
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var sku in arr.EnumerateArray())
            {
                var id = sku.TryGetProperty("skuId", out var sid) ? sid.GetString() : null;
                var part = sku.TryGetProperty("skuPartNumber", out var p) ? p.GetString() : null;
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(part)) map[id] = part!;
            }
        }
        return map;
    }

    // ---- users ----------------------------------------------------------

    private async Task<List<DirectoryUser>> ReadUsersAsync(HttpClient client, string token, CancellationToken ct)
    {
        var users = new List<DirectoryUser>();
        var url = $"{GraphBase}/users?$select={string.Join(",", UserSelect)}&$top=999";
        var pages = 0;
        while (!string.IsNullOrEmpty(url) && pages++ < PageGuard)
        {
            using var resp = await SendAsync(client, token, HttpMethod.Get, url, ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new M365AuthException("User.Read.All is not consented (users 403).");
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in arr.EnumerateArray()) users.Add(DirectoryUser.Parse(u));
            }
            url = root.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
        return users;
    }

    // ---- mailboxSettings via $batch -------------------------------------

    private async Task<List<M365MailboxRow>> ResolveMailboxesAsync(
        HttpClient client, string token, List<DirectoryUser> users,
        Dictionary<string, string> skuMap, CancellationToken ct)
    {
        var rows = new List<M365MailboxRow>();
        var resolved = 0;
        var saw403 = 0;

        for (var i = 0; i < users.Count; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var slice = users.GetRange(i, Math.Min(BatchSize, users.Count - i));
            var purposes = await BatchMailboxPurposeAsync(client, token, slice, ct);

            for (var j = 0; j < slice.Count; j++)
            {
                var (purpose, status) = purposes[j];
                if (status == 403) saw403++;
                if (string.IsNullOrEmpty(purpose)) continue; // no mailbox (404) or denied — skip

                resolved++;
                var u = slice[j];
                rows.Add(new M365MailboxRow
                {
                    ObjectId = u.Id,
                    MailboxType = purpose,
                    DisplayName = u.DisplayName,
                    GivenName = u.GivenName,
                    Surname = u.Surname,
                    Upn = u.Upn,
                    Mail = u.Mail,
                    Enabled = u.Enabled,
                    Licenses = JoinLicenses(u.SkuIds, skuMap),
                });
            }
        }

        // If we had users but resolved nothing and every attempt was forbidden,
        // MailboxSettings.Read is missing — that is a consent problem, not "no
        // mailboxes". Mirrors the probe script's failure diagnosis.
        if (users.Count > 0 && resolved == 0 && saw403 > 0)
            throw new M365AuthException("MailboxSettings.Read is not consented (mailboxSettings 403 for all users).");

        return rows;
    }

    /// userPurpose per user of the slice. Graph answers a $batch with 200 and
    /// a status per sub-request; a throttled sub-request (429) is retried on
    /// its own — honouring its Retry-After — instead of being read as "no
    /// mailbox", which would silently drop mailboxes from the sync. Still
    /// throttled after the retries → transient failure (the sync keeps the
    /// previous data and tries again next tick).
    private async Task<List<(string? Purpose, int Status)>> BatchMailboxPurposeAsync(
        HttpClient client, string token, List<DirectoryUser> slice, CancellationToken ct)
    {
        // Pre-fill so an absent response id maps to "no mailbox".
        var result = new List<(string? Purpose, int Status)>(new (string?, int)[slice.Count]);
        var pending = Enumerable.Range(0, slice.Count).ToList();

        for (var attempt = 0; ; attempt++)
        {
            var requests = pending.Select(idx => new
            {
                id = idx.ToString(),
                method = "GET",
                url = $"/users/{slice[idx].Id}/mailboxSettings?$select=userPurpose",
            }).ToArray();

            using var resp = await SendAsync(
                client, token, HttpMethod.Post, $"{GraphBase}/$batch", ct,
                () => JsonContent.Create(new { requests }));
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new M365AuthException("Graph $batch denied (401/403).");
            resp.EnsureSuccessStatusCode();

            var throttled = new List<int>();
            var wait = TimeSpan.Zero;
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("responses", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in arr.EnumerateArray())
                {
                    if (!r.TryGetProperty("id", out var idEl)) continue;
                    if (!int.TryParse(idEl.GetString(), out var idx) || idx < 0 || idx >= slice.Count) continue;
                    var status = r.TryGetProperty("status", out var st) && st.TryGetInt32(out var code) ? code : 0;
                    if (status == (int)HttpStatusCode.TooManyRequests)
                    {
                        throttled.Add(idx);
                        var retryAfter = RetryAfter(r);
                        if (retryAfter > wait) wait = retryAfter;
                        continue;
                    }
                    string? purpose = null;
                    if (status is >= 200 and < 300 && r.TryGetProperty("body", out var b)
                        && b.ValueKind == JsonValueKind.Object
                        && b.TryGetProperty("userPurpose", out var up))
                    {
                        purpose = up.GetString();
                    }
                    result[idx] = (purpose, status);
                }
            }

            if (throttled.Count == 0) return result;
            if (attempt >= RateLimitRetry.DefaultMaxRetries)
                throw new HttpRequestException(
                    $"Graph kept throttling {throttled.Count} mailbox read(s) after {RateLimitRetry.DefaultMaxRetries} retries.");

            if (wait == TimeSpan.Zero) wait = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            _logger.LogWarning("{Service} $batch: {Count} sub-request(s) throttled; retrying in {Delay}s (attempt {Attempt}/{Max}).",
                ServiceName, throttled.Count, wait.TotalSeconds, attempt + 1, RateLimitRetry.DefaultMaxRetries);
            await _delay(wait, ct);
            pending = throttled;
        }
    }

    /// Retry-After (seconds) of one $batch sub-response, zero when absent.
    private static TimeSpan RetryAfter(JsonElement subResponse)
    {
        if (!subResponse.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object)
            return TimeSpan.Zero;
        foreach (var h in headers.EnumerateObject())
        {
            if (!string.Equals(h.Name, "Retry-After", StringComparison.OrdinalIgnoreCase)) continue;
            var raw = h.Value.ValueKind == JsonValueKind.Number ? h.Value.GetRawText() : h.Value.GetString();
            if (int.TryParse(raw, out var seconds) && seconds > 0)
                return TimeSpan.FromSeconds(Math.Min(seconds, 120));
        }
        return TimeSpan.Zero;
    }

    // ---- helpers --------------------------------------------------------

    /// One Graph call with the shared HTTP 429 handling. The request (and its
    /// body, via <paramref name="content"/>) is rebuilt for every attempt.
    private Task<HttpResponseMessage> SendAsync(
        HttpClient client, string token, HttpMethod method, string url, CancellationToken ct,
        Func<HttpContent>? content = null) =>
        RateLimitRetry.SendAsync(client, () =>
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (content is not null) req.Content = content();
            return req;
        }, _logger, ServiceName, ct, delay: _delay);

    private static string JoinLicenses(IReadOnlyList<string> skuIds, Dictionary<string, string> skuMap)
    {
        if (skuIds.Count == 0) return string.Empty;
        var names = skuIds.Select(id => skuMap.TryGetValue(id, out var name) ? name : id);
        return string.Join(", ", names);
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private sealed class M365AuthException : Exception
    {
        public M365AuthException(string message) : base(message) { }
    }

    private sealed class DirectoryUser
    {
        public string Id { get; init; } = string.Empty;
        public string? DisplayName { get; init; }
        public string? GivenName { get; init; }
        public string? Surname { get; init; }
        public string? Upn { get; init; }
        public string? Mail { get; init; }
        public bool? Enabled { get; init; }
        public IReadOnlyList<string> SkuIds { get; init; } = Array.Empty<string>();

        public static DirectoryUser Parse(JsonElement u)
        {
            var skuIds = new List<string>();
            if (u.TryGetProperty("assignedLicenses", out var lic) && lic.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in lic.EnumerateArray())
                {
                    if (l.TryGetProperty("skuId", out var sid) && sid.GetString() is { Length: > 0 } id)
                        skuIds.Add(id);
                }
            }
            return new DirectoryUser
            {
                Id = u.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty,
                DisplayName = Str(u, "displayName"),
                GivenName = Str(u, "givenName"),
                Surname = Str(u, "surname"),
                Upn = Str(u, "userPrincipalName"),
                Mail = Str(u, "mail"),
                Enabled = u.TryGetProperty("accountEnabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? en.GetBoolean()
                    : null,
                SkuIds = skuIds,
            };
        }

        private static string? Str(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
