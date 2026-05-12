using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Servicedesk.Infrastructure.Timesheet;

public sealed class TimesheetTaskService : ITimesheetTaskService
{
    /// Cache key for the full catalogue (Tab-1 picker hits this on every
    /// page load). Variants are { active-only, include-archived } so the
    /// Settings page and the picker don't fight over the same key.
    private const string CacheKeyActive = "timesheet:tasks:active";
    private const string CacheKeyAll = "timesheet:tasks:all";

    private readonly NpgsqlDataSource _dataSource;
    private readonly IMemoryCache _cache;

    public TimesheetTaskService(NpgsqlDataSource dataSource, IMemoryCache cache)
    {
        _dataSource = dataSource;
        _cache = cache;
    }

    public async Task<IReadOnlyList<TimesheetTask>> ListAsync(bool includeArchived, CancellationToken ct = default)
    {
        var key = includeArchived ? CacheKeyAll : CacheKeyActive;
        if (_cache.TryGetValue(key, out IReadOnlyList<TimesheetTask>? cached) && cached is not null)
            return cached;

        const string sql = """
            SELECT  id              AS Id,
                    name            AS Name,
                    requires_ticket AS RequiresTicket,
                    is_absence      AS IsAbsence,
                    archived        AS Archived,
                    sort_order      AS SortOrder,
                    created_utc     AS CreatedUtc,
                    updated_utc     AS UpdatedUtc
            FROM timesheet_tasks
            WHERE (@includeArchived OR archived = FALSE)
            ORDER BY sort_order ASC, lower(name) ASC
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = (await conn.QueryAsync<TimesheetTask>(
            new CommandDefinition(sql, new { includeArchived }, cancellationToken: ct))).ToList();

        _cache.Set(key, (IReadOnlyList<TimesheetTask>)rows, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
        });
        return rows;
    }

    public async Task<TimesheetTask?> GetAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = """
            SELECT  id              AS Id,
                    name            AS Name,
                    requires_ticket AS RequiresTicket,
                    is_absence      AS IsAbsence,
                    archived        AS Archived,
                    sort_order      AS SortOrder,
                    created_utc     AS CreatedUtc,
                    updated_utc     AS UpdatedUtc
            FROM timesheet_tasks
            WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TimesheetTask>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<CreateTimesheetTaskResult> CreateAsync(
        string name,
        bool requiresTicket,
        bool isAbsence,
        int sortOrder,
        CancellationToken ct = default)
    {
        var trimmed = name.Trim();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        try
        {
            var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
                """
                INSERT INTO timesheet_tasks (name, requires_ticket, is_absence, sort_order)
                VALUES (@name, @requiresTicket, @isAbsence, @sortOrder)
                RETURNING id
                """,
                new { name = trimmed, requiresTicket, isAbsence, sortOrder },
                cancellationToken: ct));
            InvalidateCache();
            var saved = await GetAsync(id, ct);
            return new CreateTimesheetTaskResult.Created(saved!);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new CreateTimesheetTaskResult.NameConflict();
        }
    }

    public async Task<UpdateTimesheetTaskResult> UpdateAsync(
        Guid id,
        string name,
        bool requiresTicket,
        bool isAbsence,
        bool archived,
        int sortOrder,
        CancellationToken ct = default)
    {
        var trimmed = name.Trim();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        try
        {
            var rows = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE timesheet_tasks SET
                    name            = @name,
                    requires_ticket = @requiresTicket,
                    is_absence      = @isAbsence,
                    archived        = @archived,
                    sort_order      = @sortOrder,
                    updated_utc     = now()
                WHERE id = @id
                """,
                new { id, name = trimmed, requiresTicket, isAbsence, archived, sortOrder },
                cancellationToken: ct));
            if (rows == 0) return new UpdateTimesheetTaskResult.NotFound();
            InvalidateCache();
            var saved = await GetAsync(id, ct);
            return new UpdateTimesheetTaskResult.Updated(saved!);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return new UpdateTimesheetTaskResult.NameConflict();
        }
    }

    private void InvalidateCache()
    {
        _cache.Remove(CacheKeyActive);
        _cache.Remove(CacheKeyAll);
    }
}
