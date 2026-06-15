using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Singleton sync-state row for the Articles mirror. Sealed class with get/set +
/// AS-PascalCase aliases per the project's Dapper conventions.
public sealed class AdsolutArticleSyncState
{
    public DateTime? LastFullSyncUtc { get; set; }
    public DateTime? LastDeltaSyncUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public int ArticlesSeen { get; set; }
    public int ArticlesUpserted { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}

/// Row for the Contract Articles list (overview + global search).
public sealed class AdsolutArticleRow
{
    public Guid Id { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? VatCode { get; set; }
    public string? VatRate { get; set; }
    public bool Active { get; set; }
    public DateTime? AdsolutCreatedUtc { get; set; }
    public DateTime? AdsolutLastModified { get; set; }
    public DateTime SyncedUtc { get; set; }
}

public sealed record AdsolutArticleListResult(
    IReadOnlyList<AdsolutArticleRow> Items,
    int Total,
    int Page,
    int PageSize);

public interface IAdsolutArticleRepository
{
    /// Upsert one article (no child rows — a flat catalogue record).
    Task UpsertAsync(AdsolutArticle article, CancellationToken ct = default);

    Task<AdsolutArticleSyncState?> GetSyncStateAsync(CancellationToken ct = default);
    Task SaveSyncStateAsync(AdsolutArticleSyncState state, CancellationToken ct = default);

    Task<int> GetCountAsync(CancellationToken ct = default);

    /// Paged list. <paramref name="search"/> matches code, name and
    /// description. <paramref name="sort"/> is a whitelisted key
    /// (code/name/active); <paramref name="dir"/> is "asc"/"desc".
    /// <paramref name="activeOnly"/> = true hides inactive articles.
    Task<AdsolutArticleListResult> ListAsync(
        string? search, int page, int pageSize, string? sort, string? dir,
        bool activeOnly, CancellationToken ct = default);

