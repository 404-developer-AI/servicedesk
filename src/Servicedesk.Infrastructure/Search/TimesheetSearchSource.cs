using System.Globalization;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// v0.0.35 commit H — timesheet-entry search. Customer principal sees zero
/// hits. For Agent/Admin the row-level rule is:
///   - <c>timesheet_manager = TRUE</c> on the principal's user row → all
///     entries are searchable;
///   - otherwise the principal sees only their own entries
///     (<c>e.user_id = @principalUserId</c>).
///
/// The flag is read inside <see cref="SearchAsync"/> rather than carried on
/// <see cref="SearchPrincipal"/> — it's a single boolean per query and
/// keeping it out of the principal avoids bleeding feature-specific
/// concerns into a cross-cutting type. The cost (one extra cheap point
/// lookup) only applies when the user actually types in the search bar.
///
/// Matches against description, task name, and ticket subject/number. ILIKE
/// is fine here: the row-set is bounded by user (or by the install size for
/// managers) and the query is gated by SearchSettings.MinQueryLength.
public sealed class TimesheetSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public TimesheetSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.Timesheet;

    public bool IsAvailableFor(SearchPrincipal principal) =>
        principal.IsAdmin || principal.IsAgent;

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

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Manager-flag controls whether the user-scope filter applies.
        // Admins are NOT auto-elevated — the manager flag is orthogonal to
        // role; matches the manager-endpoint authorization in
        // TimesheetManagerEndpoints.EnsureManagerAsync.
        var isManager = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT COALESCE(timesheet_manager, FALSE) FROM users WHERE id = @userId",
            new { userId = principal.UserId },
            cancellationToken: ct));

        // Ticket-number probe: bare digits → exact-match against tickets.number.
        long? numberProbe = long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

        // orderBy is a fixed switch over SearchSort — never user input — so
        // interpolating it into the SQL below carries no injection risk.
        var orderBy = request.Sort switch
        {
            SearchSort.Newest => "entry_date DESC, start_minutes DESC",
            SearchSort.Oldest => "entry_date ASC, start_minutes ASC",
            _ => "rank DESC, entry_date DESC, start_minutes DESC",
        };

        var sql = $"""
            WITH hits AS (
                SELECT  e.id,
                        e.user_id,
                        u.email           AS user_email,
                        e.entry_date,
                        e.start_minutes,
                        e.end_minutes,
                        e.minutes,
                        e.description,
                        t.name            AS task_name,
                        t.is_absence      AS task_is_absence,
                        e.ticket_id,
                        k.number          AS ticket_number,
                        k.subject         AS ticket_subject,
                        CASE
                            WHEN @numberProbe IS NOT NULL AND k.number = @numberProbe THEN 10.0
                            WHEN e.description ILIKE @prefix THEN 3.0
                            WHEN k.subject     ILIKE @prefix THEN 2.5
                            WHEN t.name        ILIKE @prefix THEN 2.0
                            WHEN e.description ILIKE @like   THEN 1.5
                            WHEN k.subject     ILIKE @like   THEN 1.2
                            WHEN t.name        ILIKE @like   THEN 1.0
                            ELSE 0
                        END AS rank,
                        COUNT(*) OVER () AS total_hits
                  FROM timesheet_entries e
                  JOIN timesheet_tasks   t ON t.id = e.task_id
                  JOIN users             u ON u.id = e.user_id
                  LEFT JOIN tickets      k ON k.id = e.ticket_id
                 WHERE (@isManager OR e.user_id = @principalUserId)
                   AND (
                          (@numberProbe IS NOT NULL AND k.number = @numberProbe)
                       OR e.description ILIKE @like
                       OR k.subject     ILIKE @like
                       OR t.name        ILIKE @like
                       )
            )
            SELECT  id                AS Id,
                    user_id           AS UserId,
                    user_email        AS UserEmail,
                    entry_date        AS EntryDate,
                    start_minutes     AS StartMinutes,
                    end_minutes       AS EndMinutes,
                    minutes           AS Minutes,
                    description       AS Description,
                    task_name         AS TaskName,
                    task_is_absence   AS TaskIsAbsence,
                    ticket_id         AS TicketId,
                    ticket_number     AS TicketNumber,
                    ticket_subject    AS TicketSubject,
                    rank::double precision AS Rank,
                    total_hits        AS TotalHits
              FROM hits
             ORDER BY {orderBy}
             LIMIT @limit OFFSET @offset;
            """;

        var rows = (await conn.QueryAsync<TimesheetHitRow>(new CommandDefinition(
            sql,
            new
            {
                isManager,
                principalUserId = principal.UserId,
                numberProbe,
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

    private SearchHit BuildHit(TimesheetHitRow r)
    {
        var date = DateOnly.FromDateTime(r.EntryDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var start = FormatHHMM(r.StartMinutes);
        var end = FormatHHMM(r.EndMinutes);
        var taskLabel = string.IsNullOrWhiteSpace(r.TaskName) ? "—" : r.TaskName;
        var title = $"{date} {start}–{end} · {taskLabel}";

        return new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: title,
            Snippet: r.Description,
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["userId"] = r.UserId.ToString(),
                ["userEmail"] = r.UserEmail,
                ["entryDate"] = date,
                ["minutes"] = r.Minutes.ToString(CultureInfo.InvariantCulture),
                ["taskName"] = r.TaskName,
                ["taskIsAbsence"] = r.TaskIsAbsence ? "true" : "false",
                ["ticketId"] = r.TicketId?.ToString(),
                ["ticketNumber"] = r.TicketNumber?.ToString(CultureInfo.InvariantCulture),
                ["ticketSubject"] = r.TicketSubject,
            });
    }

    private static string FormatHHMM(int minutes)
    {
        var safe = Math.Max(0, Math.Min(1440, minutes));
        var hh = safe / 60;
        var mm = safe % 60;
        return $"{hh:D2}:{mm:D2}";
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class TimesheetHitRow
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string UserEmail { get; set; } = "";
        public DateTime EntryDate { get; set; }
        public int StartMinutes { get; set; }
        public int EndMinutes { get; set; }
        public int Minutes { get; set; }
        public string Description { get; set; } = "";
        public string TaskName { get; set; } = "";
        public bool TaskIsAbsence { get; set; }
        public Guid? TicketId { get; set; }
        public long? TicketNumber { get; set; }
        public string? TicketSubject { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
