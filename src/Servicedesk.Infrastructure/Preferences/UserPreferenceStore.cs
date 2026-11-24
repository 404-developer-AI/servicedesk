using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Preferences;

/// Per-user key/value preferences (<c>user_preferences</c>): column layouts,
/// workspace snapshots, theme, pinned nav items, notification-mail switches.
/// The endpoints own the keys and the validation; this store owns the SQL.
public interface IUserPreferenceStore
{
    Task<string?> GetAsync(Guid userId, string key, CancellationToken ct);

    /// The stored values for <paramref name="keys"/>; missing keys are absent.
    Task<IReadOnlyDictionary<string, string>> GetManyAsync(Guid userId, IReadOnlyCollection<string> keys, CancellationToken ct);

    /// Every value whose key starts with <paramref name="prefix"/> (literal, no wildcards).
    Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(Guid userId, string prefix, CancellationToken ct);

    /// Upserts one value (always bumps <c>updated_utc</c>).
    Task SetAsync(Guid userId, string key, string value, CancellationToken ct);

    /// Upserts many values in one statement; unchanged values are not
    /// rewritten, so a save with nothing new produces no dead rows (v0.1.25 —
    /// the workspace snapshot sends up to ~150 keys per save).
    Task SetManyAsync(Guid userId, IReadOnlyDictionary<string, string> values, CancellationToken ct);

    Task DeleteAsync(Guid userId, string key, CancellationToken ct);
}

public sealed class UserPreferenceStore : IUserPreferenceStore
{
    private readonly NpgsqlDataSource _dataSource;

    public UserPreferenceStore(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<string?> GetAsync(Guid userId, string key, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT pref_value FROM user_preferences WHERE user_id = @userId AND pref_key = @key",
            new { userId, key }, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<string, string>> GetManyAsync(
        Guid userId, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string Key, string Value)>(new CommandDefinition(
            "SELECT pref_key, pref_value FROM user_preferences WHERE user_id = @userId AND pref_key = ANY(@keys)",
            new { userId, keys = keys.ToArray() }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(Guid userId, string prefix, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string Key, string Value)>(new CommandDefinition(
            "SELECT pref_key, pref_value FROM user_preferences WHERE user_id = @userId AND starts_with(pref_key, @prefix)",
            new { userId, prefix }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    public async Task SetAsync(Guid userId, string key, string value, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO user_preferences (user_id, pref_key, pref_value)
            VALUES (@userId, @key, @value)
            ON CONFLICT (user_id, pref_key) DO UPDATE
                SET pref_value = @value, updated_utc = now()
            """,
            new { userId, key, value }, cancellationToken: ct));
    }

    public async Task SetManyAsync(Guid userId, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        if (values.Count == 0) return;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO user_preferences (user_id, pref_key, pref_value)
            SELECT @userId, k, v FROM unnest(@keys::text[], @values::text[]) AS e(k, v)
            ON CONFLICT (user_id, pref_key) DO UPDATE
                SET pref_value = EXCLUDED.pref_value, updated_utc = now()
                WHERE user_preferences.pref_value IS DISTINCT FROM EXCLUDED.pref_value
            """,
            new { userId, keys = values.Keys.ToArray(), values = values.Values.ToArray() },
            cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid userId, string key, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_preferences WHERE user_id = @userId AND pref_key = @key",
            new { userId, key }, cancellationToken: ct));
    }
}
