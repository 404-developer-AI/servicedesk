using Dapper;
using Npgsql;
using Servicedesk.Domain.ViewGroups;

namespace Servicedesk.Infrastructure.Persistence.ViewGroups;

public sealed class ViewGroupRepository : IViewGroupRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ViewGroupRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<ViewGroupSummary>> ListAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT g.id AS Id, g.name AS Name, g.description AS Description,
                   g.sort_order AS SortOrder,
                   (SELECT count(*) FROM view_group_members WHERE view_group_id = g.id)::int AS MemberCount,
                   (SELECT count(*) FROM view_group_views WHERE view_group_id = g.id)::int AS ViewCount,
                   g.created_utc AS CreatedUtc, g.updated_utc AS UpdatedUtc
            FROM view_groups g ORDER BY g.sort_order, g.name
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ViewGroupSummary>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<ViewGroupDetail?> GetDetailAsync(Guid groupId, CancellationToken ct)
    {
        const string groupSql = """
            SELECT id AS Id, name AS Name, description AS Description, sort_order AS SortOrder,
                   created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            FROM view_groups WHERE id = @groupId
            """;
        const string membersSql = """
            SELECT m.user_id AS UserId, u.email AS Email
            FROM view_group_members m JOIN users u ON u.id = m.user_id
            WHERE m.view_group_id = @groupId ORDER BY u.email
            """;
        const string viewsSql = """
            SELECT gv.view_id AS ViewId, v.name AS ViewName, gv.sort_order AS SortOrder
            FROM view_group_views gv JOIN views v ON v.id = gv.view_id
            WHERE gv.view_group_id = @groupId ORDER BY gv.sort_order, v.name
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var group = await conn.QueryFirstOrDefaultAsync<ViewGroup>(
            new CommandDefinition(groupSql, new { groupId }, cancellationToken: ct));

        if (group is null)
            return null;

        var members = await conn.QueryAsync<ViewGroupMember>(
            new CommandDefinition(membersSql, new { groupId }, cancellationToken: ct));

        var views = await conn.QueryAsync<ViewGroupView>(
            new CommandDefinition(viewsSql, new { groupId }, cancellationToken: ct));

        return new ViewGroupDetail(group, members.ToList(), views.ToList());
    }

    public async Task<ViewGroup> CreateAsync(string name, string description, int sortOrder, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO view_groups (name, description, sort_order)
            VALUES (@name, @description, @sortOrder)
            RETURNING id AS Id, name AS Name, description AS Description, sort_order AS SortOrder,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<ViewGroup>(
            new CommandDefinition(sql, new { name, description, sortOrder }, cancellationToken: ct));
    }

    public async Task<ViewGroup?> UpdateAsync(Guid id, string name, string description, int sortOrder, CancellationToken ct)
    {
        const string sql = """
            UPDATE view_groups
            SET name = @name, description = @description, sort_order = @sortOrder, updated_utc = now()
            WHERE id = @id
            RETURNING id AS Id, name AS Name, description AS Description, sort_order AS SortOrder,
                      created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<ViewGroup>(
            new CommandDefinition(sql, new { id, name, description, sortOrder }, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        const string sql = "DELETE FROM view_groups WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task SetMembersAsync(Guid groupId, IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string deleteSql = "DELETE FROM view_group_members WHERE view_group_id = @groupId";
        await conn.ExecuteAsync(new CommandDefinition(deleteSql, new { groupId }, transaction: tx, cancellationToken: ct));

        const string insertSql = "INSERT INTO view_group_members (view_group_id, user_id) VALUES (@groupId, @userId)";
        foreach (var userId in userIds)
        {
            await conn.ExecuteAsync(new CommandDefinition(insertSql, new { groupId, userId }, transaction: tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task SetViewsAsync(Guid groupId, IReadOnlyList<Guid> viewIds, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string deleteSql = "DELETE FROM view_group_views WHERE view_group_id = @groupId";
        await conn.ExecuteAsync(new CommandDefinition(deleteSql, new { groupId }, transaction: tx, cancellationToken: ct));

        const string insertSql = "INSERT INTO view_group_views (view_group_id, view_id, sort_order) VALUES (@groupId, @viewId, @sortOrder)";
        for (var i = 0; i < viewIds.Count; i++)
        {
            await conn.ExecuteAsync(new CommandDefinition(insertSql,
                new { groupId, viewId = viewIds[i], sortOrder = i },
                transaction: tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }
}
