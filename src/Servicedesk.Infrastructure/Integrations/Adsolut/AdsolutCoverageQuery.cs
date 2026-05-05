using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// v0.0.30 — backs the "Sync coverage" tile + overview page on the
/// Adsolut-settings surface. Pure SQL, no business logic. Buckets are
/// derivable from existing columns so this query group is zero-schema.
///
/// All five buckets filter <c>is_active = TRUE</c> on both companies
/// and contacts. Soft-deleted rows are out of scope for the helpdesk
/// + Adsolut sync surface; surfacing them in the coverage tile would
/// give admins a "fix me" call-to-action they can't action without
/// first restoring the row.
public sealed class AdsolutCoverageQuery : IAdsolutCoverageQuery
{
    private readonly NpgsqlDataSource _dataSource;

    public AdsolutCoverageQuery(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<AdsolutCoverageCounts> GetCountsAsync(CancellationToken ct = default)
    {
        // One round-trip that fills the tile. Each scalar comes from a
        // partial index or an indexable predicate, so a moderate dataset
        // (10K–1M companies/contacts) hits every count in milliseconds.
        // Note: ContactsPureSd uses NOT EXISTS over contact_companies
        // discriminating on adsolut_active IS NOT NULL — picks both
        // currently-active links and hard-deleted (UUID-nulled) historical
        // links, matching the v0.0.28 lessons-learned about "ever was
        // Adsolut-aware" (see Adsolut.md → Lessons learned #5).
        // Postgres COUNT(*) returns bigint; cast to int4 so Dapper does not
        // see Int64 columns against Int32 properties on AdsolutCoverageCounts.
        // Counts are bounded by row-counts on local tables — we'll never
        // approach 2.1B per bucket, so int is honest. Even if a future
        // install does, Postgres throws on overflow at the cast site, which
        // is a clearer surface than a silent Int64→Int32 truncation.
        const string sql = """
            SELECT
              (SELECT COUNT(*)::int FROM companies
                 WHERE is_active = TRUE
                   AND adsolut_id IS NULL) AS CompaniesSdOnly,
              (SELECT COUNT(*)::int FROM companies
                 WHERE is_active = TRUE
                   AND adsolut_id IS NOT NULL
                   AND adsolut_last_modified IS NOT NULL
                   AND updated_utc > adsolut_last_modified) AS CompaniesDrift,
              (SELECT COUNT(*)::int
                 FROM contact_companies cc
                 JOIN contacts  c  ON c.id  = cc.contact_id
                 JOIN companies co ON co.id = cc.company_id
                 WHERE c.is_active = TRUE
                   AND co.is_active = TRUE
                   AND co.adsolut_id IS NOT NULL
                   AND cc.adsolut_contact_id IS NULL) AS ContactLinksUnsynced,
              (SELECT COUNT(*)::int
                 FROM contact_companies cc
                 JOIN contacts  c  ON c.id  = cc.contact_id
                 JOIN companies co ON co.id = cc.company_id
                 WHERE c.is_active = TRUE
                   AND co.is_active = TRUE
                   AND cc.adsolut_contact_id IS NOT NULL
                   AND cc.adsolut_last_modified IS NOT NULL
                   AND c.updated_utc > cc.adsolut_last_modified) AS ContactLinksDrift,
              (SELECT COUNT(*)::int
                 FROM contacts c
                 WHERE c.is_active = TRUE
                   AND NOT EXISTS (
                     SELECT 1 FROM contact_companies cc
                     WHERE cc.contact_id = c.id
                       AND cc.adsolut_active IS NOT NULL
                   )) AS ContactsPureSd
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<AdsolutCoverageCounts>(
            new CommandDefinition(sql, cancellationToken: ct));
        return row;
    }

    public async Task<AdsolutCoveragePage<AdsolutCoverageCompanyRow>> ListCompaniesAsync(
        AdsolutCoverageCompaniesBucket bucket,
        string? search,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var (clampedPage, clampedSize, offset) = ClampPaging(page, pageSize);
        var bucketWhere = bucket switch
        {
            AdsolutCoverageCompaniesBucket.SdOnly =>
                "adsolut_id IS NULL",
            AdsolutCoverageCompaniesBucket.Drift =>
                "adsolut_id IS NOT NULL AND adsolut_last_modified IS NOT NULL AND updated_utc > adsolut_last_modified",
            _ => throw new ArgumentOutOfRangeException(nameof(bucket)),
        };

        // Search-string is matched against name (CITEXT) + code + email +
        // vat_number with a substring LIKE; the existing trigram indexes
        // accelerate the name match at moderate row counts.
        var searchClause = string.IsNullOrWhiteSpace(search)
            ? string.Empty
            : "AND (name ILIKE @Search OR code ILIKE @Search OR email ILIKE @Search OR vat_number ILIKE @Search)";

        var sql = $"""
            SELECT
              id                    AS Id,
              name                  AS Name,
              code                  AS Code,
              email                 AS Email,
              adsolut_id            AS AdsolutId,
              adsolut_number        AS AdsolutNumber,
              adsolut_last_modified AS AdsolutLastModified,
              updated_utc           AS UpdatedUtc,
              COUNT(*) OVER ()      AS TotalCount
            FROM companies
            WHERE is_active = TRUE
              AND ({bucketWhere})
              {searchClause}
            ORDER BY updated_utc DESC, id DESC
            LIMIT @PageSize OFFSET @Offset
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<CompanyRowWithTotal>(
            new CommandDefinition(
                sql,
                new
                {
                    Search = "%" + (search ?? string.Empty).Trim() + "%",
                    PageSize = clampedSize,
                    Offset = offset,
                },
                cancellationToken: ct))).AsList();

        var total = rows.Count > 0 ? rows[0].TotalCount : 0;
        // Slice the bookkeeping field off — callers see the public shape,
        // not the SQL-window-aggregate-leak from the COUNT(*) OVER ().
        var items = rows
            .Select(r => new AdsolutCoverageCompanyRow
            {
                Id = r.Id,
                Name = r.Name,
                Code = r.Code,
                Email = r.Email,
                AdsolutId = r.AdsolutId,
                AdsolutNumber = r.AdsolutNumber,
                AdsolutLastModified = r.AdsolutLastModified,
                UpdatedUtc = r.UpdatedUtc,
            })
            .ToList();
        return new AdsolutCoveragePage<AdsolutCoverageCompanyRow>(
            items,
            total,
            clampedPage,
            clampedSize);
    }

