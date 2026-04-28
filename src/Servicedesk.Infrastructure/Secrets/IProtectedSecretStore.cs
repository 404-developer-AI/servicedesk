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
}
