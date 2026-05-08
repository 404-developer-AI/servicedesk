using Dapper;
using Npgsql;
using Servicedesk.Domain.KnowledgeBase;

namespace Servicedesk.Infrastructure.Persistence.KnowledgeBase;

public sealed class KbSectionRepository : IKbSectionRepository
{
    private const string SectionCols = """
        id AS Id, parent_section_id AS ParentSectionId, slug AS Slug, icon_name AS IconName,
        position AS Position, created_utc AS CreatedUtc, updated_utc AS UpdatedUtc,
        created_by_user_id AS CreatedByUserId, updated_by_user_id AS UpdatedByUserId
        """;

    private const string TranslationCols = """
        id AS Id, section_id AS SectionId, locale_code AS LocaleCode,
        title AS Title, description AS Description,
        created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
        """;

    private readonly NpgsqlDataSource _dataSource;

    public KbSectionRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<KbSection>> ListSectionsAsync(CancellationToken ct)
    {
        var sql = $"""
            SELECT {SectionCols} FROM kb_sections
            ORDER BY parent_section_id NULLS FIRST, position, slug
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<KbSection>(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    public async Task<KbSection?> GetSectionAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {SectionCols} FROM kb_sections WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<KbSection>(new CommandDefinition(
            sql, new { id }, cancellationToken: ct));
    }

    public async Task<KbSection?> GetSectionBySlugAsync(Guid? parentSectionId, string slug, CancellationToken ct)
    {
        // NULL-safe lookup: roots and children use different equality semantics.
        var sql = parentSectionId is null
            ? $"SELECT {SectionCols} FROM kb_sections WHERE parent_section_id IS NULL AND slug = @slug"
            : $"SELECT {SectionCols} FROM kb_sections WHERE parent_section_id = @parentSectionId AND slug = @slug";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<KbSection>(new CommandDefinition(
            sql, new { parentSectionId, slug }, cancellationToken: ct));
    }

    public async Task<KbSection> CreateSectionAsync(
        Guid? parentSectionId, string slug, string? iconName, int position, Guid actorUserId, CancellationToken ct)
    {
        var sql = $"""
            INSERT INTO kb_sections (parent_section_id, slug, icon_name, position,
                                     created_by_user_id, updated_by_user_id)
            VALUES (@parentSectionId, @slug, @iconName, @position, @actorUserId, @actorUserId)
            RETURNING {SectionCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<KbSection>(new CommandDefinition(
            sql, new { parentSectionId, slug, iconName, position, actorUserId }, cancellationToken: ct));
    }

    public async Task<KbSection?> UpdateSectionAsync(
        Guid id, string slug, string? iconName, int position, Guid actorUserId, CancellationToken ct)
    {
        var sql = $"""
            UPDATE kb_sections
               SET slug = @slug,
                   icon_name = @iconName,
                   position = @position,
                   updated_by_user_id = @actorUserId,
                   updated_utc = now()
             WHERE id = @id
            RETURNING {SectionCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<KbSection>(new CommandDefinition(
            sql, new { id, slug, iconName, position, actorUserId }, cancellationToken: ct));
    }

    public async Task<SectionDeleteResult> DeleteSectionAsync(Guid id, CancellationToken ct)
    {
        // Hard delete only when the section has no children and no articles.
        // Returning a typed enum keeps the API endpoint free of magic ints.
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM kb_sections WHERE id = @id)",
            new { id }, tx, cancellationToken: ct));
        if (!exists) { await tx.CommitAsync(ct); return SectionDeleteResult.NotFound; }

        var hasChildren = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM kb_sections WHERE parent_section_id = @id)",
            new { id }, tx, cancellationToken: ct));
        var hasArticles = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM kb_articles WHERE section_id = @id)",
            new { id }, tx, cancellationToken: ct));
        if (hasChildren || hasArticles)
        {
            await tx.CommitAsync(ct);
            return SectionDeleteResult.NotEmpty;
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM kb_sections WHERE id = @id", new { id }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return SectionDeleteResult.Deleted;
    }

    public async Task<KbSection?> MoveSectionAsync(
        Guid id, Guid? newParentSectionId, int newPosition, Guid actorUserId, CancellationToken ct)
    {
        // Cycle-detection: walk up the new parent chain and refuse if we
        // encounter the moving section itself. Trivial self-parent is
        // already blocked by chk_kb_sections_no_self_parent.
        if (newParentSectionId is { } parentId && parentId != id)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            const string ancestorSql = """
                WITH RECURSIVE ancestors AS (
                    SELECT id, parent_section_id FROM kb_sections WHERE id = @parentId
                    UNION ALL
                    SELECT s.id, s.parent_section_id
                      FROM kb_sections s
                      JOIN ancestors a ON a.parent_section_id = s.id
                )
                SELECT EXISTS (SELECT 1 FROM ancestors WHERE id = @id)
                """;
            var wouldCycle = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                ancestorSql, new { id, parentId }, cancellationToken: ct));
            if (wouldCycle) throw new InvalidOperationException("Section move would create a cycle.");
        }

        var sql = $"""
            UPDATE kb_sections
               SET parent_section_id = @newParentSectionId,
                   position = @newPosition,
                   updated_by_user_id = @actorUserId,
                   updated_utc = now()
             WHERE id = @id
            RETURNING {SectionCols}
            """;
        await using var conn2 = await _dataSource.OpenConnectionAsync(ct);
        return await conn2.QueryFirstOrDefaultAsync<KbSection>(new CommandDefinition(
            sql, new { id, newParentSectionId, newPosition, actorUserId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<KbSectionTranslation>> ListTranslationsAsync(Guid sectionId, CancellationToken ct)
    {
        var sql = $"SELECT {TranslationCols} FROM kb_section_translations WHERE section_id = @sectionId";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<KbSectionTranslation>(new CommandDefinition(
            sql, new { sectionId }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<KbSectionTranslation>> ListAllTranslationsAsync(CancellationToken ct)
    {
        var sql = $"SELECT {TranslationCols} FROM kb_section_translations";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<KbSectionTranslation>(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    public async Task<KbSectionTranslation?> GetTranslationAsync(Guid sectionId, string localeCode, CancellationToken ct)
    {
        var sql = $"""
            SELECT {TranslationCols} FROM kb_section_translations
            WHERE section_id = @sectionId AND locale_code = @localeCode
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<KbSectionTranslation>(new CommandDefinition(
            sql, new { sectionId, localeCode }, cancellationToken: ct));
    }

    public async Task<KbSectionTranslation> UpsertTranslationAsync(
        Guid sectionId, string localeCode, string title, string? description, CancellationToken ct)
    {
        var sql = $"""
            INSERT INTO kb_section_translations (section_id, locale_code, title, description)
            VALUES (@sectionId, @localeCode, @title, @description)
            ON CONFLICT (section_id, locale_code) DO UPDATE SET
                title = EXCLUDED.title,
                description = EXCLUDED.description,
                updated_utc = now()
            RETURNING {TranslationCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<KbSectionTranslation>(new CommandDefinition(
            sql, new { sectionId, localeCode, title, description }, cancellationToken: ct));
    }
}
