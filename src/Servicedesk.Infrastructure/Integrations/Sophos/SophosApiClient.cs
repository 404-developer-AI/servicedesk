using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Secrets;

namespace Servicedesk.Infrastructure.Integrations.Sophos;

/// Outcome of listing the partner tenants. <see cref="AuthFailure"/> means the
/// credentials/token were rejected (bad client id/secret, or a 401/403) — the
/// sync surfaces "credentials rejected" rather than retrying blindly. A non-auth
/// failure is transient (network / throttle / 5xx) and the next tick retries.
public sealed record SophosTenantsResult(
    bool Success, bool AuthFailure, string? Error, IReadOnlyList<SophosTenantRow> Tenants)
{
    public static SophosTenantsResult Ok(IReadOnlyList<SophosTenantRow> tenants) =>
        new(true, false, null, tenants);
    public static SophosTenantsResult Auth(string error) =>
        new(false, true, error, Array.Empty<SophosTenantRow>());
    public static SophosTenantsResult Transient(string error) =>
        new(false, false, error, Array.Empty<SophosTenantRow>());
}

/// Outcome of listing one tenant's protected mailboxes.
public sealed record SophosMailboxesResult(
    bool Success, bool AuthFailure, string? Error, IReadOnlyList<SophosMailboxRow> Mailboxes)
{
    public static SophosMailboxesResult Ok(IReadOnlyList<SophosMailboxRow> rows) =>
        new(true, false, null, rows);
    public static SophosMailboxesResult Auth(string error) =>
        new(false, true, error, Array.Empty<SophosMailboxRow>());
    public static SophosMailboxesResult Transient(string error) =>
        new(false, false, error, Array.Empty<SophosMailboxRow>());
}

/// Outcome of the credential probe (Settings → test connection). Never stores
/// anything; validates token + whoami and counts the visible tenants.
public sealed record SophosProbeResult(
    bool Success, string? Error, string? PrincipalId, string? IdType, int TenantCount);

public interface ISophosApiClient
{
    /// List every partner tenant, app-only, using the configured credentials.
    /// Never throws for an upstream/auth problem — returns a classified result.
    Task<SophosTenantsResult> ListTenantsAsync(CancellationToken ct);

    /// List one tenant's protected (type == "user") mailbox addresses.
    Task<SophosMailboxesResult> ListMailboxesAsync(string tenantId, string apiHost, CancellationToken ct);

    /// Validate the supplied credentials without storing anything: token →
    /// whoami → first tenant page count. Used by the settings test action.
    Task<SophosProbeResult> ProbeAsync(string clientId, string clientSecret, CancellationToken ct);

    /// Drop any cached token (called when the admin changes/clears credentials).
    void InvalidateToken();
}

/// Sophos Central (MSP partner) API client. The C# translation of
/// tools/sophos-test/Test-SophosSpamfilter.ps1: client-credentials token →
/// /whoami → partner|organization tenant list → per-tenant Sophos Email
/// mailboxes. Honours HTTP 429 (Retry-After, else 1/2/4s backoff). The single
/// partner token is cached with a server-based expiry; the client id/secret are
/// read from protected_secrets and never logged.
public sealed class SophosApiClient : ISophosApiClient
{
    public const string HttpClientName = "sophos";

    private const string TokenUrl  = "https://id.sophos.com/api/v2/oauth2/token";
    private const string WhoamiUrl = "https://api.central.sophos.com/whoami/v1";
    private const string GlobalApi = "https://api.central.sophos.com";
    private const int PageSize = 100;
    private const int PageGuard = 1000; // ~100k tenants/mailboxes; a safety stop.

    private readonly IHttpClientFactory _httpFactory;
    private readonly IProtectedSecretStore _secrets;
    private readonly ILogger<SophosApiClient> _logger;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private CachedToken? _token;

