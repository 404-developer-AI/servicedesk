using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Sophos;

/// Dapper store for the Sophos Central spam-filter snapshot: the partner tenant
/// list (with the resolved company match), the protected mailbox addresses of
/// the M365-matched tenants, and the singleton sync metadata. All time-sensitive
/// values are server-stamped (the caller passes <c>DateTime.UtcNow</c>).
public interface ISophosStore
{
    /// Replace the partner tenant snapshot: upsert every tenant, drop the ones
    /// that left the partner (cascading their mailboxes), then resolve each
    /// tenant's company_id against companies.adsolut_number.
    Task ReplaceTenantsAsync(
        IReadOnlyList<SophosTenantRow> tenants, DateTime nowUtc, CancellationToken ct);

    /// Tenants we should pull mailboxes for: queryable (active + api host) AND
    /// either matched to a Microsoft 365-connected company by relation code OR
    /// manually linked to a Microsoft 365-connected company.
    Task<IReadOnlyList<SophosMailboxTarget>> GetMailboxTargetsAsync(CancellationToken ct);

    /// Replace one tenant's protected mailbox set and stamp its count.
    Task ReplaceMailboxesForTenantAsync(
        string tenantId, IReadOnlyList<SophosMailboxRow> rows, DateTime nowUtc, CancellationToken ct);

    /// Clear the protected mailboxes (and zero the count) for tenants that are
    /// no longer mailbox targets — e.g. a company disconnected from M365.
    Task ClearMailboxesExceptAsync(IReadOnlyList<string> keepTenantIds, CancellationToken ct);

    Task<IReadOnlyList<SophosTenantListRow>> GetTenantsAsync(CancellationToken ct);

    /// Every manual tenant→company rollup link, joined with the parent company
    /// name, for the settings tenant list.
    Task<IReadOnlyList<SophosTenantLinkRow>> GetTenantLinksAsync(CancellationToken ct);

    /// The Microsoft 365-connected companies an admin may roll a tenant into.
    Task<IReadOnlyList<SophosLinkableCompany>> GetLinkableCompaniesAsync(CancellationToken ct);

    /// Add a manual tenant→company rollup link (idempotent). Returns false when
    /// the tenant or the company does not exist (or the company is not
    /// M365-connected); true when the link is present afterwards.
    Task<bool> AddTenantLinkAsync(string tenantId, Guid companyId, Guid? createdBy, CancellationToken ct);

    /// Remove a manual tenant→company rollup link (idempotent).
    Task RemoveTenantLinkAsync(string tenantId, Guid companyId, CancellationToken ct);

    Task<SophosSyncStateRow?> GetSyncStateAsync(CancellationToken ct);
    Task SaveSyncStateAsync(SophosSyncStateRow state, CancellationToken ct);

    /// For the Microsoft 365 company view: whether the company has a matched or
    /// linked Sophos tenant at all, and the set of protected mailbox addresses
    /// across its automatically-matched AND manually-linked tenant(s)
    /// (case-insensitive).
    Task<(bool TenantMatched, HashSet<string> Emails)> GetCompanySpamFilterAsync(
        Guid companyId, CancellationToken ct);
}

public sealed class SophosStore : ISophosStore
{
    private readonly NpgsqlDataSource _dataSource;

