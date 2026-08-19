using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Portal;

public sealed class PortalAccountRepository : IPortalAccountRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PortalAccountRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    // Column order matches the positional PortalAccountRow record exactly.
    private const string AccountSelect = """
        SELECT  u.id                  AS UserId,
                u.email::text         AS Email,
                pa.status             AS Status,
                pa.display_name       AS DisplayName,
                pa.origin             AS Origin,
                u.is_active           AS IsActive,
                u.contact_id          AS ContactId,
                c.first_name          AS ContactFirstName,
                c.last_name           AS ContactLastName,
                c.company_role        AS ContactCompanyRole,
                co.id                 AS CompanyId,
                co.name               AS CompanyName,
                pa.registration_ip    AS RegistrationIp,
                pa.email_verified_utc AS EmailVerifiedUtc,
                pa.approval_ticket_id AS ApprovalTicketId,
                t.number              AS ApprovalTicketNumber,
                pa.approved_by_user_id AS ApprovedByUserId,
                ab.email::text        AS ApprovedByEmail,
                pa.approved_utc       AS ApprovedUtc,
                pa.rejected_by_user_id AS RejectedByUserId,
                pa.rejected_utc       AS RejectedUtc,
                pa.rejection_reason   AS RejectionReason,
                pa.invited_by_user_id AS InvitedByUserId,
                ib.email::text        AS InvitedByEmail,
                COALESCE(tt.enabled, FALSE) AS TwoFactorEnrolled,
                u.last_login_utc      AS LastLoginUtc,
                pa.created_utc        AS CreatedUtc,
                pa.updated_utc        AS UpdatedUtc
        FROM portal_accounts pa
        JOIN users u              ON u.id = pa.user_id
        LEFT JOIN contacts c      ON c.id = u.contact_id
        LEFT JOIN contact_companies cc ON cc.contact_id = c.id AND cc.role = 'primary'
        LEFT JOIN companies co    ON co.id = cc.company_id
        LEFT JOIN tickets t       ON t.id = pa.approval_ticket_id
        LEFT JOIN users ab        ON ab.id = pa.approved_by_user_id
        LEFT JOIN users ib        ON ib.id = pa.invited_by_user_id
        LEFT JOIN user_totp tt    ON tt.user_id = u.id
        """;

    public async Task<Guid?> CreatePendingRegistrationAsync(
        string email, string passwordHash, string displayName, string? ip, string? userAgent, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var userId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
                """
                INSERT INTO users (email, password_hash, role_name, auth_mode, is_active)
                VALUES (@email::citext, @hash, 'Customer', 'Local', FALSE)
                RETURNING id
                """,
                new { email, hash = passwordHash }, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO portal_accounts (user_id, status, display_name, origin, registration_ip, registration_user_agent)
                VALUES (@userId, 'PendingVerification', @displayName, 'Registration', @ip, @ua)
                """,
                new { userId, displayName, ip, ua = Truncate(userAgent, 512) }, tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return userId;
        }
        catch (PostgresException pg) when (pg.SqlState == "23505")
        {
            await tx.RollbackAsync(ct);
            return null;
        }
    }

    public async Task<Guid?> CreateInvitedAccountAsync(
        string email, string passwordHash, string displayName, Guid contactId, Guid? invitedByUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var userId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
                """
                INSERT INTO users (email, password_hash, role_name, auth_mode, is_active, contact_id)
                VALUES (@email::citext, @hash, 'Customer', 'Local', TRUE, @contactId)
                RETURNING id
                """,
                new { email, hash = passwordHash, contactId }, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO portal_accounts (user_id, status, display_name, origin, invited_by_user_id, email_verified_utc, approved_utc, approved_by_user_id)
                VALUES (@userId, 'Active', @displayName, 'Invitation', @invitedBy, now(), now(), @invitedBy)
                """,
                new { userId, displayName, invitedBy = invitedByUserId }, tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return userId;
        }
        catch (PostgresException pg) when (pg.SqlState == "23505")
        {
            await tx.RollbackAsync(ct);
            return null;
        }
    }

    public async Task<PortalAccountRow?> GetByUserIdAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PortalAccountRow>(new CommandDefinition(
            AccountSelect + " WHERE u.id = @userId", new { userId }, cancellationToken: ct));
    }

    public async Task<PortalAccountRow?> GetByEmailAsync(string email, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PortalAccountRow>(new CommandDefinition(
            AccountSelect + " WHERE u.email = @email::citext", new { email }, cancellationToken: ct));
    }

    public async Task<PortalAccountRow?> GetByContactIdAsync(Guid contactId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PortalAccountRow>(new CommandDefinition(
            AccountSelect + " WHERE u.contact_id = @contactId", new { contactId }, cancellationToken: ct));
    }

    public async Task<PortalAccountRow?> GetByApprovalTicketAsync(Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<PortalAccountRow>(new CommandDefinition(
            AccountSelect + " WHERE pa.approval_ticket_id = @ticketId ORDER BY pa.created_utc DESC",
            new { ticketId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PortalAccountRow>> ListAsync(
        IReadOnlyList<string>? statuses, string? search, int limit, CancellationToken ct)
    {
        var where = new List<string>();
        if (statuses is { Count: > 0 }) where.Add("pa.status = ANY(@statuses)");
        var term = string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%";
        if (term is not null)
            where.Add("(u.email ILIKE @term OR pa.display_name ILIKE @term OR co.name ILIKE @term)");
        var sql = AccountSelect
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty)
            + " ORDER BY pa.created_utc DESC LIMIT @limit";
        var args = new DynamicParameters();
        if (statuses is { Count: > 0 }) args.Add("statuses", statuses.ToArray());
        if (term is not null) args.Add("term", term, System.Data.DbType.String);
        args.Add("limit", Math.Clamp(limit, 1, 1000), System.Data.DbType.Int32);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PortalAccountRow>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<int> CountByStatusAsync(string status, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM portal_accounts WHERE status = @status", new { status }, cancellationToken: ct));
    }

    public async Task<bool> MarkEmailVerifiedAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE portal_accounts
               SET status = 'PendingApproval', email_verified_utc = now(), updated_utc = now()
             WHERE user_id = @userId AND status = 'PendingVerification'
            """, new { userId }, cancellationToken: ct));
        return n == 1;
    }

    public async Task SetApprovalTicketAsync(Guid userId, Guid ticketId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE portal_accounts SET approval_ticket_id = @ticketId, updated_utc = now() WHERE user_id = @userId",
            new { userId, ticketId }, cancellationToken: ct));
    }

    public async Task<bool> ApproveAsync(Guid userId, Guid contactId, Guid approvedByUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE portal_accounts
               SET status = 'Active', approved_by_user_id = @by, approved_utc = now(), updated_utc = now()
             WHERE user_id = @userId AND status = 'PendingApproval'
            """, new { userId, by = approvedByUserId }, tx, cancellationToken: ct));
        if (n != 1)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET is_active = TRUE, contact_id = @contactId WHERE id = @userId AND role_name = 'Customer'",
            new { userId, contactId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> RejectAsync(Guid userId, Guid rejectedByUserId, string? reason, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE portal_accounts
               SET status = 'Rejected', rejected_by_user_id = @by, rejected_utc = now(),
                   rejection_reason = @reason, updated_utc = now()
             WHERE user_id = @userId AND status IN ('PendingVerification','PendingApproval')
            """, new { userId, by = rejectedByUserId, reason = Truncate(reason, 1000) }, tx, cancellationToken: ct));
        if (n != 1)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET is_active = FALSE WHERE id = @userId AND role_name = 'Customer'",
            new { userId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> SetActiveAsync(Guid userId, bool active, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var n = active
            ? await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE portal_accounts SET status = 'Active', updated_utc = now() WHERE user_id = @userId AND status = 'Deactivated'",
                new { userId }, tx, cancellationToken: ct))
            : await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE portal_accounts SET status = 'Deactivated', updated_utc = now() WHERE user_id = @userId AND status = 'Active'",
                new { userId }, tx, cancellationToken: ct));
        if (n != 1)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET is_active = @active WHERE id = @userId AND role_name = 'Customer'",
            new { userId, active }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM users WHERE id = @userId AND role_name = 'Customer'",
            new { userId }, cancellationToken: ct));
        return n == 1;
    }

    public async Task<PortalViewer?> GetViewerAsync(Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT  u.id                    AS UserId,
                    u.email::text           AS Email,
                    pa.display_name         AS DisplayName,
                    pa.status               AS Status,
                    u.contact_id            AS ContactId,
                    c.first_name            AS ContactFirstName,
                    c.last_name             AS ContactLastName
            FROM users u
            JOIN portal_accounts pa        ON pa.user_id = u.id
            LEFT JOIN contacts c           ON c.id = u.contact_id
            WHERE u.id = @userId AND u.role_name = 'Customer'
            """;
        // Companies the customer may act in: primary + secondary links only
        // (supplier never grants portal access). NULL portal_role = Member.
        const string companiesSql = """
            SELECT  cc.company_id                       AS CompanyId,
                    co.name                             AS CompanyName,
                    COALESCE(cc.portal_role, 'Member')  AS Role,
                    (cc.role = 'primary')               AS IsPrimary
            FROM contact_companies cc
            JOIN companies co ON co.id = cc.company_id
            WHERE cc.contact_id = @contactId
              AND cc.role IN ('primary','secondary')
              AND co.is_active = TRUE
            ORDER BY (cc.role = 'primary') DESC, co.name
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<PortalViewerBase>(new CommandDefinition(
            sql, new { userId }, cancellationToken: ct));
        if (row is null) return null;
        IReadOnlyList<PortalCompanyAccess> companies = Array.Empty<PortalCompanyAccess>();
        if (row.ContactId is not null)
        {
            companies = (await conn.QueryAsync<PortalCompanyAccess>(new CommandDefinition(
                companiesSql, new { contactId = row.ContactId }, cancellationToken: ct))).ToList();
        }
        return new PortalViewer(row.UserId, row.Email, row.DisplayName, row.Status, row.ContactId,
            row.ContactFirstName, row.ContactLastName, companies);
    }

    public async Task<bool> SetPortalRoleAsync(Guid contactId, Guid companyId, string portalRole, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE contact_companies SET portal_role = @portalRole, updated_utc = now()
             WHERE contact_id = @contactId AND company_id = @companyId
            """, new { contactId, companyId, portalRole }, cancellationToken: ct));
        return n == 1;
    }

    public async Task SetContactCompanyRoleAsync(Guid contactId, string companyRole, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE contacts SET company_role = @companyRole, updated_utc = now() WHERE id = @contactId",
            new { contactId, companyRole }, cancellationToken: ct));
    }

    // ---- tokens -----------------------------------------------------------

    private const string TokenSelect = """
        SELECT  id                  AS Id,
                kind                AS Kind,
                email::text         AS Email,
                user_id             AS UserId,
                contact_id          AS ContactId,
                company_id          AS CompanyId,
                company_role        AS CompanyRole,
                display_name        AS DisplayName,
                created_by_user_id  AS CreatedByUserId,
                created_utc         AS CreatedUtc,
                expires_utc         AS ExpiresUtc,
                used_utc            AS UsedUtc,
                revoked_utc         AS RevokedUtc,
                company_links::text AS CompanyLinksJson
        FROM portal_tokens
        """;

    public async Task<Guid> CreateTokenAsync(
        string kind, byte[] tokenHash, string email, Guid? userId, Guid? contactId,
        Guid? companyId, string? companyRole, string displayName, Guid? createdByUserId,
        DateTime expiresUtc, CancellationToken ct, string? companyLinksJson = null)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE portal_tokens SET revoked_utc = now()
             WHERE email = @email::citext AND kind = @kind AND used_utc IS NULL AND revoked_utc IS NULL
            """, new { email, kind }, tx, cancellationToken: ct));
        var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO portal_tokens (kind, token_hash, email, user_id, contact_id, company_id, company_role,
                                       display_name, created_by_user_id, expires_utc, company_links)
            VALUES (@kind, @tokenHash, @email::citext, @userId, @contactId, @companyId, @companyRole,
                    @displayName, @createdBy, @expiresUtc, @companyLinksJson::jsonb)
            RETURNING id
            """,
            new { kind, tokenHash, email, userId, contactId, companyId, companyRole, displayName, createdBy = createdByUserId, expiresUtc, companyLinksJson },
            tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<PortalTokenRow?> GetTokenByHashAsync(byte[] tokenHash, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PortalTokenRow>(new CommandDefinition(
            TokenSelect + " WHERE token_hash = @tokenHash", new { tokenHash }, cancellationToken: ct));
    }

    public async Task<PortalTokenRow?> GetTokenByIdAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PortalTokenRow>(new CommandDefinition(
            TokenSelect + " WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<bool> ConsumeTokenAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE portal_tokens SET used_utc = now()
             WHERE id = @id AND used_utc IS NULL AND revoked_utc IS NULL AND expires_utc > now()
            """, new { id }, cancellationToken: ct));
        return n == 1;
    }

    public async Task<int> RevokeTokensAsync(string email, string kind, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE portal_tokens SET revoked_utc = now()
             WHERE email = @email::citext AND kind = @kind AND used_utc IS NULL AND revoked_utc IS NULL
            """, new { email, kind }, cancellationToken: ct));
    }

    public async Task<bool> RevokeTokenAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var n = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE portal_tokens SET revoked_utc = now() WHERE id = @id AND used_utc IS NULL AND revoked_utc IS NULL",
            new { id }, cancellationToken: ct));
        return n == 1;
    }

    public async Task<DateTime?> GetLatestTokenCreatedUtcAsync(string email, string kind, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MAX(created_utc) FROM portal_tokens WHERE email = @email::citext AND kind = @kind",
            new { email, kind }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PortalInvitationRow>> ListInvitationsAsync(Guid? contactId, bool includeExpired, CancellationToken ct)
    {
        var sql = """
            SELECT  t.id                AS Id,
                    t.email::text       AS Email,
                    t.contact_id        AS ContactId,
                    t.company_id        AS CompanyId,
                    co.name             AS CompanyName,
                    t.company_role      AS CompanyRole,
                    t.display_name      AS DisplayName,
                    t.created_by_user_id AS CreatedByUserId,
                    cb.email::text      AS CreatedByEmail,
                    t.created_utc       AS CreatedUtc,
                    t.expires_utc       AS ExpiresUtc,
                    t.used_utc          AS UsedUtc,
                    t.revoked_utc       AS RevokedUtc,
                    t.company_links::text AS CompanyLinksJson
            FROM portal_tokens t
            LEFT JOIN companies co ON co.id = t.company_id
            LEFT JOIN users cb     ON cb.id = t.created_by_user_id
            WHERE t.kind = 'Invitation' AND t.used_utc IS NULL AND t.revoked_utc IS NULL
            """
            + (includeExpired ? string.Empty : " AND t.expires_utc > now()")
            + (contactId.HasValue ? " AND t.contact_id = @contactId" : string.Empty)
            + " ORDER BY t.created_utc DESC LIMIT 500";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PortalInvitationRow>(new CommandDefinition(
            sql, new { contactId }, cancellationToken: ct));
        return rows.ToList();
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];
}
