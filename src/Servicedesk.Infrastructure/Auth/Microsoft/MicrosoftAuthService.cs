using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Auth.Microsoft;

public sealed class MicrosoftAuthService : IMicrosoftAuthService
{
    private const string ProviderName = ExternalProviders.Microsoft;

    private readonly ISettingsService _settings;
    private readonly IProtectedSecretStore _secrets;
    private readonly IUserService _users;
    private readonly IGraphDirectoryClient _directory;
    private readonly IHttpClientFactory _httpClientFactory;

    // One ConfigurationManager per tenant-id. ConfigurationManager
    // already caches the discovery doc + JWKS internally for 24h; we
    // just key by tenant so a config change in Settings (new tenant-id)
    // starts a fresh cache instead of serving the old one.
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _configByTenant = new();

    public MicrosoftAuthService(
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IUserService users,
        IGraphDirectoryClient directory,
        IHttpClientFactory httpClientFactory)
    {
        _settings = settings;
        _secrets = secrets;
        _users = users;
        _directory = directory;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ChallengeIntent> CreateChallengeAsync(string redirectUri, string? returnUrl, CancellationToken ct = default)
    {
        var (tenantId, clientId, _) = await ReadOidcConfigAsync(ct);

        var config = await GetConfigAsync(tenantId, ct);

        var state = GenerateToken(32);
        var nonce = GenerateToken(32);
        var codeVerifier = GenerateToken(48);
        var codeChallenge = ComputeCodeChallenge(codeVerifier);

        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = "openid profile email",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            // Hard-stops Azure AD from silently logging in with a cached
            // primary account when the admin expected the user to pick
            // which account to sign in with. `select_account` preserves
            // SSO when possible but always surfaces the picker.
            ["prompt"] = "select_account",
        };
        var url = config.AuthorizationEndpoint + "?" + BuildQueryString(query);

        return new ChallengeIntent(url, state, nonce, codeVerifier, returnUrl);
    }

    public async Task<CallbackResult> ValidateCallbackAsync(
        string code,
        string redirectUri,
        ChallengeIntent intent,
        CancellationToken ct = default)
    {
        var (tenantId, clientId, clientSecret) = await ReadOidcConfigAsync(ct);
        var config = await GetConfigAsync(tenantId, ct);

        // ---- 1) Code exchange ------------------------------------------
        string idToken;
        try
        {
            idToken = await ExchangeCodeForIdTokenAsync(
                config.TokenEndpoint,
                code,
                intent.CodeVerifier,
                redirectUri,
                clientId,
                clientSecret,
                ct);
        }
        catch (TokenExchangeException ex)
        {
            return new CallbackResult.Rejected(
                Reason: CallbackRejectReason.CodeExchangeFailed,
                Oid: null,
                PreferredUsername: null,
                Detail: ex.AzureErrorCode ?? "code_exchange_failed");
        }

        // ---- 2) Token validation ---------------------------------------
        var validationParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"https://login.microsoftonline.com/{tenantId}/v2.0",
            ValidateAudience = true,
            ValidAudience = clientId,
            ValidateLifetime = true,
            // Small skew — the Microsoft guidance is 5 minutes; we keep
            // it tight. The token is minted seconds before we see it.
            ClockSkew = TimeSpan.FromMinutes(2),
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = config.SigningKeys,
            // Defence-in-depth: reject tokens whose alg is not RS256. The
            // handler validates signature against the JWKS, but pinning
            // the algorithm stops a swap-attack via a rogue key.
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
        };

        var handler = new JsonWebTokenHandler();
        var tokenResult = await handler.ValidateTokenAsync(idToken, validationParams);
        if (!tokenResult.IsValid)
        {
            return new CallbackResult.Rejected(
                Reason: CallbackRejectReason.InvalidToken,
                Oid: null,
                PreferredUsername: null,
                Detail: tokenResult.Exception?.GetType().Name ?? "invalid_token");
        }

        // Nonce replay-guard: the token's nonce claim must match what we
        // sent on the authorize URL. Without this, an attacker who grabs
        // a code from a different login session could redeem it into the
        // victim's browser.
        var jwt = (JsonWebToken)tokenResult.SecurityToken;
        var tokenNonce = jwt.GetClaim("nonce")?.Value;
        if (!string.Equals(tokenNonce, intent.Nonce, StringComparison.Ordinal))
        {
            return new CallbackResult.Rejected(
                Reason: CallbackRejectReason.InvalidToken,
                Oid: null,
                PreferredUsername: null,
                Detail: "nonce_mismatch");
        }

        var oid = jwt.GetClaim("oid")?.Value;
        var preferredUsername = jwt.GetClaim("preferred_username")?.Value;
        if (string.IsNullOrWhiteSpace(oid))
        {
            return new CallbackResult.Rejected(
                Reason: CallbackRejectReason.InvalidToken,
                Oid: null,
                PreferredUsername: preferredUsername,
                Detail: "missing_oid");
        }

