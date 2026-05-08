using Dapper;
using Npgsql;
using Servicedesk.Domain.KnowledgeBase;

namespace Servicedesk.Infrastructure.Persistence.KnowledgeBase;

public sealed class KbArticleRepository : IKbArticleRepository
{
    private const string ArticleCols = """
        id AS Id, section_id AS SectionId, slug AS Slug, status AS Status,
        is_featured AS IsFeatured, editor_notes AS EditorNotes, position AS Position,
        publish_at AS PublishAt, archive_at AS ArchiveAt,
        last_status_changed_utc AS LastStatusChangedUtc,
        last_status_changed_by_user_id AS LastStatusChangedByUserId,
        created_utc AS CreatedUtc, updated_utc AS UpdatedUtc,
        created_by_user_id AS CreatedByUserId, updated_by_user_id AS UpdatedByUserId
        """;

    private const string TranslationCols = """
        id AS Id, article_id AS ArticleId, locale_code AS LocaleCode,
        title AS Title, body_html AS BodyHtml, body_text AS BodyText,
        created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
        """;

    private readonly NpgsqlDataSource _dataSource;

    public KbArticleRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<KbArticleListPage> ListArticlesAsync(
        Guid? sectionId, string? status, string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (page - 1) * pageSize;

        var statusFilter = status switch
        {
            KbArticleStatus.Draft or KbArticleStatus.Internal
                or KbArticleStatus.Published or KbArticleStatus.Archived => status,
            _ => null,
        };

        // Title surfaced via the default-locale translation. When a translation
        // is missing for the default locale, the COALESCE falls back to slug
        // so the listing never blanks out.
        var sql = $"""
            SELECT a.id AS Id, a.section_id AS SectionId, a.slug AS Slug, a.status AS Status,
                   a.is_featured AS IsFeatured, a.position AS Position,
                   a.created_utc AS CreatedUtc, a.updated_utc AS UpdatedUtc,
                   a.last_status_changed_utc AS LastStatusChangedUtc,
                   a.last_status_changed_by_user_id AS LastStatusChangedByUserId,
                   COALESCE(t.title, a.slug::text) AS Title,
                   COUNT(*) OVER () AS TotalHits
              FROM kb_articles a
              JOIN knowledge_base kb ON TRUE
              LEFT JOIN kb_article_translations t
                ON t.article_id = a.id AND t.locale_code = kb.default_locale_code
             WHERE 1=1
            """;

        if (sectionId.HasValue) sql += " AND a.section_id = @sectionId";
        if (statusFilter is not null) sql += " AND a.status = @statusFilter";
        if (!string.IsNullOrWhiteSpace(search))
        {
            sql += """
                 AND (
                    a.slug::text ILIKE @search
                    OR t.title ILIKE @search
                 )
                """;
        }

        sql += " ORDER BY a.position, a.updated_utc DESC LIMIT @pageSize OFFSET @offset";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<ListRow>(new CommandDefinition(
            sql,
            new { sectionId, statusFilter, search = $"%{search}%", pageSize, offset },
            cancellationToken: ct))).ToList();

