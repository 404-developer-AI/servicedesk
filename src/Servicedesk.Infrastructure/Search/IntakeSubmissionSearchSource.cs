using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Agent + Admin search over submitted intake-form answers. Answers are
/// stored as JSONB; we cast the whole payload to text for the trigram
/// match — good enough for typeahead without an extra search-vector table.
///
/// <para>Row-level scoping mirrors <see cref="TicketSearchSource"/>: admin
/// bypasses, agent is restricted to tickets on their accessible queues
/// (<see cref="SearchPrincipal.AllowedQueueIds"/>). An agent with zero
/// queues gets zero hits before we touch the DB — the same test-pattern
/// contact-search uses.</para>
public sealed class IntakeSubmissionSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public IntakeSubmissionSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.IntakeSubmissions;

    public bool IsAvailableFor(SearchPrincipal principal) =>
        principal.IsAdmin || principal.IsAgent;

    public async Task<SearchGroup> SearchAsync(SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (!IsAvailableFor(principal))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var allowedQueues = principal.AllowedQueueIds;
        if (!principal.IsAdmin && (allowedQueues is null || allowedQueues.Count == 0))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var normalized = request.Query.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);
        var filterQueues = principal.IsAdmin ? null : allowedQueues!.ToArray();

        // Match on template name + aggregated answer text. The GROUP BY
        // lets us include multiple answer rows per instance in the text
        // blob without expanding the result set. Trigram operators (%)
        // work against the concatenated blob for fuzzy matches; LIKE is
        // the short-query fallback.
        const string sql = """
            WITH q AS (SELECT lower(@query) AS norm),
            blob AS (
                SELECT i.id AS instance_id,
                       i.ticket_id,
                       i.submitted_event_id,
                       i.submitted_utc,
                       t.name AS template_name,
                       lower(t.name || ' ' || string_agg(a.answer_json::text, ' ')) AS text_blob
                FROM intake_form_instances i
                JOIN intake_templates t ON t.id = i.template_id
                JOIN intake_form_answers a ON a.instance_id = i.id
                JOIN tickets tk ON tk.id = i.ticket_id
                WHERE i.status = 'Submitted'
                  AND (@allAllowed = TRUE OR tk.queue_id = ANY(@allowedQueues))
                GROUP BY i.id, t.name
            ),
            hits AS (
                SELECT *,
                       GREATEST(
                           similarity(text_blob, (SELECT norm FROM q)),
                           CASE WHEN text_blob LIKE '%' || (SELECT norm FROM q) || '%' THEN 0.35 ELSE 0 END
                       ) AS rank,
                       COUNT(*) OVER () AS total_hits
                FROM blob
                WHERE text_blob % (SELECT norm FROM q)
                   OR text_blob LIKE '%' || (SELECT norm FROM q) || '%'
            )
            SELECT instance_id          AS InstanceId,
                   ticket_id            AS TicketId,
                   submitted_event_id   AS SubmittedEventId,
                   submitted_utc        AS SubmittedUtc,
                   template_name        AS TemplateName,
                   rank::double precision AS Rank,
                   total_hits           AS TotalHits
            FROM hits
            ORDER BY rank DESC, submitted_utc DESC NULLS LAST
            LIMIT @limit OFFSET @offset
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<SubmissionHit>(new CommandDefinition(
            sql,
            new
            {
                query = normalized,
                limit,
                offset,
                allAllowed = principal.IsAdmin,
                allowedQueues = filterQueues ?? Array.Empty<Guid>(),
            },
            cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.InstanceId.ToString(),
            Title: r.TemplateName,
            Snippet: null,
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["ticketId"] = r.TicketId.ToString(),
                ["eventId"] = r.SubmittedEventId?.ToString(),
                ["submittedUtc"] = r.SubmittedUtc?.ToString("O"),
            })).ToList();

        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var hasMore = totalInGroup > offset + hits.Count;
        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    private sealed record SubmissionHit(
        Guid InstanceId,
        Guid TicketId,
        long? SubmittedEventId,
        DateTime? SubmittedUtc,
        string TemplateName,
        double Rank,
        long TotalHits);
}
