using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Global-search source for the synced Microsoft 365 mailboxes shown under
/// Contracts → Microsoft 365 matching → a company. Row-level authorization: a
/// Customer principal never sees hits; an Agent/Admin sees hits only when their
/// own user row has <c>contracts_enabled = TRUE</c> — the same per-user feature
/// flag that gates the whole Contracts hub (and the matching page these
/// mailboxes belong to). The flag is checked inside <see cref="SearchAsync"/>.
/// Matches against display name, UPN and primary mail; a hit links to the
/// owning company so the agent lands on the mailbox list.
public sealed class M365MailboxSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public M365MailboxSearchSource(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public string Kind => SearchSourceKind.M365Mailboxes;

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

        // orderBy is a fixed switch over SearchSort — never user input — so
        // interpolating it into the SQL below carries no injection risk.
        var orderBy = request.Sort switch
        {
            SearchSort.Newest => "synced_utc DESC, object_id DESC",
            SearchSort.Oldest => "synced_utc ASC, object_id ASC",
            _ => "rank DESC, display_name ASC NULLS LAST",
        };

        var sql = $"""
            WITH hits AS (
                SELECT  m.company_id,
                        m.object_id,
                        m.display_name,
                        m.upn,
                        m.mail,
                        m.mailbox_type,
                        m.synced_utc,
                        co.name AS company_name,
                        CASE
                            WHEN m.display_name ILIKE @prefix THEN 4.0
                            WHEN m.upn          ILIKE @prefix THEN 3.0
                            WHEN m.display_name ILIKE @like   THEN 2.0
                            WHEN m.upn          ILIKE @like   THEN 1.5
                            WHEN m.mail         ILIKE @like   THEN 1.0
                            ELSE 0
                        END AS rank,
                        COUNT(*) OVER () AS total_hits
                  FROM m365_mailboxes m
                  JOIN companies co ON co.id = m.company_id
                 WHERE m.display_name ILIKE @like
                    OR m.upn          ILIKE @like
                    OR m.mail         ILIKE @like
            )
            SELECT  company_id   AS CompanyId,
                    object_id    AS ObjectId,
                    display_name AS DisplayName,
                    upn          AS Upn,
                    mail         AS Mail,
                    mailbox_type AS MailboxType,
                    company_name AS CompanyName,
                    rank::double precision AS Rank,
                    total_hits   AS TotalHits
              FROM hits
             ORDER BY {orderBy}
             LIMIT @limit OFFSET @offset;
            """;

        var rows = (await conn.QueryAsync<MailboxHitRow>(new CommandDefinition(
            sql,
            new
            {
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

    private SearchHit BuildHit(MailboxHitRow r)
    {
        var name = string.IsNullOrWhiteSpace(r.DisplayName) ? (r.Upn ?? r.Mail ?? "Mailbox") : r.DisplayName;
        var detail = string.IsNullOrWhiteSpace(r.Upn) ? r.Mail : r.Upn;

        return new SearchHit(
            Kind: Kind,
            // Link to the owning company so the agent lands on its mailbox list.
            EntityId: r.CompanyId.ToString(),
            Title: name!,
            Snippet: detail,
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["companyId"] = r.CompanyId.ToString(),
                ["companyName"] = r.CompanyName,
                ["upn"] = r.Upn,
                ["mail"] = r.Mail,
                ["mailboxType"] = r.MailboxType,
            });
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class MailboxHitRow
    {
        public Guid CompanyId { get; set; }
        public string ObjectId { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string? Upn { get; set; }
        public string? Mail { get; set; }
        public string? MailboxType { get; set; }
        public string? CompanyName { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