    public SophosApiClient(
        IHttpClientFactory httpFactory,
        IProtectedSecretStore secrets,
        ILogger<SophosApiClient> logger)
    {
        _httpFactory = httpFactory;
        _secrets = secrets;
        _logger = logger;
    }

    public void InvalidateToken() => _token = null;

    // ---- tenants --------------------------------------------------------

    public async Task<SophosTenantsResult> ListTenantsAsync(CancellationToken ct)
    {
        var clientId = (await _secrets.GetAsync(ProtectedSecretKeys.SophosClientId, ct) ?? string.Empty).Trim();
        var clientSecret = await _secrets.GetAsync(ProtectedSecretKeys.SophosClientSecret, ct);
        if (clientId.Length == 0 || string.IsNullOrEmpty(clientSecret))
            return SophosTenantsResult.Transient("Sophos API credentials are not configured.");

        var client = CreateClient();

        string token;
        try
        {
            token = await GetTokenAsync(client, clientId, clientSecret, ct);
        }
        catch (SophosAuthException ex) { return SophosTenantsResult.Auth(ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return SophosTenantsResult.Transient(ex.Message);
        }

        try
        {
            var (principalId, idType) = await WhoamiAsync(client, token, ct);
            var (path, idHeader) = ResolveTenantSource(idType);
            if (path is null)
                return SophosTenantsResult.Auth(
                    $"Sophos principal idType '{idType}' cannot list tenants (a single-tenant key only sees its own tenant).");

            var tenants = await ReadTenantsAsync(client, token, path!, idHeader!, principalId, ct);
            return SophosTenantsResult.Ok(tenants);
        }
        catch (SophosAuthException ex)
        {
            // A 401/403 mid-read means the credentials/token went stale; drop the
            // cached token so the next attempt re-authenticates immediately.
            InvalidateToken();
            return SophosTenantsResult.Auth(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return SophosTenantsResult.Transient(ex.Message);
        }
    }

    private async Task<List<SophosTenantRow>> ReadTenantsAsync(
        HttpClient client, string token, string path, string idHeader, string principalId, CancellationToken ct)
    {
        var all = new List<SophosTenantRow>();
        var page = 1;
        while (page <= PageGuard)
        {
            var url = $"{path}?page={page}&pageSize={PageSize}";
            using var resp = await SendWithRetryAsync(
                client, HttpMethod.Get, url, token,
                extraHeader: (idHeader, principalId), ct: ct);
            ThrowIfAuth(resp);
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var hadItems = false;
            if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in arr.EnumerateArray())
                {
                    hadItems = true;
                    all.Add(ParseTenant(t));
                }
            }
            if (!hadItems) break;

            var totalPages = root.TryGetProperty("pages", out var pages)
                && pages.ValueKind == JsonValueKind.Object
                && pages.TryGetProperty("total", out var tot) && tot.TryGetInt32(out var tp)
                ? tp : 0;
            if (totalPages > 0 && page >= totalPages) break;
            page++;
        }
        return all;
    }

    private static SophosTenantRow ParseTenant(JsonElement t)
    {
        var showAs = Str(t, "showAs");
        var (code, _) = SophosShowAs.Parse(showAs);
        return new SophosTenantRow
        {
            TenantId = Str(t, "id") ?? string.Empty,
            Name = Str(t, "name"),
            ShowAs = showAs,
            CompanyCode = code,
            ApiHost = Str(t, "apiHost"),
            Status = Str(t, "status"),
            DataRegion = Str(t, "dataRegion") ?? Str(t, "dataGeography"),
            BillingType = Str(t, "billingType"),
        };
    }

    // ---- mailboxes ------------------------------------------------------

