namespace Servicedesk.Infrastructure.Auth;

/// POCO projection of a <c>users</c> row. Not an EF entity — Dapper maps it
/// directly. Field-level encryption applies to TOTP data on a sibling table,
/// never to this record. The UTC fields are <c>DateTime</c>, not
/// <c>DateTimeOffset</c>, so Dapper's positional-record materialiser can
/// bind Npgsql's <c>timestamptz</c> value — same fix as
/// <c>AuditLogEntry.Utc</c> and <c>SettingEntry.UpdatedUtc</c>.
public sealed record ApplicationUser(
    Guid Id,
    string Email,
    string PasswordHash,
    string RoleName,
    DateTime CreatedUtc,
    DateTime? LastLoginUtc,
    int FailedAttempts,
    DateTime? LockoutUntilUtc);
