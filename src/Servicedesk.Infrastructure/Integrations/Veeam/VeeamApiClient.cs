using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Servicedesk.Infrastructure.Integrations.Veeam;

public interface IVeeamApiClient
{
    /// List every VSPC company (with its relation code). Never throws for an
    /// upstream/auth problem — returns a classified result.
    Task<VeeamCompaniesResult> ListCompaniesAsync(VeeamConnection conn, CancellationToken ct);

    /// List one company's VB365 protected objects (Exchange / OneDrive split,
    /// with restore-point counts and the latest backup timestamp).
    Task<VeeamProtectedObjectsResult> ListProtectedObjectsAsync(
        VeeamConnection conn, string organizationUid, CancellationToken ct);

    /// Validate the supplied connection without storing anything: request a
    /// password-grant token and count the visible companies. Used by the
    /// settings test action.
    Task<VeeamProbeResult> ProbeAsync(VeeamConnection conn, CancellationToken ct);

    /// Drop any cached token (called when the admin changes/clears the config).
    void InvalidateToken();
}

/// Veeam Service Provider Console (VSPC) REST API v3 client. The C# translation
/// of tools/m365-backup-match/Match-BackupByCode.ps1 (VSPC half): OAuth2
/// password-grant token → /organizations/companies → per-company
/// /protectedWorkloads/vb365ProtectedObjects. Offset/limit paging
/// (<c>{ data: [...], meta: { pagingInfo: { total } } }</c>); honours HTTP 429
/// (Retry-After, else 1/2/4s backoff). The token is cached with a server-based
/// expiry, keyed by base URL + username so a config change forces a refresh. The
/// password is supplied per call (from protected_secrets) and never logged.
///
/// Self-signed TLS is accepted only when the connection opts in
/// (Veeam.AllowSelfSignedTls) — VSPC appliances commonly ship a self-signed
/// certificate; validation is never bypassed without that explicit flag.
public sealed class VeeamApiClient : IVeeamApiClient
{
    private const int PageSize = 500;
    private const int PageGuard = 1000; // ~500k rows; a safety stop.

    private readonly ILogger<VeeamApiClient> _logger;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private CachedToken? _token;

    public VeeamApiClient(ILogger<VeeamApiClient> logger)
    {
        _logger = logger;
    }

    public void InvalidateToken() => _token = null;

    // ---- companies ------------------------------------------------------

