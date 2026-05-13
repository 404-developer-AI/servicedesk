using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Triggers;

public sealed class TriggerGroupRepository : ITriggerGroupRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public TriggerGroupRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    private const string Columns = """
        id          AS Id,
        name        AS Name,
        color       AS Color,
        sort_order  AS SortOrder,
        created_utc AS CreatedUtc,
        updated_utc AS UpdatedUtc
        """;

    public async Task<IReadOnlyList<TriggerGroupRow>> ListAllAsync(CancellationToken ct)
    {
        var sql = $"""
            SELECT {Columns}
            FROM trigger_groups
            ORDER BY sort_order, lower(name)
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TriggerGroupRow>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<TriggerGroupRow?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var sql = $"""
            SELECT {Columns}
            FROM trigger_groups
            WHERE id = @id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<TriggerGroupRow>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<TriggerGroupRow> CreateAsync(NewTriggerGroup row, CancellationToken ct)
    {
        // New groups land at the end of the list (max(sort_order) + 1).
        // COALESCE handles the very-first-group case where the table is
        // empty; the OR-pattern keeps it to one round-trip.
        var sql = $"""
            INSERT INTO trigger_groups (name, color, sort_order)
            VALUES (
                @Name,
                @Color,
                COALESCE((SELECT MAX(sort_order) + 1 FROM trigger_groups), 0)
            )
            RETURNING {Columns}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<TriggerGroupRow>(
            new CommandDefinition(sql, row, cancellationToken: ct));
    }

    public async Task<TriggerGroupRow?> UpdateAsync(Guid id, UpdateTriggerGroup row, CancellationToken ct)
    {
        var sql = $"""
            UPDATE trigger_groups SET
                name        = @Name,
                color       = @Color,
                updated_utc = now()
            WHERE id = @id
            RETURNING {Columns}
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var p = new DynamicParameters(row);
        p.Add("id", id);
        return await conn.QueryFirstOrDefaultAsync<TriggerGroupRow>(
            new CommandDefinition(sql, p, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        // ON DELETE SET NULL on triggers.group_id detaches existing
        // members; they land in the "Ungrouped" pseudo-section.
        const string sql = "DELETE FROM trigger_groups WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new { id }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task ReorderAsync(IReadOnlyList<TriggerGroupPlacement> placements, CancellationToken ct)
    {
        if (placements.Count == 0) return;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        const string sql = """
            UPDATE trigger_groups
               SET sort_order  = @SortOrder,
                   updated_utc = now()
             WHERE id = @Id
            """;
        foreach (var p in placements)
        {
            await conn.ExecuteAsync(new CommandDefinition(sql, p, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }
}
