using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Servicedesk.Infrastructure.Auth.Sessions;

/// One open session of a user, as listed on the "My sessions" surface.
/// <see cref="SessionId"/> is the cookie token: server-side only (used to
/// mark the caller's own row), never serialised to a client.
public sealed record ActiveSessionRow(
    Guid SessionId,
    Guid PublicId,
    DateTime CreatedUtc,
    DateTime LastSeenUtc,
    DateTime ExpiresUtc,
    string? Ip,
    string? UserAgent,
    string Amr);

public sealed record SessionValidation(Guid SessionId, ApplicationUser User, string Amr, DateTime ExpiresUtc, Guid? ImpersonatorUserId = null);

/// Cache-key format for validated sessions. Lives here (not in the API's
/// authentication handler) so <see cref="SessionService"/> can evict the
/// exact entries the handler caches when a session is revoked — revocation
/// must bite immediately, not after the handler's cache window expires.
public static class SessionCache
{
    public static string Key(Guid sessionId) => $"session:{sessionId}";
}

public interface ISessionService
{
    /// <paramref name="impersonatorUserId"/> (v0.1.1) marks a shadow
    /// session: an admin viewing the customer portal as this user. Stored
    /// on the session row for the audit trail and surfaced as a claim.
    Task<Guid> CreateAsync(Guid userId, string? ip, string? userAgent, TimeSpan lifetime, string amr, Guid? impersonatorUserId = null, CancellationToken ct = default);
    Task<SessionValidation?> ValidateAsync(Guid sessionId, TimeSpan idleTimeout, CancellationToken ct = default);
    Task TouchAsync(Guid sessionId, CancellationToken ct = default);
    Task RevokeAsync(Guid sessionId, CancellationToken ct = default);

    /// Revokes every open session belonging to <paramref name="userId"/>.
    /// Called when an admin deactivates or deletes a user so existing
    /// browser sessions die the next time they hit the server, rather
    /// than waiting for <c>SessionLifetimeHours</c> to expire.
    Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);

    /// Same as <see cref="RevokeAllForUserAsync"/> but keeps one session
    /// alive — the "log everything else out" semantics of a self-service
    /// password change (v0.1.3).
    Task RevokeAllForUserExceptAsync(Guid userId, Guid keepSessionId, CancellationToken ct = default);

    Task UpgradeAmrAsync(Guid sessionId, string amr, CancellationToken ct = default);

    /// Open sessions of <paramref name="userId"/>: not revoked, not past the
    /// absolute expiry and not idled out. Newest activity first. Shadow
    /// (impersonated) sessions belong to the customer row, never listed here.
    Task<IReadOnlyList<ActiveSessionRow>> ListActiveForUserAsync(Guid userId, TimeSpan idleTimeout, CancellationToken ct = default);

    /// Revokes the session with this public handle, but only when it belongs
    /// to <paramref name="userId"/> (ownership is part of the WHERE clause).
    /// Returns the revoked session's id, or null when nothing matched.
    Task<Guid?> RevokeByPublicIdForUserAsync(Guid userId, Guid publicId, CancellationToken ct = default);
}

