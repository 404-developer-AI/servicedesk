using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Servicedesk.Infrastructure.Feedback;

public sealed class FeedbackWorkPointTypeService : IFeedbackWorkPointTypeService
{
    private const string CacheKeyActive = "feedback:work-point-types:active";
    private const string CacheKeyAll = "feedback:work-point-types:all";

    private const string SelectColumns = """
        SELECT  id          AS Id,
                name        AS Name,
                color       AS Color,
                sort_order  AS SortOrder,
                is_active   AS IsActive,
                created_utc AS CreatedUtc,
                updated_utc AS UpdatedUtc
        FROM feedback_work_point_types
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly IMemoryCache _cache;

    public FeedbackWorkPointTypeService(NpgsqlDataSource dataSource, IMemoryCache cache)
    {
        _dataSource = dataSource;
        _cache = cache;
    }

    public async Task<IReadOnlyList<FeedbackWorkPointType>> ListAsync(bool includeInactive, CancellationToken ct = default)
    {
        var key = includeInactive ? CacheKeyAll : CacheKeyActive;
        if (_cache.TryGetValue(key, out IReadOnlyList<FeedbackWorkPointType>? cached) && cached is not null)
            return cached;

        var sql = SelectColumns + """

            WHERE (@includeInactive OR is_active = TRUE)
            ORDER BY sort_order ASC, lower(name) ASC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<FeedbackWorkPointType>(
            new CommandDefinition(sql, new { includeInactive }, cancellationToken: ct))).ToList();

        _cache.Set(key, (IReadOnlyList<FeedbackWorkPointType>)rows, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
        });
        return rows;
    }

    public async Task<FeedbackWorkPointType?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var sql = SelectColumns + "\n\nWHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<FeedbackWorkPointType>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<CreateWorkPointTypeResult> CreateAsync(
        string name, string color, int sortOrder, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
                """
                INSERT INTO feedback_work_point_types (name, color, sort_order)
                VALUES (@name, @color, @sortOrder)
                RETURNING id
                """,
                new { name = name.Trim(), color = NormalizeColor(color), sortOrder },
                cancellationToken: ct));
            InvalidateCache();
            var saved = await GetAsync(id, ct);
            return new CreateWorkPointTypeResult.Created(saved!);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new CreateWorkPointTypeResult.NameConflict();
        }
    }

    public async Task<UpdateWorkPointTypeResult> UpdateAsync(
        Guid id, string name, string color, int sortOrder, bool isActive, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var rows = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE feedback_work_point_types SET
                    name        = @name,
                    color       = @color,
                    sort_order  = @sortOrder,
                    is_active   = @isActive,
                    updated_utc = now()
                WHERE id = @id
                """,
                new { id, name = name.Trim(), color = NormalizeColor(color), sortOrder, isActive },
                cancellationToken: ct));
            if (rows == 0) return new UpdateWorkPointTypeResult.NotFound();
            InvalidateCache();
            var saved = await GetAsync(id, ct);
            return new UpdateWorkPointTypeResult.Updated(saved!);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new UpdateWorkPointTypeResult.NameConflict();
        }
    }

    public async Task<DeleteWorkPointTypeResult> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var inUse = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM feedback_entries WHERE work_point_type_id = @id)",
            new { id }, cancellationToken: ct));
        if (inUse) return DeleteWorkPointTypeResult.InUse;

        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM feedback_work_point_types WHERE id = @id",
            new { id }, cancellationToken: ct));
        if (rows == 0) return DeleteWorkPointTypeResult.NotFound;

        InvalidateCache();
        return DeleteWorkPointTypeResult.Deleted;
    }

    private void InvalidateCache()
    {
        _cache.Remove(CacheKeyActive);
        _cache.Remove(CacheKeyAll);
    }

    /// Keep stored colors to a small safe shape (a #RGB/#RRGGBB hex) so a type
    /// color can be dropped straight into an inline style without an XSS path.
    private static string NormalizeColor(string? color)
    {
        var c = (color ?? string.Empty).Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(c, "^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")
            ? c
            : "#7c7cff";
    }
}
