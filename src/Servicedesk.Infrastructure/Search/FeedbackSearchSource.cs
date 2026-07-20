using System.Globalization;
using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Global-search source for the Employee Feedback board. Row-level
/// authorization mirrors the board itself: a Customer never sees hits; an
/// Agent/Admin sees hits only when allowed to open the board — Admins always,
/// other agents only when their own user row carries a feedback flag. The flags
/// ride on the <see cref="SearchPrincipal"/> (resolved once when the principal
/// is built), so <see cref="IsAvailableFor"/> can keep this source out of the
/// search dropdown for users without it — no per-query DB lookup.
///
/// Two access scopes (v0.0.90): FULL users (<see cref="SearchFeature.Feedback"/>)
/// and Admins search every entry; RESTRICTED users
/// (<see cref="SearchFeature.FeedbackOwnOnly"/>) only ever match rows they
/// created themselves, enforced with a <c>created_by_user_id</c> filter below.
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
        principal.IsAdmin
        || (principal.IsAgent
            && (principal.HasFeature(SearchFeature.Feedback)
                || principal.HasFeature(SearchFeature.FeedbackOwnOnly)));

    /// True when this principal may only match its own rows: a restricted
    /// (own-only) agent. Admins and full-access agents see everything.
    private static bool IsOwnOnly(SearchPrincipal principal) =>
        !principal.IsAdmin
        && !principal.HasFeature(SearchFeature.Feedback)
        && principal.HasFeature(SearchFeature.FeedbackOwnOnly);

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

        // Feature-flag gate is enforced by IsAvailableFor via the principal —
        // checked at the top of this method and by the search façade before it
        // ever queries this source. Restricted (own-only) agents additionally
        // scope to their own rows via @actor; null = no owner restriction.
        Guid? actor = IsOwnOnly(principal) ? principal.UserId : null;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // orderBy is a fixed switch over SearchSort — never user input — so
        // interpolating it into the SQL below carries no injection risk.
        var orderBy = request.Sort switch
        {
            SearchSort.Newest => "entry_date DESC, id DESC",
            SearchSort.Oldest => "entry_date ASC, id ASC",
            _ => "rank DESC, entry_date DESC, id DESC",
        };

        var sql = $"""
            WITH hits AS (
                -- body_text is a STORED generated column (v0.0.93) — the
                -- HTML strip happens once at write time instead of per row
                -- per query, which used to be the most expensive pattern in
                -- the whole search fan-out.
                SELECT  e.id,
                        e.entry_date,
                        tu.email           AS target_email,
                        wt.name            AS type_name,
                        e.body_text,
                        CASE
                            WHEN tu.email    ILIKE @prefix THEN 4.0
                            WHEN wt.name     ILIKE @prefix THEN 3.0
                            WHEN e.body_text ILIKE @prefix THEN 2.0
                            ELSE 1.0
                        END AS rank,
                        COUNT(*) OVER () AS total_hits
                  FROM feedback_entries e
                  JOIN users tu ON tu.id = e.target_user_id
                  LEFT JOIN feedback_work_point_types wt ON wt.id = e.work_point_type_id
                 WHERE (@actor IS NULL OR e.created_by_user_id = @actor)
                   AND (tu.email    ILIKE @like
                    OR wt.name      ILIKE @like
                    OR e.body_text  ILIKE @like)
            )
            SELECT  id           AS Id,
                    entry_date   AS EntryDate,
                    target_email AS TargetEmail,
                    type_name    AS TypeName,
                    body_text    AS BodyText,
                    rank::double precision AS Rank,
                    total_hits   AS TotalHits
              FROM hits
             ORDER BY {orderBy}
             LIMIT @limit OFFSET @offset;
            """;

        var rows = (await conn.QueryAsync<FeedbackHitRow>(new CommandDefinition(
            sql,
            new
            {
                actor,
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
