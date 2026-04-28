using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Auth.Microsoft;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public sealed class AdsolutAuthService : IAdsolutAuthService
{
    /// Named HttpClient slot. Registered in DependencyInjection so a future
    /// hardening pass (Polly retry, custom proxy, mTLS) can configure it
    /// without touching this class.
    public const string HttpClientName = "adsolut-token";

    private readonly ISettingsService _settings;
    private readonly IProtectedSecretStore _secrets;
    private readonly IAdsolutConnectionStore _connections;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IIntegrationAuditLogger _integrationAudit;
    private readonly ILogger<AdsolutAuthService> _logger;

    public AdsolutAuthService(
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IAdsolutConnectionStore connections,
        IHttpClientFactory httpClientFactory,
        IIntegrationAuditLogger integrationAudit,
        ILogger<AdsolutAuthService> logger)
    {
        _settings = settings;
        _secrets = secrets;
        _connections = connections;
        _httpClientFactory = httpClientFactory;
        _integrationAudit = integrationAudit;
        _logger = logger;
    }

    // ---- /challenge -----------------------------------------------------

    public async Task<AdsolutChallengeIntent> CreateChallengeAsync(string redirectUri, CancellationToken ct = default)
    {
        var (env, clientId, _, scopes) = await ReadConfigAsync(ct);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException(
                "Adsolut integration is not configured. Set Adsolut.ClientId and the client secret via Settings.");
        }

        // 256-bit URL-safe state — same generator the M365 flow uses.
        var state = OidcProtocol.GenerateUrlSafeToken(32);

        // Adsolut docs (step 2) list four required params: response_type,
        // client_id, scope, redirect_uri. We add `state` for CSRF; the
        // upstream reflects unknown params back unchanged so this is safe.
        // No PKCE — confidential client + the spec doesn't list it, and
        // some IdentityServer instances reject unknown auth params.
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["scope"] = scopes,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
        };

        var url = AdsolutOAuthEndpoints.Authorize(env) + "?" + BuildQueryString(query);
        return new AdsolutChallengeIntent(url, state);
    }

    // ---- /callback ------------------------------------------------------

    public async Task<AdsolutCallbackResult> CompleteCallbackAsync(
        string code,
        string redirectUri,
        string? actorId = null,
        string? actorRole = null,
        CancellationToken ct = default)
    {
        var (env, clientId, clientSecret, scopes) = await ReadConfigAsync(ct);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return new AdsolutCallbackResult.Rejected(
                AdsolutCallbackRejectReason.NotConfigured,
                "client_credentials_missing");
        }

        var formBody = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        };

        TokenResponse token;
        try
        {
            token = await CallTokenEndpointWithAuditAsync(
                env, clientId, clientSecret, formBody,
                eventType: AdsolutEventTypes.OAuthCodeExchange,
                source: "callback",
                actorId: actorId,
                actorRole: actorRole,
                ct: ct);
        }
        catch (TokenEndpointException ex)
        {
            _logger.LogWarning("Adsolut code exchange failed: {Error}", ex.UpstreamErrorCode ?? ex.Message);
            return new AdsolutCallbackResult.Rejected(
                AdsolutCallbackRejectReason.CodeExchangeFailed,
                ex.UpstreamErrorCode ?? "code_exchange_failed");
        }

        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            return new AdsolutCallbackResult.Rejected(
                AdsolutCallbackRejectReason.InvalidTokenResponse,
                "missing_refresh_token");
        }

        // Decode id_token for display purposes. We do NOT validate the
        // signature here — the token came back over an mTLS-strength
        // connection that we authenticated to with our own client secret,
        // and we never grant security based on its claims. Future
        // hardening: full JWKS-validated decode if the integration ever
        // gains user-facing trust decisions.
        var (subject, email) = TryDecodeIdToken(token.IdToken);

        // Persist before returning Success — order matters: the secret
        // store rotates first so a partial failure leaves only the secret
        // store updated (safer than the connection-row-without-RT shape
        // we'd hit on the reverse order).
        await _secrets.SetAsync(ProtectedSecretKeys.AdsolutRefreshToken, token.RefreshToken, ct);

        var nowUtc = DateTime.UtcNow;

        // Carry the existing administration_id forward across reconnect — a
        // re-authorize against the same dossier should not erase the picked
        // dossier id. ScopesAtAuthorize, by contrast, is *replaced* with the
        // current Settings.Adsolut.Scopes value because the new RT was just
        // minted with that exact scope list.
        var existing = await _connections.GetAsync(ct);
        var connection = new AdsolutConnection(
            AuthorizedSubject: subject,
            AuthorizedEmail: email,
            AuthorizedUtc: nowUtc,
            LastRefreshedUtc: nowUtc,
            AccessTokenExpiresUtc: nowUtc.AddSeconds(SafeExpiresIn(token.ExpiresIn)),
            LastRefreshError: null,
            LastRefreshErrorUtc: null,
            UpdatedUtc: nowUtc,
            AdministrationId: existing?.AdministrationId,
            ScopesAtAuthorize: scopes);
        await _connections.SaveAsync(connection, ct);

        return new AdsolutCallbackResult.Success(subject ?? string.Empty, email);
    }

    // ---- /refresh -------------------------------------------------------

    public async Task<AdsolutRefreshResult> RefreshAccessTokenAsync(
        string source = "service",
        string? actorId = null,
        string? actorRole = null,
        CancellationToken ct = default)
    {
        var (env, clientId, clientSecret, _) = await ReadConfigAsync(ct);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new AdsolutRefreshException(
                "Adsolut integration is not configured.",
                requiresReconnect: false);
        }

        var refreshToken = await _secrets.GetAsync(ProtectedSecretKeys.AdsolutRefreshToken, ct);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new AdsolutRefreshException(
                "No refresh token stored — admin must reconnect.",
                requiresReconnect: true);
        }

        var formBody = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };

        TokenResponse token;
        try
        {
            token = await CallTokenEndpointWithAuditAsync(
                env, clientId, clientSecret, formBody,
                eventType: AdsolutEventTypes.OAuthRefresh,
                source: source,
                actorId: actorId,
                actorRole: actorRole,
                ct: ct);
        }
        catch (TokenEndpointException ex)
        {
            // Persist the failure so the UI can show a "reconnect" badge
            // even if the caller swallows the exception. Sliding-window
            // logic still treats LastRefreshedUtc as the last *success*.
            await StampRefreshErrorAsync(ex.UpstreamErrorCode ?? ex.Message, ct);

            // invalid_grant on refresh = the RT has been revoked (sliding
            // window expired or admin revoked at WK). Anything else (5xx,
            // network) is transient.
            var requiresReconnect = string.Equals(ex.UpstreamErrorCode, "invalid_grant", StringComparison.Ordinal);
            throw new AdsolutRefreshException(
                ex.Message,
                requiresReconnect,
                ex.UpstreamErrorCode);
        }

        if (string.IsNullOrWhiteSpace(token.RefreshToken) || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            await StampRefreshErrorAsync("invalid_token_response", ct);
            throw new AdsolutRefreshException(
                "Refresh response missing required token fields.",
                requiresReconnect: false);
        }

        // RT rotation: Adsolut returns a new refresh token on every
        // refresh. Persist before clearing the error window so a crash
        // mid-rotation leaves us with the latest valid RT.
        await _secrets.SetAsync(ProtectedSecretKeys.AdsolutRefreshToken, token.RefreshToken, ct);

        var nowUtc = DateTime.UtcNow;
        var expiresUtc = nowUtc.AddSeconds(SafeExpiresIn(token.ExpiresIn));

        // Decode id_token if present so the authorized-subject metadata
        // stays fresh (refresh responses on IdentityServer-style providers
        // include a new id_token by default).
        var (subject, email) = TryDecodeIdToken(token.IdToken);

        var existing = await _connections.GetAsync(ct);
        // Refresh must preserve fields that authorize-time owns: the
        // administration_id (chosen dossier) and the scopes_at_authorize
        // snapshot (the scope list bound to this RT). Both are unchanged
        // by a refresh — overwriting them with null would wipe the
        // dossier-pick and break the "Reconnect required" detection.
        var connection = new AdsolutConnection(
            AuthorizedSubject: subject ?? existing?.AuthorizedSubject,
            AuthorizedEmail: email ?? existing?.AuthorizedEmail,
            AuthorizedUtc: existing?.AuthorizedUtc ?? nowUtc,
            LastRefreshedUtc: nowUtc,
            AccessTokenExpiresUtc: expiresUtc,
            LastRefreshError: null,
            LastRefreshErrorUtc: null,
            UpdatedUtc: nowUtc,
            AdministrationId: existing?.AdministrationId,
            ScopesAtAuthorize: existing?.ScopesAtAuthorize);
        await _connections.SaveAsync(connection, ct);

        return new AdsolutRefreshResult(token.AccessToken, expiresUtc);
    }

    // ---- /disconnect ----------------------------------------------------

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        // Wipe refresh-token first; if the second delete throws we'd
        // rather have an orphan metadata row (visible, harmless) than an
        // orphan refresh-token (invisible, still usable).
        await _secrets.DeleteAsync(ProtectedSecretKeys.AdsolutRefreshToken, ct);
        await _connections.DeleteAsync(ct);
    }

    // ---- helpers --------------------------------------------------------

    private async Task<(AdsolutEnvironment Env, string ClientId, string? ClientSecret, string Scopes)> ReadConfigAsync(CancellationToken ct)
    {
        var envRaw = await _settings.GetAsync<string>(SettingKeys.Adsolut.Environment, ct);
        var env = AdsolutOAuthEndpoints.Parse(envRaw);
        if (env == AdsolutEnvironment.Production && !string.Equals(envRaw?.Trim(), "production", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(envRaw))
        {
            // Loud-by-log: a typo (e.g. "prod" or "uatx") silently falls
            // back to production. Logging at warn-level so an ops person
            // doing log triage spots it before the user clicks Connect.
            _logger.LogWarning("Adsolut.Environment value '{Raw}' is not a recognised environment; defaulting to production.", envRaw);
        }

        var clientId = (await _settings.GetAsync<string>(SettingKeys.Adsolut.ClientId, ct) ?? string.Empty).Trim();
        var clientSecret = await _secrets.GetAsync(ProtectedSecretKeys.AdsolutClientSecret, ct);
        var scopes = (await _settings.GetAsync<string>(SettingKeys.Adsolut.Scopes, ct) ?? "openid offline_access").Trim();
        if (scopes.Length == 0) scopes = "openid offline_access";
        return (env, clientId, clientSecret, scopes);
    }

    /// Wraps <see cref="PostTokenEndpointAsync"/> with latency-stopwatch +
    /// integration_audit logging. The audit row lands regardless of outcome
    /// (success → Ok, transient failure → Warn, terminal/invalid_grant →
    /// Error), so an admin can see slow-but-succeeding calls and revoked
    /// refresh tokens in the same table. Logging failures are swallowed
    /// inside <see cref="IntegrationAuditLogger"/> so a logging glitch
    /// never escalates into an integration outage.
    private async Task<TokenResponse> CallTokenEndpointWithAuditAsync(
        AdsolutEnvironment env,
        string clientId,
        string clientSecret,
        IDictionary<string, string> formBody,
        string eventType,
        string source,
        string? actorId,
        string? actorRole,
        CancellationToken ct)
    {
        var endpoint = AdsolutOAuthEndpoints.Token(env);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (token, httpStatus) = await PostTokenEndpointAsync(env, clientId, clientSecret, formBody, ct);
            stopwatch.Stop();
            await _integrationAudit.LogAsync(new IntegrationAuditEvent(
                Integration: AdsolutEventTypes.Integration,
                EventType: eventType,
                Outcome: IntegrationAuditOutcome.Ok,
                Endpoint: endpoint,
                HttpStatus: httpStatus,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ActorId: actorId,
                ActorRole: actorRole,
                Payload: new { source }), ct);
            return token;
        }
        catch (TokenEndpointException ex)
        {
            stopwatch.Stop();
            // invalid_grant = the RT was revoked or the sliding window
            // elapsed; admin must reconnect → flag as Error so the admin
            // overview surfaces it in red. All other token-endpoint
            // failures (5xx, network blip, parser hiccup) are transient
            // and recoverable → Warn so they don't drown out a real
            // terminal failure.
            var outcome = string.Equals(ex.UpstreamErrorCode, "invalid_grant", StringComparison.Ordinal)
                ? IntegrationAuditOutcome.Error
                : IntegrationAuditOutcome.Warn;
            await _integrationAudit.LogAsync(new IntegrationAuditEvent(
                Integration: AdsolutEventTypes.Integration,
                EventType: eventType,
                Outcome: outcome,
                Endpoint: endpoint,
                HttpStatus: ex.HttpStatus,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ActorId: actorId,
                ActorRole: actorRole,
                ErrorCode: ex.UpstreamErrorCode,
                Payload: new { source, message = ex.Message }), ct);
            throw;
        }
    }

    private async Task<(TokenResponse Token, int HttpStatus)> PostTokenEndpointAsync(
        AdsolutEnvironment env,
        string clientId,
        string clientSecret,
        IDictionary<string, string> formBody,
        CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, AdsolutOAuthEndpoints.Token(env));

        // Adsolut docs (step 3): "secured with Basic Authentication using
        // your Client ID and Client Secret as username and password
        // respectively." Some IdentityServer instances also accept the
        // credentials in the form body; we pick exactly one to avoid
        // ambiguous-client-auth rejections.
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = new FormUrlEncodedContent(formBody);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var status = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            string? upstreamError = null;
            try
            {
                using var errorDoc = JsonDocument.Parse(body);
                if (errorDoc.RootElement.TryGetProperty("error", out var err) &&
                    err.ValueKind == JsonValueKind.String)
                {
                    upstreamError = err.GetString();
                }
            }
            catch (JsonException)
            {
                // body wasn't JSON — leave upstreamError null and surface
                // the HTTP status in the message.
            }
            throw new TokenEndpointException(
                $"Token endpoint returned {status}: {upstreamError ?? "non_success"}",
                upstreamError,
                status);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<TokenResponse>(body);
            if (parsed is null) throw new TokenEndpointException("token_response_unparseable", null, status);
            return (parsed, status);
        }
        catch (JsonException ex)
        {
            throw new TokenEndpointException("token_response_unparseable: " + ex.GetType().Name, null, status);
        }
    }

    private async Task StampRefreshErrorAsync(string errorCode, CancellationToken ct)
    {
        var existing = await _connections.GetAsync(ct);
        var nowUtc = DateTime.UtcNow;
        var updated = existing is null
            ? new AdsolutConnection(null, null, null, null, null, errorCode, nowUtc, nowUtc)
            : existing with { LastRefreshError = errorCode, LastRefreshErrorUtc = nowUtc, UpdatedUtc = nowUtc };
        await _connections.SaveAsync(updated, ct);
    }

    private static int SafeExpiresIn(int? expiresIn)
    {
        // Adsolut docs: "access token is only valid for 60 minutes". Cap
        // generously to defend against an upstream bug returning 0 or a
        // wildly large value (would push our cached access-token expiry
        // beyond the refresh-token's sliding-month window).
        if (expiresIn is null or <= 0) return 60 * 60;
        return Math.Min(expiresIn.Value, 60 * 60 * 2);
    }

    private static (string? Subject, string? Email) TryDecodeIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return (null, null);
        try
        {
            var parts = idToken.Split('.');
            if (parts.Length != 3) return (null, null);
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            string? sub = root.TryGetProperty("sub", out var subEl) && subEl.ValueKind == JsonValueKind.String
                ? subEl.GetString() : null;
            string? email = null;
            // Try the common claim names in order of how Adsolut/WK is
            // most likely to populate them. Stop at the first non-empty.
            foreach (var key in new[] { "preferred_username", "email", "upn", "name" })
            {
                if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                {
                    var v = el.GetString();
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        email = v;
                        break;
                    }
                }
            }
            return (sub, email);
        }
        catch (Exception)
        {
            // Decoded id_token is informational — never surface a parse
            // failure to the caller; just treat it as "unknown subject".
            return (null, null);
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("invalid base64url");
        }
        return Convert.FromBase64String(s);
    }

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var kv in pairs)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value));
        }
        return sb.ToString();
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken);

    private sealed class TokenEndpointException : Exception
    {
        public string? UpstreamErrorCode { get; }
        public int? HttpStatus { get; }
        public TokenEndpointException(string message, string? upstreamErrorCode, int? httpStatus = null) : base(message)
        {
            UpstreamErrorCode = upstreamErrorCode;
            HttpStatus = httpStatus;
        }
    }
}
