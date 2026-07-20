using System.Text.RegularExpressions;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Settings;

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
    private readonly ISettingsService _settings;

    public TicketSearchSource(NpgsqlDataSource dataSource, ISettingsService settings)
    {
        _dataSource = dataSource;
        _settings = settings;
    }

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

        var raw = request.Query.Trim();
        var prefix = await GetPrefixAsync(ct);
        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);

        // Ticket-number shortcut: accept the configured reference form
        // ("Ticket#1042"), a bare "#1042", surrounding brackets, or plain
        // digits — all resolve to the same number probe so a pasted reference
        // ranks ticket #1042 (rank 10) above anything else.
        var refDigits = TicketReference.TryParseDigits(raw, prefix);
        long? numberProbe = refDigits is not null && long.TryParse(refDigits, out var n) ? n : null;
        // Zammad-number probe: keep the raw digit string so a Zammad ticket
        // whose number carried leading zeros ("00042") still matches against
        // the TEXT zammad_ticket_number column. Rank-boost is 8.0 below —
        // local number wins (rank 10) on ties, but imported tickets with the
        // same digit-sequence still surface just under.
        string? zammadNumberProbe = refDigits;

        // When the user pasted a reference that carried a marker (prefix word,
        // '#', or brackets) we skip FTS so the prefix word ("ticket") does not
        // match unrelated subjects. A BARE number keeps FTS so "1042" can still
        // surface a ticket whose body mentions 1042, preserving prior behaviour.
        var isBareNumber = raw.Length > 0 && raw.All(char.IsDigit);
        var skipFts = refDigits is not null && !isBareNumber;

        var normalized = raw.ToLowerInvariant();

        // Build a prefix-matching tsquery: each alphanumeric token gets a `:*`
        // suffix so "bench" matches "benchmark". User-typed `*` chars act as
        // word separators (so `*bench`, `bench*`, plain `bench` all behave
        // the same). Empty after sanitization → tsq is NULL and only the
        // number probe applies.
        var tsqueryText = skipFts ? string.Empty : BuildPrefixTsQuery(normalized);

        if (string.IsNullOrEmpty(tsqueryText) && numberProbe is null && zammadNumberProbe is null)
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        const string sql = """
            WITH q AS (
                SELECT CASE WHEN @tsqueryText = '' THEN NULL::tsquery
                            ELSE to_tsquery('simple', @tsqueryText) END AS tsq
            ),
            hits AS (
                -- Subject / local-number / Zammad-number hits. The
                -- zammad_ticket_number match-arm gives an "imported from
                -- Zammad #N" probe the same shortcut behaviour as the
                -- local-number probe — rank 8.0 keeps a real local #N
                -- (rank 10) above an imported one with the same digits.
                SELECT t.id, t.number, t.subject, t.queue_id, t.updated_utc,
                       t.requester_contact_id,
                       GREATEST(
                           COALESCE(ts_rank_cd(t.search_vector, (SELECT tsq FROM q)), 0),
                           CASE WHEN @numberProbe IS NOT NULL AND t.number = @numberProbe THEN 10.0 ELSE 0 END,
                           CASE WHEN @zammadNumberProbe IS NOT NULL AND t.zammad_ticket_number = @zammadNumberProbe THEN 8.0 ELSE 0 END
                       ) AS rank,
                       NULL::text AS body_snippet
                FROM tickets t
                WHERE t.is_deleted = FALSE
                  AND (@skipQueueFilter OR t.queue_id = ANY(@allowedQueues))
                  AND (
                        ((SELECT tsq FROM q) IS NOT NULL AND t.search_vector @@ (SELECT tsq FROM q))
                     OR (@numberProbe IS NOT NULL AND t.number = @numberProbe)
                     OR (@zammadNumberProbe IS NOT NULL AND t.zammad_ticket_number = @zammadNumberProbe)
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
                  AND (SELECT tsq FROM q) IS NOT NULL
                  AND tes.search_vector @@ (SELECT tsq FROM q)
            ),
            ranked AS (
                SELECT id, number, subject, queue_id, updated_utc, requester_contact_id,
                       MAX(rank) AS rank,
                       MAX(body_snippet) AS body_snippet
                FROM hits
                GROUP BY id, number, subject, queue_id, updated_utc, requester_contact_id
            ),
            enriched AS (
                SELECT r.id, r.number, r.subject, r.queue_id, r.updated_utc,
                       r.rank, r.body_snippet,
                       COALESCE(s.state_category IN ('Resolved','Closed'), FALSE) AS is_closed,
                       COALESCE(c.first_name || ' ' || c.last_name, c.email, '') AS requester_name,
                       COALESCE(co.name, '')  AS company_name,
                       COALESCE(s.name, '')   AS status_name,
                       COALESCE(s.color, '')  AS status_color
                FROM ranked r
                JOIN tickets t2        ON t2.id = r.id
                LEFT JOIN statuses s   ON s.id = t2.status_id
                LEFT JOIN contacts c   ON c.id = r.requester_contact_id
                LEFT JOIN companies co ON co.id = t2.company_id
            ),
            windowed AS (
                SELECT e.*,
                       ROW_NUMBER() OVER (PARTITION BY e.is_closed
                                          ORDER BY e.rank DESC, e.updated_utc DESC, e.id DESC) AS rn,
                       COUNT(*) OVER (PARTITION BY e.is_closed) AS partition_total,
                       COUNT(*) OVER () AS total_hits
                FROM enriched e
            )
            SELECT w.id             AS "Id",
                   w.number         AS "Number",
                   w.subject        AS "Subject",
                   w.queue_id       AS "QueueId",
                   w.updated_utc    AS "UpdatedUtc",
                   w.rank::double precision AS "Rank",
                   w.body_snippet   AS "BodySnippet",
                   w.is_closed      AS "IsClosed",
                   w.total_hits     AS "TotalHits",
                   w.partition_total AS "PartitionTotal",
                   w.requester_name AS "RequesterName",
                   w.company_name   AS "CompanyName",
                   w.status_name    AS "StatusName",
                   w.status_color   AS "StatusColor"
            FROM windowed w
            """;

        // Tail is assembled from fixed constants only (quick split vs a
        // whitelisted ORDER BY per SearchSort) — no user input is ever
        // concatenated into the SQL.
        string tail;
        if (request.QuickMode)
        {
            // Dropdown: top-N open AND top-N closed, open section first.
            tail = """

                WHERE w.rn <= @limit
                ORDER BY w.is_closed ASC, w.rn ASC;
                """;
        }
        else
        {
            var orderBy = request.Sort switch
            {
                SearchSort.Newest => "w.updated_utc DESC, w.id DESC",
                SearchSort.Oldest => "w.updated_utc ASC, w.id ASC",
                SearchSort.Status => "w.is_closed ASC, w.rank DESC, w.updated_utc DESC, w.id DESC",
                _ => "w.rank DESC, w.updated_utc DESC, w.id DESC",
            };
            tail = $"""

                ORDER BY {orderBy}
                LIMIT @limit OFFSET @offset;
                """;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<TicketHitRow>(new CommandDefinition(sql + tail, new
        {
            tsqueryText,
            numberProbe,
            zammadNumberProbe,
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
                ["statusName"] = string.IsNullOrWhiteSpace(r.StatusName) ? null : r.StatusName,
                ["statusColor"] = string.IsNullOrWhiteSpace(r.StatusColor) ? null : r.StatusColor,
                ["isClosed"] = r.IsClosed ? "true" : "false",
            })).ToList();

        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;

        if (request.QuickMode)
        {
            // Per-partition totals so the dropdown can show "+N more" under
            // both the open and the closed section independently.
            var openTotal = (int)(rows.FirstOrDefault(r => !r.IsClosed)?.PartitionTotal ?? 0);
            var closedTotal = (int)(rows.FirstOrDefault(r => r.IsClosed)?.PartitionTotal ?? 0);
            var hasMoreQuick = openTotal > rows.Count(r => !r.IsClosed)
                            || closedTotal > rows.Count(r => r.IsClosed);
            var partitionTotals = new Dictionary<string, int>
            {
                ["open"] = openTotal,
                ["closed"] = closedTotal,
            };
            return new SearchGroup(Kind, hits, totalInGroup, hasMoreQuick, partitionTotals);
        }

        var hasMore = totalInGroup > offset + hits.Count;
        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    private async Task<string> GetPrefixAsync(CancellationToken ct)
    {
        try
        {
            var raw = await _settings.GetAsync<string>(SettingKeys.Tickets.ReferencePrefix, ct);
            return string.IsNullOrWhiteSpace(raw) ? TicketReference.DefaultPrefix : raw;
        }
        catch
        {
            return TicketReference.DefaultPrefix;
        }
    }

    private static readonly Regex s_tokenPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    private static string BuildPrefixTsQuery(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        var tokens = s_tokenPattern.Matches(normalized)
            .Select(m => m.Value + ":*");

        return string.Join(" & ", tokens);
    }

    private sealed record TicketHitRow(
        Guid Id,
        long Number,
        string Subject,
        Guid QueueId,
        DateTime UpdatedUtc,
        double Rank,
        string? BodySnippet,
        bool IsClosed,
        long TotalHits,
        long PartitionTotal,
        string? RequesterName,
        string? CompanyName,
        string? StatusName,
        string? StatusColor);
}
