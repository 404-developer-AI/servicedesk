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
        var secret = await LoadSecretAsync(userId, ct);
        if (secret is null)
        {
            return null;
        }

        if (!VerifyTotpCode(secret, code))
        {
            return null;
        }

        var recoveryCount = await _settings.GetAsync<int>(SettingKeys.Security.TwoFactorRecoveryCodeCount, ct);
        var plaintextCodes = new List<string>(recoveryCount);
        var encryptedCodes = new List<byte[]>(recoveryCount);
        for (var i = 0; i < recoveryCount; i++)
        {
            var plaintext = GenerateRecoveryCode();
            plaintextCodes.Add(plaintext);
            encryptedCodes.Add(_protector.Protect(Encoding.UTF8.GetBytes(plaintext)));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE user_totp SET enabled = TRUE WHERE user_id = @id",
            new { id = userId },
            tx,
            cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_recovery_codes WHERE user_id = @id",
            new { id = userId },
            tx,
            cancellationToken: ct));

        foreach (var payload in encryptedCodes)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO user_recovery_codes (user_id, code_ciphertext) VALUES (@id, @ct)",
                new { id = userId, ct = payload },
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

        var secret = await LoadSecretAsync(userId, ct, requireEnabled: true);
        if (secret is not null && VerifyTotpCode(secret, code))
        {
            return TwoFactorResult.TotpAccepted;
        }

        // Fall through to recovery-code path.
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<(Guid Id, byte[] Ct)>(new CommandDefinition(
            """
            SELECT id AS Id, code_ciphertext AS Ct
            FROM user_recovery_codes
            WHERE user_id = @id AND used_utc IS NULL
            """,
            new { id = userId },
            cancellationToken: ct));

        var codeBytes = Encoding.UTF8.GetBytes(code.Trim());
        foreach (var row in rows)
        {
            byte[] stored;
            try
            {
                stored = _protector.Unprotect(row.Ct);
            }
            catch (CryptographicException)
            {
                continue;
            }
            if (CryptographicOperations.FixedTimeEquals(stored, codeBytes))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE user_recovery_codes SET used_utc = now() WHERE id = @id",
                    new { id = row.Id },
                    cancellationToken: ct));
                return TwoFactorResult.RecoveryAccepted;
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

    private async Task<byte[]?> LoadSecretAsync(Guid userId, CancellationToken ct, bool requireEnabled = false)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<(byte[] Ct, bool Enabled)>(new CommandDefinition(
            "SELECT secret_ciphertext AS Ct, enabled AS Enabled FROM user_totp WHERE user_id = @id",
            new { id = userId },
            cancellationToken: ct));
        if (row == default)
        {
            return null;
        }
        if (requireEnabled && !row.Enabled)
        {
            return null;
        }
        try
        {
            return _protector.Unprotect(row.Ct);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private bool VerifyTotpCode(byte[] secret, string code)
    {
        var step = _settings.GetAsync<int>(SettingKeys.Security.TwoFactorTotpStepSeconds).GetAwaiter().GetResult();
        var window = _settings.GetAsync<int>(SettingKeys.Security.TwoFactorTotpWindow).GetAwaiter().GetResult();
        var totp = new OtpNet.Totp(secret, step: step);
        return totp.VerifyTotp(code.Trim(), out _, new VerificationWindow(previous: window, future: window));
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
