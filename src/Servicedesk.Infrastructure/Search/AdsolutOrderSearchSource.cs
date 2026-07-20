using System.Globalization;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Search;

/// Global-search source for mirrored Adsolut orders (bestellingen) shown in the
/// Orders overview (navbar → Assets → Orders). Row-level authorization: a
/// Customer principal never sees hits; an Agent/Admin sees hits only when their
/// own user row has <c>adsolut_orders_enabled = TRUE</c> — the same per-user
/// feature flag that gates the overview. The flag is checked inside
/// <see cref="SearchAsync"/> (a single cheap point lookup, only when the user
/// actually searches).
///
/// The admin's display status filter is honored: orders whose state is not in
/// the selected set are hidden from search, exactly as they are hidden from the
/// overview. Matches against customer name, remark, document number and kluwer
/// ref.
public sealed class AdsolutOrderSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;

    public AdsolutOrderSearchSource(NpgsqlDataSource dataSource, ISettingsService settings)
    {
        _dataSource = dataSource;
        _settings = settings;
    }

    public string Kind => SearchSourceKind.AdsolutOrders;

    public bool IsAvailableFor(SearchPrincipal principal) =>
        principal.IsAdmin || (principal.IsAgent && principal.HasFeature(SearchFeature.AdsolutOrders));

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

        // Feature-flag gate (adsolut_orders_enabled) is enforced by
        // IsAvailableFor via the principal — checked at the top of this method
        // and by the search façade before it ever queries this source.
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Display status filter (DISPLAY-ONLY — the mirror holds every status).
        var filterRaw = await _settings.GetAsync<string>(SettingKeys.Adsolut.ErpOrdersStatusFilter, ct) ?? string.Empty;
        var statuses = filterRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var statusArray = statuses.Length == 0 ? null : statuses;

        long? docProbe = long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dn)
            ? dn
            : null;

        // orderBy is a fixed switch over SearchSort — never user input — so
        // interpolating it into the SQL below carries no injection risk.
        var orderBy = request.Sort switch
        {
            SearchSort.Newest => "order_date DESC NULLS LAST, doc_nr DESC NULLS LAST",
            SearchSort.Oldest => "order_date ASC NULLS FIRST, doc_nr ASC NULLS FIRST",
            _ => "rank DESC, order_date DESC NULLS LAST, doc_nr DESC NULLS LAST",
        };

        var sql = $"""
            WITH hits AS (
                SELECT  o.id,
                        o.doc_nr,
                        o.book_code,
                        o.kluwer_ref,
                        o.customer_name,
                        o.state_description,
                        o.state_code,
                        o.order_date,
                        o.total_excl_vat,
                        o.remark,
                        CASE
                            WHEN @docProbe IS NOT NULL AND o.doc_nr = @docProbe THEN 10.0
                            WHEN o.kluwer_ref    ILIKE @prefix THEN 4.0
                            WHEN o.customer_name ILIKE @prefix THEN 3.0
                            WHEN o.remark        ILIKE @prefix THEN 2.0
                            WHEN o.customer_name ILIKE @like   THEN 1.5
                            WHEN o.remark        ILIKE @like   THEN 1.0
                            ELSE 0
                        END AS rank,
                        COUNT(*) OVER () AS total_hits
                  FROM adsolut_orders o
                 WHERE (@statuses::text[] IS NULL OR o.state_code = ANY(@statuses::text[]))
                   AND ((@docProbe IS NOT NULL AND o.doc_nr = @docProbe)
                        OR o.customer_name ILIKE @like
                        OR o.remark        ILIKE @like
                        OR o.kluwer_ref    ILIKE @like)
            )
            SELECT  id                AS Id,
                    doc_nr            AS DocNr,
                    book_code         AS BookCode,
                    kluwer_ref        AS KluwerRef,
                    customer_name     AS CustomerName,
                    state_description AS StateDescription,
                    state_code        AS StateCode,
                    order_date        AS OrderDate,
                    total_excl_vat    AS TotalExclVat,
                    remark            AS Remark,
                    rank::double precision AS Rank,
                    total_hits        AS TotalHits
              FROM hits
             ORDER BY {orderBy}
             LIMIT @limit OFFSET @offset;
            """;

        var rows = (await conn.QueryAsync<OrderHitRow>(new CommandDefinition(
            sql,
            new
            {
                statuses = statusArray,
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

    private SearchHit BuildHit(OrderHitRow r)
    {
        var docLabel = r.KluwerRef
            ?? (r.DocNr?.ToString(CultureInfo.InvariantCulture) is { } d ? $"{r.BookCode} {d}".Trim() : "—");
        var customer = string.IsNullOrWhiteSpace(r.CustomerName) ? "—" : r.CustomerName;
        var title = $"{docLabel} · {customer}";
        var date = r.OrderDate is { } od
            ? od.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

        return new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: title,
            Snippet: r.Remark,
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["docNr"] = r.DocNr?.ToString(CultureInfo.InvariantCulture),
                ["kluwerRef"] = r.KluwerRef,
                ["customerName"] = r.CustomerName,
                ["stateCode"] = r.StateCode,
                ["stateDescription"] = r.StateDescription,
                ["orderDate"] = date,
                ["totalExclVat"] = r.TotalExclVat.ToString(CultureInfo.InvariantCulture),
            });
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class OrderHitRow
    {
        public Guid Id { get; set; }
        public int? DocNr { get; set; }
        public string? BookCode { get; set; }
        public string? KluwerRef { get; set; }
        public string? CustomerName { get; set; }
        public string? StateDescription { get; set; }
        public string? StateCode { get; set; }
        public DateTime? OrderDate { get; set; }
        public decimal TotalExclVat { get; set; }
        public string? Remark { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
