using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using OtpNet;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Auth.Totp;

public enum TwoFactorResult
{
    None,
    TotpAccepted,
    RecoveryAccepted,
    Rejected,
}

public sealed record TotpEnrollment(string SecretBase32, string OtpAuthUri);

public interface ITotpService
{
    Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct = default);

    /// Generates (and upserts) a non-enabled secret for this user, returning
    /// the raw base32 secret + otpauth:// URI so the frontend can render a QR.
    /// The secret is re-encrypted on every call so a partial enrollment leaves
    /// the DB in a consistent state.
    Task<TotpEnrollment> BeginEnrollAsync(Guid userId, string accountLabel, CancellationToken ct = default);

    /// Verifies <paramref name="code"/> against the pending secret. On success
    /// the secret is marked enabled, fresh recovery codes are generated and
    /// stored, and the plaintext codes are returned to the caller (to show
    /// the user exactly once).
    Task<IReadOnlyList<string>?> ConfirmEnrollAsync(Guid userId, string code, CancellationToken ct = default);

    /// Verifies a challenge code at login time. Accepts either a live TOTP
    /// code or a single-use recovery code. Recovery codes are burned on use.
    /// A TOTP code whose timestep was already accepted once is rejected
    /// (RFC 6238 §5.2 — no replay inside the verification window).
    Task<TwoFactorResult> VerifyAsync(Guid userId, string code, CancellationToken ct = default);

    Task DisableAsync(Guid userId, CancellationToken ct = default);
}

public sealed class TotpService : ITotpService
{
    private const string Issuer = "Servicedesk";
    private const string ProtectorPurpose = "Servicedesk.Auth.Totp.v1";
    private const int SecretBytes = 20; // RFC 6238 recommended minimum
    private const int RecoveryCodeBytes = 10;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataProtector _protector;
    private readonly ISettingsService _settings;

    public TotpService(NpgsqlDataSource dataSource, IDataProtectionProvider protectionProvider, ISettingsService settings)
    {
        _dataSource = dataSource;
        _protector = protectionProvider.CreateProtector(ProtectorPurpose);
        _settings = settings;
    }

