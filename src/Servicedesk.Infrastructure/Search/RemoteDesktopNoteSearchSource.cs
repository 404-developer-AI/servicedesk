using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// v0.1.10: per-client notes on Assets → Remote Desktop. Matches the note
/// body (trigram-indexed) plus the owning TRMM client's name and code, so
/// "acme" finds every note written on the Acme client. A hit routes to
/// the Remote Desktop tab with that client's notes opened.
///
/// Row-level rules mirror <see cref="AssetSearchSource"/>: Customer
/// principals never see a hit (the notes are internal MSP chatter),
/// Agent/Admin see every note — the tab is shared across the team.
public sealed class RemoteDesktopNoteSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;

    public RemoteDesktopNoteSearchSource(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public string Kind => SearchSourceKind.RemoteDesktopNotes;

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

        // orderBy is a fixed switch over SearchSort — never user input.
        var orderBy = request.Sort switch
        {
            SearchSort.Newest => "created_utc DESC, id DESC",
            SearchSort.Oldest => "created_utc ASC, id ASC",
            _ => "rank DESC, created_utc DESC",
        };

        var sql = $"""
            WITH hits AS (
                SELECT n.id,
                       n.trmm_client_id,
                       n.body,
                       n.created_utc,
                       u.email AS author_email,
                       c.name  AS client_name,
                       c.code  AS client_code,
                       (
                           CASE WHEN n.body ILIKE @like THEN 1.0 ELSE 0 END
                         + CASE WHEN lower(n.body) % lower(@query) THEN similarity(lower(n.body), lower(@query)) ELSE 0 END
                         + CASE WHEN c.name ILIKE @like THEN 0.4 ELSE 0 END
                         + CASE WHEN coalesce(c.code, '') ILIKE @like THEN 0.5 ELSE 0 END
                       )::double precision AS rank,
                       COUNT(*) OVER () AS total_hits
                  FROM trmm_client_notes n
                  JOIN trmm_clients c ON c.trmm_client_id = n.trmm_client_id
                  LEFT JOIN users u   ON u.id = n.author_id
                 WHERE n.body ILIKE @like
                    OR lower(n.body) % lower(@query)
                    OR c.name ILIKE @like
                    OR coalesce(c.code, '') ILIKE @like
            )
            SELECT id             AS Id,
                   trmm_client_id AS TrmmClientId,
                   body           AS Body,
                   created_utc    AS CreatedUtc,
                   author_email   AS AuthorEmail,
                   client_name    AS ClientName,
                   client_code    AS ClientCode,
                   rank           AS Rank,
                   total_hits     AS TotalHits
              FROM hits
             WHERE rank > 0
             ORDER BY {orderBy}
             LIMIT @limit OFFSET @offset
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<NoteHitRow>(new CommandDefinition(
            sql,
            new { query = normalized, like = "%" + EscapeLike(normalized) + "%", limit, offset },
            cancellationToken: ct))).ToList();

        var hits = rows.Select(r => new SearchHit(
            Kind: Kind,
            EntityId: r.TrmmClientId.ToString(),
            Title: BuildTitle(r),
            Snippet: Truncate(r.Body, 160),
            Rank: r.Rank,
            Meta: new Dictionary<string, string?>
            {
                ["noteId"] = r.Id.ToString(),
                ["clientName"] = r.ClientName,
                ["clientCode"] = r.ClientCode,
                ["authorEmail"] = r.AuthorEmail,
                ["createdUtc"] = r.CreatedUtc.ToString("O"),
            })).ToList();

        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var hasMore = totalInGroup > offset + hits.Count;
        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    private static string BuildTitle(NoteHitRow r) =>
        string.IsNullOrWhiteSpace(r.ClientCode) ? r.ClientName : $"[{r.ClientCode}] {StripCode(r.ClientName)}";

    /// The TRMM display name already carries the "[CODE] " prefix; drop
    /// it so the title does not read "[ACME] [ACME] Acme".
    private static string StripCode(string name)
    {
        var t = name.TrimStart();
        if (t.Length > 0 && t[0] == '[')
        {
            var close = t.IndexOf(']');
            if (close > 0) return t[(close + 1)..].Trim();
        }
        return name;
    }

    private static string Truncate(string s, int max)
    {
        var flat = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static string EscapeLike(string s) =>
        s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private sealed class NoteHitRow
    {
        public Guid Id { get; set; }
        public long TrmmClientId { get; set; }
        public string Body { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public string? AuthorEmail { get; set; }
        public string ClientName { get; set; } = "";
        public string? ClientCode { get; set; }
        public double Rank { get; set; }
        public long TotalHits { get; set; }
    }
}
