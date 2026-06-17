using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Singleton sync-state row for the Contracts mirror. Sealed class with get/set
/// + AS-PascalCase aliases per the project's Dapper conventions.
public sealed class AdsolutContractSyncState
{
    public DateTime? LastFullSyncUtc { get; set; }
    public DateTime? LastDeltaSyncUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public int ContractsSeen { get; set; }
    public int ContractsUpserted { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}

/// One distinct contract state in the mirror, with how many contracts carry it —
/// powers the dynamic status-filter checkboxes (display-only filter). The label
/// comes from the inline contractState translation, so no lookup table is needed.
public sealed class AdsolutContractStatusOption
{
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Count { get; set; }
}

/// Header row for the Contracts overview + detail. CompanyId/CompanyName/
/// RelationCode are LEFT-JOINed from the local companies mirror via the relation
/// code (contract → adsolut_erp_customers → companies.adsolut_number), because
/// the contract's ERP customer GUID does not match companies.adsolut_id. When no
/// company matches (customer not resolved yet, or no local company), those are
/// null and the UI falls back to CustomerName (the name copied onto the contract).
public sealed class AdsolutContractRow
{
    public Guid Id { get; set; }
    public int? DocNr { get; set; }
    public Guid? CustomerAdsolutId { get; set; }
    public string? CustomerName { get; set; }
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public string? RelationCode { get; set; }
    public string? StateCode { get; set; }
    public string? StateDescription { get; set; }
    public DateTime? DocDate { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? StopDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? Description { get; set; }
    public string? Memo { get; set; }
    public string? PeriodicityCode { get; set; }
    public string? PeriodicityLabel { get; set; }
    public string? InvoicingPeriodicityCode { get; set; }
    public string? InvoicingPeriodicityLabel { get; set; }
    public int? NumberOfTerms { get; set; }
    public decimal TotalExclVat { get; set; }
    public decimal TotalVat { get; set; }
    public decimal TotalInclVat { get; set; }
    public DateTime? AdsolutCreatedUtc { get; set; }
    public DateTime? AdsolutLastModified { get; set; }
    public DateTime SyncedUtc { get; set; }
}

public sealed class AdsolutContractLineRow
{
    public Guid Id { get; set; }
    public int? LineNr { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? GrossUnitPrice { get; set; }
    public decimal? Discount1 { get; set; }
    public decimal? Discount2 { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? UnitPriceIncl { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
}

public sealed record AdsolutContractDetail(
    AdsolutContractRow Header,
    IReadOnlyList<AdsolutContractLineRow> Lines);

/// One company that has at least one contract line referencing a selected
/// "Microsoft 365" article — a row in the Microsoft 365 matching list.
/// CompanyCode = companies.adsolut_number (the relation/customer code).
/// Distinct per company regardless of how many matching lines it has.
public sealed class AdsolutM365CompanyRow
{
    public Guid CompanyId { get; set; }
    public string? CompanyCode { get; set; }
    public string? CompanyName { get; set; }
}

public sealed record AdsolutContractListResult(
    IReadOnlyList<AdsolutContractRow> Items,
    int Total,
    int Page,
    int PageSize);

public interface IAdsolutContractRepository
{
    /// Upsert one contract header and replace its article-line set wholesale, in
    /// a single transaction. Totals come straight from the API (no compute).
    Task UpsertAsync(AdsolutContract contract, CancellationToken ct = default);

    Task<AdsolutContractSyncState?> GetSyncStateAsync(CancellationToken ct = default);
    Task SaveSyncStateAsync(AdsolutContractSyncState state, CancellationToken ct = default);

    /// Distinct states seen in the mirror (for the display status-filter UI).
    Task<IReadOnlyList<AdsolutContractStatusOption>> GetStatusOptionsAsync(CancellationToken ct = default);

    Task<int> GetCountAsync(CancellationToken ct = default);

    /// Paged overview list. <paramref name="search"/> matches the contract
    /// title, customer name, document number, linked company name and relation
    /// code. <paramref name="sort"/> is a whitelisted key; <paramref name="dir"/>
    /// is "asc"/"desc". <paramref name="statusFilter"/> is the admin's DISPLAY
    /// filter — when non-empty, only those state codes are returned (the mirror
    /// still holds every status).
    Task<AdsolutContractListResult> ListAsync(
        string? search, int page, int pageSize, string? sort, string? dir,
        IReadOnlyCollection<string> statusFilter, CancellationToken ct = default);

    /// One contract with its article lines (overview expand + detail).
    Task<AdsolutContractDetail?> GetDetailAsync(Guid id, CancellationToken ct = default);

    /// Distinct companies that have one or more contract lines referencing any
    /// of <paramref name="articleIds"/> — the Microsoft 365 matching list. Each
    /// company appears once. Empty input → empty result. When
    /// <paramref name="statusFilter"/> is non-empty, only contracts whose
    /// state_code is in that set count (empty = any status).
    Task<IReadOnlyList<AdsolutM365CompanyRow>> GetM365CompaniesAsync(
        IReadOnlyCollection<Guid> articleIds, IReadOnlyCollection<string> statusFilter,
        CancellationToken ct = default);
}

public sealed class AdsolutContractRepository : IAdsolutContractRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public AdsolutContractRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task UpsertAsync(AdsolutContract c, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_contracts (
                id, doc_nr, customer_adsolut_id, invoice_customer_adsolut_id, customer_name,
                state_code, state_description, doc_date, start_date, stop_date, end_date,
                description, memo, periodicity_code, periodicity_label,
                invoicing_periodicity_code, invoicing_periodicity_label, number_of_terms,
                total_excl_vat, total_vat, total_incl_vat,
                adsolut_created_utc, adsolut_last_modified, synced_utc
            ) VALUES (
                @Id, @DocNr, @CustomerAdsolutId, @InvoiceCustomerAdsolutId, @CustomerName,
                @StateCode, @StateDescription, @DocDate, @StartDate, @StopDate, @EndDate,
                @Description, @Memo, @PeriodicityCode, @PeriodicityLabel,
                @InvoicingPeriodicityCode, @InvoicingPeriodicityLabel, @NumberOfTerms,
                @TotalExclVat, @TotalVat, @TotalInclVat,
                @AdsolutCreatedUtc, @AdsolutLastModified, now()
            )
            ON CONFLICT (id) DO UPDATE SET
                doc_nr                      = EXCLUDED.doc_nr,
                customer_adsolut_id         = EXCLUDED.customer_adsolut_id,
                invoice_customer_adsolut_id = EXCLUDED.invoice_customer_adsolut_id,
                customer_name               = EXCLUDED.customer_name,
                state_code                  = EXCLUDED.state_code,
                state_description           = EXCLUDED.state_description,
                doc_date                    = EXCLUDED.doc_date,
                start_date                  = EXCLUDED.start_date,
                stop_date                   = EXCLUDED.stop_date,
                end_date                    = EXCLUDED.end_date,
                description                 = EXCLUDED.description,
                memo                        = EXCLUDED.memo,
                periodicity_code            = EXCLUDED.periodicity_code,
                periodicity_label           = EXCLUDED.periodicity_label,
                invoicing_periodicity_code  = EXCLUDED.invoicing_periodicity_code,
                invoicing_periodicity_label = EXCLUDED.invoicing_periodicity_label,
                number_of_terms             = EXCLUDED.number_of_terms,
                total_excl_vat              = EXCLUDED.total_excl_vat,
                total_vat                   = EXCLUDED.total_vat,
                total_incl_vat              = EXCLUDED.total_incl_vat,
                adsolut_created_utc         = EXCLUDED.adsolut_created_utc,
                adsolut_last_modified       = EXCLUDED.adsolut_last_modified,
                synced_utc                  = now()
            """,
            new
            {
                c.Id,
                c.DocNr,
                c.CustomerAdsolutId,
                c.InvoiceCustomerAdsolutId,
                c.CustomerName,
                c.StateCode,
                c.StateDescription,
                DocDate = c.DocDate?.UtcDateTime,
                StartDate = c.StartDate?.UtcDateTime,
                StopDate = c.StopDate?.UtcDateTime,
                EndDate = c.EndDate?.UtcDateTime,
                c.Description,
                c.Memo,
                c.PeriodicityCode,
                c.PeriodicityLabel,
                c.InvoicingPeriodicityCode,
                c.InvoicingPeriodicityLabel,
                c.NumberOfTerms,
                c.TotalExclVat,
                c.TotalVat,
                c.TotalInclVat,
                AdsolutCreatedUtc = c.AdsolutCreatedUtc?.UtcDateTime,
                AdsolutLastModified = c.AdsolutLastModified?.UtcDateTime,
            },
            transaction: tx,
            cancellationToken: ct));

        // Replace child lines wholesale — simplest correct strategy when a
        // contract's article lines can be added/removed upstream between syncs.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM adsolut_contract_lines WHERE contract_id = @Id",
            new { c.Id }, transaction: tx, cancellationToken: ct));

        if (c.Lines.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO adsolut_contract_lines (
                    id, contract_id, line_nr, article_id, name, description,
                    quantity, gross_unit_price, discount1, discount2,
                    unit_price, unit_price_incl, start_date, end_date
                ) VALUES (
                    @Id, @ContractId, @LineNr, @ArticleId, @Name, @Description,
                    @Quantity, @GrossUnitPrice, @Discount1, @Discount2,
                    @UnitPrice, @UnitPriceIncl, @StartDate, @EndDate
                )
                """,
                c.Lines.Select(l => new
                {
                    l.Id,
                    ContractId = c.Id,
                    l.LineNr,
                    l.ArticleId,
                    l.Name,
                    l.Description,
                    l.Quantity,
                    l.GrossUnitPrice,
                    l.Discount1,
                    l.Discount2,
                    l.UnitPrice,
                    l.UnitPriceIncl,
                    StartDate = l.StartDate?.UtcDateTime,
                    EndDate = l.EndDate?.UtcDateTime,
                }),
                transaction: tx,
                cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task<AdsolutContractSyncState?> GetSyncStateAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AdsolutContractSyncState>(new CommandDefinition(
            """
            SELECT
                last_full_sync_utc  AS LastFullSyncUtc,
                last_delta_sync_utc AS LastDeltaSyncUtc,
                last_error          AS LastError,
                last_error_utc      AS LastErrorUtc,
                contracts_seen      AS ContractsSeen,
                contracts_upserted  AS ContractsUpserted,
                updated_utc         AS UpdatedUtc
            FROM adsolut_contract_sync_state
            WHERE id = 1
            """,
            cancellationToken: ct));
    }

    public async Task SaveSyncStateAsync(AdsolutContractSyncState state, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_contract_sync_state (
                id, last_full_sync_utc, last_delta_sync_utc, last_error, last_error_utc,
                contracts_seen, contracts_upserted, updated_utc
            ) VALUES (
                1, @LastFullSyncUtc, @LastDeltaSyncUtc, @LastError, @LastErrorUtc,
                @ContractsSeen, @ContractsUpserted, now()
            )
            ON CONFLICT (id) DO UPDATE SET
                last_full_sync_utc  = EXCLUDED.last_full_sync_utc,
                last_delta_sync_utc = EXCLUDED.last_delta_sync_utc,
                last_error          = EXCLUDED.last_error,
                last_error_utc      = EXCLUDED.last_error_utc,
                contracts_seen      = EXCLUDED.contracts_seen,
                contracts_upserted  = EXCLUDED.contracts_upserted,
                updated_utc         = now()
            """,
            new
            {
                state.LastFullSyncUtc,
                state.LastDeltaSyncUtc,
                state.LastError,
                state.LastErrorUtc,
                state.ContractsSeen,
                state.ContractsUpserted,
            },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AdsolutContractStatusOption>> GetStatusOptionsAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdsolutContractStatusOption>(new CommandDefinition(
            """
            SELECT
                state_code             AS Code,
                MAX(state_description) AS Description,
                COUNT(*)::int          AS Count
            FROM adsolut_contracts
            WHERE state_code IS NOT NULL
            GROUP BY state_code
            ORDER BY state_code
            """,
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<int> GetCountAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*)::int FROM adsolut_contracts",
            cancellationToken: ct));
    }

    // Header columns + the LEFT-JOINed company link (relation). `c` = the
    // contract, `ec` = the resolved ERP customer (id → relation code), `co` =
    // the local company matched on that code. See FromClause for why the link
    // goes through the relation code and not the ERP customer GUID.
    private const string HeaderColumns = """
        c.id                          AS Id,
        c.doc_nr                      AS DocNr,
        c.customer_adsolut_id         AS CustomerAdsolutId,
        c.customer_name               AS CustomerName,
        co.id                         AS CompanyId,
        co.name                       AS CompanyName,
        co.adsolut_number             AS RelationCode,
        c.state_code                  AS StateCode,
        c.state_description           AS StateDescription,
        c.doc_date                    AS DocDate,
        c.start_date                  AS StartDate,
        c.stop_date                   AS StopDate,
        c.end_date                    AS EndDate,
        c.description                 AS Description,
        c.memo                        AS Memo,
        c.periodicity_code            AS PeriodicityCode,
        c.periodicity_label           AS PeriodicityLabel,
        c.invoicing_periodicity_code  AS InvoicingPeriodicityCode,
        c.invoicing_periodicity_label AS InvoicingPeriodicityLabel,
        c.number_of_terms             AS NumberOfTerms,
        c.total_excl_vat              AS TotalExclVat,
        c.total_vat                   AS TotalVat,
        c.total_incl_vat              AS TotalInclVat,
        c.adsolut_created_utc         AS AdsolutCreatedUtc,
        c.adsolut_last_modified       AS AdsolutLastModified,
        c.synced_utc                  AS SyncedUtc
        """;

    // A contract carries the ERP customer GUID, which does NOT match
    // companies.adsolut_id (ERP vs Accounting assign different GUIDs to the same
    // relation). Bridge via the relation CODE: contract → adsolut_erp_customers
    // (resolved id → code, populated by the Contracts sync) → companies on
    // adsolut_number = code. Both joins are LEFT so a contract still lists when
    // its customer isn't resolved yet or has no local company (the UI then falls
    // back to the contract's own customer_name).
    private const string FromClause = """
        FROM adsolut_contracts c
        LEFT JOIN adsolut_erp_customers ec ON ec.id = c.customer_adsolut_id
        LEFT JOIN companies co ON co.adsolut_number = ec.code AND ec.code IS NOT NULL
        """;

    public async Task<AdsolutContractListResult> ListAsync(
        string? search, int page, int pageSize, string? sort, string? dir,
        IReadOnlyCollection<string> statusFilter, CancellationToken ct = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (safePage - 1) * safePageSize;
        var term = (search ?? string.Empty).Trim();
        var hasSearch = term.Length > 0;
        var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        long? docProbe = long.TryParse(term, out var dn) ? dn : null;
        var statuses = statusFilter.Count == 0 ? null : statusFilter.ToArray();

        var sortExpr = (sort ?? "start").Trim().ToLowerInvariant() switch
        {
            "doc" => "c.doc_nr",
            "customer" => "COALESCE(co.name, c.customer_name)",
            "relation" => "co.adsolut_number",
            "status" => "c.state_code",
            "end" => "c.end_date",
            "term" => "c.periodicity_code",
            "total" => "c.total_excl_vat",
            "title" => "c.description",
            _ => "c.start_date",
        };
        var dirSql = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

        const string whereClause = """
            WHERE (@Statuses::text[] IS NULL OR c.state_code = ANY(@Statuses::text[]))
              AND (@HasSearch = FALSE
                   OR c.description   ILIKE @Like
                   OR c.customer_name ILIKE @Like
                   OR co.name         ILIKE @Like
                   OR co.adsolut_number ILIKE @Like
                   OR (@DocProbe IS NOT NULL AND c.doc_nr = @DocProbe))
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*)::int {FromClause} {whereClause}",
            new { Statuses = statuses, HasSearch = hasSearch, Like = like, DocProbe = docProbe },
            cancellationToken: ct));