    public async Task<SophosMailboxesResult> ListMailboxesAsync(
        string tenantId, string apiHost, CancellationToken ct)
    {
        var clientId = (await _secrets.GetAsync(ProtectedSecretKeys.SophosClientId, ct) ?? string.Empty).Trim();
        var clientSecret = await _secrets.GetAsync(ProtectedSecretKeys.SophosClientSecret, ct);
        if (clientId.Length == 0 || string.IsNullOrEmpty(clientSecret))
            return SophosMailboxesResult.Transient("Sophos API credentials are not configured.");

        var client = CreateClient();

        string token;
        try
        {
            token = await GetTokenAsync(client, clientId, clientSecret, ct);
        }
        catch (SophosAuthException ex) { return SophosMailboxesResult.Auth(ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return SophosMailboxesResult.Transient(ex.Message);
        }

        try
        {
            var rows = await ReadMailboxesAsync(client, token, tenantId, apiHost, ct);
            return SophosMailboxesResult.Ok(rows);
        }
        catch (SophosAuthException ex)
        {
            InvalidateToken();
            return SophosMailboxesResult.Auth(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return SophosMailboxesResult.Transient(ex.Message);
        }
    }

    private async Task<List<SophosMailboxRow>> ReadMailboxesAsync(
        HttpClient client, string token, string tenantId, string apiHost, CancellationToken ct)
    {
        var all = new List<SophosMailboxRow>();
        var host = apiHost.TrimEnd('/');
        string? fromKey = null;
        var guard = 0;
        while (guard++ < PageGuard)
        {
            var url = $"{host}/email/v1/mailboxes?pageSize={PageSize}";
            if (!string.IsNullOrEmpty(fromKey))
                url += "&pageFromKey=" + Uri.EscapeDataString(fromKey);

            using var resp = await SendWithRetryAsync(
                client, HttpMethod.Get, url, token,
                extraHeader: ("X-Tenant-ID", tenantId), ct: ct);
            ThrowIfAuth(resp);
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in arr.EnumerateArray())
                {
                    // The "spamfilter count" is mailboxes whose type == "user"
                    // (per the reference tool); other types are not protected
                    // user mailboxes.
                    var type = Str(m, "type");
                    if (!string.Equals(type, "user", StringComparison.OrdinalIgnoreCase)) continue;

                    var email = Str(m, "emailAddress") ?? Str(m, "primaryEmail")
                        ?? Str(m, "email") ?? Str(m, "address");
                    if (string.IsNullOrWhiteSpace(email)) continue;

                    all.Add(new SophosMailboxRow
                    {
                        Email = email.Trim(),
                        DisplayName = Str(m, "displayName") ?? Str(m, "name"),
                        MailboxType = type,
                    });
                }
            }

            fromKey = root.TryGetProperty("pages", out var pages)
                && pages.ValueKind == JsonValueKind.Object
                && pages.TryGetProperty("nextKey", out var nk) ? nk.GetString() : null;
            if (string.IsNullOrEmpty(fromKey)) break;
        }
        return all;
    }

    // ---- probe (test connection) ---------------------------------------

    public async Task<SophosProbeResult> ProbeAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        var client = CreateClient();
        string token;
        try
        {
            token = await FetchTokenAsync(client, clientId, clientSecret, ct);
        }
        catch (SophosAuthException ex) { return new SophosProbeResult(false, ex.Message, null, null, 0); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new SophosProbeResult(false, ex.Message, null, null, 0);
        }