    public SophosStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task ReplaceTenantsAsync(
        IReadOnlyList<SophosTenantRow> tenants, DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (tenants.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO sophos_tenants
                    (tenant_id, name, show_as, company_code, api_host, status,
                     data_region, billing_type, updated_utc)
                VALUES
                    (@TenantId, @Name, @ShowAs, @CompanyCode, @ApiHost, @Status,
                     @DataRegion, @BillingType, @NowUtc)
                ON CONFLICT (tenant_id) DO UPDATE SET
                    name         = EXCLUDED.name,
                    show_as      = EXCLUDED.show_as,
                    company_code = EXCLUDED.company_code,
                    api_host     = EXCLUDED.api_host,
                    status       = EXCLUDED.status,
                    data_region  = EXCLUDED.data_region,
                    billing_type = EXCLUDED.billing_type,
                    updated_utc  = EXCLUDED.updated_utc
                """,
                tenants.Select(t => new
                {
                    t.TenantId,
                    t.Name,
                    t.ShowAs,
                    t.CompanyCode,
                    t.ApiHost,
                    t.Status,
                    t.DataRegion,
                    t.BillingType,
                    NowUtc = nowUtc,
                }), tx, cancellationToken: ct));
        }

        // Drop tenants that left the partner snapshot (cascades their mailboxes).
        var keep = tenants.Select(t => t.TenantId).ToArray();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM sophos_tenants WHERE NOT (tenant_id = ANY(@keep))",
            new { keep }, tx, cancellationToken: ct));

        // Resolve the company match against the relation code (adsolut_number).
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE sophos_tenants t
               SET company_id = NULL
             WHERE company_id IS NOT NULL
               AND (t.company_code IS NULL
                    OR NOT EXISTS (SELECT 1 FROM companies c
                                    WHERE c.adsolut_number = t.company_code));

            UPDATE sophos_tenants t
               SET company_id = c.id
              FROM companies c
             WHERE c.adsolut_number = t.company_code
               AND (t.company_id IS DISTINCT FROM c.id);
            """, transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<SophosMailboxTarget>> GetMailboxTargetsAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // Queryable tenants whose company (automatic match) is M365-connected,
        // UNION'd with queryable tenants manually linked to an M365-connected
        // company (the daughter rollup). DISTINCT folds a tenant that is both.
        var rows = await conn.QueryAsync<SophosMailboxTarget>(new CommandDefinition(
            """
            SELECT DISTINCT t.tenant_id AS TenantId,
                            t.api_host  AS ApiHost
              FROM sophos_tenants t
             WHERE t.api_host IS NOT NULL
               AND lower(t.status) = 'active'
               AND (
                     EXISTS (SELECT 1 FROM m365_tenant_links l WHERE l.company_id = t.company_id)
                  OR EXISTS (SELECT 1 FROM sophos_tenant_links sl
                              JOIN m365_tenant_links l ON l.company_id = sl.company_id
                             WHERE sl.tenant_id = t.tenant_id)
                   )
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task ReplaceMailboxesForTenantAsync(
        string tenantId, IReadOnlyList<SophosMailboxRow> rows, DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM sophos_mailboxes WHERE tenant_id = @tenantId",
            new { tenantId }, tx, cancellationToken: ct));

        if (rows.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO sophos_mailboxes (tenant_id, email, display_name, mailbox_type, synced_utc)
                VALUES (@TenantId, @Email, @DisplayName, @MailboxType, @SyncedUtc)
                ON CONFLICT (tenant_id, email) DO UPDATE SET
                    display_name = EXCLUDED.display_name,
                    mailbox_type = EXCLUDED.mailbox_type,
                    synced_utc   = EXCLUDED.synced_utc
                """,
                rows.Select(r => new
                {
                    TenantId = tenantId,
                    r.Email,
                    r.DisplayName,
                    r.MailboxType,
                    SyncedUtc = nowUtc,
                }), tx, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sophos_tenants SET mailbox_count = @count, last_synced_utc = @nowUtc WHERE tenant_id = @tenantId",
            new { count = rows.Count, nowUtc, tenantId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    public async Task ClearMailboxesExceptAsync(IReadOnlyList<string> keepTenantIds, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var keep = keepTenantIds.ToArray();
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM sophos_mailboxes WHERE NOT (tenant_id = ANY(@keep))",
            new { keep }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE sophos_tenants SET mailbox_count = 0, last_synced_utc = NULL WHERE NOT (tenant_id = ANY(@keep)) AND mailbox_count <> 0",
            new { keep }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<SophosTenantListRow>> GetTenantsAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SophosTenantListRow>(new CommandDefinition(
            """
            SELECT t.tenant_id     AS TenantId,
                   t.name          AS Name,
                   t.show_as       AS ShowAs,
                   t.company_code  AS CompanyCode,
                   t.company_id    AS CompanyId,
                   c.name          AS CompanyName,
                   t.status        AS Status,
                   (l.company_id IS NOT NULL) AS M365Connected,
                   t.mailbox_count AS MailboxCount,
                   t.last_synced_utc AS LastSyncedUtc
              FROM sophos_tenants t
              LEFT JOIN companies c ON c.id = t.company_id
              LEFT JOIN m365_tenant_links l ON l.company_id = t.company_id
             ORDER BY (t.company_id IS NULL), t.show_as ASC NULLS LAST
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<SophosTenantLinkRow>> GetTenantLinksAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SophosTenantLinkRow>(new CommandDefinition(
            """
            SELECT sl.tenant_id  AS TenantId,
                   sl.company_id AS CompanyId,
                   c.name        AS CompanyName
              FROM sophos_tenant_links sl
              JOIN companies c ON c.id = sl.company_id
             ORDER BY c.name ASC NULLS LAST
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<SophosLinkableCompany>> GetLinkableCompaniesAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SophosLinkableCompany>(new CommandDefinition(
            """
            SELECT c.id             AS CompanyId,
                   c.name           AS Name,
                   c.adsolut_number AS Code
              FROM companies c
              JOIN m365_tenant_links l ON l.company_id = c.id
             ORDER BY c.name ASC NULLS LAST
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<bool> AddTenantLinkAsync(string tenantId, Guid companyId, Guid? createdBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Guard: tenant must exist, and the company must be M365-connected (the
        // only valid rollup target). A failed guard returns false so the API
        // answers 404/422 rather than silently inserting a dangling link.
        var tenantExists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM sophos_tenants WHERE tenant_id = @tenantId)",
            new { tenantId }, cancellationToken: ct));
        if (!tenantExists) return false;

        var companyOk = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM m365_tenant_links WHERE company_id = @companyId)",
            new { companyId }, cancellationToken: ct));
        if (!companyOk) return false;

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO sophos_tenant_links (tenant_id, company_id, created_by)
            VALUES (@tenantId, @companyId, @createdBy)
            ON CONFLICT (tenant_id, company_id) DO NOTHING
            """, new { tenantId, companyId, createdBy }, cancellationToken: ct));
        return true;
    }

    public async Task RemoveTenantLinkAsync(string tenantId, Guid companyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM sophos_tenant_links WHERE tenant_id = @tenantId AND company_id = @companyId",
            new { tenantId, companyId }, cancellationToken: ct));
    }

    public async Task<SophosSyncStateRow?> GetSyncStateAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<SophosSyncStateRow>(new CommandDefinition(
            """
            SELECT last_checked_utc AS LastCheckedUtc,
                   last_changed_utc AS LastChangedUtc,
                   last_status      AS LastStatus,
                   last_error       AS LastError,
                   tenant_count     AS TenantCount,
                   mailbox_count    AS MailboxCount,
                   content_hash     AS ContentHash,
                   duration_ms      AS DurationMs
              FROM sophos_sync_state
             WHERE id = 1
            """, cancellationToken: ct));
    }

    public async Task SaveSyncStateAsync(SophosSyncStateRow state, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO sophos_sync_state
                (id, last_checked_utc, last_changed_utc, last_status, last_error,
                 tenant_count, mailbox_count, content_hash, duration_ms)
            VALUES
                (1, @LastCheckedUtc, @LastChangedUtc, @LastStatus, @LastError,
                 @TenantCount, @MailboxCount, @ContentHash, @DurationMs)
            ON CONFLICT (id) DO UPDATE SET
                last_checked_utc = EXCLUDED.last_checked_utc,
                last_changed_utc = EXCLUDED.last_changed_utc,
                last_status      = EXCLUDED.last_status,
                last_error       = EXCLUDED.last_error,
                tenant_count     = EXCLUDED.tenant_count,
                mailbox_count    = EXCLUDED.mailbox_count,
                content_hash     = EXCLUDED.content_hash,
                duration_ms      = EXCLUDED.duration_ms
            """,
            state, cancellationToken: ct));
    }

    public async Task<(bool TenantMatched, HashSet<string> Emails)> GetCompanySpamFilterAsync(
        Guid companyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var tenantMatched = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM sophos_tenants WHERE company_id = @companyId)
                OR EXISTS (SELECT 1 FROM sophos_tenant_links WHERE company_id = @companyId)
            """, new { companyId }, cancellationToken: ct));

        // Union the protected addresses of the company's automatically-matched
        // tenant(s) AND its manually-linked tenant(s) — the daughter rollup.
        var emails = await conn.QueryAsync<string>(new CommandDefinition(
            """
            SELECT DISTINCT m.email::text
              FROM sophos_mailboxes m
              JOIN sophos_tenants t ON t.tenant_id = m.tenant_id
             WHERE t.company_id = @companyId
                OR t.tenant_id IN (SELECT tenant_id FROM sophos_tenant_links WHERE company_id = @companyId)
            """, new { companyId }, cancellationToken: ct));

        var set = new HashSet<string>(
            emails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()),
            StringComparer.OrdinalIgnoreCase);
        return (tenantMatched, set);
    }
}
