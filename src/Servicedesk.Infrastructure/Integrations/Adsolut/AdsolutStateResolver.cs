using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Single source of truth for "what state is the Adsolut connection in?" —
/// shared between <c>GetStatus</c>, the healthcheck worker, the sync worker
/// post-tick push, and any endpoint that wants to broadcast a SignalR
/// transition after a mutation. Centralised so the call sites can never
/// drift apart on the boundary definitions (e.g. "is a transient
/// refresh-error still 'connected'?", or "does a failing sync downgrade an
/// otherwise healthy OAuth state?").
public static class AdsolutStateResolver
{
    public const string NotConfigured = "not_configured";
    public const string NotConnected = "not_connected";
    public const string Connected = "connected";
    public const string RefreshFailed = "refresh_failed";

    /// OAuth side is healthy but the most recent sync.tick wrote an error
    /// into <c>adsolut_sync_state.LastError</c>. Distinct from refresh_failed
    /// so the UI can show "data pull failing — check audit" (amber) instead
    /// of "reconnect required" (red); the corrective action is different.
    public const string SyncFailing = "sync_failing";

    /// OAuth-only resolver. Kept for callers that genuinely don't care
    /// about sync-tick health (e.g. the connect/disconnect mutation path
    /// that pre-dates the sync worker). New callers should prefer the
    /// overload that takes a sync-state store so the broadcast/UI state
    /// reflects the full integration health.
    public static Task<string> ComputeAsync(
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IAdsolutConnectionStore connections,
        CancellationToken ct = default)
        => ComputeAsync(settings, secrets, connections, syncStateStore: null, ct);

    public static async Task<string> ComputeAsync(
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IAdsolutConnectionStore connections,
        IAdsolutSyncStateStore? syncStateStore,
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

        // OAuth is healthy. If the caller cares about sync health and the
        // last tick recorded an error, downgrade to sync_failing — unless
        // the admin has acknowledged it. The acknowledgement only suppresses
        // the *current* error; the next failed tick advances LastErrorUtc
        // past AcknowledgedUtc and the state flips back to sync_failing
        // automatically.
        if (syncStateStore is not null)
        {
            var syncState = await syncStateStore.GetAsync(ct);
            if (!string.IsNullOrEmpty(syncState?.LastError) && !IsAcknowledged(syncState))
            {
                return SyncFailing;
            }
        }

        return Connected;
    }

    /// True when the most recent admin acknowledgement covers the current
    /// LastErrorUtc — i.e. the admin has seen this specific error and
    /// chosen to suppress it from the dashboard tile.
    private static bool IsAcknowledged(AdsolutSyncState state)
    {
        if (state.AcknowledgedUtc is not { } ack) return false;
        if (state.LastErrorUtc is not { } err) return true;
        return ack >= err;
    }
}
