using System.Security.Cryptography;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Servicedesk.Infrastructure.Auth.Totp;

/// One-shot startup migration (v0.1.2): recovery codes used to be stored as
/// reversible DataProtection ciphertext; they are now SHA-256 only, like every
/// other one-time secret in the schema. This decrypts each legacy row, writes
/// the hash, and drops the ciphertext. Runs after <c>DatabaseBootstrapper</c>
/// (hosted-service registration order) so the <c>code_sha256</c> column
/// exists. Idempotent: once no legacy rows remain it is a single cheap SELECT.
/// A row whose ciphertext no longer unprotects (rotated/lost master key) is
/// cleared rather than left reversible — that code was unusable anyway.
public sealed class RecoveryCodeHashMigrator : IHostedService
{
    private const string ProtectorPurpose = "Servicedesk.Auth.Totp.v1";

    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataProtector _protector;
    private readonly ILogger<RecoveryCodeHashMigrator> _logger;

    public RecoveryCodeHashMigrator(
        NpgsqlDataSource dataSource,
        IDataProtectionProvider protectionProvider,
        ILogger<RecoveryCodeHashMigrator> logger)
    {
        _dataSource = dataSource;
        _protector = protectionProvider.CreateProtector(ProtectorPurpose);
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            var rows = (await connection.QueryAsync<(Guid Id, byte[] Ct)>(new CommandDefinition(
                """
                SELECT id AS Id, code_ciphertext AS Ct
                FROM user_recovery_codes
                WHERE code_sha256 IS NULL AND code_ciphertext IS NOT NULL
                """,
                cancellationToken: cancellationToken))).ToList();
            if (rows.Count == 0)
            {
                return;
            }

            var migrated = 0;
            var unreadable = 0;
            foreach (var row in rows)
            {
                byte[]? plaintext;
                try
                {
                    plaintext = _protector.Unprotect(row.Ct);
                }
                catch (CryptographicException)
                {
                    plaintext = null;
                }

                if (plaintext is null)
                {
                    unreadable++;
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE user_recovery_codes SET code_ciphertext = NULL, used_utc = COALESCE(used_utc, now()) WHERE id = @id",
                        new { id = row.Id },
                        cancellationToken: cancellationToken));
                    continue;
                }

                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE user_recovery_codes SET code_sha256 = @hash, code_ciphertext = NULL WHERE id = @id",
                    new { id = row.Id, hash = SHA256.HashData(plaintext) },
                    cancellationToken: cancellationToken));
                CryptographicOperations.ZeroMemory(plaintext);
                migrated++;
            }

            _logger.LogInformation(
                "Recovery-code hash migration complete: {Migrated} row(s) re-hashed, {Unreadable} unreadable row(s) retired.",
                migrated, unreadable);
        }
        catch (Exception ex)
        {
            // Never block boot on this: VerifyAsync still understands legacy
            // ciphertext rows, so the migration simply retries next start.
            _logger.LogWarning(ex, "Recovery-code hash migration failed; will retry on next startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
