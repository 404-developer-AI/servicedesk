using System.Collections.Concurrent;
using System.Globalization;
using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Audit;

namespace Servicedesk.Infrastructure.Settings;

public sealed class SettingsService : ISettingsService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IAuditLogger _audit;
    private readonly ConcurrentDictionary<string, SettingEntry> _cache = new();
    private bool _primed;
    private readonly SemaphoreSlim _primeLock = new(1, 1);

    public SettingsService(NpgsqlDataSource dataSource, IAuditLogger audit)
    {
        _dataSource = dataSource;
        _audit = audit;
    }

    public async Task EnsureDefaultsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO settings (key, value, value_type, category, description, default_value, updated_utc)
            VALUES (@Key, @Value, @ValueType, @Category, @Description, @DefaultValue, now())
            ON CONFLICT (key) DO UPDATE
                SET value_type = EXCLUDED.value_type,
                    category = EXCLUDED.category,
                    description = EXCLUDED.description,
                    default_value = EXCLUDED.default_value
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        foreach (var d in SettingDefaults.All)
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                d.Key,
                Value = d.Value,
                d.ValueType,
                d.Category,
                d.Description,
                DefaultValue = d.Value,
            }, cancellationToken: cancellationToken));
        }

        _cache.Clear();
        _primed = false;
    }

    public async Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        await EnsurePrimedAsync(cancellationToken);
        if (!_cache.TryGetValue(key, out var entry))
        {
            var def = SettingDefaults.All.FirstOrDefault(d => d.Key == key)
                ?? throw new KeyNotFoundException($"Unknown setting key '{key}'.");
            return Convert<T>(def.Value);
        }
        return Convert<T>(entry.Value);
    }

    public async Task SetAsync<T>(string key, T value, string actor, string actorRole, CancellationToken cancellationToken = default)
    {
        await EnsurePrimedAsync(cancellationToken);

        var def = SettingDefaults.All.FirstOrDefault(d => d.Key == key)
            ?? throw new KeyNotFoundException($"Unknown setting key '{key}'.");

        var newValue = value is null ? "" : System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        _cache.TryGetValue(key, out var previous);
        var oldValue = previous?.Value ?? def.Value;

        const string sql = """
            UPDATE settings SET value = @value, updated_utc = now() WHERE key = @key
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, new { key, value = newValue }, cancellationToken: cancellationToken));

        _cache[key] = new SettingEntry(key, newValue, def.ValueType, def.Category, def.Description, def.Value, DateTimeOffset.UtcNow);

        await _audit.LogAsync(new AuditEvent(
            EventType: "setting_changed",
            Actor: actor,
            ActorRole: actorRole,
            Target: key,
            Payload: new { oldValue, newValue }), cancellationToken);
    }

    public async Task<IReadOnlyList<SettingEntry>> ListAsync(string? category = null, CancellationToken cancellationToken = default)
    {
        await EnsurePrimedAsync(cancellationToken);
        var entries = _cache.Values.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(category))
        {
            entries = entries.Where(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase));
        }
        return entries.OrderBy(e => e.Category).ThenBy(e => e.Key).ToList();
    }

    private async Task EnsurePrimedAsync(CancellationToken cancellationToken)
    {
        if (_primed)
        {
            return;
        }
        await _primeLock.WaitAsync(cancellationToken);
        try
        {
            if (_primed)
            {
                return;
            }

            const string sql = """
                SELECT key AS Key, value AS Value, value_type AS ValueType, category AS Category,
                       description AS Description, default_value AS DefaultValue, updated_utc AS UpdatedUtc
                FROM settings
                """;
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<SettingEntry>(
                new CommandDefinition(sql, cancellationToken: cancellationToken));
            foreach (var row in rows)
            {
                _cache[row.Key] = row;
            }
            _primed = true;
        }
        finally
        {
            _primeLock.Release();
        }
    }

    private static T Convert<T>(string raw)
    {
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (targetType == typeof(string))
        {
            return (T)(object)raw;
        }
        return (T)System.Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
    }
}
