using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Singleton sync-state row for the CatalogueProducts mirror. Sealed class with
/// get/set + AS-PascalCase aliases per the project's Dapper conventions.
public sealed class AdsolutCatalogueProductSyncState
{
    public DateTime? LastFullSyncUtc { get; set; }
    public DateTime? LastDeltaSyncUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public int ProductsSeen { get; set; }
    public int ProductsUpserted { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}

/// Row for the work-hours article manager (Settings → Timesheet → manage which
/// catalogue products count as billable work hours).
public sealed class AdsolutCatalogueProductRow
{
    public Guid Id { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
    public bool ServiceProduct { get; set; }
    public bool IsActive { get; set; }
    public bool Blocked { get; set; }
    public bool EndOfSeries { get; set; }
    public bool CountsAsWorkHours { get; set; }
    public DateTime? WorkHoursUpdatedUtc { get; set; }
    public string? WorkHoursUpdatedByEmail { get; set; }
    public DateTime? AdsolutLastModified { get; set; }
    public DateTime SyncedUtc { get; set; }
}

public sealed record AdsolutCatalogueProductListResult(
    IReadOnlyList<AdsolutCatalogueProductRow> Items,
    int Total,
    int Page,
    int PageSize);

public interface IAdsolutCatalogueProductRepository
{
    /// Upsert one catalogue product. The admin-owned work-hours flag (and its
    /// audit columns) is NEVER overwritten by a sync — only the Adsolut-sourced
    /// fields are refreshed on conflict.
    Task UpsertAsync(AdsolutCatalogueProduct product, CancellationToken ct = default);

    Task<AdsolutCatalogueProductSyncState?> GetSyncStateAsync(CancellationToken ct = default);
    Task SaveSyncStateAsync(AdsolutCatalogueProductSyncState state, CancellationToken ct = default);

    Task<int> GetCountAsync(CancellationToken ct = default);

    /// Count of products currently flagged as counting toward work hours.
    Task<int> GetWorkHoursCountAsync(CancellationToken ct = default);

    /// Paged list for the work-hours article manager. <paramref name="search"/>
    /// matches code + name. <paramref name="sort"/> is whitelisted
    /// (code/name/active/workhours). <paramref name="activeOnly"/> hides inactive
    /// products. <paramref name="workHours"/> filters on the flag: "yes"/"no"
    /// (anything else = all).
    Task<AdsolutCatalogueProductListResult> ListAsync(
        string? search, int page, int pageSize, string? sort, string? dir,
        bool activeOnly, string? workHours, CancellationToken ct = default);

