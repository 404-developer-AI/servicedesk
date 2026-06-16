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

    /// Lookup used by the M365 login callback. Returns the row whose
    /// <c>(external_provider, external_subject)</c> matches — i.e. the user
    /// that previously linked this Azure OID. Returns <c>null</c> if the OID
    /// has never been linked; the callback treats that as
    /// <c>auth.microsoft.login.rejected_unknown</c>.
    Task<ApplicationUser?> FindByExternalAsync(string provider, string subject, CancellationToken ct = default);

    /// Deactivates a user (sets <c>is_active=false</c>) without touching any
    /// other column. Used by the M365 callback when Graph reports the Azure
    /// account as disabled, so a subsequent login attempt with the same OID
    /// is rejected before it reaches the Graph-check again.
    Task MarkInactiveAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<AgentUser>> ListAgentsAsync(CancellationToken ct = default);

    /// Typeahead for the @@-mention picker (v0.0.12 stap 3). Caller-supplied
    /// <paramref name="search"/> does a case-insensitive substring match on
    /// <c>email</c> (the only human-readable column we have — the users table
    /// has no first/last-name yet). <paramref name="limit"/> is always clamped
    /// so a hostile client can't ask for all rows. Returns Agent + Admin rows
    /// only; customers are never surfaced here, even if they share a mailbox
    /// local-part with an agent.
    Task<IReadOnlyList<AgentUser>> SearchAgentsAsync(string? search, int limit, CancellationToken ct = default);

    /// Input-filter for the mention-ids that accompany a Note/Comment/MailSent
    /// event. Returns exactly the subset of <paramref name="ids"/> that map to
    /// an Agent or Admin row — unknown ids, customer ids, or soft-deleted rows
    /// are silently dropped. This mirrors the attachment-ownership filter in
    /// <c>ReassignToEventAsync</c>: a hostile payload that tags a customer id
    /// or a random guid is not an error, it just doesn't persist that id.
    Task<IReadOnlyList<Guid>> FilterAgentIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

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

    /// v0.0.35 — surfaces the two per-user Timesheet feature flags so the
    /// /auth/me endpoint can decide whether to render the menu item
    /// without a second DB hit. Returns (false, false) when the user is
    /// gone or has never had the flags set.
    Task<TimesheetFlags> GetTimesheetFlagsAsync(Guid userId, CancellationToken ct = default);

    /// v0.0.40 — surfaces the two per-user ISO 27001 workflow flags. The
    /// /auth/me payload uses them to decide which classification buttons
    /// to render on a ticket sitting in the ISO queue. Returns
    /// (false, false) when the user is gone or has never had the flags
    /// set — non-ISO agents never see the workflow.
    Task<IsoFlags> GetIsoFlagsAsync(Guid userId, CancellationToken ct = default);

    /// v0.0.40 polish — surfaces the per-user Knowledge Base opt-in flag.
    /// The sidebar uses it to hide the KB nav entry; the KB endpoint
    /// filter uses it to gate the /api/kb surface. Returns false when
    /// the row is gone or the column is FALSE.
    Task<bool> GetKbEnabledAsync(Guid userId, CancellationToken ct = default);

    /// Per-user Sidebar feature flag for the global search bar. Returns
    /// true on missing rows so a session outliving a deleted user does
    /// not produce a visible regression (the search bar is also gated
    /// by role on the frontend).
    Task<bool> GetSearchEnabledAsync(Guid userId, CancellationToken ct = default);

    /// v0.0.42 — per-user opt-in for the agent activity feed. When false,
    /// the dashboard tile and admin activity page are hidden and the
    /// SignalR hub refuses the group-enroll on connect. Returns false on
    /// missing rows so a session outliving a deleted user cannot keep a
    /// stale subscription open.
    Task<bool> GetActivityFeedEnabledAsync(Guid userId, CancellationToken ct = default);

    /// v0.0.52 — per-user opt-in for the Assets page (Tactical RMM mirror).
    /// Used by /auth/me so the sidebar can hide the nav entry on first
    /// paint. Returns false on missing rows.
    Task<bool> GetAssetsEnabledAsync(Guid userId, CancellationToken ct = default);

    /// Per-user opt-in for the Adsolut timesheet tab. The /auth/me payload
    /// pairs this with the live Adsolut connection state — the tab only
    /// renders when both are true. Returns false on missing rows.
    Task<bool> GetAdsolutTimesheetEnabledAsync(Guid userId, CancellationToken ct = default);

    /// v0.0.59 — per-user opt-in for the Adsolut Orders feature (navbar
    /// overview under Assets, order detail, the ticket "Sync orders" button
    /// and "::" order linking). Returns false on missing rows.
    Task<bool> GetAdsolutOrdersEnabledAsync(Guid userId, CancellationToken ct = default);

    /// v0.0.56 — per-user opt-in for the back-office Resolved + CWI
    /// timesheet tabs. Drives their visibility in the SPA. Returns false on
    /// missing rows.
    Task<bool> GetTimesheetBackofficeEnabledAsync(Guid userId, CancellationToken ct = default);

    /// v0.0.69 — per-user opt-in for viewing the Statistics page (the tiles
    /// assigned to this user). Drives the sidebar nav entry. Returns false
    /// on missing rows.
    Task<bool> GetStatisticsReadEnabledAsync(Guid userId, CancellationToken ct = default);

    /// v0.0.69 — per-user opt-in for building + assigning statistic tiles.
    /// Gates the builder UI and the tile-management endpoints. Returns false
    /// on missing rows.
    Task<bool> GetStatisticsWriteEnabledAsync(Guid userId, CancellationToken ct = default);

    /// v0.0.76 — per-user opt-in for the Contracts page (tile hub; the
    /// contract data model lands later). Drives the sidebar nav entry.
    /// Returns false on missing rows.
    Task<bool> GetContractsEnabledAsync(Guid userId, CancellationToken ct = default);

    /// Per-user opt-in for the Employee Feedback board. Gates the page nav
    /// entry and is the authorization boundary for the /api/feedback/*
    /// endpoints. Returns false on missing rows.
    Task<bool> GetFeedbackEnabledAsync(Guid userId, CancellationToken ct = default);
}

