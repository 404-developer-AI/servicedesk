using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Veeam;

/// Dapper store for the Veeam backup snapshot: which M365-connected companies
/// exist in VSPC, the per-mailbox Exchange / OneDrive backup status (keyed by
/// the normalized display name — the only identity VSPC exposes), and the
/// singleton sync metadata. All time-sensitive values are server-stamped (the
/// caller passes <c>DateTime.UtcNow</c>).
public interface IVeeamStore
{
    /// M365-connected companies with a relation code (companies.adsolut_number),
    /// which is matched against the VSPC company code (ownerCredentials.userName).
    Task<IReadOnlyList<VeeamSyncTarget>> GetSyncTargetsAsync(CancellationToken ct);

    /// Replace one company's backup snapshot: record the VSPC link + object count
    /// and rewrite its per-mailbox rows.
    Task ReplaceCompanyBackupsAsync(
        Guid companyId, string vspcUid, string? vspcName,
        IReadOnlyList<VeeamBackupRow> rows, DateTime nowUtc, CancellationToken ct);

    /// Drop the Veeam link + backups for companies no longer matched in VSPC
    /// (e.g. a company disconnected from M365, or left the console).
    Task ClearCompaniesExceptAsync(IReadOnlyList<Guid> keepCompanyIds, CancellationToken ct);

    /// For the M365 company view: whether the company is known in Veeam (a VSPC
    /// company matched) and, if so, its per-mailbox backup rows keyed by the
    /// normalized display name.
    Task<(bool Known, Dictionary<string, VeeamBackupRow> ByName)> GetCompanyBackupsAsync(
        Guid companyId, CancellationToken ct);

    Task<IReadOnlyList<VeeamCompanyListRow>> GetCompaniesAsync(CancellationToken ct);

    /// Replace the snapshot of every VSPC company seen this sync (the pick list
    /// for the link UI), including the ones that match no Servicedesk company.
    Task ReplaceVspcCompaniesAsync(
        IReadOnlyList<VeeamCompanyRow> companies, DateTime nowUtc, CancellationToken ct);

    /// The VSPC company snapshot joined with its automatic company match, for
    /// the link picker.
    Task<IReadOnlyList<VeeamVspcCompanyRow>> GetVspcCompaniesAsync(CancellationToken ct);

    /// Every manual VSPC-company→parent rollup link, joined with the parent
    /// company name, for the settings UI and the sync's source map.
    Task<IReadOnlyList<VeeamCompanyLinkRow>> GetCompanyLinksAsync(CancellationToken ct);

    /// The Microsoft 365-connected companies an admin may roll a VSPC company
    /// into.
    Task<IReadOnlyList<VeeamLinkableCompany>> GetLinkableCompaniesAsync(CancellationToken ct);

    /// Add a manual VSPC-company→parent rollup link (idempotent). The VSPC name
    /// and code are snapshotted onto the link for display resilience. Returns
    /// false when the parent is not M365-connected or the VSPC company is not in
    /// the snapshot; true when the link is present afterwards.
    Task<bool> AddCompanyLinkAsync(
        Guid parentCompanyId, string vspcCompanyUid, Guid? createdBy, CancellationToken ct);

    /// Remove a manual VSPC-company→parent rollup link (idempotent).
    Task RemoveCompanyLinkAsync(Guid parentCompanyId, string vspcCompanyUid, CancellationToken ct);

    Task<VeeamSyncStateRow?> GetSyncStateAsync(CancellationToken ct);
    Task SaveSyncStateAsync(VeeamSyncStateRow state, CancellationToken ct);
}

public sealed class VeeamStore : IVeeamStore
{
    private readonly NpgsqlDataSource _dataSource;

    public VeeamStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<VeeamSyncTarget>> GetSyncTargetsAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<VeeamSyncTarget>(new CommandDefinition(
            """
            SELECT c.id              AS CompanyId,
                   c.adsolut_number  AS Code
              FROM companies c
              JOIN m365_tenant_links l ON l.company_id = c.id
             WHERE c.adsolut_number IS NOT NULL
               AND length(trim(c.adsolut_number)) > 0
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task ReplaceCompanyBackupsAsync(
        Guid companyId, string vspcUid, string? vspcName,
        IReadOnlyList<VeeamBackupRow> rows, DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO veeam_companies
                (company_id, vspc_company_uid, vspc_company_name, object_count, last_synced_utc)
            VALUES
                (@CompanyId, @VspcUid, @VspcName, @ObjectCount, @NowUtc)
            ON CONFLICT (company_id) DO UPDATE SET
                vspc_company_uid  = EXCLUDED.vspc_company_uid,
                vspc_company_name = EXCLUDED.vspc_company_name,
                object_count      = EXCLUDED.object_count,
                last_synced_utc   = EXCLUDED.last_synced_utc
            """,
            new
            {
                CompanyId = companyId,
                VspcUid = vspcUid,
                VspcName = vspcName,
                ObjectCount = rows.Count,
                NowUtc = nowUtc,
            }, tx, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM veeam_backups WHERE company_id = @companyId",
            new { companyId }, tx, cancellationToken: ct));

