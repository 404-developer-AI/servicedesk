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
}