public sealed class SessionService : ISessionService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IMemoryCache _cache;

    public SessionService(NpgsqlDataSource dataSource, IMemoryCache cache)
    {
        _dataSource = dataSource;
        _cache = cache;
    }

    public async Task<Guid> CreateAsync(
        Guid userId, string? ip, string? userAgent, TimeSpan lifetime, string amr, Guid? impersonatorUserId = null, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO user_sessions (user_id, expires_utc, ip, user_agent, amr, impersonator_user_id)
            VALUES (@userId, @expires, @ip, @userAgent, @amr, @impersonatorUserId)
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
                impersonatorUserId,
            },
            cancellationToken: ct));
    }

    public async Task<SessionValidation?> ValidateAsync(Guid sessionId, TimeSpan idleTimeout, CancellationToken ct = default)
    {
        const string sql = """
            SELECT s.id AS SessionId, s.amr AS Amr, s.impersonator_user_id AS ImpersonatorUserId,
                   s.expires_utc AS ExpiresUtc,
                   s.last_seen_utc AS LastSeenUtc, s.revoked_utc AS RevokedUtc,
                   u.id AS UserId, u.email AS Email, u.password_hash AS PasswordHash,
                   u.role_name AS RoleName, u.created_utc AS CreatedUtc,
                   u.last_login_utc AS LastLoginUtc, u.failed_attempts AS FailedAttempts,
                   u.lockout_until_utc AS LockoutUntilUtc,
                   u.auth_mode AS AuthMode, u.external_provider AS ExternalProvider,
                   u.external_subject AS ExternalSubject, u.is_active AS IsActive
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

        // An inactive user's session is treated as if it never existed — the
        // deprovision path (M365 accountEnabled=false, admin-initiated
        // deactivate) sets is_active=false and the next request hits this
        // branch and is logged out. Existing sessions don't need to be
        // revoked explicitly; this short-circuit covers them.
        if (!row.IsActive)
        {
            await RevokeAsync(sessionId, ct);
            return null;
        }

        var user = new ApplicationUser(
            row.UserId, row.Email, row.PasswordHash, row.RoleName, row.CreatedUtc,
            row.LastLoginUtc, row.FailedAttempts, row.LockoutUntilUtc,
            row.AuthMode, row.ExternalProvider, row.ExternalSubject, row.IsActive);
        return new SessionValidation(row.SessionId, user, row.Amr, row.ExpiresUtc, row.ImpersonatorUserId);
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
        // Evict here — not at the call sites — so no caller can forget it and
        // leave the revoked cookie usable for the rest of the cache window.
        _cache.Remove(SessionCache.Key(sessionId));
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var revoked = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            UPDATE user_sessions SET revoked_utc = now()
            WHERE user_id = @userId AND revoked_utc IS NULL
            RETURNING id
            """,
            new { userId },
            cancellationToken: ct));
        foreach (var sessionId in revoked)
        {
            _cache.Remove(SessionCache.Key(sessionId));
        }
    }

    public async Task RevokeAllForUserExceptAsync(Guid userId, Guid keepSessionId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var revoked = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            UPDATE user_sessions SET revoked_utc = now()
            WHERE user_id = @userId AND id <> @keepSessionId AND revoked_utc IS NULL
            RETURNING id
            """,
            new { userId, keepSessionId },
            cancellationToken: ct));
        foreach (var sessionId in revoked)
        {
            _cache.Remove(SessionCache.Key(sessionId));
        }
    }

    public async Task UpgradeAmrAsync(Guid sessionId, string amr, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE user_sessions SET amr = @amr WHERE id = @id",
            new { id = sessionId, amr },
            cancellationToken: ct));
        _cache.Remove(SessionCache.Key(sessionId));
    }

    public async Task<IReadOnlyList<ActiveSessionRow>> ListActiveForUserAsync(
        Guid userId, TimeSpan idleTimeout, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id AS SessionId, public_id AS PublicId, created_utc AS CreatedUtc,
                   last_seen_utc AS LastSeenUtc, expires_utc AS ExpiresUtc,
                   ip AS Ip, user_agent AS UserAgent, amr AS Amr
            FROM user_sessions
            WHERE user_id = @userId
              AND revoked_utc IS NULL
              AND impersonator_user_id IS NULL
              AND expires_utc > now()
              AND (@idleSeconds <= 0 OR last_seen_utc > now() - make_interval(secs => @idleSeconds))
            ORDER BY last_seen_utc DESC
            LIMIT 100
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<ActiveSessionRow>(new CommandDefinition(
            sql,
            new { userId, idleSeconds = idleTimeout.TotalSeconds },
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<Guid?> RevokeByPublicIdForUserAsync(Guid userId, Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var revoked = await connection.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            UPDATE user_sessions SET revoked_utc = now()
            WHERE public_id = @publicId AND user_id = @userId AND revoked_utc IS NULL
            RETURNING id
            """,
            new { publicId, userId },
            cancellationToken: ct));
        if (revoked is Guid sessionId)
        {
            _cache.Remove(SessionCache.Key(sessionId));
        }
        return revoked;
    }

    private sealed record SessionRow(
        Guid SessionId,
        string Amr,
        Guid? ImpersonatorUserId,
        DateTime ExpiresUtc,
        DateTime LastSeenUtc,
        DateTime? RevokedUtc,
        Guid UserId,
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
}
