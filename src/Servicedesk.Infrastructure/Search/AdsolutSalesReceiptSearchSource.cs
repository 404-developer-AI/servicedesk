using System.Globalization;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Global-search source for mirrored Adsolut sales receipts (verkoopbonnen)
/// shown in the Timesheet → Adsolut tab. Row-level authorization: a Customer
/// principal never sees hits; an Agent/Admin sees hits only when their own
/// user row has <c>adsolut_timesheet_enabled = TRUE</c> — the same per-user
/// feature flag that gates the tab. The flag is checked inside
/// <see cref="SearchAsync"/> (a single cheap point lookup, only when the user
/// actually searches) rather than carried on the principal, mirroring how
/// <see cref="TimesheetSearchSource"/> reads timesheet_manager.
///
/// Matches against customer name, description and document number. The mirror
/// is install-wide (not per-user), so there is no per-row owner filter — the
/// authorization boundary is the feature flag.
public sealed class AdsolutSalesReceiptSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public AdsolutSalesReceiptSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.AdsolutSalesReceipts;

    public bool IsAvailableFor(SearchPrincipal principal) =>
        principal.IsAdmin || (principal.IsAgent && principal.HasFeature(SearchFeature.AdsolutTimesheet));

    public async Task<SearchGroup> SearchAsync(
        SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (!IsAvailableFor(principal))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var normalized = request.Query.Trim();
        if (normalized.Length == 0)
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);

        // Feature-flag gate (adsolut_timesheet_enabled) is enforced by
        // IsAvailableFor via the principal — checked at the top of this method
        // and by the search façade before it ever queries this source.
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        long? docProbe = long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dn)
            ? dn
            : null;

        // orderBy is a fixed switch over SearchSort — never user input — so
        // interpolating it into the SQL below carries no injection risk.
        var orderBy = request.Sort switch
        {
            SearchSort.Newest => "sales_receipt_date DESC NULLS LAST, doc_nr DESC NULLS LAST",
            SearchSort.Oldest => "sales_receipt_date ASC NULLS FIRST, doc_nr ASC NULLS FIRST",
            _ => "rank DESC, sales_receipt_date DESC NULLS LAST, doc_nr DESC NULLS LAST",
        };

        var sql = $"""
            WITH hits AS (
                SELECT  r.id,
                        r.doc_nr,
                        r.customer_name,
                        r.state_description,
                        r.state_code,
                        r.sales_receipt_date,
                        r.total_excl_vat,
                        r.description,
                        CASE
                            WHEN @docProbe IS NOT NULL AND r.doc_nr = @docProbe THEN 10.0
                            WHEN r.customer_name ILIKE @prefix THEN 3.0
                            WHEN r.description    ILIKE @prefix THEN 2.0
                            WHEN r.customer_name ILIKE @like   THEN 1.5
                            WHEN r.description    ILIKE @like   THEN 1.0
                            ELSE 0
                        END AS rank,
                        COUNT(*) OVER () AS total_hits
                  FROM adsolut_sales_receipts r
                 WHERE (@docProbe IS NOT NULL AND r.doc_nr = @docProbe)
                    OR r.customer_name ILIKE @like
                    OR r.description    ILIKE @like
            )
            SELECT  id                 AS Id,
                    doc_nr             AS DocNr,
                    customer_name      AS CustomerName,
                    state_description  AS StateDescription,
                    state_code         AS StateCode,
                    sales_receipt_date AS SalesReceiptDate,
                    total_excl_vat     AS TotalExclVat,
                    description        AS Description,
                    rank::double precision AS Rank,
                    total_hits         AS TotalHits
              FROM hits
             ORDER BY {orderBy}
             LIMIT @limit OFFSET @offset;
            """;

        var rows = (await conn.QueryAsync<ReceiptHitRow>(new CommandDefinition(
            sql,
            new
            {
                docProbe,
                prefix = EscapeLike(normalized) + "%",
                like = "%" + EscapeLike(normalized) + "%",
                limit,
                offset,
            },
            cancellationToken: ct))).ToList();

        var hits = rows.Select(BuildHit).ToList();
        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var hasMore = totalInGroup > offset + hits.Count;
        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    private SearchHit BuildHit(ReceiptHitRow r)
    {
        var docLabel = r.DocNr?.ToString(CultureInfo.InvariantCulture) ?? "—";
        var customer = string.IsNullOrWhiteSpace(r.CustomerName) ? "—" : r.CustomerName;
        var title = $"VK {docLabel} · {customer}";
        var date = r.SalesReceiptDate is { } d
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

        return new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: title,
            Snippet: r.Description,
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["docNr"] = r.DocNr?.ToString(CultureInfo.InvariantCulture),
                ["customerName"] = r.CustomerName,
                ["stateCode"] = r.StateCode,
                ["stateDescription"] = r.StateDescription,
                ["salesReceiptDate"] = date,
                ["totalExclVat"] = r.TotalExclVat.ToString(CultureInfo.InvariantCulture),
            });
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class ReceiptHitRow
    {
        public Guid Id { get; set; }
        public int? DocNr { get; set; }
        public string? CustomerName { get; set; }
        public string? StateDescription { get; set; }
        public string? StateCode { get; set; }
        public DateTime? SalesReceiptDate { get; set; }
        public decimal TotalExclVat { get; set; }
        public string? Description { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