        // ---- 3) Local user lookup --------------------------------------
        var user = await _users.FindByExternalAsync(ProviderName, oid, ct);
        if (user is null)
        {
            return new CallbackResult.Rejected(
                Reason: CallbackRejectReason.UnknownOid,
                Oid: oid,
                PreferredUsername: preferredUsername,
                Detail: null);
        }

        if (string.Equals(user.RoleName, "Customer", StringComparison.Ordinal))
        {
            return new CallbackResult.Rejected(
                Reason: CallbackRejectReason.CustomerRole,
                Oid: oid,
                PreferredUsername: preferredUsername,
                Detail: null);
        }

        if (!user.IsActive)
        {
            return new CallbackResult.Rejected(
                Reason: CallbackRejectReason.Inactive,
                Oid: oid,
                PreferredUsername: preferredUsername,
                Detail: null);
        }

        // ---- 4) Graph accountEnabled cross-check -----------------------
        // Graph outage: we fail closed. A transient Graph 5xx turns into
        // AccountDisabled-with-"graph_error" so the user retries — we do
        // not want to bypass the check just because Graph is flaky, and
        // we do not want to mint a session for a potentially-disabled
        // account. The local user is NOT marked inactive on a Graph
        // error, only on a definitive false / 404.
        GraphUserStatus? status;
        try
        {
            status = await _directory.GetUserStatusAsync(oid, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CallbackResult.Rejected(
                Reason: CallbackRejectReason.AccountDisabled,
                Oid: oid,
                PreferredUsername: preferredUsername,
                Detail: "graph_error:" + ex.GetType().Name);
        }

        if (status is null || !status.AccountEnabled)
        {
            // Definitive "no" — propagate to the local row so the next
            // attempt short-circuits at Inactive.
            await _users.MarkInactiveAsync(user.Id, ct);
            return new CallbackResult.Rejected(
                Reason: CallbackRejectReason.AccountDisabled,
                Oid: oid,
                PreferredUsername: preferredUsername,
                Detail: status is null ? "oid_not_in_tenant" : "account_enabled_false");
        }

        return new CallbackResult.Success(user, oid);
    }

    // ---- helpers -------------------------------------------------------

    private async Task<(string TenantId, string ClientId, string ClientSecret)> ReadOidcConfigAsync(CancellationToken ct)
    {
        var tenantId = await _settings.GetAsync<string>(SettingKeys.Graph.TenantId, ct);
        var clientId = await _settings.GetAsync<string>(SettingKeys.Graph.ClientId, ct);
        var clientSecret = await _secrets.GetAsync(ProtectedSecretKeys.GraphClientSecret, ct);

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "Microsoft 365 login is not fully configured. Set Graph.TenantId, Graph.ClientId, and the client secret via Settings.");
        }

        return (tenantId, clientId, clientSecret);
    }

    private async Task<OpenIdConnectConfiguration> GetConfigAsync(string tenantId, CancellationToken ct)
    {
        var manager = _configByTenant.GetOrAdd(tenantId, tid =>
        {
            var metadataAddress = $"https://login.microsoftonline.com/{tid}/v2.0/.well-known/openid-configuration";
            return new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(_httpClientFactory.CreateClient("oidc-discovery"))
                {
                    RequireHttps = true,
                });
        });
        return await manager.GetConfigurationAsync(ct);
    }

    private async Task<string> ExchangeCodeForIdTokenAsync(
        string tokenEndpoint,
        string code,
        string codeVerifier,
        string redirectUri,
        string clientId,
        string clientSecret,
        CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient("oidc-token");
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["scope"] = "openid profile email",
        });
        using var response = await http.SendAsync(request, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string? aadErrorCode = null;
            try
            {
                var errorDoc = JsonDocument.Parse(body);
                if (errorDoc.RootElement.TryGetProperty("error", out var err))
                {
                    aadErrorCode = err.GetString();
                }
            }
            catch (JsonException)
            {
                // fall through — AzureErrorCode stays null
            }
            throw new TokenExchangeException(aadErrorCode);
        }

        var token = JsonSerializer.Deserialize<TokenResponse>(body)
            ?? throw new TokenExchangeException("unparseable_response");
        if (string.IsNullOrWhiteSpace(token.IdToken))
        {
            throw new TokenExchangeException("missing_id_token");
        }
        return token.IdToken;
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

    private static string GenerateToken(int byteLength) => OidcProtocol.GenerateUrlSafeToken(byteLength);

    private static string ComputeCodeChallenge(string codeVerifier) => OidcProtocol.ComputeCodeChallengeS256(codeVerifier);

    private sealed record TokenResponse(
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn);

    private sealed class TokenExchangeException : Exception
    {
        public string? AzureErrorCode { get; }
        public TokenExchangeException(string? azureErrorCode)
            : base("Token exchange failed: " + (azureErrorCode ?? "unknown"))
        {
            AzureErrorCode = azureErrorCode;
        }
    }
}
