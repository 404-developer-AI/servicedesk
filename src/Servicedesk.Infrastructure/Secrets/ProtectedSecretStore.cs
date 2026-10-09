using System.Collections.Concurrent;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace Servicedesk.Infrastructure.Secrets;

public sealed class ProtectedSecretStore : IProtectedSecretStore
{
    private const string Purpose = "Servicedesk.ProtectedSecrets";

    // v0.1.25 — short read-through cache. The pollers (Telavox every ~10 s
    // per agent, mail, M365) and the health check re-read the same secrets
    // constantly: ~4,000 queries an hour in the Performance monitor. Only
    // the *protected* (encrypted) value is cached, so plaintext never sits
    // in this cache; every Set/Delete on this instance drops the entry at
    // once. The TTL only bounds staleness for a second app instance during
    // an update. Rotating credentials (OAuth refresh tokens, renewed on
    // every use) bypass the cache entirely so two instances can never
    // replay an already-rotated token.
    // v0.1.27 — 30 s was about the health poll interval, so /api/system/health
    // still missed on nearly every request (8 secret reads each). The app runs
    // as a single process and Set/Delete invalidate immediately, so a longer
    // TTL only matters for the brief old/new overlap during an update.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private const string SelectSql =
        "SELECT value_protected AS ValueProtected FROM protected_secrets WHERE key = @key";

    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, CachedSecret> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedExists> _exists = new(StringComparer.Ordinal);

    private const string ExistsSql =
        "SELECT EXISTS (SELECT 1 FROM protected_secrets WHERE key = @key)";

    // Bumped by every Set/Delete: a read that started before a write must
    // not put the pre-write value back into the cache.
    private long _generation;

    private sealed record CachedSecret(string? ProtectedValue, long ExpiresTimestamp);
    private sealed record CachedExists(bool Exists, long ExpiresTimestamp);

    public ProtectedSecretStore(NpgsqlDataSource dataSource, IDataProtectionProvider provider, TimeProvider? time = null)
    {
        _dataSource = dataSource;
        _protector = provider.CreateProtector(Purpose);
        _time = time ?? TimeProvider.System;
    }

    private static bool Cacheable(string key) =>
        !key.Contains("RefreshToken", StringComparison.OrdinalIgnoreCase);

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var protectedValue = await GetProtectedAsync(key, ct);
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try { return _protector.Unprotect(protectedValue); }
        catch { return null; }
    }

    public async Task SetAsync(string key, string plaintext, CancellationToken ct = default)
    {
        var protectedValue = _protector.Protect(plaintext);
        Invalidate(key);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO protected_secrets (key, value_protected, updated_utc)
            VALUES (@key, @protectedValue, now())
            ON CONFLICT (key) DO UPDATE
                SET value_protected = EXCLUDED.value_protected,
                    updated_utc = now()
            """, new { key, protectedValue }, cancellationToken: ct));
        Invalidate(key);
    }

    public async Task<bool> HasAsync(string key, CancellationToken ct = default)
    {
        // value_protected is NOT NULL, so "row exists" == "value not null";
        // shares the cache entry with GetAsync.
        if (Cacheable(key)) return await GetProtectedAsync(key, ct) is not null;

        // v0.1.33 — rotating keys never cache their value, but whether the row
        // exists doesn't change when the token rotates (only Set/Delete change
        // it, and those invalidate). The health check asks this on every
        // evaluation; cache the answer, never the value.
        if (_exists.TryGetValue(key, out var hit) && hit.ExpiresTimestamp > _time.GetTimestamp())
            return hit.Exists;

        var generation = Interlocked.Read(ref _generation);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            ExistsSql, new { key }, cancellationToken: ct));
        if (Interlocked.Read(ref _generation) == generation)
        {
            var expires = _time.GetTimestamp() + (long)(CacheTtl.TotalSeconds * _time.TimestampFrequency);
            _exists[key] = new CachedExists(exists, expires);
        }
        return exists;
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Invalidate(key);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM protected_secrets WHERE key = @key",
            new { key }, cancellationToken: ct));
        Invalidate(key);
    }

    private void Invalidate(string key)
    {
        Interlocked.Increment(ref _generation);
        _cache.TryRemove(key, out _);
        _exists.TryRemove(key, out _);
    }

    private async Task<string?> GetProtectedAsync(string key, CancellationToken ct)
    {
        var cacheable = Cacheable(key);
        if (cacheable && _cache.TryGetValue(key, out var hit) && hit.ExpiresTimestamp > _time.GetTimestamp())
            return hit.ProtectedValue;

        var generation = Interlocked.Read(ref _generation);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var value = await conn.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(
            SelectSql, new { key }, cancellationToken: ct));

        // A missing row is cached too (as null): "not configured" is the
        // common answer for unused integrations and is asked just as often.
        if (cacheable && Interlocked.Read(ref _generation) == generation)
        {
            var expires = _time.GetTimestamp() + (long)(CacheTtl.TotalSeconds * _time.TimestampFrequency);
            _cache[key] = new CachedSecret(value, expires);
        }
        return value;
    }
}