    public async Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var enabled = await connection.ExecuteScalarAsync<bool?>(
            new CommandDefinition(
                "SELECT enabled FROM user_totp WHERE user_id = @id",
                new { id = userId },
                cancellationToken: ct));
        return enabled == true;
    }

    public async Task<TotpEnrollment> BeginEnrollAsync(Guid userId, string accountLabel, CancellationToken ct = default)
    {
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        var ciphertext = _protector.Protect(secret);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO user_totp (user_id, secret_ciphertext, enabled)
            VALUES (@id, @ct, FALSE)
            ON CONFLICT (user_id) DO UPDATE
                SET secret_ciphertext = EXCLUDED.secret_ciphertext,
                    enabled = FALSE,
                    last_used_step = NULL,
                    created_utc = now()
            """,
            new { id = userId, ct = ciphertext },
            cancellationToken: ct));

        var base32 = Base32Encoding.ToString(secret);
        var uri = BuildOtpAuthUri(base32, accountLabel);
        return new TotpEnrollment(base32, uri);
    }

    public async Task<IReadOnlyList<string>?> ConfirmEnrollAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var row = await LoadSecretAsync(userId, ct);
        if (row is null)
        {
            return null;
        }

        if (!VerifyTotpCode(row.Secret, code, out var matchedStep))
        {
            return null;
        }

        var recoveryCount = await _settings.GetAsync<int>(SettingKeys.Security.TwoFactorRecoveryCodeCount, ct);
        var plaintextCodes = new List<string>(recoveryCount);
        var hashedCodes = new List<byte[]>(recoveryCount);
        for (var i = 0; i < recoveryCount; i++)
        {
            var plaintext = GenerateRecoveryCode();
            plaintextCodes.Add(plaintext);
            hashedCodes.Add(HashRecoveryCode(plaintext));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        // Stamp the confirmation code's timestep so the very same code cannot
        // be replayed on the login challenge right after enrolling.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE user_totp SET enabled = TRUE, last_used_step = @step WHERE user_id = @id",
            new { id = userId, step = matchedStep },
            tx,
            cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_recovery_codes WHERE user_id = @id",
            new { id = userId },
            tx,
            cancellationToken: ct));

        foreach (var hash in hashedCodes)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO user_recovery_codes (user_id, code_sha256) VALUES (@id, @hash)",
                new { id = userId, hash },
                tx,
                cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return plaintextCodes;
    }

    public async Task<TwoFactorResult> VerifyAsync(Guid userId, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return TwoFactorResult.Rejected;
        }

        var row = await LoadSecretAsync(userId, ct, requireEnabled: true);
        if (row is not null && VerifyTotpCode(row.Secret, code, out var matchedStep))
        {
            // Replay guard: a code whose timestep was already accepted once —
            // observed over a shoulder, phished, or captured from a logged
            // request — is dead for the rest of its verification window.
            if (row.LastUsedStep is { } last && matchedStep <= last)
            {
                return TwoFactorResult.Rejected;
            }

            // Conditional write so two concurrent verifies of the same code
            // can't both pass: only the first one advances the step.
            await using var totpConnection = await _dataSource.OpenConnectionAsync(ct);
            var advanced = await totpConnection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE user_totp SET last_used_step = @step
                WHERE user_id = @id AND (last_used_step IS NULL OR last_used_step < @step)
                """,
                new { id = userId, step = matchedStep },
                cancellationToken: ct));
            return advanced == 1 ? TwoFactorResult.TotpAccepted : TwoFactorResult.Rejected;
        }

        // Fall through to recovery-code path.
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<RecoveryCodeRow>(new CommandDefinition(
            """
            SELECT id AS Id, code_sha256 AS Hash, code_ciphertext AS LegacyCiphertext
            FROM user_recovery_codes
            WHERE user_id = @id AND used_utc IS NULL
            """,
            new { id = userId },
            cancellationToken: ct));

        var codeBytes = Encoding.UTF8.GetBytes(code.Trim());
        var codeHash = SHA256.HashData(codeBytes);
        foreach (var recovery in rows)
        {
            bool matches;
            if (recovery.Hash is { Length: > 0 })
            {
                matches = CryptographicOperations.FixedTimeEquals(recovery.Hash, codeHash);
            }
            else if (recovery.LegacyCiphertext is { Length: > 0 })
            {
                // Pre-v0.1.3 row that the startup migrator has not reached
                // (e.g. the DataProtection key was briefly unavailable).
                try
                {
                    matches = CryptographicOperations.FixedTimeEquals(
                        _protector.Unprotect(recovery.LegacyCiphertext), codeBytes);
                }
                catch (CryptographicException)
                {
                    continue;
                }
            }
            else
            {
                continue;
            }

            if (matches)
            {
                // Conditional burn: a racing second use of the same code
                // loses because used_utc is no longer NULL.
                var burned = await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE user_recovery_codes SET used_utc = now() WHERE id = @id AND used_utc IS NULL",
                    new { id = recovery.Id },
                    cancellationToken: ct));
                return burned == 1 ? TwoFactorResult.RecoveryAccepted : TwoFactorResult.Rejected;
            }
        }

        return TwoFactorResult.Rejected;
    }

    public async Task DisableAsync(Guid userId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_totp WHERE user_id = @id",
            new { id = userId },
            tx,
            cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_recovery_codes WHERE user_id = @id",
            new { id = userId },
            tx,
            cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    internal static byte[] HashRecoveryCode(string plaintext)
        => SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));

    private sealed class SecretRow
    {
        public byte[] Secret { get; set; } = Array.Empty<byte>();
        public long? LastUsedStep { get; set; }
    }

    private sealed class RecoveryCodeRow
    {
        public Guid Id { get; set; }
        public byte[]? Hash { get; set; }
        public byte[]? LegacyCiphertext { get; set; }
    }

    private async Task<SecretRow?> LoadSecretAsync(Guid userId, CancellationToken ct, bool requireEnabled = false)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<StoredSecretRow>(new CommandDefinition(
            """
            SELECT secret_ciphertext AS Ct, enabled AS Enabled, last_used_step AS LastUsedStep
            FROM user_totp WHERE user_id = @id
            """,
            new { id = userId },
            cancellationToken: ct));
        if (row is null)
        {
            return null;
        }
        if (requireEnabled && !row.Enabled)
        {
            return null;
        }
        try
        {
            return new SecretRow { Secret = _protector.Unprotect(row.Ct), LastUsedStep = row.LastUsedStep };
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private sealed class StoredSecretRow
    {
        public byte[] Ct { get; set; } = Array.Empty<byte>();
        public bool Enabled { get; set; }
        public long? LastUsedStep { get; set; }
    }

    private bool VerifyTotpCode(byte[] secret, string code, out long timeStepMatched)
    {
        var step = _settings.GetAsync<int>(SettingKeys.Security.TwoFactorTotpStepSeconds).GetAwaiter().GetResult();
        var window = _settings.GetAsync<int>(SettingKeys.Security.TwoFactorTotpWindow).GetAwaiter().GetResult();
        var totp = new OtpNet.Totp(secret, step: step);
        return totp.VerifyTotp(code.Trim(), out timeStepMatched, new VerificationWindow(previous: window, future: window));
    }

    private static string BuildOtpAuthUri(string base32Secret, string accountLabel)
    {
        var label = Uri.EscapeDataString($"{Issuer}:{accountLabel}");
        var issuer = Uri.EscapeDataString(Issuer);
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
    }

    private static string GenerateRecoveryCode()
    {
        // 10 bytes → 16 base32 chars, formatted as xxxx-xxxx-xxxx-xxxx for readability.
        var bytes = RandomNumberGenerator.GetBytes(RecoveryCodeBytes);
        var encoded = Base32Encoding.ToString(bytes).TrimEnd('=').ToLowerInvariant();
        return string.Join('-',
            encoded[..4],
            encoded[4..8],
            encoded[8..12],
            encoded[12..16]);
    }
}
