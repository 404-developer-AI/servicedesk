using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.M365;

/// Dapper store for the per-customer Microsoft 365 connect feature: the pending
/// consent states, the per-company tenant links, the synced mailbox mirror and
/// the per-company sync metadata. All time-sensitive values are server-stamped
/// (the caller passes <c>DateTime.UtcNow</c>); nothing trusts a client clock.
public interface IM365TenantStore
{
    // ---- consent states ----
    Task CreateConsentStateAsync(
        string state, Guid companyId, Guid? initiatedBy, DateTime expiresUtc, CancellationToken ct);

    /// Atomically consume a state: mark it used and return the row, but only if
    /// it is unknown-free, not yet consumed and not expired. Returns null
    /// otherwise — the callback rejects unknown / replayed / expired states.
    Task<M365ConsentStateRow?> ConsumeConsentStateAsync(
        string state, DateTime nowUtc, CancellationToken ct);

    Task PurgeExpiredConsentStatesAsync(DateTime nowUtc, CancellationToken ct);

    // ---- tenant links ----
    Task UpsertConnectedLinkAsync(
        Guid companyId, string tenantId, Guid? grantedBy, DateTime nowUtc, CancellationToken ct);

    Task SetLinkStatusAsync(
        Guid companyId, string status, string? error, DateTime? verifiedUtc, DateTime nowUtc, CancellationToken ct);

    Task<M365TenantLinkRow?> GetLinkAsync(Guid companyId, CancellationToken ct);

    /// Links the sync worker should attempt — everything except a hard
    /// disconnect (which deletes the row). Includes needs_reconsent/error so a
    /// recovered tenant heals itself on the next successful read.
    Task<IReadOnlyList<M365TenantLinkRow>> GetActiveLinksAsync(CancellationToken ct);

    /// Removes the link, its mailboxes and sync state for one company.
    Task DeleteLinkAsync(Guid companyId, CancellationToken ct);

    /// Link + sync meta joined, keyed by company, for the matching list.
    Task<IReadOnlyList<M365ConnectionRow>> GetConnectionsAsync(CancellationToken ct);

    /// Company display label (relation code + name) for the detail header.
    Task<M365CompanyLabel?> GetCompanyLabelAsync(Guid companyId, CancellationToken ct);

    // ---- mailboxes + sync state ----
    Task ReplaceMailboxesAsync(
        Guid companyId, IReadOnlyList<M365MailboxRow> rows, DateTime nowUtc, CancellationToken ct);

    Task<IReadOnlyList<M365MailboxRow>> GetMailboxesAsync(Guid companyId, CancellationToken ct);

    Task SaveSyncStateAsync(M365CompanySyncRow state, CancellationToken ct);

    Task<M365CompanySyncRow?> GetSyncStateAsync(Guid companyId, CancellationToken ct);
}

public sealed class M365TenantStore : IM365TenantStore
{
    private readonly NpgsqlDataSource _dataSource;

    public M365TenantStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    // ---- consent states -------------------------------------------------

