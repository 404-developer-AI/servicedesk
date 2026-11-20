using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Integrations.Http;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.TdSynnex;

/// Which orders to read: a delta (modified since), the first sync (created
/// since), or a full re-read of the given statuses (active refresh).
public sealed record TdSynnexOrderQuery(
    DateTime? ModifiedFromUtc = null,
    DateTime? CreatedFromUtc = null,
    IReadOnlyList<string>? Statuses = null,
    string? OrderNumber = null);

/// Outcome of a list call. <see cref="AuthFailure"/> = the credentials were
/// rejected (no blind retry); otherwise a failure is transient.
public sealed record TdSynnexOrdersResult(
    bool Success, bool AuthFailure, string? Error, IReadOnlyList<TdSynnexOrder> Orders, string? ListKey)
{
    public static TdSynnexOrdersResult Ok(IReadOnlyList<TdSynnexOrder> orders, string? listKey) =>
        new(true, false, null, orders, listKey);
    public static TdSynnexOrdersResult Auth(string error) =>
        new(false, true, error, Array.Empty<TdSynnexOrder>(), null);
    public static TdSynnexOrdersResult Transient(string error) =>
        new(false, false, error, Array.Empty<TdSynnexOrder>(), null);
}

/// Outcome of the settings "Test connection" action.
public sealed record TdSynnexProbeResult(bool Success, string? Error, int TotalResults, string? ListKey);

public interface ITdSynnexApiClient
{
    /// Read every page of orders matching <paramref name="query"/> (with line
    /// details). Never throws for an upstream/auth problem.
    Task<TdSynnexOrdersResult> ListOrdersAsync(TdSynnexOrderQuery query, CancellationToken ct);

    /// Token + one small order page with the saved configuration; stores nothing.
    Task<TdSynnexProbeResult> ProbeAsync(CancellationToken ct);

    void InvalidateToken();
}

/// TD SYNNEX EU Resellers API client — the C# translation of
/// tools/tdsynnex-test/Test-TdSynnexOrders.ps1. OAuth2 client credentials,
/// <c>GET /resellers/v1/orders</c> with <c>IncludeDetails=true</c> (max 20 per
/// page), HTTP 429 handled by <see cref="RateLimitRetry"/>. The base and
/// token URLs come from settings but must be https on a tdsynnex.com host
/// (<see cref="TdSynnexEndpointPolicy"/>), so a settings edit can never point
/// the client — and the client secret — elsewhere.
public sealed class TdSynnexApiClient : ITdSynnexApiClient
{
    public const string HttpClientName = "tdsynnex";

    private const int PageSize = 20;      // IncludeDetails=true caps the page at 20.
    private const int PageGuard = 500;    // 10k orders per pass; a safety stop.

    private readonly IHttpClientFactory _httpFactory;
    private readonly IProtectedSecretStore _secrets;
    private readonly ISettingsService _settings;
    private readonly ILogger<TdSynnexApiClient> _logger;
    private readonly BearerTokenCache _token = new();

    public TdSynnexApiClient(
        IHttpClientFactory httpFactory,
        IProtectedSecretStore secrets,
        ISettingsService settings,
        ILogger<TdSynnexApiClient> logger)
    {
        _httpFactory = httpFactory;
        _secrets = secrets;
        _settings = settings;
        _logger = logger;
    }

    public void InvalidateToken() => _token.Invalidate();

