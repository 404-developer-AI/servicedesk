using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Npgsql;
using Servicedesk.Domain.Views;

namespace Servicedesk.Infrastructure.Access;

public sealed class ViewAccessService : IViewAccessService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IMemoryCache _cache;
    private CancellationTokenSource _viewCts = new();
    private readonly object _ctsLock = new();

    public ViewAccessService(NpgsqlDataSource dataSource, IMemoryCache cache)
    {
        _dataSource = dataSource;
        _cache = cache;
    }

    public async Task<IReadOnlyList<View>> GetAccessibleViewsAsync(Guid userId, string role, CancellationToken ct = default)
    {
        var cacheKey = $"va:{userId}";

        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<View>? cached) && cached is not null)
            return cached;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        IEnumerable<View> rows;

        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            const string sql = """
                SELECT id AS Id, user_id AS UserId, name AS Name, filters::text AS FiltersJson,
                       columns AS Columns, sort_order AS SortOrder, is_shared AS IsShared,
                       display_config::text AS DisplayConfigJson,
                       created_utc AS CreatedUtc, updated_utc AS UpdatedUtc
                FROM views ORDER BY sort_order, name
                """;
            rows = await conn.QueryAsync<View>(new CommandDefinition(sql, cancellationToken: ct));
        }
        else
        {
            const string sql = """
                SELECT DISTINCT v.id AS Id, v.user_id AS UserId, v.name AS Name, v.filters::text AS FiltersJson,
                       v.columns AS Columns, v.sort_order AS SortOrder, v.is_shared AS IsShared,
                       v.display_config::text AS DisplayConfigJson,
                       v.created_utc AS CreatedUtc, v.updated_utc AS UpdatedUtc
                FROM views v
                WHERE v.id IN (
                    SELECT gv.view_id FROM view_group_views gv
                    JOIN view_group_members gm ON gm.view_group_id = gv.view_group_id
                    WHERE gm.user_id = @userId
                    UNION
                    SELECT uva.view_id FROM user_view_access uva WHERE uva.user_id = @userId
                )
                ORDER BY v.sort_order, v.name
                """;
            rows = await conn.QueryAsync<View>(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
        }

        var result = rows.ToList();

        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2)
        };
        lock (_ctsLock)
        {
            options.AddExpirationToken(new CancellationChangeToken(_viewCts.Token));
        }
        _cache.Set(cacheKey, (IReadOnlyList<View>)result, options);

        return result;
    }

    public async Task<bool> HasViewAccessAsync(Guid userId, string role, Guid viewId, CancellationToken ct = default)
    {
        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            return true;

        var accessible = await GetAccessibleViewsAsync(userId, role, ct);
        return accessible.Any(v => v.Id == viewId);
    }

    public async Task<IReadOnlyList<Guid>> GetDirectViewIdsAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "SELECT view_id FROM user_view_access WHERE user_id = @userId";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Guid>(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task SetDirectViewAccessAsync(Guid userId, IReadOnlyList<Guid> viewIds, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_view_access WHERE user_id = @userId",
            new { userId }, transaction: tx, cancellationToken: ct));

        const string insertSql = "INSERT INTO user_view_access (user_id, view_id) VALUES (@userId, @viewId)";
        foreach (var viewId in viewIds)
        {
            await conn.ExecuteAsync(new CommandDefinition(insertSql,
                new { userId, viewId }, transaction: tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        InvalidateCache(userId);
    }

    public void InvalidateCache(Guid userId)
    {
        _cache.Remove($"va:{userId}");
    }

    /// <summary>
    /// Evicts all cached view-access entries for every user.
    /// Call after any view CUD operation.
    /// </summary>
    public void InvalidateAllViewCaches()
    {
        lock (_ctsLock)
        {
            _viewCts.Cancel();
            _viewCts.Dispose();
            _viewCts = new CancellationTokenSource();
        }
    }
}
