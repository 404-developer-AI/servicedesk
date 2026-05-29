using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// HTTP-side implementation of <see cref="ITrmmApiClient"/>. One TRMM
/// install per Servicedesk install; the base URL + API key are read per
/// call from <see cref="ISettingsService"/> / <see cref="IProtectedSecretStore"/>.
///
/// Authentication: TRMM exposes a per-user API key minted in the TRMM
/// admin web UI. Sent on every call via the <c>X-API-KEY</c> header.
///
/// Token-handling rules (mirrors Zammad / Telavox):
/// <list type="bullet">
/// <item>API key never enters <c>integration_audit</c> payloads.</item>
/// <item>API key never enters <see cref="ILogger"/> calls.</item>
/// <item>API key is read per HTTP call and discarded after the request
/// completes.</item>
/// </list>
public sealed class TrmmApiClient : ITrmmApiClient
{
    public const string HttpClientName = "trmm-api";

    // H2-style release token (e.g. "23H2", "24H2", "25H2"). The
    // canonical marketing release on both Windows 10/11 AND modern
    // Windows Server (2022+) installs, so we always prefer it as the
    // Build value. TRMM emits it prefixed with a literal "v" inside the
    // OS string ("Windows 11 Pro, 64 bit v23H2 …"); a plain \b before
    // the token fails there because "v" is a word character. The
    // lookbehind (?<=\b|v) accepts either a real word boundary OR a
    // literal "v".
    private static readonly Regex WindowsH2ReleaseRegex = new(
        @"(?<=\b|v)2[0-9]H[12]\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Legacy "Server YYYY[ R2]" release token. Older Windows Server
    // installs (pre-Server 2022) don't carry a v24H2-style marker, so
    // when the H2 regex misses we still surface something meaningful
    // by walking back to this looser pattern.
    private static readonly Regex WindowsServerReleaseRegex = new(
        @"\bServer\s+\d{4}(?:\s+R2)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Windows family ("Windows 11", "Windows Server 2022", …) derived
    // from the full OS string. Two alternatives:
    //   1. "Windows Server YYYY[ R2]" — keeps the year and the optional
    //      R2 suffix on the family token.
    //   2. "Windows X" where X ∈ {XP, Vista, 7, 8, 8.1, 10, 11} — covers
    //      every consumer release likely to still be in service.
    // Match.Value is captured so we get the family slug as-is from the
    // upstream OS string.
    private static readonly Regex WindowsFamilyRegex = new(
        @"Windows\s+Server\s+\d{4}(?:\s+R2)?|Windows\s+(?:XP|Vista|7|8\.1|8|10|11)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProtectedSecretStore _secrets;
    private readonly ISettingsService _settings;
    private readonly IIntegrationAuditLogger _audit;
    private readonly ILogger<TrmmApiClient> _logger;

    public TrmmApiClient(
        IHttpClientFactory httpClientFactory,
        IProtectedSecretStore secrets,
        ISettingsService settings,
        IIntegrationAuditLogger audit,
        ILogger<TrmmApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secrets = secrets;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    public async Task<TrmmClientSnapshot> ListClientsAndSitesAsync(CancellationToken ct)
    {
        var body = await SendAsync(TrmmEventTypes.ClientsList, "/clients/", ct);
        return ParseClientsAndSites(body);
    }

    public async Task<IReadOnlyList<TrmmAgent>> ListAgentsAsync(CancellationToken ct)
    {
        // includeSuccessBodySnippet captures the first ~8KB of the upstream
        // response in the audit row so an admin can inspect the actual
        // TRMM payload shape (field names vary between TRMM versions —
        // older builds emit monitoring_type, newer ones may use a
        // different key, agent_type field renames, etc.).
        var body = await SendAsync(TrmmEventTypes.AgentsList, "/agents/", ct, includeSuccessBodySnippet: true);
        return ParseAgents(body);
    }

    public async Task<TrmmConnectionTestResult> TestConnectionAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // /clients/ doubles as both the connectivity probe and an
            // authorisation check: an invalid API key returns 401, an
            // unreachable host throws transport. We surface the count so
            // the admin sees real data flowed end-to-end.
            var clientsBody = await SendAsync(TrmmEventTypes.ConnectionTested, "/clients/", ct);
            var snapshot = ParseClientsAndSites(clientsBody);
            stopwatch.Stop();
            return new TrmmConnectionTestResult(
                Success: true,
                Version: null,
                ClientCount: snapshot.Clients.Count,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: null,
                ErrorMessage: null);
        }
        catch (TrmmApiException ex)
        {
            stopwatch.Stop();
            return new TrmmConnectionTestResult(
                Success: false,
                Version: null,
                ClientCount: 0,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: ex.UpstreamErrorCode ?? (ex.HttpStatus is null ? "transport_error" : $"http_{ex.HttpStatus}"),
                ErrorMessage: ex.Message);
        }
    }

    // ---- HTTP ----------------------------------------------------------

    private async Task<string> SendAsync(
        string eventType,
        string path,
        CancellationToken ct,
        bool includeSuccessBodySnippet = false)
    {
        var baseUrl = await ResolveBaseUrlAsync(ct);
        var apiKey = await _secrets.GetAsync(ProtectedSecretKeys.TrmmApiKey, ct);

        if (string.IsNullOrEmpty(apiKey))
        {
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: TrmmEventTypes.Integration,
                EventType: eventType,
                Outcome: IntegrationAuditOutcome.Warn,
                Endpoint: path,
                HttpStatus: null,
                LatencyMs: 0,
                ErrorCode: "api_key_missing"), ct);
            throw new TrmmApiException(
                "Tactical RMM API key is not configured. Save one under Settings → Integrations → Tactical RMM before running a sync.",
                upstreamErrorCode: "api_key_missing");
        }

        if (string.IsNullOrEmpty(baseUrl))
        {
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: TrmmEventTypes.Integration,
                EventType: eventType,
                Outcome: IntegrationAuditOutcome.Warn,
                Endpoint: path,
                HttpStatus: null,
                LatencyMs: 0,
                ErrorCode: "base_url_missing"), ct);
            throw new TrmmApiException(
                "Tactical RMM base URL is not configured. Save one under Settings → Integrations → Tactical RMM before running a sync.",
                upstreamErrorCode: "base_url_missing");
        }

        var url = baseUrl + path;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-API-KEY", apiKey);
        request.Headers.Accept.ParseAdd("application/json");

        var http = _httpClientFactory.CreateClient(HttpClientName);
        var timeoutSeconds = Math.Clamp(
            await _settings.GetAsync<int>(SettingKeys.Trmm.RequestTimeoutSeconds, ct),
            5, 300);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

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
                Integration: TrmmEventTypes.Integration,
                EventType: eventType,
                Outcome: IntegrationAuditOutcome.Warn,
                Endpoint: path,
                HttpStatus: null,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: "transport_error",
                Payload: new { message = ex.Message }), ct);
            throw new TrmmApiException("Transport error talking to Tactical RMM: " + ex.Message, ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: TrmmEventTypes.Integration,
                EventType: eventType,
                Outcome: IntegrationAuditOutcome.Warn,
                Endpoint: path,
                HttpStatus: null,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: "timeout"), ct);
            throw new TrmmApiException("Timed out talking to Tactical RMM.");
        }
        stopwatch.Stop();

        try
        {
            var status = (int)response.StatusCode;
            var responseBody = await SafeReadBodyAsync(response, ct);

            if (response.IsSuccessStatusCode)
            {
                object? successPayload = includeSuccessBodySnippet
                    ? new
                    {
                        snippet = Truncate(responseBody, 8192),
                        responseLength = responseBody.Length,
                    }
                    : null;
                await _audit.LogAsync(new IntegrationAuditEvent(
                    Integration: TrmmEventTypes.Integration,
                    EventType: eventType,
                    Outcome: IntegrationAuditOutcome.Ok,
                    Endpoint: path,
                    HttpStatus: status,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    Payload: successPayload), ct);
                return responseBody;
            }

            var upstreamCode = response.StatusCode == HttpStatusCode.Unauthorized
                ? "invalid_credentials"
                : $"http_{status}";
            var outcome = response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500
                ? IntegrationAuditOutcome.Warn
                : IntegrationAuditOutcome.Error;
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: TrmmEventTypes.Integration,
                EventType: eventType,
                Outcome: outcome,
                Endpoint: path,
                HttpStatus: status,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: upstreamCode,
                Payload: new { snippet = Truncate(responseBody, 256) }), ct);
            throw new TrmmApiException(
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
        var raw = (await _settings.GetAsync<string>(SettingKeys.Trmm.BaseUrl, ct) ?? string.Empty).Trim();
        return raw.TrimEnd('/');
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];

    private static string BuildHttpMessage(int status, string? upstreamCode)
    {
        if (status == 401) return "Tactical RMM rejected the API key (401). Save a valid key and try again.";
        if (status == 403) return "Tactical RMM rejected the API key (403). The key may lack permissions to read clients/sites/agents.";
        if (status == 404) return "Tactical RMM returned 404 — base URL may be misconfigured.";
        if (status >= 500) return $"Tactical RMM upstream error ({status}). Try again or check the TRMM server logs.";
        return $"Tactical RMM call failed with HTTP {status} ({upstreamCode}).";
    }

    // ---- Parsing -------------------------------------------------------

    /// Parses a TRMM <c>/clients/</c> response — each client carries an
    /// embedded <c>sites</c> array, so we yield both lists in one pass
    /// (TRMM does not expose a top-level <c>/sites/</c> endpoint). The
    /// site's <c>client</c>/<c>client_id</c> field is preferred when
    /// present; otherwise we fall back to the parent client's id so the
    /// FK is always populated.
    private static TrmmClientSnapshot ParseClientsAndSites(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var array = ExtractArray(doc.RootElement);
        var clients = new List<TrmmClient>(array.GetArrayLength());
        var sites = new List<TrmmSite>(array.GetArrayLength() * 2);
        foreach (var el in array.EnumerateArray())
        {
            var id = ReadLong(el, "id");
            var name = ReadString(el, "name") ?? string.Empty;
            if (id is null || string.IsNullOrWhiteSpace(name)) continue;
            clients.Add(new TrmmClient(id.Value, name.Trim(), ExtractClientCode(name)));

            if (el.TryGetProperty("sites", out var sitesEl) && sitesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var siteEl in sitesEl.EnumerateArray())
                {
                    var siteId = ReadLong(siteEl, "id");
                    var siteName = ReadString(siteEl, "name") ?? string.Empty;
                    if (siteId is null || string.IsNullOrWhiteSpace(siteName)) continue;
                    var parentId = ReadLong(siteEl, "client")
                        ?? ReadLong(siteEl, "client_id")
                        ?? id.Value;
                    sites.Add(new TrmmSite(siteId.Value, parentId, siteName.Trim()));
                }
            }
        }
        return new TrmmClientSnapshot(clients, sites);
    }

    private static IReadOnlyList<TrmmAgent> ParseAgents(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var array = ExtractArray(doc.RootElement);
        var list = new List<TrmmAgent>(array.GetArrayLength());
        foreach (var el in array.EnumerateArray())
        {
            var agentId = ReadString(el, "agent_id");
            var hostname = ReadString(el, "hostname");
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(hostname)) continue;

            var monitoringType = (ReadString(el, "monitoring_type") ?? "workstation").ToLowerInvariant();
            var agentType = monitoringType == "server" ? "server" : "workstation";

            var osName = ReadString(el, "operating_system");
            var osBuild = ReadString(el, "win_release") ?? ExtractWindowsReleaseFromOsName(osName);
            var osFamily = ExtractOsFamily(osName);

            var lastSeen = ReadDateTime(el, "last_seen");

            // TRMM "status" is "online" / "offline" / "overdue". Anything
            // other than "online" maps to offline for the binary indicator.
            var statusRaw = (ReadString(el, "status") ?? string.Empty).ToLowerInvariant();
            var online = statusRaw == "online";

            var publicIp = ReadString(el, "public_ip");

            // The listing endpoint emits site/client as names (string),
            // the detail endpoint as ids (long). Capture whichever is
            // present — the sync service resolves the missing side by
            // name lookup against the just-upserted clients/sites.
            var siteId = ReadLong(el, "site") ?? ReadLong(el, "site_id");
            var siteName = ReadString(el, "site_name") ?? ReadString(el, "site");
            var clientId = ReadLong(el, "client") ?? ReadLong(el, "client_id");
            var clientName = ReadString(el, "client_name") ?? ReadString(el, "client");

            // Reject only when we have neither an id nor a name on a side
            // — there's no way to link those rows.
            if ((siteId is null && string.IsNullOrWhiteSpace(siteName))
                || (clientId is null && string.IsNullOrWhiteSpace(clientName)))
            {
                continue;
            }

            list.Add(new TrmmAgent(
                AgentId: agentId.Trim(),
                Hostname: hostname.Trim(),
                AgentType: agentType,
                OsName: osName,
                OsFamily: osFamily,
                OsBuild: osBuild,
                LastSeenUtc: lastSeen,
                Online: online,
                PublicIp: publicIp,
                ClientId: clientId,
                ClientName: clientName?.Trim(),
                SiteId: siteId,
                SiteName: siteName?.Trim()));
        }
        return list;
    }

    private static JsonElement ExtractArray(JsonElement root)
    {
        // TRMM returns top-level arrays for these endpoints in current
        // releases; older / paged variants may wrap under "results".
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array) return results;
        return default;
    }

    /// Pulls the leading <c>[CODE]</c> token off a TRMM client display
    /// name. <c>[ACME] Acme Inc.</c> → <c>ACME</c>; an empty or missing
    /// bracketed prefix returns null (the client will not auto-link).
    internal static string? ExtractClientCode(string name)
    {
        var trimmed = name.TrimStart();
        if (trimmed.Length < 3 || trimmed[0] != '[') return null;
        var close = trimmed.IndexOf(']');
        if (close <= 1) return null;
        var code = trimmed.Substring(1, close - 1).Trim();
        return code.Length == 0 ? null : code;
    }

    /// Last-resort extraction when TRMM did not give us <c>win_release</c>
    /// — we look for the marketing release token inside the full OS
    /// display string. Order is intentional: H2 wins over "Server YYYY"
    /// when both are present (Server 2022+ rows carry a v24H2-style
    /// marker that lines up with the Windows 11 column visually).
    internal static string? ExtractWindowsReleaseFromOsName(string? osName)
    {
        if (string.IsNullOrWhiteSpace(osName)) return null;

        var h2 = WindowsH2ReleaseRegex.Match(osName);
        if (h2.Success) return h2.Value.ToUpperInvariant();

        var server = WindowsServerReleaseRegex.Match(osName);
        if (server.Success)
        {
            return System.Text.RegularExpressions.Regex
                .Replace(server.Value, @"\s+", " ").Trim();
        }

        return null;
    }

    /// Windows family slug parsed from the full OS display string.
    /// "Windows 11 Pro, 64 bit v23H2 …" → "Windows 11";
    /// "Windows Server 2022 Standard, …" → "Windows Server 2022".
    /// Falls back to a coarse Linux/macOS bucket for non-Windows hosts so
    /// the column is filled even when an agent runs a non-Windows
    /// platform. Whitespace inside the captured token is collapsed so
    /// double-spaces in upstream strings don't produce duplicate
    /// dropdown entries.
    internal static string? ExtractOsFamily(string? osName)
    {
        if (string.IsNullOrWhiteSpace(osName)) return null;

        var match = WindowsFamilyRegex.Match(osName);
        if (match.Success)
        {
            var raw = System.Text.RegularExpressions.Regex.Replace(match.Value, @"\s+", " ").Trim();
            // Title-case the leading "windows" / "server" tokens so that
            // case drift in the upstream string doesn't fragment the
            // dropdown ("WINDOWS 11" + "Windows 11" → two entries).
            return NormalizeFamilyCase(raw);
        }

        if (osName.Contains("Mac", StringComparison.OrdinalIgnoreCase)
            || osName.Contains("Darwin", StringComparison.OrdinalIgnoreCase))
            return "macOS";

        if (osName.Contains("Linux", StringComparison.OrdinalIgnoreCase)
            || osName.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase)
            || osName.Contains("Debian", StringComparison.OrdinalIgnoreCase)
            || osName.Contains("CentOS", StringComparison.OrdinalIgnoreCase)
            || osName.Contains("Red Hat", StringComparison.OrdinalIgnoreCase)
            || osName.Contains("RHEL", StringComparison.OrdinalIgnoreCase))
            return "Linux";

        return null;
    }

    private static string NormalizeFamilyCase(string raw)
    {
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            // Numeric tokens ("2022", "11") and the special "R2" suffix
            // stay as-is; everything else gets title-cased.
            if (parts[i].Length > 0 && char.IsLetter(parts[i][0])
                && !parts[i].Equals("R2", StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant();
            }
            else if (parts[i].Equals("R2", StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = "R2";
            }
        }
        return string.Join(' ', parts);
    }

    private static long? ReadLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), out var n) => n,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static DateTime? ReadDateTime(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        if (DateTime.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }
        return null;
    }
}
