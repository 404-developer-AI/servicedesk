using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Singleton sync-state row for the SalesReceipts mirror. Sealed class with
/// get/set + AS-PascalCase aliases per the project's Dapper conventions.
public sealed class AdsolutSalesReceiptSyncState
{
    public DateTime? LastFullSyncUtc { get; set; }
    public DateTime? LastDeltaSyncUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public int ReceiptsSeen { get; set; }
    public int ReceiptsUpserted { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}

/// One distinct Adsolut state encountered in the mirror, with how many
/// receipts carry it — powers the dynamic status-filter checkboxes.
public sealed class AdsolutSalesReceiptStatusOption
{
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Count { get; set; }
}

/// Header row for the Timesheet → Adsolut tab list.
public sealed class AdsolutSalesReceiptRow
{
    public Guid Id { get; set; }
    public int? DocNr { get; set; }
    public string? BookCode { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerCode { get; set; }
    public string? StateCode { get; set; }
    public string? StateDescription { get; set; }
    public DateTime? SalesReceiptDate { get; set; }
    public string? Description { get; set; }
    public string? EmployeeName { get; set; }
    public string? EmployeeCode { get; set; }
    public string? RepresentativeName { get; set; }
    public decimal TotalExclVat { get; set; }
    public string? CurrencyIso { get; set; }
    public DateTime? AdsolutCreatedUtc { get; set; }
    public DateTime? AdsolutLastModified { get; set; }
    public DateTime SyncedUtc { get; set; }

    /// Ticket number parsed from the description ("Ticket#<digits>"), or null.
    public long? TicketNumber { get; set; }

    /// Id of the matching ticket (tickets.number = parsed ticket_number), or
    /// null when there is no Ticket# ref or no ticket with that number exists
    /// in this install. Drives the "open the ticket" link in the timesheet UI.
    public Guid? TicketId { get; set; }

    /// Total registered timesheet minutes on the matched ticket — computed
    /// live in the list query. Null when there is no Ticket# in the
    /// description, no matching ticket, or no registered hours. Surfaced only
    /// on the *primary* receipt of a ticket (see IsPrimary) so a ticket billed
    /// across several verkoopbonnen never double-counts its hours.
    public int? TotalMinutes { get; set; }

    /// Receipt grouping for tickets billed across multiple verkoopbonnen.
    /// Computed over the whole mirror (independent of search/paging): a
    /// ticket's registered hours live once on the *primary* receipt (the
    /// lowest doc-nr), and the comparison runs against CombinedTotalExclVat —
    /// the summed excl-VAT total of every receipt on that ticket. A solo
    /// receipt (or one with no Ticket# ref) is its own group: Count/Ordinal = 1,
    /// IsPrimary = true, CombinedTotalExclVat = its own total.
    public bool IsPrimary { get; set; }
    public int TicketReceiptCount { get; set; }
    public int TicketReceiptOrdinal { get; set; }
    public decimal CombinedTotalExclVat { get; set; }

    /// "VK Werkuren" — the excl-VAT total of only this receipt's product lines
    /// whose product is flagged as counting toward work hours (adsolut_catalogue
    /// _products.counts_as_work_hours). Hardware and other non-work-hours lines
    /// are excluded, so this — not TotalExclVat — is what the registered hours
    /// are matched against. CombinedWerkurenExclVat is the same figure summed
    /// over every receipt on the ticket (the value the Difference uses on the
    /// primary receipt). 0 when no line maps to a flagged product (incl. before
    /// the catalogue is synced).
    public decimal WerkurenExclVat { get; set; }
    public decimal CombinedWerkurenExclVat { get; set; }

    /// "Back Office checked" marker for this receipt (context 'adsolut' in
    /// timesheet_bo_checks). CheckedByEmail / CheckedUtc record who/when.
    public bool BoChecked { get; set; }
    public DateTime? CheckedUtc { get; set; }
    public string? CheckedByEmail { get; set; }
}

/// One task's registered minutes for a receipt's matched ticket (hours pill
/// breakdown). Sealed class + AS-PascalCase per the Dapper conventions.
public sealed class AdsolutReceiptTaskHoursRow
{
    public string TaskName { get; set; } = string.Empty;
    public bool IsAbsence { get; set; }
    public int Minutes { get; set; }
}

public sealed class AdsolutSalesReceiptLineRow
{
    public Guid Id { get; set; }
    public int? LineNr { get; set; }
    public string? ProductCode { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public string? UnitCode { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? TotalExclVat { get; set; }
    public decimal? TotalInclVat { get; set; }
    public string? VatCode { get; set; }
}

public sealed class AdsolutSalesReceiptPerformanceRow
{
    public Guid Id { get; set; }
    public string? EmployeeCode { get; set; }
    public DateTime? PerformanceDate { get; set; }
    public string? FromTime { get; set; }
    public string? UntilTime { get; set; }
    public decimal? DurationMinutes { get; set; }
    public decimal? InvoiceDurationMinutes { get; set; }
    public decimal? InvoiceUnitPrice { get; set; }
    public decimal? InvoiceTotal { get; set; }
    public string? PerformanceCode { get; set; }
    public string? Description { get; set; }
}

public sealed record AdsolutSalesReceiptDetail(
    AdsolutSalesReceiptRow Header,
    IReadOnlyList<AdsolutSalesReceiptLineRow> Lines,
    IReadOnlyList<AdsolutSalesReceiptPerformanceRow> Performances);

public sealed record AdsolutSalesReceiptListResult(
    IReadOnlyList<AdsolutSalesReceiptRow> Items,
    int Total,
    int Page,
    int PageSize);

public interface IAdsolutSalesReceiptRepository
{
    /// Upsert one receipt header and replace its line-sets wholesale, in a
    /// single transaction. <paramref name="totalExclVat"/> is computed by the
    /// caller (sum of product-line excl-VAT totals + performance invoice
    /// totals) because the API exposes no header total.
    Task UpsertAsync(AdsolutSalesReceipt receipt, decimal totalExclVat, CancellationToken ct = default);

    Task<AdsolutSalesReceiptSyncState?> GetSyncStateAsync(CancellationToken ct = default);
    Task SaveSyncStateAsync(AdsolutSalesReceiptSyncState state, CancellationToken ct = default);

    /// Distinct states seen in the mirror (for the status-filter UI).
    Task<IReadOnlyList<AdsolutSalesReceiptStatusOption>> GetStatusOptionsAsync(CancellationToken ct = default);

    Task<int> GetCountAsync(CancellationToken ct = default);

    /// Drop mirrored receipts whose state code is NOT in <paramref name="keepCodes"/>.
    /// Called after a tick when the admin's status filter is non-empty, so the
    /// mirror reflects exactly the selected statuses (children cascade). No-op
    /// when <paramref name="keepCodes"/> is empty — that means "keep all".
    Task<int> DeleteReceiptsNotInStatusesAsync(IReadOnlyCollection<string> keepCodes, CancellationToken ct = default);

    /// Paged list for the Timesheet → Adsolut tab. Optional <paramref name="search"/>
    /// matches customer name, description and document number. <paramref name="sort"/>
    /// is a whitelisted key (date/doc/customer/status/total/hours/bruto/difference);
    /// <paramref name="dir"/> is "asc"/"desc". <paramref name="hourlyRate"/> lets the
    /// bruto/difference sorts order on rate × registered hours. Empty values always
    /// sort last regardless of direction.
    /// <paramref name="year"/>/<paramref name="month"/> optionally scope the list to a
    /// single calendar month by <c>sales_receipt_date</c> (a half-open UTC range, so the
    /// indexed column is used). Both null = no date scope (all receipts). The per-ticket
    /// grouping (count/ordinal/combined totals) still spans the whole mirror, so a receipt
    /// whose siblings fall in another month keeps its true ticket-wide totals.
    Task<AdsolutSalesReceiptListResult> ListAsync(
        string? search, int page, int pageSize, string? sort, string? dir, decimal hourlyRate,
        string? boFilter, int? year, int? month, CancellationToken ct = default);

    /// One receipt with its product + performance lines (for the expand view).
    Task<AdsolutSalesReceiptDetail?> GetDetailAsync(Guid id, CancellationToken ct = default);

    /// Per-task registered timesheet hours for the ticket referenced in this
    /// receipt's description. Empty when there is no Ticket# match or no hours.
    Task<IReadOnlyList<AdsolutReceiptTaskHoursRow>> GetHoursBreakdownAsync(Guid receiptId, CancellationToken ct = default);
}

public sealed class AdsolutSalesReceiptRepository : IAdsolutSalesReceiptRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public AdsolutSalesReceiptRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task UpsertAsync(AdsolutSalesReceipt r, decimal totalExclVat, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_sales_receipts (
                id, doc_nr, book_code, customer_adsolut_id, customer_code, customer_name,
                state_id, state_code, state_description, sales_receipt_date,
                description, internal_memo, memo,
                employee_code, employee_name, employee_email,
                representative_code, representative_name,
                currency_iso, vat_included, total_excl_vat, ticket_number,
                adsolut_created_utc, adsolut_last_modified, synced_utc
            ) VALUES (
                @Id, @DocNr, @BookCode, @CustomerAdsolutId, @CustomerCode, @CustomerName,
                @StateId, @StateCode, @StateDescription, @SalesReceiptDate,
                @Description, @InternalMemo, @Memo,
                @EmployeeCode, @EmployeeName, @EmployeeEmail,
                @RepresentativeCode, @RepresentativeName,
                @CurrencyIso, @VatIncluded, @TotalExclVat, @TicketNumber,
                @AdsolutCreatedUtc, @AdsolutLastModified, now()
            )
            ON CONFLICT (id) DO UPDATE SET
                doc_nr                = EXCLUDED.doc_nr,
                book_code             = EXCLUDED.book_code,
                customer_adsolut_id   = EXCLUDED.customer_adsolut_id,
                customer_code         = EXCLUDED.customer_code,
                customer_name         = EXCLUDED.customer_name,
                state_id              = EXCLUDED.state_id,
                state_code            = EXCLUDED.state_code,
                state_description     = EXCLUDED.state_description,
                sales_receipt_date    = EXCLUDED.sales_receipt_date,
                description           = EXCLUDED.description,
                internal_memo         = EXCLUDED.internal_memo,
                memo                  = EXCLUDED.memo,
                employee_code         = EXCLUDED.employee_code,
                employee_name         = EXCLUDED.employee_name,
                employee_email        = EXCLUDED.employee_email,
                representative_code   = EXCLUDED.representative_code,
                representative_name   = EXCLUDED.representative_name,
                currency_iso          = EXCLUDED.currency_iso,
                vat_included          = EXCLUDED.vat_included,
                total_excl_vat        = EXCLUDED.total_excl_vat,
                ticket_number         = EXCLUDED.ticket_number,
                adsolut_created_utc   = EXCLUDED.adsolut_created_utc,
                adsolut_last_modified = EXCLUDED.adsolut_last_modified,
                synced_utc            = now()
            """,
            new
            {
                r.Id,
                r.DocNr,
                r.BookCode,
                r.CustomerAdsolutId,
                r.CustomerCode,
                r.CustomerName,
                r.StateId,
                r.StateCode,
                r.StateDescription,
                SalesReceiptDate = r.SalesReceiptDate?.UtcDateTime,
                r.Description,
                r.InternalMemo,
                r.Memo,
                r.EmployeeCode,
                r.EmployeeName,
                r.EmployeeEmail,
                r.RepresentativeCode,
                r.RepresentativeName,
                r.CurrencyIso,
                r.VatIncluded,
                TotalExclVat = totalExclVat,
                TicketNumber = ParseTicketNumber(r.Description),
                AdsolutCreatedUtc = r.AdsolutCreatedUtc?.UtcDateTime,
                AdsolutLastModified = r.AdsolutLastModified?.UtcDateTime,
            },
            transaction: tx,
            cancellationToken: ct));

        // Replace children wholesale — simplest correct strategy when a
        // receipt's lines can be added/removed upstream between syncs.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM adsolut_sales_receipt_lines WHERE receipt_id = @Id",
            new { r.Id }, transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM adsolut_sales_receipt_performances WHERE receipt_id = @Id",
            new { r.Id }, transaction: tx, cancellationToken: ct));

        if (r.Lines.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO adsolut_sales_receipt_lines (
                    id, receipt_id, line_nr, product_code, name, description,
                    quantity, unit_code, unit_price, total_excl_vat, total_incl_vat, vat_code
                ) VALUES (
                    @Id, @ReceiptId, @LineNr, @ProductCode, @Name, @Description,
                    @Quantity, @UnitCode, @UnitPrice, @TotalExclVat, @TotalInclVat, @VatCode
                )
                """,
                r.Lines.Select(l => new
                {
                    l.Id,
                    ReceiptId = r.Id,
                    l.LineNr,
                    l.ProductCode,
                    l.Name,
                    l.Description,
                    l.Quantity,
                    l.UnitCode,
                    l.UnitPrice,
                    l.TotalExclVat,
                    l.TotalInclVat,
                    l.VatCode,
                }),
                transaction: tx,
                cancellationToken: ct));
        }

