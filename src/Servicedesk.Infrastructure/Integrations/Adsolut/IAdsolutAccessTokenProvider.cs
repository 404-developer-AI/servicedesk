namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Hands out a valid Adsolut access token to the API clients (Administrations,
/// Customers, …). Wraps <see cref="IAdsolutAuthService.RefreshAccessTokenAsync"/>
/// with a short in-memory cache so a tight loop of API calls (e.g. the sync
/// worker paging through Customers) doesn't roundtrip the WK token endpoint
/// for every request. The Adsolut access token is valid for ~60 minutes; we
/// cache 5 minutes to leave plenty of headroom.
public interface IAdsolutAccessTokenProvider
{
    /// Returns a current access token, refreshing via WK if the cached value
    /// is missing or near expiry. Throws <see cref="AdsolutRefreshException"/>
    /// when the integration is not configured or the refresh fails — callers
    /// are expected to surface these as a sync-tick failure rather than retry
    /// indefinitely.
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);

    /// Drops the cached token so the next call re-fetches. Used by the
    /// disconnect-flow and by API clients that just saw a 401 (signals the
    /// cached value was rejected upstream and we should not retry with it).
    void Invalidate();
}