    /// One article by id (per-row resync read-back).
    Task<AdsolutArticleRow?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// The given articles by id (the M365 selection chips). Missing ids are
    /// silently skipped; ordered by code. Empty input → empty result.
    Task<IReadOnlyList<AdsolutArticleRow>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}

public sealed class AdsolutArticleRepository : IAdsolutArticleRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public AdsolutArticleRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task UpsertAsync(AdsolutArticle a, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_articles (
                id, code, name, description, vat_code, vat_rate, active,
                adsolut_created_utc, adsolut_last_modified, synced_utc
            ) VALUES (
                @Id, @Code, @Name, @Description, @VatCode, @VatRate, @Active,
                @AdsolutCreatedUtc, @AdsolutLastModified, now()
            )
            ON CONFLICT (id) DO UPDATE SET
                code                  = EXCLUDED.code,
                name                  = EXCLUDED.name,
                description           = EXCLUDED.description,
                vat_code              = EXCLUDED.vat_code,
                vat_rate              = EXCLUDED.vat_rate,
                active                = EXCLUDED.active,
                adsolut_created_utc   = EXCLUDED.adsolut_created_utc,
                adsolut_last_modified = EXCLUDED.adsolut_last_modified,
                synced_utc            = now()
            """,
            new
            {
                a.Id,
                a.Code,
                a.Name,
                a.Description,
                a.VatCode,
                a.VatRate,
                a.Active,
                AdsolutCreatedUtc = a.AdsolutCreatedUtc?.UtcDateTime,
                AdsolutLastModified = a.AdsolutLastModified?.UtcDateTime,
            },
            cancellationToken: ct));
    }

    public async Task<AdsolutArticleSyncState?> GetSyncStateAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AdsolutArticleSyncState>(new CommandDefinition(
            """
            SELECT
                last_full_sync_utc  AS LastFullSyncUtc,
                last_delta_sync_utc AS LastDeltaSyncUtc,
                last_error          AS LastError,
                last_error_utc      AS LastErrorUtc,
                articles_seen       AS ArticlesSeen,
                articles_upserted   AS ArticlesUpserted,
                updated_utc         AS UpdatedUtc
            FROM adsolut_article_sync_state
            WHERE id = 1
            """,
            cancellationToken: ct));
    }

    public async Task SaveSyncStateAsync(AdsolutArticleSyncState state, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO adsolut_article_sync_state (
                id, last_full_sync_utc, last_delta_sync_utc, last_error, last_error_utc,
                articles_seen, articles_upserted, updated_utc
            ) VALUES (
                1, @LastFullSyncUtc, @LastDeltaSyncUtc, @LastError, @LastErrorUtc,
                @ArticlesSeen, @ArticlesUpserted, now()
            )
            ON CONFLICT (id) DO UPDATE SET
                last_full_sync_utc  = EXCLUDED.last_full_sync_utc,
                last_delta_sync_utc = EXCLUDED.last_delta_sync_utc,
                last_error          = EXCLUDED.last_error,
                last_error_utc      = EXCLUDED.last_error_utc,
                articles_seen       = EXCLUDED.articles_seen,
                articles_upserted   = EXCLUDED.articles_upserted,
                updated_utc         = now()
            """,
            new
            {
                state.LastFullSyncUtc,
                state.LastDeltaSyncUtc,
                state.LastError,
                state.LastErrorUtc,
                state.ArticlesSeen,
                state.ArticlesUpserted,
            },
            cancellationToken: ct));
    }

    public async Task<int> GetCountAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*)::int FROM adsolut_articles",
            cancellationToken: ct));
    }

    private const string Columns = """
        id                    AS Id,
        code                  AS Code,
        name                  AS Name,
        description           AS Description,
        vat_code              AS VatCode,
        vat_rate              AS VatRate,
        active                AS Active,
        adsolut_created_utc   AS AdsolutCreatedUtc,
        adsolut_last_modified AS AdsolutLastModified,
        synced_utc            AS SyncedUtc
        """;

    public async Task<AdsolutArticleListResult> ListAsync(
        string? search, int page, int pageSize, string? sort, string? dir,
        bool activeOnly, CancellationToken ct = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (safePage - 1) * safePageSize;
        var term = (search ?? string.Empty).Trim();
        var hasSearch = term.Length > 0;
        var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

        var sortExpr = (sort ?? "code").Trim().ToLowerInvariant() switch
        {
            "name" => "name",
            "active" => "active",
            _ => "code",
        };
        var dirSql = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

        const string whereClause = """
            WHERE (@ActiveOnly = FALSE OR active = TRUE)
              AND (@HasSearch = FALSE
                   OR code        ILIKE @Like
                   OR name        ILIKE @Like
                   OR description ILIKE @Like)
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*)::int FROM adsolut_articles {whereClause}",
            new { ActiveOnly = activeOnly, HasSearch = hasSearch, Like = like },
            cancellationToken: ct));

        var items = await conn.QueryAsync<AdsolutArticleRow>(new CommandDefinition(
            $"""
            SELECT {Columns}
            FROM adsolut_articles
            {whereClause}
            ORDER BY {sortExpr} {dirSql} NULLS LAST, code ASC NULLS LAST
            LIMIT @Limit OFFSET @Offset
            """,
            new { ActiveOnly = activeOnly, HasSearch = hasSearch, Like = like, Limit = safePageSize, Offset = offset },
            cancellationToken: ct));

        return new AdsolutArticleListResult(items.ToList(), total, safePage, safePageSize);
    }

    public async Task<AdsolutArticleRow?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<AdsolutArticleRow>(new CommandDefinition(
            $"SELECT {Columns} FROM adsolut_articles WHERE id = @id",
            new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AdsolutArticleRow>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Array.Empty<AdsolutArticleRow>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdsolutArticleRow>(new CommandDefinition(
            $"""
            SELECT {Columns}
            FROM adsolut_articles
            WHERE id = ANY(@Ids::uuid[])
            ORDER BY code ASC NULLS LAST
            """,
            new { Ids = ids.ToArray() }, cancellationToken: ct));
        return rows.ToList();
    }
}