        if (r.Performances.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO adsolut_sales_receipt_performances (
                    id, receipt_id, employee_code, performance_date, from_time, until_time,
                    duration_minutes, invoice_duration_minutes, invoice_unit_price, invoice_total,
                    performance_code, description
                ) VALUES (
                    @Id, @ReceiptId, @EmployeeCode, @PerformanceDate, @FromTime, @UntilTime,
                    @DurationMinutes, @InvoiceDurationMinutes, @InvoiceUnitPrice, @InvoiceTotal,
                    @PerformanceCode, @Description
                )
                """,
                r.Performances.Select(p => new
                {
                    p.Id,
                    ReceiptId = r.Id,
                    p.EmployeeCode,
                    PerformanceDate = p.PerformanceDate?.UtcDateTime,
                    p.FromTime,
                    p.UntilTime,
                    p.DurationMinutes,
                    p.InvoiceDurationMinutes,
                    p.InvoiceUnitPrice,
                    p.InvoiceTotal,
                    p.PerformanceCode,
                    p.Description,
                }),
                transaction: tx,
                cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task<AdsolutSalesReceiptSyncState?> GetSyncStateAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AdsolutSalesReceiptSyncState>(new CommandDefinition(
            """
            SELECT
                last_full_sync_utc  AS LastFullSyncUtc,
                last_delta_sync_utc AS LastDeltaSyncUtc,
                last_error          AS LastError,
                last_error_utc      AS LastErrorUtc,
                receipts_seen       AS ReceiptsSeen,
                receipts_upserted   AS ReceiptsUpserted,
                updated_utc         AS UpdatedUtc
            FROM adsolut_sales_receipt_sync_state
            WHERE id = 1
            """,
            cancellationToken: ct));
    }

    public async Task SaveSyncStateAsync(AdsolutSalesReceiptSyncState state, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_sales_receipt_sync_state (
                id, last_full_sync_utc, last_delta_sync_utc, last_error, last_error_utc,
                receipts_seen, receipts_upserted, updated_utc
            ) VALUES (
                1, @LastFullSyncUtc, @LastDeltaSyncUtc, @LastError, @LastErrorUtc,
                @ReceiptsSeen, @ReceiptsUpserted, now()
            )
            ON CONFLICT (id) DO UPDATE SET
                last_full_sync_utc  = EXCLUDED.last_full_sync_utc,
                last_delta_sync_utc = EXCLUDED.last_delta_sync_utc,
                last_error          = EXCLUDED.last_error,
                last_error_utc      = EXCLUDED.last_error_utc,
                receipts_seen       = EXCLUDED.receipts_seen,
                receipts_upserted   = EXCLUDED.receipts_upserted,
                updated_utc         = now()
            """,
            new
            {
                state.LastFullSyncUtc,
                state.LastDeltaSyncUtc,
                state.LastError,
                state.LastErrorUtc,
                state.ReceiptsSeen,
                state.ReceiptsUpserted,
            },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AdsolutSalesReceiptStatusOption>> GetStatusOptionsAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdsolutSalesReceiptStatusOption>(new CommandDefinition(
            """
            SELECT
                state_code            AS Code,
                MAX(state_description) AS Description,
                COUNT(*)::int         AS Count
            FROM adsolut_sales_receipts
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
            "SELECT COUNT(*)::int FROM adsolut_sales_receipts",
            cancellationToken: ct));
    }

    public async Task<int> DeleteReceiptsNotInStatusesAsync(IReadOnlyCollection<string> keepCodes, CancellationToken ct = default)
    {
        if (keepCodes.Count == 0) return 0;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // Child rows cascade via the FK ON DELETE CASCADE. A NULL state_code
        // can never be "kept" by an explicit filter, so those are dropped too.
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM adsolut_sales_receipts
            WHERE state_code IS NULL OR NOT (state_code = ANY(@KeepCodes))
            """,
            new { KeepCodes = keepCodes.ToArray() },
            cancellationToken: ct));
    }