        if (rows.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO veeam_backups
                    (company_id, match_key, display_name,
                     exchange_protected, exchange_restore_points, exchange_last_backup_utc,
                     onedrive_protected, onedrive_restore_points, onedrive_last_backup_utc,
                     synced_utc)
                VALUES
                    (@CompanyId, @MatchKey, @DisplayName,
                     @ExchangeProtected, @ExchangeRestorePoints, @ExchangeLastBackupUtc,
                     @OneDriveProtected, @OneDriveRestorePoints, @OneDriveLastBackupUtc,
                     @SyncedUtc)
                ON CONFLICT (company_id, match_key) DO UPDATE SET
                    display_name             = EXCLUDED.display_name,
                    exchange_protected       = EXCLUDED.exchange_protected,
                    exchange_restore_points  = EXCLUDED.exchange_restore_points,
                    exchange_last_backup_utc = EXCLUDED.exchange_last_backup_utc,
                    onedrive_protected       = EXCLUDED.onedrive_protected,
                    onedrive_restore_points  = EXCLUDED.onedrive_restore_points,
                    onedrive_last_backup_utc = EXCLUDED.onedrive_last_backup_utc,
                    synced_utc               = EXCLUDED.synced_utc
                """,
                rows.Select(r => new
                {
                    CompanyId = companyId,
                    r.MatchKey,
                    r.DisplayName,
                    r.ExchangeProtected,
                    r.ExchangeRestorePoints,
                    r.ExchangeLastBackupUtc,
                    r.OneDriveProtected,
                    r.OneDriveRestorePoints,
                    r.OneDriveLastBackupUtc,
                    SyncedUtc = nowUtc,
                }), tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task ClearCompaniesExceptAsync(IReadOnlyList<Guid> keepCompanyIds, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var keep = keepCompanyIds.ToArray();
        // veeam_backups cascades on the veeam_companies FK, so dropping the
        // company row also clears its mailbox rows.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM veeam_companies WHERE NOT (company_id = ANY(@keep))",
            new { keep }, cancellationToken: ct));
    }

    public async Task<(bool Known, Dictionary<string, VeeamBackupRow> ByName)> GetCompanyBackupsAsync(
        Guid companyId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var known = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM veeam_companies WHERE company_id = @companyId AND vspc_company_uid IS NOT NULL)",
            new { companyId }, cancellationToken: ct));

        var rows = await conn.QueryAsync<VeeamBackupRow>(new CommandDefinition(
            """
            SELECT match_key                AS MatchKey,
                   display_name             AS DisplayName,
                   exchange_protected       AS ExchangeProtected,
                   exchange_restore_points  AS ExchangeRestorePoints,
                   exchange_last_backup_utc AS ExchangeLastBackupUtc,
                   onedrive_protected       AS OneDriveProtected,
                   onedrive_restore_points  AS OneDriveRestorePoints,
                   onedrive_last_backup_utc AS OneDriveLastBackupUtc
              FROM veeam_backups
             WHERE company_id = @companyId
            """, new { companyId }, cancellationToken: ct));

        var map = new Dictionary<string, VeeamBackupRow>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (!string.IsNullOrEmpty(r.MatchKey))
                map[r.MatchKey] = r;
        }
        return (known, map);
    }

    public async Task<IReadOnlyList<VeeamCompanyListRow>> GetCompaniesAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<VeeamCompanyListRow>(new CommandDefinition(
            """
            SELECT v.company_id        AS CompanyId,
                   c.adsolut_number    AS CompanyCode,
                   c.name              AS CompanyName,
                   v.vspc_company_name AS VspcCompanyName,
                   v.object_count      AS ObjectCount,
                   v.last_synced_utc   AS LastSyncedUtc
              FROM veeam_companies v
              JOIN companies c ON c.id = v.company_id
             ORDER BY c.name ASC NULLS LAST
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task ReplaceVspcCompaniesAsync(
        IReadOnlyList<VeeamCompanyRow> companies, DateTime nowUtc, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (companies.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO veeam_vspc_companies (vspc_company_uid, code, name, updated_utc)
                VALUES (@InstanceUid, @Code, @Name, @NowUtc)
                ON CONFLICT (vspc_company_uid) DO UPDATE SET
                    code        = EXCLUDED.code,
                    name        = EXCLUDED.name,
                    updated_utc = EXCLUDED.updated_utc
                """,
                companies
                    .Where(c => !string.IsNullOrWhiteSpace(c.InstanceUid))
                    .Select(c => new { c.InstanceUid, c.Code, c.Name, NowUtc = nowUtc }),
                tx, cancellationToken: ct));
        }

        var keep = companies
            .Where(c => !string.IsNullOrWhiteSpace(c.InstanceUid))
            .Select(c => c.InstanceUid).ToArray();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM veeam_vspc_companies WHERE NOT (vspc_company_uid = ANY(@keep))",
            new { keep }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<VeeamVspcCompanyRow>> GetVspcCompaniesAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<VeeamVspcCompanyRow>(new CommandDefinition(
            """
            SELECT v.vspc_company_uid AS VspcCompanyUid,
                   v.code             AS Code,
                   v.name             AS Name,
                   c.id               AS MatchedCompanyId,
                   c.name             AS MatchedCompanyName
              FROM veeam_vspc_companies v
              LEFT JOIN companies c
                     ON v.code IS NOT NULL
                    AND c.adsolut_number = v.code
             ORDER BY v.name ASC NULLS LAST
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<VeeamCompanyLinkRow>> GetCompanyLinksAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<VeeamCompanyLinkRow>(new CommandDefinition(
            """
            SELECT k.parent_company_id  AS ParentCompanyId,
                   c.name               AS ParentCompanyName,
                   k.vspc_company_uid   AS VspcCompanyUid,
                   COALESCE(v.name, k.vspc_company_name) AS VspcCompanyName,
                   COALESCE(v.code, k.vspc_company_code) AS VspcCompanyCode
              FROM veeam_company_links k
              JOIN companies c ON c.id = k.parent_company_id
              LEFT JOIN veeam_vspc_companies v ON v.vspc_company_uid = k.vspc_company_uid
             ORDER BY c.name ASC NULLS LAST
            """, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<VeeamLinkableCompany>> GetLinkableCompaniesAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<VeeamLinkableCompany>(new CommandDefinition(
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

    public async Task<bool> AddCompanyLinkAsync(
        Guid parentCompanyId, string vspcCompanyUid, Guid? createdBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var parentOk = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM m365_tenant_links WHERE company_id = @parentCompanyId)",
            new { parentCompanyId }, cancellationToken: ct));
        if (!parentOk) return false;

        var vspc = await conn.QuerySingleOrDefaultAsync<VeeamVspcCompanyRow>(new CommandDefinition(
            "SELECT name AS Name, code AS Code FROM veeam_vspc_companies WHERE vspc_company_uid = @vspcCompanyUid",
            new { vspcCompanyUid }, cancellationToken: ct));
        if (vspc is null) return false;

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO veeam_company_links
                (parent_company_id, vspc_company_uid, vspc_company_name, vspc_company_code, created_by)
            VALUES (@parentCompanyId, @vspcCompanyUid, @Name, @Code, @createdBy)
            ON CONFLICT (parent_company_id, vspc_company_uid) DO UPDATE SET
                vspc_company_name = EXCLUDED.vspc_company_name,
                vspc_company_code = EXCLUDED.vspc_company_code
            """,
            new { parentCompanyId, vspcCompanyUid, vspc.Name, vspc.Code, createdBy },
            cancellationToken: ct));
        return true;
    }

    public async Task RemoveCompanyLinkAsync(Guid parentCompanyId, string vspcCompanyUid, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM veeam_company_links WHERE parent_company_id = @parentCompanyId AND vspc_company_uid = @vspcCompanyUid",
            new { parentCompanyId, vspcCompanyUid }, cancellationToken: ct));
    }

    public async Task<VeeamSyncStateRow?> GetSyncStateAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<VeeamSyncStateRow>(new CommandDefinition(
            """
            SELECT last_checked_utc AS LastCheckedUtc,
                   last_changed_utc AS LastChangedUtc,
                   last_status      AS LastStatus,
                   last_error       AS LastError,
                   company_count    AS CompanyCount,
                   object_count     AS ObjectCount,
                   content_hash     AS ContentHash,
                   duration_ms      AS DurationMs
              FROM veeam_sync_state
             WHERE id = 1
            """, cancellationToken: ct));
    }

    public async Task SaveSyncStateAsync(VeeamSyncStateRow state, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO veeam_sync_state
                (id, last_checked_utc, last_changed_utc, last_status, last_error,
                 company_count, object_count, content_hash, duration_ms)
            VALUES
                (1, @LastCheckedUtc, @LastChangedUtc, @LastStatus, @LastError,
                 @CompanyCount, @ObjectCount, @ContentHash, @DurationMs)
            ON CONFLICT (id) DO UPDATE SET
                last_checked_utc = EXCLUDED.last_checked_utc,
                last_changed_utc = EXCLUDED.last_changed_utc,
                last_status      = EXCLUDED.last_status,
                last_error       = EXCLUDED.last_error,
                company_count    = EXCLUDED.company_count,
                object_count     = EXCLUDED.object_count,
                content_hash     = EXCLUDED.content_hash,
                duration_ms      = EXCLUDED.duration_ms
            """,
            state, cancellationToken: ct));
    }
}
