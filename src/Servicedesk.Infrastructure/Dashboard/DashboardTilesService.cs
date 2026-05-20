using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Dashboard;

public sealed class DashboardTilesService : IDashboardTilesService
{
    private readonly NpgsqlDataSource _dataSource;

    public DashboardTilesService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<string>> GetForUserAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = "SELECT tile_id FROM user_dashboard_tiles WHERE user_id = @id";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<string>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<SetDashboardTilesResult> SetForUserAsync(
        Guid userId,
        IReadOnlyCollection<string> tileIds,
        Guid actingAdminId,
        CancellationToken ct = default)
    {
        // Deduplicate + validate before opening a transaction so a bad
        // payload never touches the database.
        var requested = tileIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var unknown = requested
            .Where(id => !DashboardTileIds.IsKnown(id))
            .ToList();
        if (unknown.Count > 0)
        {
            return new SetDashboardTilesResult.UnknownTileIds(unknown);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var currentRole = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT role_name FROM users WHERE id = @id FOR UPDATE",
                new { id = userId },
                tx,
                cancellationToken: ct));
        if (currentRole is null)
        {
            await tx.RollbackAsync(ct);
            return new SetDashboardTilesResult.UserNotFound();
        }
        if (string.Equals(currentRole, "Customer", StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return new SetDashboardTilesResult.CustomerNotAllowed();
        }

        // Replace strategy: delete tiles outside the new set, then
        // upsert the new set. Two statements inside the row-lock keep
        // the table consistent under concurrent admin toggles.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM user_dashboard_tiles
            WHERE user_id = @id
              AND (@tiles::text[] IS NULL OR NOT (tile_id = ANY(@tiles::text[])))
            """,
            new { id = userId, tiles = requested.ToArray() },
            tx,
            cancellationToken: ct));

        if (requested.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO user_dashboard_tiles (user_id, tile_id)
                SELECT @id, t FROM unnest(@tiles::text[]) AS t
                ON CONFLICT (user_id, tile_id) DO NOTHING
                """,
                new { id = userId, tiles = requested.ToArray() },
                tx,
                cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        _ = actingAdminId;

        return new SetDashboardTilesResult.Updated(requested);
    }
}