    public async Task CreateConsentStateAsync(
        string state, Guid companyId, Guid? initiatedBy, DateTime expiresUtc, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO m365_consent_states (state, company_id, initiated_by, expires_utc)
            VALUES (@state, @companyId, @initiatedBy, @expiresUtc)
            """,
            new { state, companyId, initiatedBy, expiresUtc }, cancellationToken: ct));
    }

    public async Task<M365ConsentStateRow?> ConsumeConsentStateAsync(
        string state, DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // Single-statement compare-and-consume: the row flips to consumed and is
        // returned only when it was valid, so a replay finds nothing to update.
        return await conn.QuerySingleOrDefaultAsync<M365ConsentStateRow>(new CommandDefinition(
            """
            UPDATE m365_consent_states
               SET consumed_utc = @nowUtc
             WHERE state = @state
               AND consumed_utc IS NULL
               AND expires_utc > @nowUtc
            RETURNING company_id AS CompanyId, initiated_by AS InitiatedBy
            """,
            new { state, nowUtc }, cancellationToken: ct));
    }

    public async Task PurgeExpiredConsentStatesAsync(DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM m365_consent_states WHERE expires_utc < @nowUtc OR consumed_utc IS NOT NULL",
            new { nowUtc }, cancellationToken: ct));
    }

    // ---- tenant links ---------------------------------------------------

    public async Task UpsertConnectedLinkAsync(
        Guid companyId, string tenantId, Guid? grantedBy, DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO m365_tenant_links
                (company_id, tenant_id, status, consented_at, granted_by, last_verified_utc, last_error, updated_utc)
            VALUES
                (@companyId, @tenantId, 'connected', @nowUtc, @grantedBy, NULL, NULL, @nowUtc)
            ON CONFLICT (company_id) DO UPDATE SET
                tenant_id    = EXCLUDED.tenant_id,
                status       = 'connected',
                consented_at = EXCLUDED.consented_at,
                granted_by   = EXCLUDED.granted_by,
                last_error   = NULL,
                updated_utc  = EXCLUDED.updated_utc
            """,
            new { companyId, tenantId, grantedBy, nowUtc }, cancellationToken: ct));
    }

    public async Task SetLinkStatusAsync(
        Guid companyId, string status, string? error, DateTime? verifiedUtc, DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE m365_tenant_links
               SET status = @status,
                   last_error = @error,
                   last_verified_utc = COALESCE(@verifiedUtc, last_verified_utc),
                   updated_utc = @nowUtc
             WHERE company_id = @companyId
            """,
            new { companyId, status, error, verifiedUtc, nowUtc }, cancellationToken: ct));
    }

    public async Task<M365TenantLinkRow?> GetLinkAsync(Guid companyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<M365TenantLinkRow>(new CommandDefinition(
            LinkSelect + " WHERE company_id = @companyId",
            new { companyId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<M365TenantLinkRow>> GetActiveLinksAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<M365TenantLinkRow>(new CommandDefinition(
            LinkSelect + " ORDER BY updated_utc ASC", cancellationToken: ct));
        return rows.ToList();
    }

    public async Task DeleteLinkAsync(Guid companyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM m365_mailboxes WHERE company_id = @companyId", new { companyId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM m365_company_sync WHERE company_id = @companyId", new { companyId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM m365_tenant_links WHERE company_id = @companyId", new { companyId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<M365ConnectionRow>> GetConnectionsAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<M365ConnectionRow>(new CommandDefinition(
            """
            SELECT l.company_id        AS CompanyId,
                   l.tenant_id         AS TenantId,
                   l.status            AS Status,
                   l.consented_at      AS ConsentedAt,
                   s.last_checked_utc  AS LastCheckedUtc,
                   s.last_changed_utc  AS LastChangedUtc,
                   COALESCE(s.mailbox_count, 0) AS MailboxCount,
                   l.last_error        AS LastError
              FROM m365_tenant_links l
              LEFT JOIN m365_company_sync s ON s.company_id = l.company_id
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<M365CompanyLabel?> GetCompanyLabelAsync(Guid companyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<M365CompanyLabel>(new CommandDefinition(
            "SELECT adsolut_number AS Code, name AS Name FROM companies WHERE id = @companyId",
            new { companyId }, cancellationToken: ct));
    }

    // ---- mailboxes + sync state -----------------------------------------

    public async Task ReplaceMailboxesAsync(
        Guid companyId, IReadOnlyList<M365MailboxRow> rows, DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM m365_mailboxes WHERE company_id = @companyId", new { companyId }, tx, cancellationToken: ct));
        if (rows.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO m365_mailboxes
                    (company_id, object_id, mailbox_type, display_name, given_name,
                     surname, upn, mail, enabled, licenses, synced_utc)
                VALUES
                    (@CompanyId, @ObjectId, @MailboxType, @DisplayName, @GivenName,
                     @Surname, @Upn, @Mail, @Enabled, @Licenses, @SyncedUtc)
                """,
                rows.Select(r => new
                {
                    CompanyId = companyId,
                    r.ObjectId,
                    r.MailboxType,
                    r.DisplayName,
                    r.GivenName,
                    r.Surname,
                    r.Upn,
                    r.Mail,
                    r.Enabled,
                    r.Licenses,
                    SyncedUtc = nowUtc,
                }), tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<M365MailboxRow>> GetMailboxesAsync(Guid companyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<M365MailboxRow>(new CommandDefinition(
            """
            SELECT object_id    AS ObjectId,
                   mailbox_type AS MailboxType,
                   display_name AS DisplayName,
                   given_name   AS GivenName,
                   surname      AS Surname,
                   upn          AS Upn,
                   mail         AS Mail,
                   enabled      AS Enabled,
                   licenses     AS Licenses
              FROM m365_mailboxes
             WHERE company_id = @companyId
             ORDER BY mailbox_type ASC NULLS LAST, display_name ASC NULLS LAST
            """,
            new { companyId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task SaveSyncStateAsync(M365CompanySyncRow state, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO m365_company_sync
                (company_id, last_checked_utc, last_changed_utc, last_status,
                 last_error, mailbox_count, content_hash, duration_ms)
            VALUES
                (@CompanyId, @LastCheckedUtc, @LastChangedUtc, @LastStatus,
                 @LastError, @MailboxCount, @ContentHash, @DurationMs)
            ON CONFLICT (company_id) DO UPDATE SET
                last_checked_utc = EXCLUDED.last_checked_utc,
                last_changed_utc = EXCLUDED.last_changed_utc,
                last_status      = EXCLUDED.last_status,
                last_error       = EXCLUDED.last_error,
                mailbox_count    = EXCLUDED.mailbox_count,
                content_hash     = EXCLUDED.content_hash,
                duration_ms      = EXCLUDED.duration_ms
            """,
            state, cancellationToken: ct));
    }

    public async Task<M365CompanySyncRow?> GetSyncStateAsync(Guid companyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<M365CompanySyncRow>(new CommandDefinition(
            """
            SELECT company_id       AS CompanyId,
                   last_checked_utc AS LastCheckedUtc,
                   last_changed_utc AS LastChangedUtc,
                   last_status      AS LastStatus,
                   last_error       AS LastError,
                   mailbox_count    AS MailboxCount,
                   content_hash     AS ContentHash,
                   duration_ms      AS DurationMs
              FROM m365_company_sync
             WHERE company_id = @companyId
            """,
            new { companyId }, cancellationToken: ct));
    }

    private const string LinkSelect = """
        SELECT company_id        AS CompanyId,
               tenant_id         AS TenantId,
               status            AS Status,
               consented_at      AS ConsentedAt,
               granted_by        AS GrantedBy,
               last_verified_utc AS LastVerifiedUtc,
               last_error        AS LastError
          FROM m365_tenant_links
        """;
}