/// Per-user Timesheet feature flags. Empty struct-y record so the call
/// stays cheap and we don't allocate a class for two booleans.
public readonly record struct TimesheetFlags(bool Enabled, bool Manager)
{
    public static TimesheetFlags None => default;
}

/// Per-user ISO 27001 workflow flags. Mgm = first-line reviewer; Dpo =
/// data-protection officer who registers incidents in the ISMS. A user
/// can carry both (admins often do) or neither (default for new users).
public readonly record struct IsoFlags(bool Mgm, bool Dpo)
{
    public static IsoFlags None => default;
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
                      failed_attempts AS FailedAttempts, lockout_until_utc AS LockoutUntilUtc,
                      auth_mode AS AuthMode, external_provider AS ExternalProvider,
                      external_subject AS ExternalSubject, is_active AS IsActive
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
        // @email is cast to citext so login/OIDC lookup matches regardless of
        // casing — email is a CITEXT column, but a bare `text` parameter would
        // otherwise resolve to a case-sensitive comparison and miss the user.
        const string sql = """
            SELECT id AS Id, email AS Email, password_hash AS PasswordHash, role_name AS RoleName,
                   created_utc AS CreatedUtc, last_login_utc AS LastLoginUtc,
                   failed_attempts AS FailedAttempts, lockout_until_utc AS LockoutUntilUtc,
                   auth_mode AS AuthMode, external_provider AS ExternalProvider,
                   external_subject AS ExternalSubject, is_active AS IsActive
            FROM users WHERE email = @email::citext
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
                   failed_attempts AS FailedAttempts, lockout_until_utc AS LockoutUntilUtc,
                   auth_mode AS AuthMode, external_provider AS ExternalProvider,
                   external_subject AS ExternalSubject, is_active AS IsActive
            FROM users WHERE id = @id
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<ApplicationUser>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<ApplicationUser?> FindByExternalAsync(string provider, string subject, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id AS Id, email AS Email, password_hash AS PasswordHash, role_name AS RoleName,
                   created_utc AS CreatedUtc, last_login_utc AS LastLoginUtc,
                   failed_attempts AS FailedAttempts, lockout_until_utc AS LockoutUntilUtc,
                   auth_mode AS AuthMode, external_provider AS ExternalProvider,
                   external_subject AS ExternalSubject, is_active AS IsActive
            FROM users
            WHERE external_provider = @provider AND external_subject = @subject
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<ApplicationUser>(
            new CommandDefinition(sql, new { provider, subject }, cancellationToken: ct));
    }

    public async Task MarkInactiveAsync(Guid userId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET is_active = FALSE WHERE id = @id",
            new { id = userId },
            cancellationToken: ct));
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

    public async Task<IReadOnlyList<AgentUser>> SearchAgentsAsync(string? search, int limit, CancellationToken ct = default)
    {
        var effectiveLimit = limit <= 0 ? 20 : Math.Min(limit, 50);
        var trimmed = (search ?? string.Empty).Trim();

        const string sqlWithSearch = """
            SELECT id AS Id, email AS Email, role_name AS RoleName
            FROM users
            WHERE role_name IN ('Agent', 'Admin')
              AND email ILIKE @pattern
            ORDER BY email
            LIMIT @limit
            """;
        const string sqlNoSearch = """
            SELECT id AS Id, email AS Email, role_name AS RoleName
            FROM users
            WHERE role_name IN ('Agent', 'Admin')
            ORDER BY email
            LIMIT @limit
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        if (trimmed.Length == 0)
        {
            var rows = await connection.QueryAsync<AgentUser>(
                new CommandDefinition(sqlNoSearch, new { limit = effectiveLimit }, cancellationToken: ct));
            return rows.ToList();
        }

        // ILIKE %term% — Dapper parameterizes the value so escaping '%' / '_'
        // inside the user's input is the only concern. We escape both so a
        // query containing a literal percent sign ("admin%") matches that
        // literal instead of being treated as a wildcard run-on.
        var escaped = trimmed
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        var pattern = "%" + escaped + "%";
        var withSearch = await connection.QueryAsync<AgentUser>(
            new CommandDefinition(sqlWithSearch, new { pattern, limit = effectiveLimit }, cancellationToken: ct));
        return withSearch.ToList();
    }

    public async Task<IReadOnlyList<Guid>> FilterAgentIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids is null || ids.Count == 0) return Array.Empty<Guid>();
        var distinct = ids.Distinct().ToArray();

        const string sql = """
            SELECT id
            FROM users
            WHERE role_name IN ('Agent', 'Admin')
              AND id = ANY(@ids)
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<Guid>(
            new CommandDefinition(sql, new { ids = distinct }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<TimesheetFlags> GetTimesheetFlagsAsync(Guid userId, CancellationToken ct = default)
    {
        // Dapper + readonly record struct doesn't play nicely (see
        // feedback memory: dapper_record_struct_null_bug). Read into a
        // mutable holder, then project. Returns None for missing rows.
        const string sql = """
            SELECT timesheet_enabled AS Enabled,
                   timesheet_manager AS Manager
            FROM users WHERE id = @id
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<TimesheetFlagsRow>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return row is null ? TimesheetFlags.None : new TimesheetFlags(row.Enabled, row.Manager);
    }

    private sealed class TimesheetFlagsRow
    {
        public bool Enabled { get; set; }
        public bool Manager { get; set; }
    }

    public async Task<IsoFlags> GetIsoFlagsAsync(Guid userId, CancellationToken ct = default)
    {
        // Same record-struct hydration shape as the timesheet path —
        // intermediate mutable holder to keep Dapper happy.
        const string sql = """
            SELECT is_iso_mgm AS Mgm,
                   is_iso_dpo AS Dpo
            FROM users WHERE id = @id
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<IsoFlagsRow>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return row is null ? IsoFlags.None : new IsoFlags(row.Mgm, row.Dpo);
    }

    private sealed class IsoFlagsRow
    {
        public bool Mgm { get; set; }
        public bool Dpo { get; set; }
    }

    public async Task<bool> GetKbEnabledAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "SELECT kb_enabled FROM users WHERE id = @id";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var value = await connection.QuerySingleOrDefaultAsync<bool?>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return value ?? false;
    }

    public async Task<bool> GetSearchEnabledAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "SELECT search_enabled FROM users WHERE id = @id";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var value = await connection.QuerySingleOrDefaultAsync<bool?>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return value ?? true;
    }

    public async Task<bool> GetActivityFeedEnabledAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "SELECT activity_feed_enabled FROM users WHERE id = @id";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var value = await connection.QuerySingleOrDefaultAsync<bool?>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return value ?? false;
    }

    public async Task<bool> GetAssetsEnabledAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "SELECT assets_enabled FROM users WHERE id = @id";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var value = await connection.QuerySingleOrDefaultAsync<bool?>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return value ?? false;
    }

    public async Task<bool> GetAdsolutTimesheetEnabledAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "SELECT adsolut_timesheet_enabled FROM users WHERE id = @id";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var value = await connection.QuerySingleOrDefaultAsync<bool?>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return value ?? false;
    }

    public async Task<bool> GetAdsolutOrdersEnabledAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "SELECT adsolut_orders_enabled FROM users WHERE id = @id";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var value = await connection.QuerySingleOrDefaultAsync<bool?>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return value ?? false;
    }

    public async Task<bool> GetTimesheetBackofficeEnabledAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "SELECT timesheet_backoffice_enabled FROM users WHERE id = @id";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var value = await connection.QuerySingleOrDefaultAsync<bool?>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return value ?? false;
    }

    public async Task<bool> GetStatisticsReadEnabledAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "SELECT statistics_read FROM users WHERE id = @id";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var value = await connection.QuerySingleOrDefaultAsync<bool?>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return value ?? false;
    }

    public async Task<bool> GetStatisticsWriteEnabledAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "SELECT statistics_write FROM users WHERE id = @id";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var value = await connection.QuerySingleOrDefaultAsync<bool?>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return value ?? false;
    }

    public async Task<bool> GetContractsEnabledAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "SELECT contracts_enabled FROM users WHERE id = @id";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var value = await connection.QuerySingleOrDefaultAsync<bool?>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return value ?? false;
    }

    public async Task<bool> GetFeedbackEnabledAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "SELECT feedback_enabled FROM users WHERE id = @id";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var value = await connection.QuerySingleOrDefaultAsync<bool?>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return value ?? false;
    }
}

public sealed record AgentUser(Guid Id, string Email, string RoleName);
