using System.Globalization;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Global-search source for the shared Employee Feedback board. Row-level
/// authorization mirrors the board itself: a Customer never sees hits; an
/// Agent/Admin sees hits only when allowed to open the board — Admins always,
/// other agents only when their own user row has <c>feedback_enabled = TRUE</c>.
/// The flag is checked inside <see cref="SearchAsync"/> (a single cheap point
/// lookup, only when the user actually searches). Because the board is shared,
/// an authorized user sees every entry — there is no per-row owner scope.
///
/// Matches against the feedback body (tags stripped), the employee email, and
/// the work-point type name.
public sealed class FeedbackSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public FeedbackSearchSource(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public string Kind => SearchSourceKind.EmployeeFeedback;

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

        // Feature-flag gate: a non-admin without feedback_enabled gets zero
        // hits, exactly as the board is hidden from them. Admins always pass.
        if (!principal.IsAdmin)
        {
            var enabled = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT COALESCE(feedback_enabled, FALSE) FROM users WHERE id = @userId",
                new { userId = principal.UserId },
                cancellationToken: ct));
            if (!enabled)
                return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);
        }

        const string sql = """
            WITH hits AS (
                SELECT  e.id,
                        e.entry_date,
                        tu.email           AS target_email,
                        wt.name            AS type_name,
                        regexp_replace(e.body_html, '<[^>]*>', ' ', 'g') AS body_text,
                        CASE
                            WHEN tu.email   ILIKE @prefix THEN 4.0
                            WHEN wt.name    ILIKE @prefix THEN 3.0
                            WHEN regexp_replace(e.body_html, '<[^>]*>', ' ', 'g') ILIKE @prefix THEN 2.0
                            ELSE 1.0
                        END AS rank,
                        COUNT(*) OVER () AS total_hits
                  FROM feedback_entries e
                  JOIN users tu ON tu.id = e.target_user_id
                  LEFT JOIN feedback_work_point_types wt ON wt.id = e.work_point_type_id
                 WHERE tu.email ILIKE @like
                    OR wt.name   ILIKE @like
                    OR regexp_replace(e.body_html, '<[^>]*>', ' ', 'g') ILIKE @like
            )
            SELECT  id           AS Id,
                    entry_date   AS EntryDate,
                    target_email AS TargetEmail,
                    type_name    AS TypeName,
                    body_text    AS BodyText,
                    rank::double precision AS Rank,
                    total_hits   AS TotalHits
              FROM hits
             ORDER BY rank DESC, entry_date DESC, id DESC
             LIMIT @limit OFFSET @offset;
            """;

        var rows = (await conn.QueryAsync<FeedbackHitRow>(new CommandDefinition(
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

    private SearchHit BuildHit(FeedbackHitRow r)
    {
        var date = r.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var title = $"{date} · {r.TargetEmail}";
        var snippet = Snippet(r.BodyText);

        return new SearchHit(
            Kind: Kind,
            EntityId: r.Id.ToString(),
            Title: title,
            Snippet: string.IsNullOrWhiteSpace(snippet) ? r.TypeName : snippet,
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["entryDate"] = date,
                ["targetEmail"] = r.TargetEmail,
                ["typeName"] = r.TypeName,
            });
    }

    private static string Snippet(string? bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText)) return string.Empty;
        var collapsed = System.Text.RegularExpressions.Regex.Replace(bodyText.Trim(), "\\s+", " ");
        return collapsed.Length <= 120 ? collapsed : collapsed[..120] + "…";
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private sealed class FeedbackHitRow
    {
        public Guid Id { get; set; }
        public DateTime EntryDate { get; set; }
        public string TargetEmail { get; set; } = "";
        public string? TypeName { get; set; }
        public string? BodyText { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
