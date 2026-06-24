using Dapper;
using Npgsql;
using Servicedesk.Infrastructure.Auth.Microsoft;
using Servicedesk.Infrastructure.Auth.Sessions;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Auth.Admin;

public sealed class UserAdminService : IUserAdminService
{
    private const string ProviderName = ExternalProviders.Microsoft;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IGraphDirectoryClient _directory;
    private readonly ISessionService _sessions;
    private readonly IPasswordHasher _hasher;
    private readonly ISettingsService _settings;

    public UserAdminService(
        NpgsqlDataSource dataSource,
        IGraphDirectoryClient directory,
        ISessionService sessions,
        IPasswordHasher hasher,
        ISettingsService settings)
    {
        _dataSource = dataSource;
        _directory = directory;
        _sessions = sessions;
        _hasher = hasher;
        _settings = settings;
    }

    public async Task<IReadOnlyList<UserAdminRow>> ListAllAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT  u.id              AS Id,
                    u.email           AS Email,
                    u.role_name       AS Role,
                    u.auth_mode       AS AuthMode,
                    u.external_subject AS ExternalSubject,
                    u.is_active       AS IsActive,
                    (t.enabled IS TRUE) AS TwoFactorEnabled,
                    u.created_utc     AS CreatedUtc,
                    u.last_login_utc  AS LastLoginUtc,
                    u.timesheet_enabled AS TimesheetEnabled,
                    u.timesheet_manager AS TimesheetManager,
                    u.is_iso_mgm        AS IsIsoMgm,
                    u.is_iso_dpo        AS IsIsoDpo,
                    u.kb_enabled        AS KbEnabled,
                    u.search_enabled    AS SearchEnabled,
                    u.activity_feed_enabled AS ActivityFeedEnabled,
                    u.assets_enabled    AS AssetsEnabled,
                    u.adsolut_timesheet_enabled AS AdsolutTimesheetEnabled,
                    u.timesheet_backoffice_enabled AS TimesheetBackofficeEnabled,
                    u.adsolut_orders_enabled AS AdsolutOrdersEnabled,
                    u.statistics_read   AS StatisticsRead,
                    u.statistics_write  AS StatisticsWrite,
                    u.contracts_enabled AS ContractsEnabled,
                    u.feedback_enabled  AS FeedbackEnabled,
                    u.feedback_own_only AS FeedbackOwnOnly
            FROM users u
            LEFT JOIN user_totp t ON t.user_id = u.id
            ORDER BY u.created_utc ASC
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<UserAdminRow>(
            new CommandDefinition(sql, cancellationToken: ct))).ToList();

        // Pull every (user_id, tile_id) pair in one round-trip and
        // group them client-side. Cheap even at thousands of users —
        // the junction is small (≤ 7 tiles * N users) and avoids the
        // N+1 we'd get from per-user fetches.
        var tilesByUser = await LoadAllDashboardTilesAsync(connection, ct);
        for (var i = 0; i < rows.Count; i++)
        {
            if (tilesByUser.TryGetValue(rows[i].Id, out var tiles))
            {
                rows[i] = rows[i] with { DashboardTiles = tiles };
            }
        }
        return rows;
    }

    public async Task<UserAdminRow?> GetByIdAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT  u.id              AS Id,
                    u.email           AS Email,
                    u.role_name       AS Role,
                    u.auth_mode       AS AuthMode,
                    u.external_subject AS ExternalSubject,
                    u.is_active       AS IsActive,
                    (t.enabled IS TRUE) AS TwoFactorEnabled,
                    u.created_utc     AS CreatedUtc,
                    u.last_login_utc  AS LastLoginUtc,
                    u.timesheet_enabled AS TimesheetEnabled,
                    u.timesheet_manager AS TimesheetManager,
                    u.is_iso_mgm        AS IsIsoMgm,
                    u.is_iso_dpo        AS IsIsoDpo,
                    u.kb_enabled        AS KbEnabled,
                    u.search_enabled    AS SearchEnabled,
                    u.activity_feed_enabled AS ActivityFeedEnabled,
                    u.assets_enabled    AS AssetsEnabled,
                    u.adsolut_timesheet_enabled AS AdsolutTimesheetEnabled,
                    u.timesheet_backoffice_enabled AS TimesheetBackofficeEnabled,
                    u.adsolut_orders_enabled AS AdsolutOrdersEnabled,
                    u.statistics_read   AS StatisticsRead,
                    u.statistics_write  AS StatisticsWrite,
                    u.contracts_enabled AS ContractsEnabled,
                    u.feedback_enabled  AS FeedbackEnabled,
                    u.feedback_own_only AS FeedbackOwnOnly
            FROM users u
            LEFT JOIN user_totp t ON t.user_id = u.id
            WHERE u.id = @id
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var row = await connection.QueryFirstOrDefaultAsync<UserAdminRow>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        if (row is null) return null;

        var tiles = (await connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT tile_id FROM user_dashboard_tiles WHERE user_id = @id",
                new { id = userId },
                cancellationToken: ct))).ToList();
        return row with { DashboardTiles = tiles };
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<string>>> LoadAllDashboardTilesAsync(
        NpgsqlConnection connection,
        CancellationToken ct)
    {
        var rows = await connection.QueryAsync<(Guid UserId, string TileId)>(
            new CommandDefinition(
                "SELECT user_id AS UserId, tile_id AS TileId FROM user_dashboard_tiles",
                cancellationToken: ct));
        return rows
            .GroupBy(r => r.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(r => r.TileId).ToList());
    }

    public async Task<AddLocalUserResult> AddLocalUserAsync(
        string email,
        string password,
        string role,
        Guid actingAdminId,
        CancellationToken ct = default)
    {
        if (!IsValidAssignableRole(role))
        {
            return new AddLocalUserResult.InvalidRole();
        }

        var trimmedEmail = (email ?? string.Empty).Trim();
        if (trimmedEmail.Length == 0 || !trimmedEmail.Contains('@', StringComparison.Ordinal))
        {
            return new AddLocalUserResult.InvalidEmail();
        }

        var minLength = await _settings.GetAsync<int>(SettingKeys.Security.PasswordMinimumLength, ct);
        if (string.IsNullOrEmpty(password) || password.Length < minLength)
        {
            return new AddLocalUserResult.WeakPassword(minLength);
        }

        // Hash BEFORE opening the transaction so a slow Argon2 pass
        // (64 MB memory cost by default) doesn't hold the row lock.
        var hash = _hasher.Hash(password);

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var collision = await connection.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(
                // Cast to citext: the duplicate-check must fold case in step with
                // the UNIQUE(email) constraint, else a casing-only collision slips
                // past here and the INSERT below trips 23505.
                "SELECT id FROM users WHERE email = @email::citext",
                new { email = trimmedEmail },
                tx,
                cancellationToken: ct));
        if (collision is not null)
        {
            await tx.RollbackAsync(ct);
            return new AddLocalUserResult.EmailAlreadyUsed();
        }

        var userId = await connection.ExecuteScalarAsync<Guid>(
            new CommandDefinition(
                """
                INSERT INTO users (
                    email, password_hash, role_name,
                    auth_mode, external_provider, external_subject,
                    is_active
                )
                VALUES (@email, @hash, @role, 'Local', NULL, NULL, TRUE)
                RETURNING id
                """,
                new { email = trimmedEmail, hash, role },
                tx,
                cancellationToken: ct));

        await tx.CommitAsync(ct);
        _ = actingAdminId;

        var row = await GetByIdAsync(userId, ct);
        return row is null
            ? new AddLocalUserResult.InvalidEmail()
            : new AddLocalUserResult.Created(row);
    }

    public async Task<AddMicrosoftUserResult> AddMicrosoftUserAsync(
        string oid,
        string role,
        Guid actingAdminId,
        CancellationToken ct = default)
    {
        if (!IsValidAssignableRole(role))
        {
            return new AddMicrosoftUserResult.InvalidRole();
        }

        GraphUserStatus? azure;
        try
        {
            azure = await _directory.GetUserStatusAsync(oid, ct);
        }
        catch (InvalidOperationException)
        {
            return new AddMicrosoftUserResult.MissingGraphConfig();
        }

        if (azure is null)
        {
            return new AddMicrosoftUserResult.OidNotFound();
        }
        if (!azure.AccountEnabled)
        {
            return new AddMicrosoftUserResult.AzureAccountDisabled();
        }

        // Canonical email: prefer the tenant's primary SMTP ("mail"),
        // fall back to UPN. Never trust a client-supplied email on this
        // path; the row's email is whatever Graph says, period.
        var email = !string.IsNullOrWhiteSpace(azure.Mail)
            ? azure.Mail!
            : azure.UserPrincipalName;
        if (string.IsNullOrWhiteSpace(email))
        {
            return new AddMicrosoftUserResult.OidNotFound();
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        // OID-already-linked takes precedence over email-already-used
        // because an OID collision is deterministic (one Azure object =
        // one row) while an email collision may be a legit local user
        // the admin meant to upgrade via the Upgrade-to-M365 flow.
        var existingByOid = await connection.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(
                "SELECT id FROM users WHERE external_provider = @provider AND external_subject = @subject",
                new { provider = ProviderName, subject = azure.Oid },
                tx,
                cancellationToken: ct));
        if (existingByOid is not null)
        {
            await tx.RollbackAsync(ct);
            return new AddMicrosoftUserResult.OidAlreadyLinked();
        }

        var existingByEmail = await connection.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(
                // Cast to citext so a casing-only collision is caught here rather
                // than surfacing as a 23505 on the INSERT.
                "SELECT id FROM users WHERE email = @email::citext",
                new { email },
                tx,
                cancellationToken: ct));
        if (existingByEmail is not null)
        {
            await tx.RollbackAsync(ct);
            return new AddMicrosoftUserResult.EmailAlreadyUsed();
        }

        var userId = await connection.ExecuteScalarAsync<Guid>(
            new CommandDefinition(
                """
                INSERT INTO users (
                    email, password_hash, role_name,
                    auth_mode, external_provider, external_subject,
                    is_active
                )
                VALUES (@email, NULL, @role, 'Microsoft', @provider, @subject, TRUE)
                RETURNING id
                """,
                new
                {
                    email,
                    role,
                    provider = ProviderName,
                    subject = azure.Oid,
                },
                tx,
                cancellationToken: ct));

        await tx.CommitAsync(ct);
        _ = actingAdminId; // used by audit at the endpoint layer

        var row = await GetByIdAsync(userId, ct);
        return row is null
            ? new AddMicrosoftUserResult.OidNotFound()
            : new AddMicrosoftUserResult.Created(row);
    }

    public async Task<UpgradeToMicrosoftResult> UpgradeToMicrosoftAsync(
        Guid userId,
        string oid,
        Guid actingAdminId,
        CancellationToken ct = default)
    {
        if (userId == actingAdminId)
        {
            return new UpgradeToMicrosoftResult.SelfUpgradeForbidden();
        }

        GraphUserStatus? azure;
        try
        {
            azure = await _directory.GetUserStatusAsync(oid, ct);
        }
        catch (InvalidOperationException)
        {
            return new UpgradeToMicrosoftResult.MissingGraphConfig();
        }

        if (azure is null)
        {
            return new UpgradeToMicrosoftResult.OidNotFound();
        }
        if (!azure.AccountEnabled)
        {
            return new UpgradeToMicrosoftResult.AzureAccountDisabled();
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        // Row lock on the target so two admins upgrading the same user
        // concurrently can't race past the CHECK constraint.
        var targetMode = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT auth_mode FROM users WHERE id = @id FOR UPDATE",
                new { id = userId },
                tx,
                cancellationToken: ct));
        if (targetMode is null)
        {
            await tx.RollbackAsync(ct);
            return new UpgradeToMicrosoftResult.UserNotFound();
        }
        if (!string.Equals(targetMode, AuthModes.Local, StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return new UpgradeToMicrosoftResult.NotLocalUser();
        }

        // OID collision check — partial unique index on
        // (external_provider, external_subject) would throw on INSERT,
        // but we want a typed result not a PostgresException.
        var collision = await connection.ExecuteScalarAsync<Guid?>(
            new CommandDefinition(
                """
                SELECT id FROM users
                WHERE external_provider = @provider AND external_subject = @subject
                  AND id <> @selfId
                """,
                new { provider = ProviderName, subject = azure.Oid, selfId = userId },
                tx,
                cancellationToken: ct));
        if (collision is not null)
        {
            await tx.RollbackAsync(ct);
            return new UpgradeToMicrosoftResult.OidAlreadyLinked();
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE users SET
                auth_mode = 'Microsoft',
                external_provider = @provider,
                external_subject = @subject,
                password_hash = NULL,
                failed_attempts = 0,
                lockout_until_utc = NULL
            WHERE id = @id
            """,
            new { id = userId, provider = ProviderName, subject = azure.Oid },
            tx,
            cancellationToken: ct));

        // TOTP + recovery rows go with the mode-flip so the Microsoft
        // user never carries local-auth artefacts. FK cascades would
        // NOT handle this — the user isn't being deleted.
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

        // Revoke open sessions — the user's password no longer works,
        // so a stale cookie tied to the previous auth_mode should die.
        // Safe to do after commit: worst case a racing request slips
        // through before revoke lands, but on the next request it
        // re-validates and sees the row's new state.
        await _sessions.RevokeAllForUserAsync(userId, ct);

        var row = await GetByIdAsync(userId, ct);
        return row is null
            ? new UpgradeToMicrosoftResult.UserNotFound()
            : new UpgradeToMicrosoftResult.Upgraded(row);
    }

    public async Task<UpdateRoleResult> UpdateRoleAsync(
        Guid userId,
        string role,
        Guid actingAdminId,
        CancellationToken ct = default)
    {
        if (!IsValidAssignableRole(role))
        {
            return new UpdateRoleResult.InvalidRole();
        }
        if (userId == actingAdminId)
        {
            return new UpdateRoleResult.SelfChangeForbidden();
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var currentRole = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT role_name FROM users WHERE id = @id FOR UPDATE",
                new { id = userId },
                tx,
                cancellationToken: ct));
        if (currentRole is null)
        {
            await tx.RollbackAsync(ct);
            return new UpdateRoleResult.UserNotFound();
        }

        if (string.Equals(currentRole, "Admin", StringComparison.Ordinal) &&
            !string.Equals(role, "Admin", StringComparison.Ordinal))
        {
            if (await IsLastActiveAdminAsync(connection, tx, userId, ct))
            {
                await tx.RollbackAsync(ct);
                return new UpdateRoleResult.LastAdminForbidden();
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET role_name = @role WHERE id = @id",
            new { id = userId, role },
            tx,
            cancellationToken: ct));
        await tx.CommitAsync(ct);

        var row = await GetByIdAsync(userId, ct);
        return row is null
            ? new UpdateRoleResult.UserNotFound()
            : new UpdateRoleResult.Updated(row);
    }

    public async Task<SetActiveResult> SetActiveAsync(
        Guid userId,
        bool active,
        Guid actingAdminId,
        CancellationToken ct = default)
    {
        // Deactivating yourself locks you out instantly. Blocked.
        // Reactivating yourself is impossible anyway (you can't reach
        // this endpoint while inactive), but symmetrically guard it.
        if (userId == actingAdminId && !active)
        {
            return new SetActiveResult.SelfChangeForbidden();
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var current = await connection.QueryFirstOrDefaultAsync<(string Role, bool IsActive)?>(
            new CommandDefinition(
                "SELECT role_name AS Role, is_active AS IsActive FROM users WHERE id = @id FOR UPDATE",
                new { id = userId },
                tx,
                cancellationToken: ct));
        if (current is null)
        {
            await tx.RollbackAsync(ct);
            return new SetActiveResult.UserNotFound();
        }

        if (!active && string.Equals(current.Value.Role, "Admin", StringComparison.Ordinal))
        {
            if (await IsLastActiveAdminAsync(connection, tx, userId, ct))
            {
                await tx.RollbackAsync(ct);
                return new SetActiveResult.LastAdminForbidden();
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET is_active = @active WHERE id = @id",
            new { id = userId, active },
            tx,
            cancellationToken: ct));
        await tx.CommitAsync(ct);

        if (!active)
        {
            await _sessions.RevokeAllForUserAsync(userId, ct);
        }

        var row = await GetByIdAsync(userId, ct);
        return row is null
            ? new SetActiveResult.UserNotFound()
            : new SetActiveResult.Updated(row);
    }

    public async Task<DeleteResult> DeleteAsync(
        Guid userId,
        Guid actingAdminId,
        CancellationToken ct = default)
    {
        if (userId == actingAdminId)
        {
            return new DeleteResult.SelfDeleteForbidden();
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var current = await connection.QueryFirstOrDefaultAsync<(string Role, bool IsActive)?>(
            new CommandDefinition(
                "SELECT role_name AS Role, is_active AS IsActive FROM users WHERE id = @id FOR UPDATE",
                new { id = userId },
                tx,
                cancellationToken: ct));
        if (current is null)
        {
            await tx.RollbackAsync(ct);
            return new DeleteResult.UserNotFound();
        }

        if (string.Equals(current.Value.Role, "Admin", StringComparison.Ordinal) &&
            await IsLastActiveAdminAsync(connection, tx, userId, ct))
        {
            await tx.RollbackAsync(ct);
            return new DeleteResult.LastAdminForbidden();
        }

        // Referential-integrity scan. Users that have authored tickets,
        // events, or notes cannot be hard-deleted without losing audit
        // history. Admins should deactivate those users instead.
        var reasons = new List<string>();
        // tickets.assignee_user_id and ticket_events.author_user_id both
        // declare ON DELETE SET NULL, so the delete wouldn't error — but
        // losing the "who assigned this" / "who wrote this note" link is
        // destructive for audit purposes. Surface the counts and force
        // the admin toward Deactivate instead.
        var ticketCount = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM tickets WHERE assignee_user_id = @id",
                new { id = userId },
                tx,
                cancellationToken: ct));
        if (ticketCount > 0) reasons.Add($"assigned to {ticketCount} ticket(s)");

        var eventCount = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM ticket_events WHERE author_user_id = @id",
                new { id = userId },
                tx,
                cancellationToken: ct));
        if (eventCount > 0) reasons.Add($"authored {eventCount} timeline event(s)");

        if (reasons.Count > 0)
        {
            await tx.RollbackAsync(ct);
            return new DeleteResult.BlockedReferences(reasons);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM users WHERE id = @id",
            new { id = userId },
            tx,
            cancellationToken: ct));
        await tx.CommitAsync(ct);

        return new DeleteResult.Deleted();
    }

    public async Task<UpdateTimesheetFlagsResult> UpdateTimesheetFlagsAsync(
        Guid userId,
        bool enabled,
        bool manager,
        Guid actingAdminId,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var currentRole = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT role_name FROM users WHERE id = @id FOR UPDATE",
                new { id = userId },
                tx,
                cancellationToken: ct));
        if (currentRole is null)
        {
            await tx.RollbackAsync(ct);
            return new UpdateTimesheetFlagsResult.UserNotFound();
        }

        // Customer accounts have no Timesheet UI; reject the mutation
        // rather than silently writing flags that no UI surfaces.
        if (string.Equals(currentRole, "Customer", StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return new UpdateTimesheetFlagsResult.CustomerNotAllowed();
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE users SET
                timesheet_enabled = @enabled,
                timesheet_manager = @manager
            WHERE id = @id
            """,
            new { id = userId, enabled, manager },
            tx,
            cancellationToken: ct));
        await tx.CommitAsync(ct);
        _ = actingAdminId;

        var row = await GetByIdAsync(userId, ct);
        return row is null
            ? new UpdateTimesheetFlagsResult.UserNotFound()
            : new UpdateTimesheetFlagsResult.Updated(row);
    }

    public async Task<UpdateIsoFlagsResult> UpdateIsoFlagsAsync(
        Guid userId,
        bool mgm,
        bool dpo,
        Guid actingAdminId,
        CancellationToken ct = default)
    {
        // Mirrors UpdateTimesheetFlagsAsync — single UPDATE inside a
        // FOR UPDATE row lock, Customer rows rejected. Self-edit is
        // allowed (the flags don't gate authentication, only feature
        // visibility, so there's no lockout risk).
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var currentRole = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT role_name FROM users WHERE id = @id FOR UPDATE",
                new { id = userId },
                tx,
                cancellationToken: ct));
        if (currentRole is null)
        {
            await tx.RollbackAsync(ct);
            return new UpdateIsoFlagsResult.UserNotFound();
        }

        if (string.Equals(currentRole, "Customer", StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return new UpdateIsoFlagsResult.CustomerNotAllowed();
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE users SET
                is_iso_mgm = @mgm,
                is_iso_dpo = @dpo
            WHERE id = @id
            """,
            new { id = userId, mgm, dpo },
            tx,
            cancellationToken: ct));
        await tx.CommitAsync(ct);
        _ = actingAdminId;

        var row = await GetByIdAsync(userId, ct);
        return row is null
            ? new UpdateIsoFlagsResult.UserNotFound()
            : new UpdateIsoFlagsResult.Updated(row);
    }

    public async Task<UpdateKbFlagResult> UpdateKbFlagAsync(
        Guid userId,
        bool enabled,
        Guid actingAdminId,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var currentRole = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT role_name FROM users WHERE id = @id FOR UPDATE",
                new { id = userId },
                tx,
                cancellationToken: ct));
        if (currentRole is null)
        {
            await tx.RollbackAsync(ct);
            return new UpdateKbFlagResult.UserNotFound();
        }

        if (string.Equals(currentRole, "Customer", StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return new UpdateKbFlagResult.CustomerNotAllowed();
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET kb_enabled = @enabled WHERE id = @id",
            new { id = userId, enabled },
            tx,
            cancellationToken: ct));
        await tx.CommitAsync(ct);
        _ = actingAdminId;

        var row = await GetByIdAsync(userId, ct);
        return row is null
            ? new UpdateKbFlagResult.UserNotFound()
            : new UpdateKbFlagResult.Updated(row);
    }

    public async Task<UpdateFeatureFlagsResult> UpdateFeatureFlagsAsync(
        Guid userId,
        FeatureFlagsUpdate update,
        Guid actingAdminId,
        CancellationToken ct = default)
    {
        if (update.TimesheetEnabled is null
            && update.TimesheetManager is null
            && update.IsIsoMgm is null
            && update.IsIsoDpo is null
            && update.KbEnabled is null
            && update.SearchEnabled is null
            && update.ActivityFeedEnabled is null
            && update.AssetsEnabled is null
            && update.AdsolutTimesheetEnabled is null
            && update.TimesheetBackofficeEnabled is null
            && update.AdsolutOrdersEnabled is null
            && update.StatisticsRead is null
            && update.StatisticsWrite is null
            && update.ContractsEnabled is null
            && update.FeedbackEnabled is null
            && update.FeedbackOwnOnly is null)
        {
            return new UpdateFeatureFlagsResult.NoChange();
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var currentRole = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT role_name FROM users WHERE id = @id FOR UPDATE",
                new { id = userId },
                tx,
                cancellationToken: ct));
        if (currentRole is null)
        {
            await tx.RollbackAsync(ct);
            return new UpdateFeatureFlagsResult.UserNotFound();
        }

        if (string.Equals(currentRole, "Customer", StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return new UpdateFeatureFlagsResult.CustomerNotAllowed();
        }

        // COALESCE(@param, column) keeps the existing value where the
        // caller sent null. One atomic UPDATE inside the FOR UPDATE row
        // lock — concurrent admins toggling different flags on the same
        // row cannot lose each other's writes.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE users SET
                timesheet_enabled     = COALESCE(@timesheetEnabled,     timesheet_enabled),
                timesheet_manager     = COALESCE(@timesheetManager,     timesheet_manager),
                is_iso_mgm            = COALESCE(@isIsoMgm,             is_iso_mgm),
                is_iso_dpo            = COALESCE(@isIsoDpo,             is_iso_dpo),
                kb_enabled            = COALESCE(@kbEnabled,            kb_enabled),
                search_enabled        = COALESCE(@searchEnabled,        search_enabled),
                activity_feed_enabled = COALESCE(@activityFeedEnabled,  activity_feed_enabled),
                assets_enabled        = COALESCE(@assetsEnabled,        assets_enabled),
                adsolut_timesheet_enabled = COALESCE(@adsolutTimesheetEnabled, adsolut_timesheet_enabled),
                timesheet_backoffice_enabled = COALESCE(@timesheetBackofficeEnabled, timesheet_backoffice_enabled),
                adsolut_orders_enabled = COALESCE(@adsolutOrdersEnabled, adsolut_orders_enabled),
                statistics_read  = COALESCE(@statisticsRead,  statistics_read),
                statistics_write = COALESCE(@statisticsWrite, statistics_write),
                contracts_enabled = COALESCE(@contractsEnabled, contracts_enabled),
                feedback_enabled = COALESCE(@feedbackEnabled, feedback_enabled),
                feedback_own_only = COALESCE(@feedbackOwnOnly, feedback_own_only)
            WHERE id = @id
            """,
            new
            {
                id = userId,
                timesheetEnabled = update.TimesheetEnabled,
                timesheetManager = update.TimesheetManager,
                isIsoMgm = update.IsIsoMgm,
                isIsoDpo = update.IsIsoDpo,
                kbEnabled = update.KbEnabled,
                searchEnabled = update.SearchEnabled,
                activityFeedEnabled = update.ActivityFeedEnabled,
                assetsEnabled = update.AssetsEnabled,
                adsolutTimesheetEnabled = update.AdsolutTimesheetEnabled,
                timesheetBackofficeEnabled = update.TimesheetBackofficeEnabled,
                adsolutOrdersEnabled = update.AdsolutOrdersEnabled,
                statisticsRead = update.StatisticsRead,
                statisticsWrite = update.StatisticsWrite,
                contractsEnabled = update.ContractsEnabled,
                feedbackEnabled = update.FeedbackEnabled,
                feedbackOwnOnly = update.FeedbackOwnOnly,
            },
            tx,
            cancellationToken: ct));
        await tx.CommitAsync(ct);
        _ = actingAdminId;

        var row = await GetByIdAsync(userId, ct);
        return row is null
            ? new UpdateFeatureFlagsResult.UserNotFound()
            : new UpdateFeatureFlagsResult.Updated(row);
    }

    // ---- helpers -------------------------------------------------------

    private static bool IsValidAssignableRole(string role) =>
        string.Equals(role, "Agent", StringComparison.Ordinal) ||
        string.Equals(role, "Admin", StringComparison.Ordinal);

    /// True if <paramref name="targetUserId"/> is the only active admin.
    /// The query counts admins OTHER than the target — if that count is
    /// zero and the target itself is currently an active admin, demoting
    /// / deactivating / deleting the target would leave the install with
    /// zero admins.
    private static async Task<bool> IsLastActiveAdminAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid targetUserId,
        CancellationToken ct)
    {
        var otherActiveAdmins = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                SELECT COUNT(*) FROM users
                WHERE role_name = 'Admin'
                  AND is_active = TRUE
                  AND id <> @target
                """,
                new { target = targetUserId },
                tx,
                cancellationToken: ct));
        return otherActiveAdmins == 0;
    }
}
