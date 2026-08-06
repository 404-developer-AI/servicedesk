namespace Servicedesk.Infrastructure.Secrets;

/// Runtime-editable, encrypted secret store. Used for credentials an admin
/// configures via the Settings UI (e.g. Microsoft Graph client secret).
/// Values are encrypted at rest via DataProtection; callers only see
/// plaintext when they explicitly request it.
public interface IProtectedSecretStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string plaintext, CancellationToken ct = default);
    Task<bool> HasAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public static class ProtectedSecretKeys
{
    public const string GraphClientSecret = "Graph.ClientSecret";

    // Adsolut OAuth integration (v0.0.25). The client secret is provisioned
    // by Wolters Kluwer per install; the refresh token is the long-lived
    // credential of the admin who authorized the integration and is rotated
    // on every refresh per the Adsolut docs.
    public const string AdsolutClientSecret = "Adsolut.ClientSecret";
    public const string AdsolutRefreshToken = "Adsolut.RefreshToken";

    // Telavox integration (v0.0.34). The PAPI partner-token is a single
    // install-wide credential, the CAPI tokens are per-agent and minted by
    // POST /customers/{customer}/api-users after an admin manually links
    // the SD-user to a Telavox-extension. CAPI keys are dynamic per user-id
    // so they are built via AgentCapiToken(userId) rather than declared as
    // a const.
    public const string TelavoxPartnerToken = "Telavox.PartnerToken";

    // Zammad migration link (v0.0.41). One HTTP token per Servicedesk
    // install — minted by the admin under their personal Zammad profile
    // (Settings → Profile → Token Access). The token grants access to
    // tickets, articles, attachments, users and organizations of the
    // source Zammad instance, scoped to whatever permissions the minting
    // agent has.
    public const string ZammadToken = "Zammad.Token";

    /// Tactical RMM integration (v0.0.52). One API key per Servicedesk
    /// install — minted in TRMM under an admin account that has read
    /// access to clients, sites and agents. Sent on every TRMM call via
    /// the <c>X-API-KEY</c> header.
    public const string TrmmApiKey = "Trmm.ApiKey";

    /// Microsoft 365 customer-tenant reader (v0.0.77). Client secret of the
    /// MSP tenant's multi-tenant app registration. One install-wide secret;
    /// customer tenants contribute only a tenant id (stored per-company),
    /// never a secret. Distinct from <see cref="GraphClientSecret"/>, which is
    /// the single-tenant app used for mail + OIDC.
    public const string M365ClientSecret = "M365.ClientSecret";

    /// Sophos Central spam-filter matching (v0.0.78). The MSP partner API
    /// credential pair. Both are write-only secrets in the partner model: the
    /// client id is provisioned alongside the secret in the Sophos Central
    /// partner dashboard and is not a per-customer identifier, so it is kept in
    /// the encrypted store rather than the plaintext settings table.
    public const string SophosClientId = "Sophos.ClientId";
    public const string SophosClientSecret = "Sophos.ClientSecret";

    /// Veeam backup matching. Password of the Veeam Service Provider Console
    /// (VSPC) API account used for the OAuth2 password grant. One install-wide
    /// credential; the username + base URL are non-secret settings.
    public const string VeeamPassword = "Veeam.Password";

    /// Timesheet migration import (v0.0.54). One install-wide pre-shared
    /// secret minted by an admin under Settings → Timesheet → Migration
    /// import. The standalone migration tool sends it on every call to the
    /// secret-gated import surface (X-Timesheet-Import-Token). Rotatable and
    /// clearable from the same panel; clearing it disables the surface.
    public const string TimesheetImportToken = "Timesheet.ImportToken";

    /// Reporting API (machine-to-machine ticket statistics). One install-wide
    /// pre-shared key minted by an admin under Settings → Reporting API. An
    /// external caller sends it on every request via the
    /// <c>X-Reporting-Api-Key</c> header. Rotatable and clearable from the
    /// same panel; clearing it disables the surface.
    public const string ReportingApiKey = "Reporting.ApiKey";

    /// Claude AI assist integration. The Anthropic API key used for the
    /// "AI proposal" feature inside a ticket. One install-wide key, sent on
    /// every Messages API call via the <c>x-api-key</c> header. Write-only:
    /// only its existence is ever reported back to the admin UI, never the
    /// value. The organisation that owns this key must be configured for
    /// zero data retention (a separate, admin-confirmed precondition).
    public const string ClaudeApiKey = "Claude.ApiKey";

    /// Encrypted-secret key for a per-agent Telavox CAPI token. Returns a
    /// stable string built from the SD user-id so the protected_secrets
    /// row can be located on every poll and cleared on de-provision.
    public static string TelavoxAgentCapiToken(Guid userId) =>
        $"Telavox.AgentCapiToken.{userId:D}";
}
