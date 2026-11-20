using System.Globalization;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Global-search source for TD SYNNEX orders (Order Management, v0.1.59).
/// Row-level authorization: only an Agent/Admin whose own
/// <c>order_management_enabled</c> flag is on gets hits — admins are not
/// exempt, the flag is the access boundary. Customers never. Matches the
/// order number, customer PO, end-user reference, ship-to and the line part
/// numbers / descriptions / delivery notes / invoices (search_text, trigram).
public sealed class TdSynnexOrderSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public TdSynnexOrderSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.TdSynnexOrders;

    public bool IsAvailableFor(SearchPrincipal principal) =>
        (principal.IsAdmin || principal.IsAgent) && principal.HasFeature(SearchFeature.OrderManagement);

    public async Task<SearchGroup> SearchAsync(SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (!IsAvailableFor(principal))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var q = request.Query.Trim().ToLowerInvariant();
        if (q.Length == 0) return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);
        if (q.Length > 200) q = q[..200];

        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);

        // Fixed switch over SearchSort — never user input.
        var orderBy = request.Sort switch
        {
            SearchSort.Newest => "order_created_utc DESC NULLS LAST, id",
            SearchSort.Oldest => "order_created_utc ASC NULLS LAST, id",
            _ => "rank DESC, order_created_utc DESC NULLS LAST, id",
        };

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<HitRow>(new CommandDefinition($"""
            WITH hits AS (
                SELECT o.id, o.order_number, o.customer_order_number, o.status, o.ship_to_name,
                       o.order_created_utc, o.first_ship_date, o.total_net, o.currency,
                       CASE
                           WHEN lower(o.order_number) = @q OR lower(o.customer_order_number) = @q THEN 10.0
                           WHEN lower(o.order_number) LIKE @prefix OR lower(o.customer_order_number) LIKE @prefix THEN 4.0
                           WHEN lower(o.ship_to_name) LIKE @prefix THEN 3.0
                           ELSE 1.0
                       END AS rank,
                       COUNT(*) OVER () AS total_hits
                  FROM tdsynnex_orders o
                 WHERE o.search_text LIKE @like
            )
            SELECT id AS Id, order_number AS OrderNumber, customer_order_number AS CustomerOrderNumber,
                   status AS Status, ship_to_name AS ShipToName, order_created_utc AS OrderCreatedUtc,
                   first_ship_date AS FirstShipDate, total_net AS TotalNet, currency AS Currency,
                   rank::double precision AS Rank, total_hits AS TotalHits
              FROM hits
             ORDER BY {orderBy}
             LIMIT @limit OFFSET @offset
            """, new
        {
            q,
            prefix = EscapeLike(q) + "%",
            like = "%" + EscapeLike(q) + "%",
            limit,
            offset,
        }, cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: string.IsNullOrWhiteSpace(r.ShipToName) ? r.OrderNumber : $"{r.OrderNumber} · {r.ShipToName}",
            Snippet: r.CustomerOrderNumber is null ? null : $"PO {r.CustomerOrderNumber}",
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["orderNumber"] = r.OrderNumber,
                ["customerPo"] = r.CustomerOrderNumber,
                ["status"] = r.Status,
                ["shipTo"] = r.ShipToName,
                ["createdUtc"] = r.OrderCreatedUtc?.ToString("O", CultureInfo.InvariantCulture),
                ["eta"] = r.FirstShipDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            })).ToList();

        var total = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        return new SearchGroup(Kind, hits, total, total > offset + hits.Count);
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class HitRow
    {
        public Guid Id { get; set; }
        public string OrderNumber { get; set; } = string.Empty;
        public string? CustomerOrderNumber { get; set; }
        public string? Status { get; set; }
        public string? ShipToName { get; set; }
        public DateTime? OrderCreatedUtc { get; set; }
        public DateTime? FirstShipDate { get; set; }
        public decimal? TotalNet { get; set; }
        public string? Currency { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
