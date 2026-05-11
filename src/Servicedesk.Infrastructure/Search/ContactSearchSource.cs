using Dapper;
using Npgsql;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Phones;

namespace Servicedesk.Infrastructure.Search;

/// Contact typeahead via pg_trgm similarity on email + full name, plus an
/// E.164 exact-match phone branch (v0.0.34). When the query parses as a
/// valid phone number in the install's default region, an exact hit on
/// phone_e164 or mobile_phone_e164 ranks above any fuzzy match — so
/// pasting "+32498123456" or "0498 12 34 56" instantly surfaces the
/// owning contact. Not exposed to Customer in v1 (the customer portal
/// lands together with the Companies/Users feature, which will re-scope
/// this source).
public sealed class ContactSearchSource : ISearchSource
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IContactPhoneNormalizer _phoneNormalizer;

    public ContactSearchSource(NpgsqlDataSource dataSource, IContactPhoneNormalizer phoneNormalizer)
    {
        _dataSource = dataSource;
        _phoneNormalizer = phoneNormalizer;
    }

    public string Kind => SearchSourceKind.Contacts;

    public bool IsAvailableFor(SearchPrincipal principal) =>
        principal.IsAdmin || principal.IsAgent;

    public async Task<SearchGroup> SearchAsync(
        SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (!IsAvailableFor(principal))
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var normalized = request.Query.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return new SearchGroup(Kind, Array.Empty<SearchHit>(), 0, false);

        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);

        // Try to interpret the query as a phone number, but only when it
        // looks phone-like — otherwise libphonenumber happily accepts a
        // string like "32498123456" (no leading +) as a valid international
        // number and the phone-branch would promote the wrong contact above
        // a legitimate email/name fuzzy hit. Pre-gate: must contain a "+" or
        // be at least 7 chars long, and never look like an email. Cheap
        // string checks beat a libphonenumber parse + DB-side equality probe
        // on every typeahead keystroke.
        var phoneE164 = LooksLikePhone(request.Query)
            ? await _phoneNormalizer.NormalizeAsync(request.Query, ct)
            : string.Empty;

        // similarity() returns 0..1; 0.25 is a balanced cut-off for
        // typeahead — strict enough to hide noise, lax enough to forgive
        // one typo. The phone branch is an exact-equality probe against
        // ix_contacts_phone_e164 / ix_contacts_mobile_phone_e164 (partial
        // indices skipping empty strings) — it returns rank 1.0 so a phone
        // hit always tops any fuzzy email/name hit.
        const string sql = """
            WITH q AS (
                SELECT lower(@query)  AS norm,
                       @phoneE164::text AS phone_e164
            ),
            hits AS (
                SELECT c.id, c.first_name, c.last_name, c.email,
                       cc.company_id AS company_id,
                       GREATEST(
                           CASE
                               WHEN (SELECT phone_e164 FROM q) <> ''
                                AND (c.phone_e164 = (SELECT phone_e164 FROM q)
                                  OR c.mobile_phone_e164 = (SELECT phone_e164 FROM q))
                               THEN 1.0
                               ELSE 0.0
                           END,
                           similarity(lower(c.email::text), (SELECT norm FROM q)),
                           similarity(lower(coalesce(c.first_name,'') || ' ' || coalesce(c.last_name,'')),
                                      (SELECT norm FROM q))
                       ) AS rank,
                       COUNT(*) OVER () AS total_hits
                FROM contacts c
                LEFT JOIN contact_companies cc ON cc.contact_id = c.id AND cc.role = 'primary'
                WHERE c.is_active = TRUE
                  AND (
                        lower(c.email::text) % (SELECT norm FROM q)
                     OR lower(coalesce(c.first_name,'') || ' ' || coalesce(c.last_name,'')) % (SELECT norm FROM q)
                     OR lower(c.email::text) LIKE '%' || (SELECT norm FROM q) || '%'
                     OR lower(coalesce(c.first_name,'') || ' ' || coalesce(c.last_name,'')) LIKE '%' || (SELECT norm FROM q) || '%'
                     OR (
                            (SELECT phone_e164 FROM q) <> ''
                        AND (c.phone_e164 = (SELECT phone_e164 FROM q)
                          OR c.mobile_phone_e164 = (SELECT phone_e164 FROM q))
                        )
                  )
            )
            SELECT id,
                   first_name  AS "FirstName",
                   last_name   AS "LastName",
                   email,
                   company_id  AS "CompanyId",
                   rank::double precision AS "Rank",
                   total_hits  AS "TotalHits"
            FROM hits
            ORDER BY rank DESC, last_name, first_name
            LIMIT @limit OFFSET @offset;
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<ContactHitRow>(new CommandDefinition(sql,
            new { query = normalized, phoneE164, limit, offset },
            cancellationToken: ct))).ToList();

        var hits = rows.Select(r =>
        {
            var fullName = $"{r.FirstName} {r.LastName}".Trim();
            var title = string.IsNullOrWhiteSpace(fullName) ? r.Email : $"{fullName} — {r.Email}";
            return new SearchHit(
                Kind: Kind,
                EntityId: r.Id.ToString(),
                Title: title,
                Snippet: null,
                Rank: r.Rank,
                Meta: new Dictionary<string, string?>
                {
                    ["email"] = r.Email,
                    ["companyId"] = r.CompanyId?.ToString(),
                });
        }).ToList();

        var totalInGroup = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var hasMore = totalInGroup > offset + hits.Count;
        return new SearchGroup(Kind, hits, totalInGroup, hasMore);
    }

    /// Heuristic phone-likeness gate. A leading "+" is the strongest signal
    /// — once present the user clearly intends a phone number, no further
    /// checks. Without a "+", require at least 7 characters AND no "@" (so
    /// "alice@example.com" never enters the phone branch even though it
    /// contains digits). Conservative on purpose: a false negative just
    /// means the user gets fuzzy email/name results, which is graceful;
    /// a false positive promotes the wrong contact to the top.
    internal static bool LooksLikePhone(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var trimmed = query.Trim();
        if (trimmed.StartsWith('+')) return true;
        if (trimmed.Contains('@')) return false;
        return trimmed.Length >= 7;
    }

    private sealed record ContactHitRow(
        Guid Id,
        string FirstName,
        string LastName,
        string Email,
        Guid? CompanyId,
        double Rank,
        long TotalHits);
}