        var items = await conn.QueryAsync<AdsolutContractRow>(new CommandDefinition(
            $"""
            SELECT {HeaderColumns}
            {FromClause}
            {whereClause}
            ORDER BY {sortExpr} {dirSql} NULLS LAST, c.doc_nr DESC NULLS LAST
            LIMIT @Limit OFFSET @Offset
            """,
            new { Statuses = statuses, HasSearch = hasSearch, Like = like, DocProbe = docProbe, Limit = safePageSize, Offset = offset },
            cancellationToken: ct));

        return new AdsolutContractListResult(items.ToList(), total, safePage, safePageSize);
    }

    public async Task<AdsolutContractDetail?> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var header = await conn.QueryFirstOrDefaultAsync<AdsolutContractRow>(new CommandDefinition(
            $"SELECT {HeaderColumns} {FromClause} WHERE c.id = @id",
            new { id }, cancellationToken: ct));
        if (header is null) return null;

        var lines = await conn.QueryAsync<AdsolutContractLineRow>(new CommandDefinition(
            """
            SELECT id AS Id, line_nr AS LineNr, name AS Name, description AS Description,
                   quantity AS Quantity, gross_unit_price AS GrossUnitPrice,
                   discount1 AS Discount1, discount2 AS Discount2,
                   unit_price AS UnitPrice, unit_price_incl AS UnitPriceIncl,
                   start_date AS StartDate, end_date AS EndDate
            FROM adsolut_contract_lines
            WHERE contract_id = @id
            ORDER BY line_nr NULLS LAST
            """,
            new { id }, cancellationToken: ct));