    private const string HeaderColumns = """
        id                    AS Id,
        doc_nr                AS DocNr,
        book_code             AS BookCode,
        customer_name         AS CustomerName,
        customer_code         AS CustomerCode,
        state_code            AS StateCode,
        state_description     AS StateDescription,
        sales_receipt_date    AS SalesReceiptDate,
        description           AS Description,
        employee_name         AS EmployeeName,
        employee_code         AS EmployeeCode,
        representative_name   AS RepresentativeName,
        total_excl_vat        AS TotalExclVat,
        currency_iso          AS CurrencyIso,
        adsolut_created_utc   AS AdsolutCreatedUtc,
        adsolut_last_modified AS AdsolutLastModified,
        synced_utc            AS SyncedUtc
        """;

    public async Task<AdsolutSalesReceiptListResult> ListAsync(
        string? search, int page, int pageSize, string? sort, string? dir, decimal hourlyRate,
        string? boFilter, int? year, int? month, CancellationToken ct = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (safePage - 1) * safePageSize;
        var term = (search ?? string.Empty).Trim();
        var hasSearch = term.Length > 0;
        var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        long? docProbe = long.TryParse(term, out var dn) ? dn : null;

        // Optional single-month scope on the indexed sales_receipt_date. A
        // half-open UTC range [start, start+1month) keeps the DESC index usable;
        // both params are always passed (null when unscoped) so the same SQL text
        // serves both the filtered and the all-receipts case.
        DateTime? monthStart = null, monthEnd = null;
        if (year is int y && month is int m && m is >= 1 and <= 12 && y is >= 1 and <= 9999)
        {
            monthStart = new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc);
            monthEnd = monthStart.Value.AddMonths(1);
        }
        var monthClause = monthStart is null
            ? string.Empty
            : "AND r.sales_receipt_date >= @MonthStart AND r.sales_receipt_date < @MonthEnd";