    public async Task<AdsolutCoveragePage<AdsolutCoverageContactRow>> ListContactsAsync(
        AdsolutCoverageContactsBucket bucket,
        string? search,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var (clampedPage, clampedSize, offset) = ClampPaging(page, pageSize);

        // The link-buckets project from contact_companies (one row per
        // link) so the same email under three companies surfaces as three
        // rows; the pure-SD bucket projects from contacts (one row per
        // person) because there is no link to anchor on. The shape is
        // unified via a CTE so the caller can render one column-set.
        var (sql, parameters) = bucket switch
        {
            AdsolutCoverageContactsBucket.LinksUnsynced => BuildLinkBucket(
                "cc.adsolut_contact_id IS NULL",
                requireCompanyAdsolut: true,
                search,
                clampedSize,
                offset),
            AdsolutCoverageContactsBucket.LinksDrift => BuildLinkBucket(
                "cc.adsolut_contact_id IS NOT NULL AND cc.adsolut_last_modified IS NOT NULL AND c.updated_utc > cc.adsolut_last_modified",
                requireCompanyAdsolut: false,
                search,
                clampedSize,
                offset),
            AdsolutCoverageContactsBucket.PureSd => BuildPureSdBucket(search, clampedSize, offset),
            _ => throw new ArgumentOutOfRangeException(nameof(bucket)),
        };

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<ContactRowWithTotal>(
            new CommandDefinition(sql, parameters, cancellationToken: ct))).AsList();