        var total = rows.Count > 0 ? (int)rows[0].TotalHits : 0;
        var items = rows.Select(r => new KbArticleListItem(
            r.Id, r.SectionId, r.Slug, r.Status, r.IsFeatured, r.Position,
            r.CreatedUtc, r.UpdatedUtc, r.LastStatusChangedUtc, r.LastStatusChangedByUserId,
            r.Title)).ToList();
        return new KbArticleListPage(items, total, page, pageSize);
    }

    private sealed record ListRow(
        Guid Id, Guid SectionId, string Slug, string Status,
        bool IsFeatured, int Position,
        DateTime CreatedUtc, DateTime UpdatedUtc,
        DateTime LastStatusChangedUtc, Guid? LastStatusChangedByUserId,
        string Title, long TotalHits);

    public async Task<KbArticle?> GetArticleAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {ArticleCols} FROM kb_articles WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<KbArticle>(new CommandDefinition(
            sql, new { id }, cancellationToken: ct));
    }

    public async Task<KbArticleWithTranslation?> GetArticleBySlugAsync(
        string sectionSlug, string articleSlug, CancellationToken ct)
    {
        // Two round-trips intentional: keeps the column-list reuse simple and
        // the by-slug path is cold (resolves once per page-load), so the
        // saving of one round-trip is not worth the multi-mapping ceremony.
        //
        // Slug uniqueness is per-sibling, so a global slug lookup is
        // ambiguous when nested sections reuse the same slug. v0.0.31
        // resolves only root-level sections; nested-path resolution
        // (`/section/sub/article`) lands with the public portal in v0.1.x.
        var lookupSql = $"""
            SELECT {ArticleCols}
              FROM kb_articles a
             WHERE a.section_id = (
                       SELECT id FROM kb_sections
                        WHERE parent_section_id IS NULL AND slug = @sectionSlug
                        LIMIT 1
                   )
               AND a.slug = @articleSlug
             LIMIT 1
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var article = await conn.QueryFirstOrDefaultAsync<KbArticle>(new CommandDefinition(
            lookupSql, new { sectionSlug, articleSlug }, cancellationToken: ct));
        if (article is null) return null;

        const string defaultLocaleSql = "SELECT default_locale_code FROM knowledge_base LIMIT 1";
        var locale = await conn.ExecuteScalarAsync<string>(
            new CommandDefinition(defaultLocaleSql, cancellationToken: ct));
        var translation = await GetTranslationAsync(article.Id, locale ?? "nl-BE", ct)
                          ?? new KbArticleTranslation(Guid.Empty, article.Id, locale ?? "nl-BE",
                              article.Slug, string.Empty, string.Empty,
                              article.CreatedUtc, article.UpdatedUtc);
        return new KbArticleWithTranslation(article, translation);
    }

    public async Task<IReadOnlyList<KbFeaturedArticle>> ListFeaturedAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 50);
        const string sql = """
            SELECT a.id AS Id, a.section_id AS SectionId, a.slug AS Slug,
                   COALESCE(t.title, a.slug::text) AS Title, a.updated_utc AS UpdatedUtc
              FROM kb_articles a
              JOIN knowledge_base kb ON TRUE
              LEFT JOIN kb_article_translations t
                ON t.article_id = a.id AND t.locale_code = kb.default_locale_code
             WHERE a.is_featured AND a.status = 'Published'
             ORDER BY a.updated_utc DESC
             LIMIT @limit
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<KbFeaturedArticle>(new CommandDefinition(
            sql, new { limit }, cancellationToken: ct))).ToList();
    }

    public async Task<KbArticle> CreateArticleAsync(
        Guid sectionId, string slug, string status, string? editorNotes, int position,
        Guid actorUserId, CancellationToken ct)
    {
        if (!KbArticleStatus.IsValid(status))
            throw new ArgumentException($"Status '{status}' is not one of Draft|Internal|Published|Archived.", nameof(status));

        var sql = $"""
            INSERT INTO kb_articles (section_id, slug, status, editor_notes, position,
                                     last_status_changed_by_user_id,
                                     created_by_user_id, updated_by_user_id)
            VALUES (@sectionId, @slug, @status, @editorNotes, @position,
                    @actorUserId, @actorUserId, @actorUserId)
            RETURNING {ArticleCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<KbArticle>(new CommandDefinition(
            sql, new { sectionId, slug, status, editorNotes, position, actorUserId },
            cancellationToken: ct));
    }

    public async Task<KbArticle?> UpdateArticleAsync(
        Guid id, Guid sectionId, string slug, string? editorNotes, int position,
        Guid actorUserId, CancellationToken ct)
    {
        var sql = $"""
            UPDATE kb_articles
               SET section_id = @sectionId,
                   slug = @slug,
                   editor_notes = @editorNotes,
                   position = @position,
                   updated_by_user_id = @actorUserId,
                   updated_utc = now()
             WHERE id = @id
            RETURNING {ArticleCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<KbArticle>(new CommandDefinition(
            sql, new { id, sectionId, slug, editorNotes, position, actorUserId },
            cancellationToken: ct));
    }

    public async Task<KbArticle?> FlipStatusAsync(
        Guid id, string newStatus, Guid actorUserId, CancellationToken ct)
    {
        if (!KbArticleStatus.IsValid(newStatus))
            throw new ArgumentException($"Status '{newStatus}' is not one of Draft|Internal|Published|Archived.", nameof(newStatus));

        var sql = $"""
            UPDATE kb_articles
               SET status = @newStatus,
                   last_status_changed_utc = now(),
                   last_status_changed_by_user_id = @actorUserId,
                   updated_by_user_id = @actorUserId,
                   updated_utc = now()
             WHERE id = @id
            RETURNING {ArticleCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<KbArticle>(new CommandDefinition(
            sql, new { id, newStatus, actorUserId }, cancellationToken: ct));
    }

    public async Task<KbArticle?> SetFeaturedAsync(
        Guid id, bool isFeatured, Guid actorUserId, CancellationToken ct)
    {
        var sql = $"""
            UPDATE kb_articles
               SET is_featured = @isFeatured,
                   updated_by_user_id = @actorUserId,
                   updated_utc = now()
             WHERE id = @id
            RETURNING {ArticleCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<KbArticle>(new CommandDefinition(
            sql, new { id, isFeatured, actorUserId }, cancellationToken: ct));
    }

    public async Task<KbArticle?> MoveArticleAsync(
        Guid id, Guid newSectionId, int newPosition, Guid actorUserId, CancellationToken ct)
    {
        var sql = $"""
            UPDATE kb_articles
               SET section_id = @newSectionId,
                   position = @newPosition,
                   updated_by_user_id = @actorUserId,
                   updated_utc = now()
             WHERE id = @id
            RETURNING {ArticleCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<KbArticle>(new CommandDefinition(
            sql, new { id, newSectionId, newPosition, actorUserId }, cancellationToken: ct));
    }

    public async Task<bool> HardDeleteArticleAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM kb_articles WHERE id = @id", new { id }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<KbArticleTranslation?> GetTranslationAsync(
        Guid articleId, string localeCode, CancellationToken ct)
    {
        var sql = $"""
            SELECT {TranslationCols} FROM kb_article_translations
            WHERE article_id = @articleId AND locale_code = @localeCode
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<KbArticleTranslation>(new CommandDefinition(
            sql, new { articleId, localeCode }, cancellationToken: ct));
    }

    public async Task<bool> ArticleSlugExistsInSectionAsync(Guid sectionId, string slug, CancellationToken ct)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM kb_articles
                 WHERE section_id = @sectionId AND slug = @slug
            )
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            sql, new { sectionId, slug }, cancellationToken: ct));
    }

    public async Task<KbArticleTranslation> UpsertTranslationAsync(
        Guid articleId, string localeCode, string title, string bodyHtml, string bodyText, CancellationToken ct)
    {
        var sql = $"""
            INSERT INTO kb_article_translations (article_id, locale_code, title, body_html, body_text)
            VALUES (@articleId, @localeCode, @title, @bodyHtml, @bodyText)
            ON CONFLICT (article_id, locale_code) DO UPDATE SET
                title = EXCLUDED.title,
                body_html = EXCLUDED.body_html,
                body_text = EXCLUDED.body_text,
                updated_utc = now()
            RETURNING {TranslationCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<KbArticleTranslation>(new CommandDefinition(
            sql, new { articleId, localeCode, title, bodyHtml, bodyText }, cancellationToken: ct));
    }
}
