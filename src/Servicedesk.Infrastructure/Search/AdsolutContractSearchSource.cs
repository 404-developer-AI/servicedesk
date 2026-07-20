using System.Globalization;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Search;

/// Global-search source for mirrored Adsolut contracts (contracten) shown in the
/// Contracts overview (Contracts hub → Contracts overview). Row-level
/// authorization: a Customer principal never sees hits; an Agent/Admin sees hits
/// only when their own user row has <c>contracts_enabled = TRUE</c> — the same
/// per-user feature flag that gates the overview. The flag is checked inside
/// <see cref="SearchAsync"/> (a single cheap point lookup, only when the user
/// actually searches).
///
/// The admin's display status filter is honored: contracts whose state is not in
/// the selected set are hidden from search, exactly as they are hidden from the
/// overview. Matches against the contract title (description), customer name,
/// the linked company name + relation code, and document number.
public sealed class AdsolutContractSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ISettingsService _settings;

    public AdsolutContractSearchSource(NpgsqlDataSource dataSource, ISettingsService settings)
    {
        _dataSource = dataSource;
        _settings = settings;
    }

    public string Kind => SearchSourceKind.AdsolutContracts;

    public bool IsAvailableFor(SearchPrincipal principal) =>
        principal.IsAdmin || (principal.IsAgent && principal.HasFeature(SearchFeature.Contracts));

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

        // Feature-flag gate (contracts_enabled) is enforced by IsAvailableFor
        // via the principal — checked at the top of this method and by the
        // search façade before it ever queries this source.
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Display status filter (DISPLAY-ONLY — the mirror holds every status).
        var filterRaw = await _settings.GetAsync<string>(SettingKeys.Adsolut.ErpContractsStatusFilter, ct) ?? string.Empty;
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
            SearchSort.Newest => "end_date DESC NULLS LAST, doc_nr DESC NULLS LAST",
            SearchSort.Oldest => "end_date ASC NULLS FIRST, doc_nr ASC NULLS FIRST",
            _ => "rank DESC, end_date DESC NULLS LAST, doc_nr DESC NULLS LAST",
        };

        var sql = $"""
            WITH hits AS (
                SELECT  c.id,
                        c.doc_nr,
                        c.description,
                        c.customer_name,
                        co.id   AS company_id,
                        co.name AS company_name,
                        co.adsolut_number AS relation_code,
                        c.state_description,
                        c.state_code,
                        c.end_date,
                        c.total_excl_vat,
                        CASE
                            WHEN @docProbe IS NOT NULL AND c.doc_nr = @docProbe THEN 10.0
                            WHEN c.description     ILIKE @prefix THEN 4.0
                            WHEN co.name           ILIKE @prefix THEN 3.5
                            WHEN c.customer_name   ILIKE @prefix THEN 3.0
                            WHEN co.adsolut_number ILIKE @prefix THEN 2.5
                            WHEN c.description     ILIKE @like   THEN 2.0
                            WHEN co.name           ILIKE @like   THEN 1.5
                            WHEN c.customer_name   ILIKE @like   THEN 1.0
                            ELSE 0
                        END AS rank,
                        COUNT(*) OVER () AS total_hits
                  FROM adsolut_contracts c
                  -- Bridge to the local company via the relation CODE, not the
                  -- ERP customer GUID (which differs from companies.adsolut_id):
                  -- contract → adsolut_erp_customers (id → code) → companies.
                  LEFT JOIN adsolut_erp_customers ec ON ec.id = c.customer_adsolut_id
                  LEFT JOIN companies co ON co.adsolut_number = ec.code AND ec.code IS NOT NULL
                 WHERE (@statuses::text[] IS NULL OR c.state_code = ANY(@statuses::text[]))
                   AND ((@docProbe IS NOT NULL AND c.doc_nr = @docProbe)
                        OR c.description     ILIKE @like
                        OR c.customer_name   ILIKE @like
                        OR co.name           ILIKE @like
                        OR co.adsolut_number ILIKE @like)
            )
            SELECT  id                AS Id,
                    doc_nr            AS DocNr,
                    description       AS Description,
                    customer_name     AS CustomerName,
                    company_id        AS CompanyId,
                    company_name      AS CompanyName,
                    relation_code     AS RelationCode,
                    state_description AS StateDescription,
                    state_code        AS StateCode,
                    end_date          AS EndDate,
                    total_excl_vat    AS TotalExclVat,
                    rank::double precision AS Rank,
                    total_hits        AS TotalHits
              FROM hits
             ORDER BY {orderBy}
             LIMIT @limit OFFSET @offset;
            """;

        var rows = (await conn.QueryAsync<ContractHitRow>(new CommandDefinition(
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

    private SearchHit BuildHit(ContractHitRow r)
    {
        var title = string.IsNullOrWhiteSpace(r.Description)
            ? (r.DocNr?.ToString(CultureInfo.InvariantCulture) is { } d ? $"Contract {d}" : "Contract")
            : r.Description!;
        // Prefer the linked relation name; fall back to the copied customer name.
        var customer = !string.IsNullOrWhiteSpace(r.CompanyName) ? r.CompanyName
            : string.IsNullOrWhiteSpace(r.CustomerName) ? null : r.CustomerName;
        var date = r.EndDate is { } ed
            ? ed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

        return new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: title,
            Snippet: customer,
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["docNr"] = r.DocNr?.ToString(CultureInfo.InvariantCulture),
                ["customerName"] = customer,
                ["companyId"] = r.CompanyId?.ToString(),
                ["relationCode"] = r.RelationCode,
                ["stateCode"] = r.StateCode,
                ["stateDescription"] = r.StateDescription,
                ["endDate"] = date,
                ["totalExclVat"] = r.TotalExclVat.ToString(CultureInfo.InvariantCulture),
            });
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class ContractHitRow
    {
        public Guid Id { get; set; }
        public int? DocNr { get; set; }
        public string? Description { get; set; }
        public string? CustomerName { get; set; }
        public Guid? CompanyId { get; set; }
        public string? CompanyName { get; set; }
        public string? RelationCode { get; set; }
        public string? StateDescription { get; set; }
        public string? StateCode { get; set; }
        public DateTime? EndDate { get; set; }
        public decimal TotalExclVat { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
