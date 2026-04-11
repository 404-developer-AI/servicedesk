using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Auth.Sessions;

public sealed record SessionValidation(Guid SessionId, ApplicationUser User, string Amr, DateTime ExpiresUtc);

public interface ISessionService
{
    Task<Guid> CreateAsync(Guid userId, string? ip, string? userAgent, TimeSpan lifetime, string amr, CancellationToken ct = default);
    Task<SessionValidation?> ValidateAsync(Guid sessionId, TimeSpan idleTimeout, CancellationToken ct = default);
    Task TouchAsync(Guid sessionId, CancellationToken ct = default);
    Task RevokeAsync(Guid sessionId, CancellationToken ct = default);
    Task UpgradeAmrAsync(Guid sessionId, string amr, CancellationToken ct = default);
}

public sealed class SessionService : ISessionService
{
    private readonly NpgsqlDataSource _dataSource;

    public SessionService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<Guid> CreateAsync(
        Guid userId, string? ip, string? userAgent, TimeSpan lifetime, string amr, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO user_sessions (user_id, expires_utc, ip, user_agent, amr)
            VALUES (@userId, @expires, @ip, @userAgent, @amr)
            RETURNING id
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            sql,
            new
            {
                userId,
                expires = DateTime.UtcNow.Add(lifetime),
                ip,
                userAgent,
                amr,
            },
            cancellationToken: ct));
    }

    public async Task<SessionValidation?> ValidateAsync(Guid sessionId, TimeSpan idleTimeout, CancellationToken ct = default)
    {
        const string sql = """
            SELECT s.id AS SessionId, s.amr AS Amr, s.expires_utc AS ExpiresUtc,
                   s.last_seen_utc AS LastSeenUtc, s.revoked_utc AS RevokedUtc,
                   u.id AS UserId, u.email AS Email, u.password_hash AS PasswordHash,
                   u.role_name AS RoleName, u.created_utc AS CreatedUtc,
                   u.last_login_utc AS LastLoginUtc, u.failed_attempts AS FailedAttempts,
                   u.lockout_until_utc AS LockoutUntilUtc
            FROM user_sessions s
            INNER JOIN users u ON u.id = s.user_id
            WHERE s.id = @sessionId
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<SessionRow>(
            new CommandDefinition(sql, new { sessionId }, cancellationToken: ct));
        if (row is null)
        {
            return null;
        }
        var now = DateTime.UtcNow;
        if (row.RevokedUtc.HasValue || row.ExpiresUtc <= now)
        {
            return null;
        }
        if (idleTimeout > TimeSpan.Zero && now - row.LastSeenUtc > idleTimeout)
        {
            await RevokeAsync(sessionId, ct);
            return null;
        }

        var user = new ApplicationUser(
            row.UserId, row.Email, row.PasswordHash, row.RoleName, row.CreatedUtc,
            row.LastLoginUtc, row.FailedAttempts, row.LockoutUntilUtc);
        return new SessionValidation(row.SessionId, user, row.Amr, row.ExpiresUtc);
    }

    public async Task TouchAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE user_sessions SET last_seen_utc = now() WHERE id = @id AND revoked_utc IS NULL",
            new { id = sessionId },
            cancellationToken: ct));
    }

    public async Task RevokeAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE user_sessions SET revoked_utc = now() WHERE id = @id AND revoked_utc IS NULL",
            new { id = sessionId },
            cancellationToken: ct));
    }

    public async Task UpgradeAmrAsync(Guid sessionId, string amr, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE user_sessions SET amr = @amr WHERE id = @id",
            new { id = sessionId, amr },
            cancellationToken: ct));
    }

    private sealed record SessionRow(
        Guid SessionId,
        string Amr,
        DateTime ExpiresUtc,
        DateTime LastSeenUtc,
        DateTime? RevokedUtc,
        Guid UserId,
        string Email,
        string PasswordHash,
        string RoleName,
        DateTime CreatedUtc,
        DateTime? LastLoginUtc,
        int FailedAttempts,
        DateTime? LockoutUntilUtc);
}
