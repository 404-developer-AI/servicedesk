using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Single source of truth for "what state is the Adsolut connection in?" —
/// shared between <c>GetStatus</c>, the healthcheck worker, and any endpoint
/// that wants to push a SignalR transition after a mutation. Centralised
/// so the four call sites can never drift apart on the boundary
/// definitions (e.g. "is a transient refresh-error still 'connected'?").
public static class AdsolutStateResolver
{
    public const string NotConfigured = "not_configured";
    public const string NotConnected = "not_connected";
    public const string Connected = "connected";
    public const string RefreshFailed = "refresh_failed";

    public static async Task<string> ComputeAsync(
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IAdsolutConnectionStore connections,
        CancellationToken ct = default)
    {
        var clientId = (await settings.GetAsync<string>(SettingKeys.Adsolut.ClientId, ct) ?? string.Empty).Trim();
        var hasSecret = await secrets.HasAsync(ProtectedSecretKeys.AdsolutClientSecret, ct);
        if (string.IsNullOrEmpty(clientId) || !hasSecret)
        {
            return NotConfigured;
        }

        var hasRefreshToken = await secrets.HasAsync(ProtectedSecretKeys.AdsolutRefreshToken, ct);
        if (!hasRefreshToken)
        {
            return NotConnected;
        }

        var connection = await connections.GetAsync(ct);
        if (connection?.LastRefreshError is not null)
        {
            return RefreshFailed;
        }

        return Connected;
    }
}
