using Dapper;
using Microsoft.AspNetCore.DataProtection;
using OtpNet;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Auth.Totp;
using Xunit;

namespace Servicedesk.Api.Tests.Database;

/// The real <see cref="TotpService"/> on real PostgreSQL. Every other auth
/// test fakes it, so these pin the security-critical parts of the SQL: the
/// replay guard (a timestep is accepted once, also right after enrolment),
/// the conditional writes that make two concurrent uses of one code yield
/// one success, single-use hashed recovery codes and encryption at rest.
[Collection(PostgresCollection.Name)]
public sealed class TotpServiceDbTests
{
    private const int Step = 30; // SettingDefaults: Security.TwoFactorTotpStepSeconds

    private readonly PostgresDatabase _db;

    public TotpServiceDbTests(PostgresDatabase db) => _db = db;

    [PostgresFact]
    public async Task Confirming_with_a_valid_code_enables_two_factor_and_returns_the_recovery_codes()
    {
        var (svc, userId) = await UserAsync();
        var secret = await BeginAsync(svc, userId);

        var codes = await svc.ConfirmEnrollAsync(userId, CodeAt(secret, 0));

        Assert.NotNull(codes);
        Assert.Equal(10, codes!.Count); // SettingDefaults: Security.TwoFactorRecoveryCodeCount
        Assert.Equal(10, codes.Distinct().Count());
        Assert.True(await svc.IsEnabledAsync(userId));
    }

    [PostgresFact]
    public async Task A_wrong_code_neither_confirms_nor_signs_in()
    {
        var (svc, userId) = await UserAsync();
        var secret = await BeginAsync(svc, userId);

        Assert.Null(await svc.ConfirmEnrollAsync(userId, WrongCode(secret)));
        Assert.False(await svc.IsEnabledAsync(userId));

        await svc.ConfirmEnrollAsync(userId, CodeAt(secret, 0));
        Assert.Equal(TwoFactorResult.Rejected, await svc.VerifyAsync(userId, WrongCode(secret)));
    }

    [PostgresFact]
    public async Task A_started_but_unconfirmed_enrolment_never_accepts_a_login_code()
    {
        var (svc, userId) = await UserAsync();
        var secret = await BeginAsync(svc, userId);

        Assert.Equal(TwoFactorResult.Rejected, await svc.VerifyAsync(userId, CodeAt(secret, 0)));
    }

    [PostgresFact]
    public async Task The_enrolment_code_cannot_be_replayed_on_the_login_challenge()
    {
        var (svc, userId) = await UserAsync();
        var secret = await BeginAsync(svc, userId);
        var code = CodeAt(secret, 0);
        await svc.ConfirmEnrollAsync(userId, code);

        Assert.Equal(TwoFactorResult.Rejected, await svc.VerifyAsync(userId, code));
    }

    [PostgresFact]
    public async Task A_login_code_is_accepted_once_and_older_steps_are_dead_after_it()
    {
        var (svc, userId) = await UserAsync();
        var secret = await BeginAsync(svc, userId);
        await svc.ConfirmEnrollAsync(userId, CodeAt(secret, -1));
        var next = CodeAt(secret, 1); // inside the ±1 step window

        Assert.Equal(TwoFactorResult.TotpAccepted, await svc.VerifyAsync(userId, next));
        Assert.Equal(TwoFactorResult.Rejected, await svc.VerifyAsync(userId, next));
        Assert.Equal(TwoFactorResult.Rejected, await svc.VerifyAsync(userId, CodeAt(secret, 0)));
    }

