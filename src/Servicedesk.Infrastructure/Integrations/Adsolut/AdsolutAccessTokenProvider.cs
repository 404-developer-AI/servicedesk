namespace Servicedesk.Infrastructure.Integrations.Adsolut;

public sealed class AdsolutAccessTokenProvider : IAdsolutAccessTokenProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IAdsolutAuthService _auth;
    private readonly object _lock = new();

    private string? _cachedToken;
    private DateTime _cachedExpiryUtc;

    public AdsolutAccessTokenProvider(IAdsolutAuthService auth)
    {
        _auth = auth;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // Fast path: cached value still inside the 5-minute window.
        lock (_lock)
        {
            if (_cachedToken is not null && _cachedExpiryUtc > DateTime.UtcNow)
            {
                return _cachedToken;
            }
        }

        // Refresh outside the lock so a second caller hitting the slow path
        // doesn't block the WK roundtrip, but a brief duplicate refresh is
        // acceptable — RT rotation handles concurrent rotates idempotently
        // and the WK call-budget is loose at our cadence.
        var refreshed = await _auth.RefreshAccessTokenAsync("api_call", ct: ct);

        lock (_lock)
        {
            _cachedToken = refreshed.AccessToken;
            // Cap our cache window at 5 minutes regardless of how long the
            // upstream access token lives — gives plenty of headroom to
            // notice an admin-side disconnect and stop using a token that's
            // about to be revoked.
            var window = refreshed.ExpiresUtc.AddMinutes(-1) - DateTime.UtcNow;
            _cachedExpiryUtc = DateTime.UtcNow + (window < CacheDuration ? window : CacheDuration);
            return _cachedToken;
        }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _cachedToken = null;
            _cachedExpiryUtc = DateTime.MinValue;
        }
    }
}
