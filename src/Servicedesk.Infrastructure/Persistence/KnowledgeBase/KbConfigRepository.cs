using Dapper;
using Npgsql;
using Servicedesk.Domain.KnowledgeBase;

namespace Servicedesk.Infrastructure.Persistence.KnowledgeBase;

public sealed class KbConfigRepository : IKbConfigRepository
{
    private const string ConfigCols = """
        id AS Id, is_active AS IsActive, default_locale_code AS DefaultLocaleCode,
        created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
        """;

    private const string LocaleCols = """
        code AS Code, display_name AS DisplayName, is_active AS IsActive, sort_order AS SortOrder
        """;

    private readonly NpgsqlDataSource _dataSource;

    public KbConfigRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<KnowledgeBaseConfig> GetConfigAsync(CancellationToken ct)
    {
        var sql = $"SELECT {ConfigCols} FROM knowledge_base LIMIT 1";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<KnowledgeBaseConfig>(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<KnowledgeBaseConfig> UpdateConfigAsync(bool isActive, string defaultLocaleCode, CancellationToken ct)
    {
        var sql = $"""
            UPDATE knowledge_base
               SET is_active = @isActive,
                   default_locale_code = @defaultLocaleCode,
                   updated_utc = now()
            RETURNING {ConfigCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<KnowledgeBaseConfig>(new CommandDefinition(
            sql, new { isActive, defaultLocaleCode }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<KbLocale>> ListLocalesAsync(bool includeInactive, CancellationToken ct)
    {
        var sql = $"SELECT {LocaleCols} FROM kb_locales";
        if (!includeInactive) sql += " WHERE is_active = TRUE";
        sql += " ORDER BY sort_order, code";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<KbLocale>(new CommandDefinition(sql, cancellationToken: ct))).ToList();
    }

    public async Task<KbLocale?> GetLocaleAsync(string code, CancellationToken ct)
    {
        var sql = $"SELECT {LocaleCols} FROM kb_locales WHERE code = @code";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<KbLocale>(new CommandDefinition(
            sql, new { code }, cancellationToken: ct));
    }

    public async Task<KbLocale> UpsertLocaleAsync(KbLocale locale, CancellationToken ct)
    {
        var sql = $"""
            INSERT INTO kb_locales (code, display_name, is_active, sort_order)
            VALUES (@Code, @DisplayName, @IsActive, @SortOrder)
            ON CONFLICT (code) DO UPDATE SET
                display_name = EXCLUDED.display_name,
                is_active = EXCLUDED.is_active,
                sort_order = EXCLUDED.sort_order
            RETURNING {LocaleCols}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<KbLocale>(new CommandDefinition(sql, locale, cancellationToken: ct));
    }

    public async Task<bool> RemoveLocaleAsync(string code, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM kb_locales WHERE code = @code", new { code }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> LocaleHasTranslationsAsync(string code, CancellationToken ct)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM kb_section_translations WHERE locale_code = @code
                UNION ALL
                SELECT 1 FROM kb_article_translations WHERE locale_code = @code
            )
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            sql, new { code }, cancellationToken: ct));
    }
}