    /// Set (or clear) the "counts as work hours" flag for one product. Records
    /// who/when. Returns false when the product id is unknown.
    Task<bool> SetWorkHoursAsync(Guid id, bool countsAsWorkHours, Guid actorUserId, CancellationToken ct = default);
}

public sealed class AdsolutCatalogueProductRepository : IAdsolutCatalogueProductRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public AdsolutCatalogueProductRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task UpsertAsync(AdsolutCatalogueProduct p, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_catalogue_products (
                id, code, name, service_product, is_active, blocked, end_of_series,
                adsolut_created_utc, adsolut_last_modified, synced_utc
            ) VALUES (
                @Id, @Code, @Name, @ServiceProduct, @IsActive, @Blocked, @EndOfSeries,
                @AdsolutCreatedUtc, @AdsolutLastModified, now()
            )
            ON CONFLICT (id) DO UPDATE SET
                code                  = EXCLUDED.code,
                name                  = EXCLUDED.name,
                service_product       = EXCLUDED.service_product,
                is_active             = EXCLUDED.is_active,
                blocked               = EXCLUDED.blocked,
                end_of_series         = EXCLUDED.end_of_series,
                adsolut_created_utc   = EXCLUDED.adsolut_created_utc,
                adsolut_last_modified = EXCLUDED.adsolut_last_modified,
                synced_utc            = now()
                -- counts_as_work_hours + work_hours_* are admin-owned and
                -- deliberately left untouched here.
            """,
            new
            {
                p.Id,
                p.Code,
                p.Name,
                p.ServiceProduct,
                p.IsActive,
                p.Blocked,
                p.EndOfSeries,
                AdsolutCreatedUtc = p.AdsolutCreatedUtc?.UtcDateTime,
                AdsolutLastModified = p.AdsolutLastModified?.UtcDateTime,
            },
            cancellationToken: ct));
    }

    public async Task<AdsolutCatalogueProductSyncState?> GetSyncStateAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AdsolutCatalogueProductSyncState>(new CommandDefinition(
            """
            SELECT
                last_full_sync_utc  AS LastFullSyncUtc,
                last_delta_sync_utc AS LastDeltaSyncUtc,
                last_error          AS LastError,
                last_error_utc      AS LastErrorUtc,
                products_seen       AS ProductsSeen,
                products_upserted   AS ProductsUpserted,
                updated_utc         AS UpdatedUtc
            FROM adsolut_catalogue_product_sync_state
            WHERE id = 1
            """,
            cancellationToken: ct));
    }

    public async Task SaveSyncStateAsync(AdsolutCatalogueProductSyncState state, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_catalogue_product_sync_state (
                id, last_full_sync_utc, last_delta_sync_utc, last_error, last_error_utc,
                products_seen, products_upserted, updated_utc
            ) VALUES (
                1, @LastFullSyncUtc, @LastDeltaSyncUtc, @LastError, @LastErrorUtc,
                @ProductsSeen, @ProductsUpserted, now()
            )
            ON CONFLICT (id) DO UPDATE SET
                last_full_sync_utc  = EXCLUDED.last_full_sync_utc,
                last_delta_sync_utc = EXCLUDED.last_delta_sync_utc,
                last_error          = EXCLUDED.last_error,
                last_error_utc      = EXCLUDED.last_error_utc,
                products_seen       = EXCLUDED.products_seen,
                products_upserted   = EXCLUDED.products_upserted,
                updated_utc         = now()
            """,
            new
            {
                state.LastFullSyncUtc,
                state.LastDeltaSyncUtc,
                state.LastError,
                state.LastErrorUtc,
                state.ProductsSeen,
                state.ProductsUpserted,
            },
            cancellationToken: ct));
    }

    public async Task<int> GetCountAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*)::int FROM adsolut_catalogue_products",
            cancellationToken: ct));
    }

    public async Task<int> GetWorkHoursCountAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*)::int FROM adsolut_catalogue_products WHERE counts_as_work_hours = TRUE",
            cancellationToken: ct));
    }

    private const string Columns = """
        p.id                    AS Id,
        p.code                  AS Code,
        p.name                  AS Name,
        p.service_product       AS ServiceProduct,
        p.is_active             AS IsActive,
        p.blocked               AS Blocked,
        p.end_of_series         AS EndOfSeries,
        p.counts_as_work_hours  AS CountsAsWorkHours,
        p.work_hours_updated_utc AS WorkHoursUpdatedUtc,
        u.email                 AS WorkHoursUpdatedByEmail,
        p.adsolut_last_modified AS AdsolutLastModified,
        p.synced_utc            AS SyncedUtc
        """;

    public async Task<AdsolutCatalogueProductListResult> ListAsync(
        string? search, int page, int pageSize, string? sort, string? dir,
        bool activeOnly, string? workHours, CancellationToken ct = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (safePage - 1) * safePageSize;
        var term = (search ?? string.Empty).Trim();
        var hasSearch = term.Length > 0;
        var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

        // Whitelisted work-hours filter → -1 (all) / 1 (yes) / 0 (no).
        var workHoursFilter = (workHours ?? "all").Trim().ToLowerInvariant() switch
        {
            "yes" => 1,
            "no" => 0,
            _ => -1,
        };

        var sortExpr = (sort ?? "code").Trim().ToLowerInvariant() switch
        {
            "name" => "p.name",
            "active" => "p.is_active",
            "workhours" => "p.counts_as_work_hours",
            _ => "p.code",
        };
        var dirSql = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

        const string whereClause = """
            WHERE (@ActiveOnly = FALSE OR p.is_active = TRUE)
              AND (@WorkHoursFilter = -1
                   OR (@WorkHoursFilter = 1 AND p.counts_as_work_hours = TRUE)
                   OR (@WorkHoursFilter = 0 AND p.counts_as_work_hours = FALSE))
              AND (@HasSearch = FALSE
                   OR p.code ILIKE @Like
                   OR p.name ILIKE @Like)
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*)::int FROM adsolut_catalogue_products p {whereClause}",
            new { ActiveOnly = activeOnly, WorkHoursFilter = workHoursFilter, HasSearch = hasSearch, Like = like },
            cancellationToken: ct));

        var items = await conn.QueryAsync<AdsolutCatalogueProductRow>(new CommandDefinition(
            $"""
            SELECT {Columns}
            FROM adsolut_catalogue_products p
            LEFT JOIN users u ON u.id = p.work_hours_updated_by
            {whereClause}
            ORDER BY {sortExpr} {dirSql} NULLS LAST, p.code ASC NULLS LAST
            LIMIT @Limit OFFSET @Offset
            """,
            new { ActiveOnly = activeOnly, WorkHoursFilter = workHoursFilter, HasSearch = hasSearch, Like = like, Limit = safePageSize, Offset = offset },
            cancellationToken: ct));

        return new AdsolutCatalogueProductListResult(items.ToList(), total, safePage, safePageSize);
    }

    public async Task<bool> SetWorkHoursAsync(Guid id, bool countsAsWorkHours, Guid actorUserId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE adsolut_catalogue_products
            SET counts_as_work_hours   = @CountsAsWorkHours,
                work_hours_updated_utc = now(),
                work_hours_updated_by  = @ActorUserId
            WHERE id = @Id
            """,
            new { Id = id, CountsAsWorkHours = countsAsWorkHours, ActorUserId = actorUserId },
            cancellationToken: ct));
        return rows > 0;
    }
}
