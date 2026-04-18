using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Ticket + event FTS. Matches against:
///   - tickets.search_vector (subject)
///   - ticket_event_search.search_vector (body text, auto-filled by trigger)
///   - ticket number prefix (so "1042" finds ticket 1042)
/// Row-level scoping: admin bypasses; agent restricted to
/// <see cref="SearchPrincipal.AllowedQueueIds"/>. Customer is not
/// available in v1 (returns false from IsAvailableFor).
public sealed class TicketSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public TicketSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.Tickets;

    public bool IsAvailableFor(SearchPrincipal principal) =>
        principal.IsAdmin || principal.IsAgent;

    public async Task<SearchGroup> SearchAsync(
        SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (!IsAvailableFor(principal))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        // Agent with zero queues -> no hits, ever.
        var allowedQueues = principal.AllowedQueueIds;
        if (!principal.IsAdmin && (allowedQueues is null || allowedQueues.Count == 0))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var normalized = request.Query.Trim().ToLowerInvariant();
        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);

        // Ticket-number shortcut: if the whole query parses as a number, try
        // an exact-match probe first so "1042" ranks a matching ticket #1042
        // above anything else.
        long? numberProbe = long.TryParse(normalized, out var n) ? n : null;

        // websearch_to_tsquery tolerates bare user input (unquoted terms,
        // phrase-like usage) without throwing on special chars.
        const string sql = """
            WITH q AS (
                SELECT websearch_to_tsquery('simple', lower(@query)) AS tsq,
                       lower(@query)                                 AS norm
            ),
            hits AS (
                -- Subject / number hits
                SELECT t.id, t.number, t.subject, t.queue_id, t.updated_utc,
                       t.requester_contact_id,
                       GREATEST(
                           ts_rank_cd(t.search_vector, (SELECT tsq FROM q)),
                           CASE WHEN @numberProbe IS NOT NULL AND t.number = @numberProbe THEN 10.0 ELSE 0 END
                       ) AS rank,
                       NULL::text AS body_snippet
                FROM tickets t
                WHERE t.is_deleted = FALSE
                  AND (@skipQueueFilter OR t.queue_id = ANY(@allowedQueues))
                  AND (
                        t.search_vector @@ (SELECT tsq FROM q)
                     OR (@numberProbe IS NOT NULL AND t.number = @numberProbe)
                  )
                UNION ALL
                -- Body hits via ticket_event_search sidecar
                SELECT t.id, t.number, t.subject, t.queue_id, t.updated_utc,
                       t.requester_contact_id,
                       ts_rank_cd(tes.search_vector, (SELECT tsq FROM q)) AS rank,
                       ts_headline('simple', tes.normalized_text,
                                   (SELECT tsq FROM q),
                                   'MaxFragments=1, MaxWords=18, MinWords=5, ShortWord=2') AS body_snippet
                FROM ticket_event_search tes
                JOIN tickets t ON t.id = tes.ticket_id
                WHERE t.is_deleted = FALSE
                  AND (@skipQueueFilter OR t.queue_id = ANY(@allowedQueues))
                  AND tes.search_vector @@ (SELECT tsq FROM q)
            ),
            ranked AS (
                SELECT id, number, subject, queue_id, updated_utc, requester_contact_id,
                       MAX(rank) AS rank,
                       MAX(body_snippet) AS body_snippet,
                       COUNT(*) OVER () AS total_hits
                FROM hits
                GROUP BY id, number, subject, queue_id, updated_utc, requester_contact_id
            )
            SELECT r.id, r.number, r.subject,
                   r.queue_id       AS "QueueId",
                   r.updated_utc    AS "UpdatedUtc",
                   r.rank::double precision AS "Rank",
                   r.body_snippet   AS "BodySnippet",
                   r.total_hits     AS "TotalHits",
                   COALESCE(c.first_name || ' ' || c.last_name, c.email, '') AS "RequesterName",
                   COALESCE(co.name, '')  AS "CompanyName"
            FROM ranked r
            LEFT JOIN contacts c  ON c.id = r.requester_contact_id
            LEFT JOIN tickets t2  ON t2.id = r.id
            LEFT JOIN companies co ON co.id = t2.company_id
            ORDER BY r.rank DESC, r.updated_utc DESC, r.id DESC
            LIMIT @limit OFFSET @offset;
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<TicketHitRow>(new CommandDefinition(sql, new
        {
            query = normalized,
            numberProbe,
            skipQueueFilter = principal.IsAdmin,
            allowedQueues = allowedQueues?.ToArray() ?? Array.Empty<Guid>(),
            limit,
            offset,
        }, cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: $"#{r.Number} — {r.Subject}",
            Snippet: r.BodySnippet,
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["number"] = r.Number.ToString(),
                ["queueId"] = r.QueueId.ToString(),
                ["updatedUtc"] = r.UpdatedUtc.ToString("O"),
                ["requester"] = string.IsNullOrWhiteSpace(r.RequesterName) ? null : r.RequesterName.Trim(),
                ["company"] = string.IsNullOrWhiteSpace(r.CompanyName) ? null : r.CompanyName,
            })).ToList();

        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var hasMore = totalInGroup > offset + hits.Count;

        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    private sealed record TicketHitRow(
        Guid Id,
        long Number,
        string Subject,
        Guid QueueId,
        DateTime UpdatedUtc,
        double Rank,
        string? BodySnippet,
        long TotalHits,
        string? RequesterName,
        string? CompanyName);
}