    [PostgresFact]
    public async Task Two_concurrent_uses_of_one_code_let_exactly_one_through()
    {
        var (svc, userId) = await UserAsync();
        var secret = await BeginAsync(svc, userId);
        await svc.ConfirmEnrollAsync(userId, CodeAt(secret, -1));
        var code = CodeAt(secret, 0);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => svc.VerifyAsync(userId, code))));

        Assert.Equal(1, results.Count(r => r == TwoFactorResult.TotpAccepted));
    }

    [PostgresFact]
    public async Task A_recovery_code_works_once()
    {
        var (svc, userId) = await UserAsync();
        var secret = await BeginAsync(svc, userId);
        var codes = (await svc.ConfirmEnrollAsync(userId, CodeAt(secret, 0)))!;

        Assert.Equal(TwoFactorResult.RecoveryAccepted, await svc.VerifyAsync(userId, codes[3]));
        Assert.Equal(TwoFactorResult.Rejected, await svc.VerifyAsync(userId, codes[3]));
        Assert.Equal(TwoFactorResult.RecoveryAccepted, await svc.VerifyAsync(userId, " " + codes[4] + " "));
    }

    [PostgresFact]
    public async Task Two_concurrent_uses_of_one_recovery_code_let_exactly_one_through()
    {
        var (svc, userId) = await UserAsync();
        var secret = await BeginAsync(svc, userId);
        var code = (await svc.ConfirmEnrollAsync(userId, CodeAt(secret, 0)))![0];

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => svc.VerifyAsync(userId, code))));

        Assert.Equal(1, results.Count(r => r == TwoFactorResult.RecoveryAccepted));
    }

    [PostgresFact]
    public async Task Secrets_are_encrypted_and_recovery_codes_stored_only_as_hashes()
    {
        var (svc, userId) = await UserAsync();
        var secret = await BeginAsync(svc, userId);
        var codes = (await svc.ConfirmEnrollAsync(userId, CodeAt(secret, 0)))!;

        await using var c = await _db.DataSource.OpenConnectionAsync();
        var stored = await c.ExecuteScalarAsync<byte[]>(
            "SELECT secret_ciphertext FROM user_totp WHERE user_id = @userId", new { userId });
        Assert.NotEqual(secret, stored);
        Assert.False(ContainsSubsequence(stored!, secret));

        var hashes = (await c.QueryAsync<byte[]>(
            "SELECT code_sha256 FROM user_recovery_codes WHERE user_id = @userId", new { userId })).ToList();
        Assert.Equal(codes.Count, hashes.Count);
        Assert.Equal(
            codes.Select(TotpService.HashRecoveryCode).Select(Convert.ToHexString).OrderBy(h => h),
            hashes.Select(Convert.ToHexString).OrderBy(h => h));
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM user_recovery_codes WHERE user_id = @userId AND code_ciphertext IS NOT NULL",
            new { userId }));
    }

    [PostgresFact]
    public async Task Re_enrolling_replaces_the_recovery_codes_and_disabling_removes_everything()
    {
        var (svc, userId) = await UserAsync();
        var first = (await svc.ConfirmEnrollAsync(userId, CodeAt(await BeginAsync(svc, userId), 0)))!;
        var secret = await BeginAsync(svc, userId);
        Assert.False(await svc.IsEnabledAsync(userId));
        await svc.ConfirmEnrollAsync(userId, CodeAt(secret, -1));

        Assert.Equal(TwoFactorResult.Rejected, await svc.VerifyAsync(userId, first[0]));

        await svc.DisableAsync(userId);
        Assert.False(await svc.IsEnabledAsync(userId));
        await using var c = await _db.DataSource.OpenConnectionAsync();
        Assert.Equal(0, await c.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM user_recovery_codes WHERE user_id = @userId", new { userId }));
    }

    // ---- plumbing -----------------------------------------------------------

    private async Task<(TotpService Svc, Guid UserId)> UserAsync()
    {
        var svc = new TotpService(_db.DataSource, new EphemeralDataProtectionProvider(), new InMemorySettingsService());
        return (svc, await new DbSeed(_db.DataSource).UserAsync());
    }

    private static async Task<byte[]> BeginAsync(TotpService svc, Guid userId) =>
        Base32Encoding.ToBytes((await svc.BeginEnrollAsync(userId, "agent@example.test")).SecretBase32);

    /// The code for the timestep <paramref name="stepOffset"/> steps from now.
    private static string CodeAt(byte[] secret, int stepOffset) =>
        new Totp(secret, step: Step).ComputeTotp(DateTime.UtcNow.AddSeconds(stepOffset * Step));

    /// A six-digit code that matches no step in the window.
    private static string WrongCode(byte[] secret)
    {
        var valid = Enumerable.Range(-2, 5).Select(o => CodeAt(secret, o)).ToHashSet();
        return Enumerable.Range(0, 1_000_000).Select(n => n.ToString("D6")).First(c => !valid.Contains(c));
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }
}
