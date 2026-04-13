using Dapper;
using Npgsql;
using Servicedesk.Domain.Views;

namespace Servicedesk.Infrastructure.Persistence.Views;

public sealed class ViewRepository : IViewRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ViewRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private const string SelectColumns = """
        id AS Id, user_id AS UserId, name AS Name, filters::text AS FiltersJson,
        columns AS Columns, sort_order AS SortOrder, is_shared AS IsShared,
        display_config::text AS DisplayConfigJson,
        created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
        """;

    public async Task<IReadOnlyList<View>> ListAsync(Guid userId, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM views WHERE user_id = @userId OR is_shared = TRUE ORDER BY sort_order, name";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<View>(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<View?> GetAsync(Guid id, CancellationToken ct)
    {
        var sql = $"SELECT {SelectColumns} FROM views WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<View>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<View> CreateAsync(Guid userId, string name, string filtersJson, string? columns, int sortOrder, bool isShared, string displayConfigJson, CancellationToken ct)
    {
        var sql = $"""
            INSERT INTO views (user_id, name, filters, columns, sort_order, is_shared, display_config)
            VALUES (@userId, @name, @filtersJson::jsonb, @columns, @sortOrder, @isShared, @displayConfigJson::jsonb)
            RETURNING {SelectColumns}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<View>(new CommandDefinition(sql,
            new { userId, name, filtersJson, columns, sortOrder, isShared, displayConfigJson }, cancellationToken: ct));
    }

    public async Task<View?> UpdateAsync(Guid id, string name, string filtersJson, string? columns, int sortOrder, bool isShared, string displayConfigJson, CancellationToken ct)
    {
        var sql = $"""
            UPDATE views SET name = @name, filters = @filtersJson::jsonb,
                             columns = @columns, sort_order = @sortOrder, is_shared = @isShared,
                             display_config = @displayConfigJson::jsonb, updated_utc = now()
            WHERE id = @id
            RETURNING {SelectColumns}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<View>(new CommandDefinition(sql,
            new { id, name, filtersJson, columns, sortOrder, isShared, displayConfigJson }, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        const string sql = "DELETE FROM views WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return affected > 0;
    }
}
