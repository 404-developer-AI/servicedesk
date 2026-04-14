using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace Servicedesk.Infrastructure.Secrets;

public sealed class ProtectedSecretStore : IProtectedSecretStore
{
    private const string Purpose = "Servicedesk.ProtectedSecrets";
    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataProtector _protector;

    public ProtectedSecretStore(NpgsqlDataSource dataSource, IDataProtectionProvider provider)
    {
        _dataSource = dataSource;
        _protector = provider.CreateProtector(Purpose);
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var protectedValue = await conn.QueryFirstOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT value_protected FROM protected_secrets WHERE key = @key",
            new { key }, cancellationToken: ct));
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try { return _protector.Unprotect(protectedValue); }
        catch { return null; }
    }

    public async Task SetAsync(string key, string plaintext, CancellationToken ct = default)
    {
        var protectedValue = _protector.Protect(plaintext);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO protected_secrets (key, value_protected, updated_utc)
            VALUES (@key, @protectedValue, now())
            ON CONFLICT (key) DO UPDATE
                SET value_protected = EXCLUDED.value_protected,
                    updated_utc = now()
            """, new { key, protectedValue }, cancellationToken: ct));
    }

    public async Task<bool> HasAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM protected_secrets WHERE key = @key",
            new { key }, cancellationToken: ct));
        return count > 0;
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM protected_secrets WHERE key = @key",
            new { key }, cancellationToken: ct));
    }
}
