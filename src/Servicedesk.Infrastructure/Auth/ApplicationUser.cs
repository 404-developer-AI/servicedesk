namespace Servicedesk.Infrastructure.Auth;

/// POCO projection of a <c>users</c> row. Not an EF entity — Dapper maps it
/// directly. Field-level encryption applies to TOTP data on a sibling table,
/// never to this record. The UTC fields are <c>DateTime</c>, not
/// <c>DateTimeOffset</c>, so Dapper's positional-record materialiser can
/// bind Npgsql's <c>timestamptz</c> value — same fix as
/// <c>AuditLogEntry.Utc</c> and <c>SettingEntry.UpdatedUtc</c>.
///
/// <para>
/// <see cref="PasswordHash"/> is nullable because a v0.0.13+ Microsoft user
/// never has a local password. The <c>chk_users_auth_mode</c> DB-side CHECK
/// enforces that Local rows always have a password and Microsoft rows
/// always have an <see cref="ExternalSubject"/>; consumers should branch on
/// <see cref="AuthMode"/> instead of probing for null.
/// </para>
public sealed record ApplicationUser(
    Guid Id,
    string Email,
    string? PasswordHash,
    string RoleName,
    DateTime CreatedUtc,
    DateTime? LastLoginUtc,
    int FailedAttempts,
    DateTime? LockoutUntilUtc,
    string AuthMode,
    string? ExternalProvider,
    string? ExternalSubject,
    bool IsActive);

/// Fixed set of authentication modes. A user's row is in exactly one mode.
public static class AuthModes
{
    public const string Local = "Local";
    public const string Microsoft = "Microsoft";
}

/// Fixed set of external-identity providers. Currently only Microsoft / Azure
/// AD; placeholder so the column value isn't a free-form string.
public static class ExternalProviders
{
    public const string Microsoft = "microsoft";
}
