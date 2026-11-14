using System.Security.Cryptography;
using System.Text;

namespace Servicedesk.Infrastructure.Integrations.Http;

/// One cached OAuth bearer token per integration client, shared by the
/// integrations that fetch a client-credentials token (Sophos, Veeam VSPC).
///
/// The token is tied to the credentials it was fetched with: callers pass a
/// key (use <see cref="KeyFor"/>), and a different key always refetches — so
/// a changed client id/secret or base URL never keeps using a token of the
/// previous account. Refreshes a minute before expiry (at least 60 s
/// lifetime); one fetch at a time.
public sealed class BearerTokenCache
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeProvider _clock;
    private Entry? _entry;

    public BearerTokenCache(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    private sealed record Entry(string Key, string Token, DateTimeOffset ExpiresUtc);

    /// A stable, non-reversible key for a set of credential parts.
    public static string KeyFor(params string?[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", parts.Select(p => p ?? ""))));
        return Convert.ToHexString(bytes);
    }

    public async Task<string> GetAsync(
        string key,
        Func<CancellationToken, Task<(string Token, int ExpiresInSeconds)>> fetch,
        CancellationToken ct)
    {
        var cached = _entry;
        if (IsValid(cached, key)) return cached!.Token;

        await _lock.WaitAsync(ct);
        try
        {
            cached = _entry;
            if (IsValid(cached, key)) return cached!.Token;

            var (token, expiresIn) = await fetch(ct);
            _entry = new Entry(key, token, _clock.GetUtcNow().AddSeconds(Math.Max(60, expiresIn - 60)));
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// Drops the cached token (e.g. after the API answered 401 with it).
    public void Invalidate() => _entry = null;

    private bool IsValid(Entry? e, string key) =>
        e is not null && e.Key == key && e.ExpiresUtc > _clock.GetUtcNow();
}