        var total = rows.Count > 0 ? rows[0].TotalCount : 0;
        var items = rows
            .Select(r => new AdsolutCoverageContactRow
            {
                LinkId = r.LinkId,
                ContactId = r.ContactId,
                Email = r.Email,
                FirstName = r.FirstName,
                LastName = r.LastName,
                CompanyId = r.CompanyId,
                CompanyName = r.CompanyName,
                CompanyAdsolutId = r.CompanyAdsolutId,
                AdsolutContactId = r.AdsolutContactId,
                AdsolutLastModified = r.AdsolutLastModified,
                ContactUpdatedUtc = r.ContactUpdatedUtc,
            })
            .ToList();
        return new AdsolutCoveragePage<AdsolutCoverageContactRow>(
            items,
            total,
            clampedPage,
            clampedSize);
    }

    private static (string Sql, object Parameters) BuildLinkBucket(
        string bucketWhere,
        bool requireCompanyAdsolut,
        string? search,
        int pageSize,
        int offset)
    {
        var searchClause = string.IsNullOrWhiteSpace(search)
            ? string.Empty
            : "AND (c.email ILIKE @Search OR c.first_name ILIKE @Search OR c.last_name ILIKE @Search OR co.name ILIKE @Search)";

        var companyAdsolutClause = requireCompanyAdsolut
            ? "AND co.adsolut_id IS NOT NULL"
            : string.Empty;

        var sql = $"""
            SELECT
              cc.id                    AS LinkId,
              c.id                     AS ContactId,
              c.email                  AS Email,
              c.first_name             AS FirstName,
              c.last_name              AS LastName,
              co.id                    AS CompanyId,
              co.name                  AS CompanyName,
              co.adsolut_id            AS CompanyAdsolutId,
              cc.adsolut_contact_id    AS AdsolutContactId,
              cc.adsolut_last_modified AS AdsolutLastModified,
              c.updated_utc            AS ContactUpdatedUtc,
              COUNT(*) OVER ()         AS TotalCount
            FROM contact_companies cc
            JOIN contacts  c  ON c.id  = cc.contact_id
            JOIN companies co ON co.id = cc.company_id
            WHERE c.is_active = TRUE
              AND co.is_active = TRUE
              {companyAdsolutClause}
              AND ({bucketWhere})
              {searchClause}
            ORDER BY c.updated_utc DESC, cc.id DESC
            LIMIT @PageSize OFFSET @Offset
            """;

        var parameters = new
        {
            Search = "%" + (search ?? string.Empty).Trim() + "%",
            PageSize = pageSize,
            Offset = offset,
        };
        return (sql, parameters);
    }

    private static (string Sql, object Parameters) BuildPureSdBucket(
        string? search,
        int pageSize,
        int offset)
    {
        // Pure-SD contacts have no link with adsolut_active stamped on
        // any link row (matches the v0.0.28 derive-rule for is_active —
        // see Adsolut.md → Lessons learned #5). The optional join to
        // contact_companies on role='primary' surfaces the primary
        // company name when present so the row reads "Wendy at Acme",
        // not just "Wendy".
        var searchClause = string.IsNullOrWhiteSpace(search)
            ? string.Empty
            : "AND (c.email ILIKE @Search OR c.first_name ILIKE @Search OR c.last_name ILIKE @Search OR co.name ILIKE @Search)";

        var sql = $"""
            SELECT
              cc.id                    AS LinkId,
              c.id                     AS ContactId,
              c.email                  AS Email,
              c.first_name             AS FirstName,
              c.last_name              AS LastName,
              co.id                    AS CompanyId,
              co.name                  AS CompanyName,
              co.adsolut_id            AS CompanyAdsolutId,
              NULL::uuid               AS AdsolutContactId,
              NULL::timestamptz        AS AdsolutLastModified,
              c.updated_utc            AS ContactUpdatedUtc,
              COUNT(*) OVER ()         AS TotalCount
            FROM contacts c
            LEFT JOIN contact_companies cc
              ON cc.contact_id = c.id AND cc.role = 'primary'
            LEFT JOIN companies co
              ON co.id = cc.company_id
            WHERE c.is_active = TRUE
              AND NOT EXISTS (
                SELECT 1 FROM contact_companies x
                WHERE x.contact_id = c.id
                  AND x.adsolut_active IS NOT NULL
              )
              {searchClause}
            ORDER BY c.updated_utc DESC, c.id DESC
            LIMIT @PageSize OFFSET @Offset
            """;

        var parameters = new
        {
            Search = "%" + (search ?? string.Empty).Trim() + "%",
            PageSize = pageSize,
            Offset = offset,
        };
        return (sql, parameters);
    }

    private static (int Page, int PageSize, int Offset) ClampPaging(int page, int pageSize)
    {
        var p = page < 1 ? 1 : page;
        var s = Math.Clamp(pageSize, 1, 200);
        var offset = (p - 1) * s;
        return (p, s, offset);
    }

    /// Dapper helpers — Dapper materializes by property name, so the
    /// COUNT(*) OVER () window-aggregate column needs to live on the
    /// concrete type the SELECT projects. Inheriting from the public row
    /// shape keeps the SQL → object map simple; we strip the bookkeeping
    /// field at the boundary so the API contract stays clean.
    private sealed class CompanyRowWithTotal : AdsolutCoverageCompanyRow
    {
        public int TotalCount { get; set; }
    }

    private sealed class ContactRowWithTotal : AdsolutCoverageContactRow
    {
        public int TotalCount { get; set; }
    }
}
