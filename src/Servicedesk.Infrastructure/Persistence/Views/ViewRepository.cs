using Dapper;
using Npgsql;
using Servicedesk.Domain.Views;

namespace Servicedesk.Infrastructure.Persistence.Views;

public sealed class ViewRepository : IViewRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ViewRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<View>> ListAsync(Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, user_id AS UserId, name AS Name, filters::text AS FiltersJson,
                   sort_order AS SortOrder, is_shared AS IsShared,
                   created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            FROM views WHERE user_id = @userId OR is_shared = TRUE
            ORDER BY sort_order, name
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<View>(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<View?> GetAsync(Guid id, CancellationToken ct)
    {
        const string sql = """
            SELECT id AS Id, user_id AS UserId, name AS Name, filters::text AS FiltersJson,
                   sort_order AS SortOrder, is_shared AS IsShared,
                   created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            FROM views WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<View>(new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<View> CreateAsync(Guid userId, string name, string filtersJson, int sortOrder, bool isShared, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO views (user_id, name, filters, sort_order, is_shared)
            VALUES (@userId, @name, @filtersJson::jsonb, @sortOrder, @isShared)
            RETURNING id AS Id, user_id AS UserId, name AS Name, filters::text AS FiltersJson,
                      sort_order AS SortOrder, is_shared AS IsShared,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<View>(new CommandDefinition(sql,
            new { userId, name, filtersJson, sortOrder, isShared }, cancellationToken: ct));
    }

    public async Task<View?> UpdateAsync(Guid id, string name, string filtersJson, int sortOrder, bool isShared, CancellationToken ct)
    {
        const string sql = """
            UPDATE views SET name = @name, filters = @filtersJson::jsonb,
                             sort_order = @sortOrder, is_shared = @isShared, updated_utc = now()
            WHERE id = @id
            RETURNING id AS Id, user_id AS UserId, name AS Name, filters::text AS FiltersJson,
                      sort_order AS SortOrder, is_shared AS IsShared,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<View>(new CommandDefinition(sql,
            new { id, name, filtersJson, sortOrder, isShared }, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        const string sql = "DELETE FROM views WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return affected > 0;
    }
}