    public async Task<TdSynnexOrdersResult> ListOrdersAsync(TdSynnexOrderQuery query, CancellationToken ct)
    {
        var cfg = await LoadConfigAsync(ct);
        if (cfg.Error is not null) return TdSynnexOrdersResult.Transient(cfg.Error);

        var client = CreateClient();
        string token;
        try
        {
            token = await GetTokenAsync(client, cfg, ct);
        }
        catch (TdSynnexAuthException ex) { return TdSynnexOrdersResult.Auth(ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return TdSynnexOrdersResult.Transient(ex.Message);
        }

        try
        {
            var all = new List<TdSynnexOrder>();
            string? listKey = null;
            for (var page = 1; page <= PageGuard; page++)
            {
                var parsed = await ReadPageAsync(client, token, cfg.BaseUrl!, query, page, PageSize, ct);
                listKey ??= parsed.ListKey;
                all.AddRange(parsed.Orders);
                if (parsed.ListKey is null || parsed.Orders.Count == 0) break;
                if (parsed.TotalPages > 0 ? page >= parsed.TotalPages : parsed.Orders.Count < PageSize) break;
            }
            return TdSynnexOrdersResult.Ok(all, listKey);
        }
        catch (TdSynnexAuthException ex)
        {
            InvalidateToken();
            return TdSynnexOrdersResult.Auth(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return TdSynnexOrdersResult.Transient(ex.Message);
        }
    }

    public async Task<TdSynnexProbeResult> ProbeAsync(CancellationToken ct)
    {
        var cfg = await LoadConfigAsync(ct);
        if (cfg.Error is not null) return new TdSynnexProbeResult(false, cfg.Error, 0, null);

        var client = CreateClient();
        try
        {
            // A probe always fetches a fresh token: it must prove the saved
            // credentials work right now, not that an old token still does.
            var (token, _) = await FetchTokenAsync(client, cfg, ct);
            var page = await ReadPageAsync(client, token, cfg.BaseUrl!,
                new TdSynnexOrderQuery(CreatedFromUtc: DateTime.UtcNow.AddDays(-30)), 1, 5, ct);
            if (page.ListKey is null)
                return new TdSynnexProbeResult(false,
                    "Connected, but the response holds no 'data' or 'orders' list — check the base URL.", 0, null);
            return new TdSynnexProbeResult(true, null, page.TotalResults, page.ListKey);
        }
        catch (TdSynnexAuthException ex) { return new TdSynnexProbeResult(false, ex.Message, 0, null); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new TdSynnexProbeResult(false, ex.Message, 0, null);
        }
    }

    // ---- requests -----------------------------------------------------

    private async Task<TdSynnexOrderPage> ReadPageAsync(
        HttpClient client, string token, string baseUrl, TdSynnexOrderQuery q, int page, int pageSize, CancellationToken ct)
    {
        var url = BuildOrdersUrl(baseUrl, q, page, pageSize);
        using var resp = await RateLimitRetry.SendAsync(client, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("TraceId", "sd-" + Guid.NewGuid().ToString("N")[..12]);
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-BE");
            return req;
        }, _logger, "TD SYNNEX", ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new TdSynnexAuthException($"TD SYNNEX denied the request ({(int)resp.StatusCode}).");
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"TD SYNNEX answered {(int)resp.StatusCode}: {TryReadError(body) ?? resp.ReasonPhrase}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return TdSynnexOrderParser.ParsePage(doc.RootElement);
    }

    /// Pure URL builder (unit-tested). Dates in the documented
    /// <c>yyyy-MM-dd'T'HH:mm:ss'Z'</c> form; OrderStatus repeated per value.
    public static string BuildOrdersUrl(string baseUrl, TdSynnexOrderQuery q, int page, int pageSize)
    {
        var parts = new List<string>
        {
            "IncludeDetails=true",
            "SortBy=UPDATED",
            "SortDirection=ASC",
            $"Page={page.ToString(CultureInfo.InvariantCulture)}",
            $"PageSize={pageSize.ToString(CultureInfo.InvariantCulture)}",
        };
        if (q.ModifiedFromUtc is { } m) parts.Add("ModifiedFrom=" + Uri.EscapeDataString(Iso(m)));
        if (q.CreatedFromUtc is { } c) parts.Add("CreatedFrom=" + Uri.EscapeDataString(Iso(c)));
        if (!string.IsNullOrWhiteSpace(q.OrderNumber))
            parts.Add("TdsynnexOrderNumber=" + Uri.EscapeDataString(q.OrderNumber.Trim()));
        foreach (var s in q.Statuses ?? Array.Empty<string>())
            parts.Add("OrderStatus=" + Uri.EscapeDataString(s));
        return $"{baseUrl.TrimEnd('/')}/resellers/v1/orders?{string.Join('&', parts)}";
    }

    private static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ---- token --------------------------------------------------------

    private Task<string> GetTokenAsync(HttpClient client, Config cfg, CancellationToken ct) =>
        _token.GetAsync(
            BearerTokenCache.KeyFor(cfg.TokenUrl, cfg.ClientId, cfg.ClientSecret),
            c => FetchTokenAsync(client, cfg, c),
            ct);

    private static async Task<(string Token, int ExpiresIn)> FetchTokenAsync(HttpClient client, Config cfg, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = cfg.ClientId!,
            ["client_secret"] = cfg.ClientSecret!,
        });
        using var resp = await client.PostAsync(cfg.TokenUrl, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new TdSynnexAuthException($"Token request failed: {TryReadError(body) ?? $"HTTP {(int)resp.StatusCode}"}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (string.IsNullOrEmpty(accessToken))
            throw new TdSynnexAuthException("Token response carried no access_token.");
        var expiresIn = root.TryGetProperty("expires_in", out var e)
            ? e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n
              : e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var ns) ? ns : 3600
            : 3600;
        return (accessToken, expiresIn);
    }

    // ---- config -------------------------------------------------------

    private sealed record Config(string? BaseUrl, string? TokenUrl, string? ClientId, string? ClientSecret, string? Error);

    private async Task<Config> LoadConfigAsync(CancellationToken ct)
    {
        var baseUrl = (await _settings.GetAsync<string>(SettingKeys.TdSynnex.BaseUrl, ct) ?? string.Empty).Trim().TrimEnd('/');
        var tokenOverride = (await _settings.GetAsync<string>(SettingKeys.TdSynnex.TokenUrl, ct) ?? string.Empty).Trim();
        var clientId = (await _secrets.GetAsync(ProtectedSecretKeys.TdSynnexClientId, ct) ?? string.Empty).Trim();
        var clientSecret = await _secrets.GetAsync(ProtectedSecretKeys.TdSynnexClientSecret, ct);

        if (baseUrl.Length == 0)
            return new Config(null, null, null, null, "TD SYNNEX base URL is not configured.");
        if (!TdSynnexEndpointPolicy.IsAllowed(baseUrl))
            return new Config(null, null, null, null, "TD SYNNEX base URL must be https on a tdsynnex.com host.");
        var tokenUrl = tokenOverride.Length > 0 ? tokenOverride : baseUrl + "/auth/token";
        if (!TdSynnexEndpointPolicy.IsAllowed(tokenUrl))
            return new Config(null, null, null, null, "TD SYNNEX token URL must be https on a tdsynnex.com host.");
        if (clientId.Length == 0 || string.IsNullOrEmpty(clientSecret))
            return new Config(null, null, null, null, "TD SYNNEX API credentials are not configured.");

        return new Config(baseUrl, tokenUrl, clientId, clientSecret, null);
    }

    private HttpClient CreateClient()
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(100);
        return client;
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "error_description", "message", "errorMessage", "error", "title" })
            {
                if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    return s is { Length: > 300 } ? s[..300] : s;
                }
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    private sealed class TdSynnexAuthException : Exception
    {
        public TdSynnexAuthException(string message) : base(message) { }
    }
}

/// The only hosts the TD SYNNEX client will talk to: https, default port,
/// no credentials in the URL, host <c>tdsynnex.com</c> or a subdomain.
public static class TdSynnexEndpointPolicy
{
    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 500) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        var host = uri.IdnHost.ToLowerInvariant();
        return host == "tdsynnex.com" || host.EndsWith(".tdsynnex.com", StringComparison.Ordinal);
    }
}
