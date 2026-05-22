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

    public async Task<IReadOnlyList<DashboardTilePref>> GetForUserAsync(Guid userId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT tile_id AS TileId, size AS Size
            FROM user_dashboard_tiles
            WHERE user_id = @id
            ORDER BY position ASC, tile_id ASC
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<DashboardTilePref>(
            new CommandDefinition(sql, new { id = userId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<SetDashboardTilesResult> SetEnabledForUserAsync(
        Guid userId,
        IReadOnlyDictionary<string, string> tileSizes,
        Guid actingAdminId,
        CancellationToken ct = default)
    {
        // Validate up-front so a bad payload never opens a transaction.
        var unknownIds = tileSizes.Keys
            .Where(id => !string.IsNullOrWhiteSpace(id) && !DashboardTileIds.IsKnown(id.Trim()))
            .Select(id => id.Trim())
            .ToList();
        if (unknownIds.Count > 0)
        {
            return new SetDashboardTilesResult.UnknownTileIds(unknownIds);
        }
        var badSize = tileSizes.Values.FirstOrDefault(s => !DashboardTileSizes.IsKnown(s));
        if (badSize is not null)
        {
            return new SetDashboardTilesResult.InvalidSize(badSize);
        }

        var desired = tileSizes
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value, StringComparer.Ordinal);

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

        // Snapshot current rows so we can preserve position/size for
        // tiles that stay enabled and append new ones at the end.
        var existing = (await connection.QueryAsync<(string TileId, int Position, string Size)>(
            new CommandDefinition(
                "SELECT tile_id AS TileId, position AS Position, size AS Size " +
                "FROM user_dashboard_tiles WHERE user_id = @id ORDER BY position",
                new { id = userId },
                tx,
                cancellationToken: ct))).ToList();

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_dashboard_tiles WHERE user_id = @id",
            new { id = userId },
            tx,
            cancellationToken: ct));

        var maxExistingPos = existing.Count == 0 ? -1 : existing.Max(e => e.Position);
        var nextPos = maxExistingPos + 1;

        var rowsToWrite = new List<(string TileId, int Position, string Size)>();
        // Preserve order for tiles that survive the toggle.
        foreach (var row in existing)
        {
            if (desired.TryGetValue(row.TileId, out _))
            {
                rowsToWrite.Add((row.TileId, row.Position, row.Size));
            }
        }
        // Append newly-enabled tiles at the end with the caller-supplied
        // default size (the registry default for that tile id).
        foreach (var kv in desired)
        {
            if (rowsToWrite.Any(r => r.TileId == kv.Key)) continue;
            rowsToWrite.Add((kv.Key, nextPos++, kv.Value));
        }

        if (rowsToWrite.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO user_dashboard_tiles (user_id, tile_id, position, size)
                VALUES (@UserId, @TileId, @Position, @Size)
                """,
                rowsToWrite.Select(r => new
                {
                    UserId = userId,
                    r.TileId,
                    r.Position,
                    r.Size,
                }),
                tx,
                cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        _ = actingAdminId;

        return new SetDashboardTilesResult.Updated(
            rowsToWrite
                .OrderBy(r => r.Position)
                .Select(r => new DashboardTilePref(r.TileId, r.Size))
                .ToList());
    }

    public async Task<SetDashboardTilesResult> SaveLayoutForUserAsync(
        Guid userId,
        IReadOnlyList<DashboardTilePref> layout,
        CancellationToken ct = default)
    {
        // Validate every id + size before opening the transaction.
        var unknownIds = layout
            .Select(p => p.TileId?.Trim() ?? "")
            .Where(id => id.Length > 0 && !DashboardTileIds.IsKnown(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknownIds.Count > 0)
        {
            return new SetDashboardTilesResult.UnknownTileIds(unknownIds);
        }
        var badSize = layout.Select(p => p.Size).FirstOrDefault(s => !DashboardTileSizes.IsKnown(s));
        if (badSize is not null)
        {
            return new SetDashboardTilesResult.InvalidSize(badSize);
        }

        // Deduplicate by tile_id keeping the first occurrence so the
        // primary key stays intact even if the client posts a duplicate.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<(string TileId, int Position, string Size)>();
        for (var i = 0; i < layout.Count; i++)
        {
            var p = layout[i];
            var id = p.TileId.Trim();
            if (id.Length == 0 || !seen.Add(id)) continue;
            ordered.Add((id, ordered.Count, p.Size));
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

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_dashboard_tiles WHERE user_id = @id",
            new { id = userId },
            tx,
            cancellationToken: ct));

        if (ordered.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO user_dashboard_tiles (user_id, tile_id, position, size)
                VALUES (@UserId, @TileId, @Position, @Size)
                """,
                ordered.Select(r => new
                {
                    UserId = userId,
                    r.TileId,
                    r.Position,
                    r.Size,
                }),
                tx,
                cancellationToken: ct));
        }

        await tx.CommitAsync(ct);

        return new SetDashboardTilesResult.Updated(
            ordered.Select(r => new DashboardTilePref(r.TileId, r.Size)).ToList());
    }
}
