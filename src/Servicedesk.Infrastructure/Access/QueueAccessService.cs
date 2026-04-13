using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Servicedesk.Infrastructure.Access;

public sealed class QueueAccessService : IQueueAccessService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IMemoryCache _cache;

    public QueueAccessService(NpgsqlDataSource dataSource, IMemoryCache cache)
    {
        _dataSource = dataSource;
        _cache = cache;
    }

    public async Task<IReadOnlyList<Guid>> GetAccessibleQueueIdsAsync(Guid userId, string role, CancellationToken ct = default)
    {
        var cacheKey = $"qa:{userId}";

        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<Guid>? cached) && cached is not null)
            return cached;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        IEnumerable<Guid> rows;

        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            const string sql = """
                SELECT id FROM queues
                WHERE is_active = TRUE
                ORDER BY sort_order, name
                """;
            rows = await conn.QueryAsync<Guid>(new CommandDefinition(sql, cancellationToken: ct));
        }
        else
        {
            const string sql = """
                SELECT q.id FROM queues q
                JOIN user_queue_access uqa ON uqa.queue_id = q.id
                WHERE uqa.user_id = @userId AND q.is_active = TRUE
                ORDER BY q.sort_order, q.name
                """;
            rows = await conn.QueryAsync<Guid>(new CommandDefinition(sql, new { userId }, cancellationToken: ct));
        }

        var result = rows.ToList();

        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2)
        };
        _cache.Set(cacheKey, (IReadOnlyList<Guid>)result, options);

        return result;
    }

    public async Task<bool> HasQueueAccessAsync(Guid userId, string role, Guid queueId, CancellationToken ct = default)
    {
        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            return true;

        var accessible = await GetAccessibleQueueIdsAsync(userId, role, ct);
        return accessible.Contains(queueId);
    }

    public async Task SetQueueAccessAsync(Guid userId, IReadOnlyList<Guid> queueIds, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string deleteSql = "DELETE FROM user_queue_access WHERE user_id = @userId";
        await conn.ExecuteAsync(new CommandDefinition(deleteSql, new { userId }, transaction: tx, cancellationToken: ct));

        const string insertSql = "INSERT INTO user_queue_access (user_id, queue_id) VALUES (@userId, @queueId)";
        foreach (var queueId in queueIds)
        {
            await conn.ExecuteAsync(new CommandDefinition(insertSql, new { userId, queueId }, transaction: tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        InvalidateCache(userId);
    }

    public async Task<IReadOnlyList<Guid>> GetUsersForQueueAsync(Guid queueId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT user_id FROM user_queue_access WHERE queue_id = @queueId
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Guid>(new CommandDefinition(sql, new { queueId }, cancellationToken: ct));
        return rows.ToList();
    }

    public void InvalidateCache(Guid userId)
    {
        _cache.Remove($"qa:{userId}");
    }
}