    public async Task<VeeamCompaniesResult> ListCompaniesAsync(VeeamConnection conn, CancellationToken ct)
    {
        using var handler = CreateHandler(conn);
        using var client = CreateClient(handler);

        string token;
        try
        {
            token = await GetTokenAsync(client, conn, ct);
        }
        catch (VeeamAuthException ex) { return VeeamCompaniesResult.Auth(ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return VeeamCompaniesResult.Transient(ex.Message);
        }

        try
        {
            var companies = new List<VeeamCompanyRow>();
            await foreach (var el in ReadPagesAsync(
                client, token, $"{conn.BaseUrl}/api/v3/organizations/companies", ct))
            {
                companies.Add(ParseCompany(el));
            }
            return VeeamCompaniesResult.Ok(companies);
        }
        catch (VeeamAuthException ex)
        {
            InvalidateToken();
            return VeeamCompaniesResult.Auth(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return VeeamCompaniesResult.Transient(ex.Message);
        }
    }

    private static VeeamCompanyRow ParseCompany(JsonElement c)
    {
        string? code = null;
        if (c.TryGetProperty("ownerCredentials", out var oc) && oc.ValueKind == JsonValueKind.Object)
            code = Str(oc, "userName");

        return new VeeamCompanyRow
        {
            InstanceUid = Str(c, "instanceUid") ?? string.Empty,
            Name = Str(c, "name"),
            Code = code?.Trim(),
        };
    }

    // ---- protected objects ----------------------------------------------

    public async Task<VeeamProtectedObjectsResult> ListProtectedObjectsAsync(
        VeeamConnection conn, string organizationUid, CancellationToken ct)
    {
        using var handler = CreateHandler(conn);
        using var client = CreateClient(handler);

        string token;
        try
        {
            token = await GetTokenAsync(client, conn, ct);
        }
        catch (VeeamAuthException ex) { return VeeamProtectedObjectsResult.Auth(ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return VeeamProtectedObjectsResult.Transient(ex.Message);
        }

        try
        {
            var url = $"{conn.BaseUrl}/api/v3/protectedWorkloads/vb365ProtectedObjects"
                + $"?organizationFilter={Uri.EscapeDataString(organizationUid)}";
            var objects = new List<VeeamProtectedObjectRow>();
            await foreach (var el in ReadPagesAsync(client, token, url, ct))
            {
                objects.Add(ParseProtectedObject(el));
            }
            return VeeamProtectedObjectsResult.Ok(objects);
        }
        catch (VeeamAuthException ex)
        {
            InvalidateToken();
            return VeeamProtectedObjectsResult.Auth(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return VeeamProtectedObjectsResult.Transient(ex.Message);
        }
    }

    private static VeeamProtectedObjectRow ParseProtectedObject(JsonElement o)
    {
        var count = 0;
        if (o.TryGetProperty("restorePointsCount", out var rc) && rc.TryGetInt32(out var c)) count = c;

        DateTime? latest = null;
        if (o.TryGetProperty("latestRestorePointDate", out var lr) && lr.ValueKind == JsonValueKind.String
            && DateTime.TryParse(lr.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
        {
            latest = d;
        }

        return new VeeamProtectedObjectRow
        {
            Name = Str(o, "name"),
            RepositoryName = Str(o, "repositoryName"),
            RestorePointsCount = count,
            LatestRestorePointDate = latest,
        };
    }

    // ---- probe (test connection) ----------------------------------------

    public async Task<VeeamProbeResult> ProbeAsync(VeeamConnection conn, CancellationToken ct)
    {
        // Force a fresh token (don't trust a cached one for a deliberate test).
        InvalidateToken();
        var result = await ListCompaniesAsync(conn, ct);
        return result.Success
            ? new VeeamProbeResult(true, null, result.Companies.Count)
            : new VeeamProbeResult(false, result.Error, 0);
    }

    // ---- paging ---------------------------------------------------------

    /// Walk VSPC offset/limit paging, yielding each element of <c>data</c>.
    private async IAsyncEnumerable<JsonElement> ReadPagesAsync(
        HttpClient client, string token, string url,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var offset = 0;
        var guard = 0;
        var sep = url.Contains('?') ? '&' : '?';
        while (guard++ < PageGuard)
        {
            var pageUrl = $"{url}{sep}limit={PageSize}&offset={offset}";
            using var resp = await SendWithRetryAsync(client, pageUrl, token, ct);
            ThrowIfAuth(resp);
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var batch = 0;
            int? total = null;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in data.EnumerateArray())
                {
                    batch++;
                    yield return el.Clone();
                }
            }
            if (batch == 0) break;

            if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("pagingInfo", out var pi) && pi.ValueKind == JsonValueKind.Object
                && pi.TryGetProperty("total", out var t) && t.TryGetInt32(out var tot))
            {
                total = tot;
            }

            offset += PageSize;
            if (total is { } tv && offset >= tv) break;
            if (batch < PageSize) break;
        }
    }

    // ---- token ----------------------------------------------------------

    private async Task<string> GetTokenAsync(HttpClient client, VeeamConnection conn, CancellationToken ct)
    {
        var key = TokenKey(conn);
        var cached = _token;
        if (cached is not null && cached.Key == key && cached.ExpiresUtc > DateTime.UtcNow) return cached.Token;

        await _tokenLock.WaitAsync(ct);
        try
        {
            cached = _token;
            if (cached is not null && cached.Key == key && cached.ExpiresUtc > DateTime.UtcNow) return cached.Token;

            var (token, expiresIn) = await FetchTokenAsync(client, conn, ct);
            _token = new CachedToken(key, token, DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)));
            return token;
        }
        finally { _tokenLock.Release(); }
    }

    private static async Task<(string Token, int ExpiresIn)> FetchTokenAsync(
        HttpClient client, VeeamConnection conn, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = conn.Username,
            ["password"] = conn.Password,
        });
        using var resp = await client.PostAsync($"{conn.BaseUrl}/api/v3/token", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || !resp.IsSuccessStatusCode)
        {
            var desc = TryReadError(body) ?? $"HTTP {(int)resp.StatusCode}";
            throw new VeeamAuthException($"Token request failed: {desc}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(accessToken))
            throw new VeeamAuthException("Token response carried no access_token.");
        var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
        return (accessToken, expiresIn);
    }

    private static string TokenKey(VeeamConnection conn) =>
        $"{conn.BaseUrl}\n{conn.Username}";

    // ---- HTTP helpers ---------------------------------------------------

    private static HttpClientHandler CreateHandler(VeeamConnection conn)
    {
        var handler = new HttpClientHandler();
        if (conn.AllowSelfSigned)
        {
            // Explicit, audited opt-in (Veeam.AllowSelfSignedTls). Never bypass
            // certificate validation without that flag.
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        return handler;
    }

    private static HttpClient CreateClient(HttpClientHandler handler) =>
        new(handler) { Timeout = TimeSpan.FromSeconds(100) };

    /// GET with HTTP 429 handling: honour Retry-After, else 1/2/4s exponential
    /// backoff, max 3 retries.
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client, string url, string token, CancellationToken ct)
    {
        const int maxRetries = 3;
        for (var attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

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
            _logger.LogWarning("Veeam VSPC 429 rate-limited; retrying in {Delay}s (attempt {Attempt}/{Max}).",
                delay.TotalSeconds, attempt + 1, maxRetries);
            await Task.Delay(delay, ct);
        }
    }

    private static void ThrowIfAuth(HttpResponseMessage resp)
    {
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new VeeamAuthException($"VSPC API denied the request ({(int)resp.StatusCode}).");
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

    private sealed record CachedToken(string Key, string Token, DateTime ExpiresUtc);

    private sealed class VeeamAuthException : Exception
    {
        public VeeamAuthException(string message) : base(message) { }
    }
}
