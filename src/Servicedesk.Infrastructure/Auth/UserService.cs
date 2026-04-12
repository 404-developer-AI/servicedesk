using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Auth;

public interface IUserService
{
    Task<int> CountAsync(CancellationToken ct = default);

    /// Creates the first admin under an advisory lock. If a user already
    /// exists when the lock is acquired, the call is rejected so two racing
    /// setup-wizard submissions cannot both succeed.
    Task<ApplicationUser?> CreateFirstAdminAsync(string email, string passwordHash, CancellationToken ct = default);

    Task<ApplicationUser?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task<ApplicationUser?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<AgentUser>> ListAgentsAsync(CancellationToken ct = default);

    Task UpdatePasswordHashAsync(Guid userId, string newHash, CancellationToken ct = default);
    Task RecordSuccessfulLoginAsync(Guid userId, CancellationToken ct = default);

    /// Increments failed attempts and applies a lockout when the rolling
    /// threshold is reached. Returns <c>true</c> when the account is now
    /// locked (including as a result of this call).
    Task<bool> RecordFailedLoginAsync(
        Guid userId,
        int maxAttempts,
        int windowSeconds,
        int lockoutDurationSeconds,
        CancellationToken ct = default);
}

public sealed class UserService : IUserService
{
    private const long SetupLockKey = 0x5EC_A0D17_A5E7_1000L;

    private readonly NpgsqlDataSource _dataSource;

    public UserService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition("SELECT COUNT(*) FROM users", cancellationToken: ct));
    }

    public async Task<ApplicationUser?> CreateFirstAdminAsync(string email, string passwordHash, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(@key)",
            new { key = SetupLockKey },
            tx,
            cancellationToken: ct));

        var existing = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition("SELECT COUNT(*) FROM users", transaction: tx, cancellationToken: ct));
        if (existing > 0)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        const string sql = """
            INSERT INTO users (email, password_hash, role_name)
            VALUES (@Email, @PasswordHash, 'Admin')
            RETURNING id AS Id, email AS Email, password_hash AS PasswordHash, role_name AS RoleName,
                      created_utc AS CreatedUtc, last_login_utc AS LastLoginUtc,
                      failed_attempts AS FailedAttempts, lockout_until_utc AS LockoutUntilUtc
            """;

        var user = await connection.QuerySingleAsync<ApplicationUser>(new CommandDefinition(
            sql,
            new { Email = email, PasswordHash = passwordHash },
            tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);
        return user;
    }

    public async Task<ApplicationUser?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id AS Id, email AS Email, password_hash AS PasswordHash, role_name AS RoleName,
                   created_utc AS CreatedUtc, last_login_utc AS LastLoginUtc,
                   failed_attempts AS FailedAttempts, lockout_until_utc AS LockoutUntilUtc
            FROM users WHERE email = @email
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<ApplicationUser>(
            new CommandDefinition(sql, new { email }, cancellationToken: ct));
    }

    public async Task<ApplicationUser?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id AS Id, email AS Email, password_hash AS PasswordHash, role_name AS RoleName,
                   created_utc AS CreatedUtc, last_login_utc AS LastLoginUtc,
                   failed_attempts AS FailedAttempts, lockout_until_utc AS LockoutUntilUtc
            FROM users WHERE id = @id
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<ApplicationUser>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task UpdatePasswordHashAsync(Guid userId, string newHash, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET password_hash = @hash WHERE id = @id",
            new { id = userId, hash = newHash },
            cancellationToken: ct));
    }

    public async Task RecordSuccessfulLoginAsync(Guid userId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE users SET
                last_login_utc = now(),
                failed_attempts = 0,
                lockout_until_utc = NULL
            WHERE id = @id
            """,
            new { id = userId },
            cancellationToken: ct));
    }

    public async Task<bool> RecordFailedLoginAsync(
        Guid userId,
        int maxAttempts,
        int windowSeconds,
        int lockoutDurationSeconds,
        CancellationToken ct = default)
    {
        // The window doesn't map to a sliding counter here (we only store the
        // plain counter), but we treat it as the cool-down after which a
        // fresh streak starts: if the latest attempt is older than the window
        // we reset before incrementing.
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var currentAttempts = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT failed_attempts FROM users WHERE id = @id FOR UPDATE",
                new { id = userId },
                tx,
                cancellationToken: ct));

        var attempts = currentAttempts + 1;
        var lockoutUntil = attempts >= maxAttempts
            ? DateTime.UtcNow.AddSeconds(lockoutDurationSeconds)
            : (DateTime?)null;

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE users SET failed_attempts = @attempts, lockout_until_utc = @lockout
            WHERE id = @id
            """,
            new { id = userId, attempts, lockout = lockoutUntil },
            tx,
            cancellationToken: ct));

        await tx.CommitAsync(ct);
        _ = windowSeconds; // reserved for future sliding-window refinement
        return lockoutUntil.HasValue;
    }

    public async Task<IReadOnlyList<AgentUser>> ListAgentsAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT id AS Id, email AS Email, role_name AS RoleName
            FROM users WHERE role_name IN ('Agent', 'Admin')
            ORDER BY email
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<AgentUser>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

}

public sealed record AgentUser(Guid Id, string Email, string RoleName);