        try
        {
            var (principalId, idType) = await WhoamiAsync(client, token, ct);
            var (path, idHeader) = ResolveTenantSource(idType);
            if (path is null)
                return new SophosProbeResult(false,
                    $"Principal idType '{idType}' cannot list tenants (a single-tenant key only sees its own tenant).",
                    principalId, idType, 0);

            var tenants = await ReadTenantsAsync(client, token, path!, idHeader!, principalId, ct);
            return new SophosProbeResult(true, null, principalId, idType, tenants.Count);
        }
        catch (SophosAuthException ex) { return new SophosProbeResult(false, ex.Message, null, null, 0); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new SophosProbeResult(false, ex.Message, null, null, 0);
        }
    }

    // ---- whoami ---------------------------------------------------------

    private async Task<(string Id, string? IdType)> WhoamiAsync(HttpClient client, string token, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(client, HttpMethod.Get, WhoamiUrl, token, ct: ct);
        ThrowIfAuth(resp);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var id = Str(root, "id") ?? throw new SophosAuthException("Sophos /whoami returned no principal id.");
        return (id, Str(root, "idType"));
    }

    private static (string? Path, string? IdHeader) ResolveTenantSource(string? idType) => idType switch
    {
        "partner" => ($"{GlobalApi}/partner/v1/tenants", "X-Partner-ID"),
        "organization" => ($"{GlobalApi}/organization/v1/tenants", "X-Organization-ID"),
        _ => (null, null),
    };

    // ---- token ----------------------------------------------------------

    private async Task<string> GetTokenAsync(HttpClient client, string clientId, string clientSecret, CancellationToken ct)
    {
        var cached = _token;
        if (cached is not null && cached.ExpiresUtc > DateTime.UtcNow) return cached.Token;

        await _tokenLock.WaitAsync(ct);
        try
        {
            cached = _token;
            if (cached is not null && cached.ExpiresUtc > DateTime.UtcNow) return cached.Token;

            var (token, expiresIn) = await FetchTokenWithExpiryAsync(client, clientId, clientSecret, ct);
            _token = new CachedToken(token, DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)));
            return token;
        }
        finally { _tokenLock.Release(); }
    }

    private async Task<string> FetchTokenAsync(HttpClient client, string clientId, string clientSecret, CancellationToken ct)
    {
        var (token, _) = await FetchTokenWithExpiryAsync(client, clientId, clientSecret, ct);
        return token;
    }

    private async Task<(string Token, int ExpiresIn)> FetchTokenWithExpiryAsync(
        HttpClient client, string clientId, string clientSecret, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = "token",
        });
        using var resp = await client.PostAsync(TokenUrl, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            // A denied token is a credential problem, not transient — surface the
            // error description so the connector's last_error explains it.
            var desc = TryReadError(body) ?? $"HTTP {(int)resp.StatusCode}";
            throw new SophosAuthException($"Token request failed: {desc}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(accessToken))
            throw new SophosAuthException("Token response carried no access_token.");
        var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
        return (accessToken, expiresIn);
    }

    // ---- HTTP helpers ---------------------------------------------------

    private HttpClient CreateClient()
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(100);
        return client;
    }

    /// GET/POST with HTTP 429 handling: honour Retry-After, else 1/2/4s
    /// exponential backoff, max 3 retries.
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client, HttpMethod method, string url, string token,
        (string Name, string Value)? extraHeader = null, CancellationToken ct = default)
    {
        const int maxRetries = 3;
        for (var attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (extraHeader is { } h) req.Headers.TryAddWithoutValidation(h.Name, h.Value);

            var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode != HttpStatusCode.TooManyRequests || attempt >= maxRetries)
                return resp;

            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1, 2, 4
            if (resp.Headers.RetryAfter?.Delta is { } d) delay = d;
            else if (resp.Headers.RetryAfter?.Date is { } when)
            {
                var diff = when - DateTimeOffset.UtcNow;
                if (diff > TimeSpan.Zero) delay = diff;
            }
            resp.Dispose();
            _logger.LogWarning("Sophos 429 rate-limited; retrying in {Delay}s (attempt {Attempt}/{Max}).",
                delay.TotalSeconds, attempt + 1, maxRetries);
            await Task.Delay(delay, ct);
        }
    }

    private static void ThrowIfAuth(HttpResponseMessage resp)
    {
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SophosAuthException($"Sophos API denied the request ({(int)resp.StatusCode}).");
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return Str(root, "error_description") ?? Str(root, "message") ?? Str(root, "error");
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private sealed record CachedToken(string Token, DateTime ExpiresUtc);

    private sealed class SophosAuthException : Exception
    {
        public SophosAuthException(string message) : base(message) { }
    }
}