        return new AdsolutContractDetail(header, lines.ToList());
    }

    public async Task<IReadOnlyList<AdsolutM365CompanyRow>> GetM365CompaniesAsync(
        IReadOnlyCollection<Guid> articleIds, IReadOnlyCollection<string> statusFilter,
        CancellationToken ct = default)
    {
        if (articleIds.Count == 0) return Array.Empty<AdsolutM365CompanyRow>();

        var statuses = statusFilter.Count == 0 ? null : statusFilter.ToArray();

        // The ERP customer GUID on a contract does NOT match companies.adsolut_id
        // (ERP vs Accounting assign different GUIDs to the same relation). The
        // shared key is the relation CODE: contracts → adsolut_erp_customers
        // (resolved id → code) → companies.adsolut_number. Same bridge the
        // Orders/SalesReceipts mirrors use. The optional status filter keeps a
        // company out when its only matching contract has an excluded state
        // (e.g. terminated) — empty filter = any status counts.
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdsolutM365CompanyRow>(new CommandDefinition(
            """
            SELECT DISTINCT
                co.id             AS CompanyId,
                co.adsolut_number AS CompanyCode,
                co.name           AS CompanyName
            FROM adsolut_contract_lines l
            JOIN adsolut_contracts c      ON c.id = l.contract_id
            JOIN adsolut_erp_customers ec ON ec.id = c.customer_adsolut_id
            JOIN companies co             ON co.adsolut_number = ec.code
            WHERE l.article_id = ANY(@ArticleIds::uuid[])
              AND ec.code IS NOT NULL
              AND (@Statuses::text[] IS NULL OR c.state_code = ANY(@Statuses::text[]))
            ORDER BY co.name ASC NULLS LAST
            """,
            new { ArticleIds = articleIds.ToArray(), Statuses = statuses },
            cancellationToken: ct));
        return rows.ToList();
    }
}