        // Whitelisted "BO checked" filter. The bo join is on receipt id with
        // context 'adsolut'; both the count and the page query carry it so
        // pagination stays consistent with the filter.
        var boClause = (boFilter ?? "all").Trim().ToLowerInvariant() switch
        {
            "checked" => "AND bo.entity_id IS NOT NULL",
            "unchecked" => "AND bo.entity_id IS NULL",
            _ => string.Empty,
        };

        // Whitelisted sort expression (no user string ever reaches the SQL).
        // bruto/difference order on rate × registered hours via the @Rate param.
        // Hours live only on the primary receipt of a ticket (g.ord = 1), so the
        // hours/bruto/difference sorts use the primary-gated minutes — siblings
        // sort as empty (NULLS LAST). The difference sort compares against the
        // combined excl-VAT total of all receipts on the ticket (g.combined_total).
        const string primaryMinutes = "(CASE WHEN g.ord = 1 THEN te.total_minutes END)";
        var sortExpr = (sort ?? "date").Trim().ToLowerInvariant() switch
        {
            "doc" => "r.doc_nr",
            "customer" => "r.customer_name",
            "status" => "r.state_code",
            "total" => "r.total_excl_vat",
            "werkuren" => "g.werkuren_total",
            "hours" => primaryMinutes,
            "bruto" => $"(@Rate * {primaryMinutes} / 60.0)",
            // Difference now compares the registered-hours bruto against the
            // ticket-wide WORK-HOURS total (VK Werkuren), not the full excl-VAT
            // total — hardware no longer skews the match.
            "difference" => $"(g.combined_werkuren - @Rate * {primaryMinutes} / 60.0)",
            _ => "r.sales_receipt_date",
        };
        var dirSql = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"""
            SELECT COUNT(*)::int
            FROM adsolut_sales_receipts r
            LEFT JOIN timesheet_bo_checks bo ON bo.entity_id = r.id AND bo.context = 'adsolut'
            WHERE (@HasSearch = FALSE
                   OR r.customer_name ILIKE @Like
                   OR r.description ILIKE @Like
                   OR (@DocProbe IS NOT NULL AND r.doc_nr = @DocProbe))
            {boClause}
            {monthClause}
            """,
            new { HasSearch = hasSearch, Like = like, DocProbe = docProbe, MonthStart = monthStart, MonthEnd = monthEnd },
            cancellationToken: ct));

        // Registered timesheet hours are computed live: match the receipt's
        // parsed ticket_number to tickets.number, then sum the minutes of the
        // entries on that ticket. te.total_minutes is NULL when there is no
        // Ticket# ref, no matching ticket, or no entries → the column is empty.
        // All tasks count (incl. absence) per the agreed behaviour.
        var items = await conn.QueryAsync<AdsolutSalesReceiptRow>(new CommandDefinition(
            $"""
            SELECT
                r.id                    AS Id,
                r.doc_nr                AS DocNr,
                r.book_code             AS BookCode,
                r.customer_name         AS CustomerName,
                r.customer_code         AS CustomerCode,
                r.state_code            AS StateCode,
                r.state_description     AS StateDescription,
                r.sales_receipt_date    AS SalesReceiptDate,
                r.description           AS Description,
                r.employee_name         AS EmployeeName,
                r.employee_code         AS EmployeeCode,
                r.representative_name   AS RepresentativeName,
                r.total_excl_vat        AS TotalExclVat,
                r.currency_iso          AS CurrencyIso,
                r.adsolut_created_utc   AS AdsolutCreatedUtc,
                r.adsolut_last_modified AS AdsolutLastModified,
                r.synced_utc            AS SyncedUtc,
                r.ticket_number         AS TicketNumber,
                tk.id                   AS TicketId,
                (CASE WHEN g.ord = 1 THEN te.total_minutes END) AS TotalMinutes,
                (g.ord = 1)             AS IsPrimary,
                g.cnt                   AS TicketReceiptCount,
                g.ord                   AS TicketReceiptOrdinal,
                g.combined_total        AS CombinedTotalExclVat,
                COALESCE(g.werkuren_total, 0)    AS WerkurenExclVat,
                COALESCE(g.combined_werkuren, 0) AS CombinedWerkurenExclVat,
                (bo.entity_id IS NOT NULL) AS BoChecked,
                bo.checked_utc          AS CheckedUtc,
                bu.email                AS CheckedByEmail
            FROM adsolut_sales_receipts r
            LEFT JOIN tickets tk ON tk.number = r.ticket_number
            LEFT JOIN (
                SELECT ticket_id, SUM(minutes)::int AS total_minutes
                FROM timesheet_entries
                GROUP BY ticket_id
            ) te ON te.ticket_id = tk.id
            -- Per-ticket grouping over the WHOLE mirror (not the filtered/paged
            -- set), so count, ordinal and combined total are always the true
            -- ticket-wide values. Receipts with no Ticket# ref get a unique
            -- 'solo:<id>' partition key so they never group together.
            LEFT JOIN (
                SELECT
                    s.id,
                    COUNT(*)              OVER w AS cnt,
                    ROW_NUMBER()          OVER (PARTITION BY COALESCE(s.ticket_number::text, 'solo:' || s.id::text)
                                                ORDER BY s.doc_nr ASC NULLS LAST, s.id) AS ord,
                    SUM(s.total_excl_vat) OVER w AS combined_total,
                    -- This receipt's own work-hours total + the ticket-wide sum.
                    COALESCE(wl.werkuren_total, 0)         AS werkuren_total,
                    SUM(COALESCE(wl.werkuren_total, 0)) OVER w AS combined_werkuren
                FROM adsolut_sales_receipts s
                LEFT JOIN (
                    -- Per-receipt VK Werkuren: sum the excl-VAT of only the lines
                    -- whose product code maps to a catalogue product flagged as
                    -- counting toward work hours. Computed live, so toggling a
                    -- flag is reflected immediately (no receipt re-sync needed).
                    SELECT l.receipt_id, SUM(l.total_excl_vat) AS werkuren_total
                    FROM adsolut_sales_receipt_lines l
                    JOIN adsolut_catalogue_products cp
                      ON cp.code = l.product_code AND cp.counts_as_work_hours = TRUE
                    GROUP BY l.receipt_id
                ) wl ON wl.receipt_id = s.id
                WINDOW w AS (PARTITION BY COALESCE(s.ticket_number::text, 'solo:' || s.id::text))
            ) g ON g.id = r.id
            LEFT JOIN timesheet_bo_checks bo ON bo.entity_id = r.id AND bo.context = 'adsolut'
            LEFT JOIN users bu ON bu.id = bo.checked_by
            WHERE (@HasSearch = FALSE
                   OR r.customer_name ILIKE @Like
                   OR r.description ILIKE @Like
                   OR (@DocProbe IS NOT NULL AND r.doc_nr = @DocProbe))
            {boClause}
            {monthClause}
            ORDER BY {sortExpr} {dirSql} NULLS LAST, r.doc_nr DESC NULLS LAST
            LIMIT @Limit OFFSET @Offset
            """,
            new { HasSearch = hasSearch, Like = like, DocProbe = docProbe, Rate = hourlyRate, Limit = safePageSize, Offset = offset, MonthStart = monthStart, MonthEnd = monthEnd },
            cancellationToken: ct));

        return new AdsolutSalesReceiptListResult(items.ToList(), total, safePage, safePageSize);
    }

    public async Task<IReadOnlyList<AdsolutReceiptTaskHoursRow>> GetHoursBreakdownAsync(Guid receiptId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdsolutReceiptTaskHoursRow>(new CommandDefinition(
            """
            SELECT  t.name                       AS TaskName,
                    COALESCE(t.is_absence, FALSE) AS IsAbsence,
                    SUM(e.minutes)::int          AS Minutes
            FROM adsolut_sales_receipts r
            JOIN tickets tk            ON tk.number = r.ticket_number
            JOIN timesheet_entries e   ON e.ticket_id = tk.id
            JOIN timesheet_tasks t     ON t.id = e.task_id
            WHERE r.id = @receiptId
            GROUP BY t.name, t.is_absence
            ORDER BY SUM(e.minutes) DESC
            """,
            new { receiptId },
            cancellationToken: ct));
        return rows.ToList();
    }

    /// Parse the ticket number from a receipt description. Adsolut descriptions
    /// for ticket-linked receipts start with "Ticket#<digits>" (optionally
    /// followed by " [extra text]"). Case-insensitive; returns null when no
    /// Ticket# reference is present.
    private static readonly Regex TicketRefRegex =
        new(@"Ticket#(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static long? ParseTicketNumber(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var m = TicketRefRegex.Match(description);
        if (!m.Success) return null;
        return long.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    public async Task<AdsolutSalesReceiptDetail?> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var header = await conn.QueryFirstOrDefaultAsync<AdsolutSalesReceiptRow>(new CommandDefinition(
            $"SELECT {HeaderColumns} FROM adsolut_sales_receipts WHERE id = @id",
            new { id }, cancellationToken: ct));
        if (header is null) return null;

        var lines = await conn.QueryAsync<AdsolutSalesReceiptLineRow>(new CommandDefinition(
            """
            SELECT id AS Id, line_nr AS LineNr, product_code AS ProductCode, name AS Name,
                   description AS Description, quantity AS Quantity, unit_code AS UnitCode,
                   unit_price AS UnitPrice, total_excl_vat AS TotalExclVat,
                   total_incl_vat AS TotalInclVat, vat_code AS VatCode
            FROM adsolut_sales_receipt_lines
            WHERE receipt_id = @id
            ORDER BY line_nr NULLS LAST
            """,
            new { id }, cancellationToken: ct));

        var performances = await conn.QueryAsync<AdsolutSalesReceiptPerformanceRow>(new CommandDefinition(
            """
            SELECT id AS Id, employee_code AS EmployeeCode, performance_date AS PerformanceDate,
                   from_time AS FromTime, until_time AS UntilTime, duration_minutes AS DurationMinutes,
                   invoice_duration_minutes AS InvoiceDurationMinutes, invoice_unit_price AS InvoiceUnitPrice,
                   invoice_total AS InvoiceTotal, performance_code AS PerformanceCode, description AS Description
            FROM adsolut_sales_receipt_performances
            WHERE receipt_id = @id
            ORDER BY performance_date NULLS LAST, from_time NULLS LAST
            """,
            new { id }, cancellationToken: ct));

        return new AdsolutSalesReceiptDetail(header, lines.ToList(), performances.ToList());
    }
}
