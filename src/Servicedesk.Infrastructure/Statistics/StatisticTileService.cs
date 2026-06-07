using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Statistics;

public sealed class StatisticTileService : IStatisticTileService
{
    private readonly NpgsqlDataSource _dataSource;

    public StatisticTileService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    private const string TileColumns = """
        id          AS Id,
        title       AS Title,
        metric_key  AS MetricKey,
        chart_type  AS ChartType,
        period      AS Period,
        grouping    AS Grouping,
        scope       AS Scope,
        scope_user_id AS ScopeUserId,
        created_by  AS CreatedBy,
        created_utc AS CreatedUtc,
        updated_utc AS UpdatedUtc
        """;

    // ---- write surface ----------------------------------------------------

    public async Task<IReadOnlyList<StatisticTileSummary>> ListAllAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT  t.id          AS Id,
                    t.title       AS Title,
                    t.metric_key  AS MetricKey,
                    t.chart_type  AS ChartType,
                    t.period      AS Period,
                    t.grouping    AS Grouping,
                    t.scope       AS Scope,
                    t.scope_user_id AS ScopeUserId,
                    t.created_by  AS CreatedBy,
                    t.created_utc AS CreatedUtc,
                    t.updated_utc AS UpdatedUtc,
                    (SELECT COUNT(*) FROM statistic_tile_assignments a WHERE a.tile_id = t.id)::int AS AssignedCount,
                    su.email AS ScopeUserEmail
            FROM statistic_tiles t
            LEFT JOIN users su ON su.id = t.scope_user_id
            ORDER BY t.updated_utc DESC, t.id DESC
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<SummaryRow>(
            new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => new StatisticTileSummary(r.ToTile(), r.AssignedCount, r.ScopeUserEmail)).ToList();
    }

    private sealed class SummaryRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string MetricKey { get; set; } = "";
        public string ChartType { get; set; } = "";
        public string Period { get; set; } = "";
        public string Grouping { get; set; } = "";
        public string Scope { get; set; } = "";
        public Guid? ScopeUserId { get; set; }
        public Guid? CreatedBy { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int AssignedCount { get; set; }
        public string? ScopeUserEmail { get; set; }

        public StatisticTile ToTile() => new()
        {
            Id = Id, Title = Title, MetricKey = MetricKey, ChartType = ChartType,
            Period = Period, Grouping = Grouping, Scope = Scope, ScopeUserId = ScopeUserId,
            CreatedBy = CreatedBy, CreatedUtc = CreatedUtc, UpdatedUtc = UpdatedUtc,
        };
    }

    public async Task<StatisticTile?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var sql = $"SELECT {TileColumns} FROM statistic_tiles t WHERE t.id = @id";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        return await connection.QueryFirstOrDefaultAsync<StatisticTile>(
            new CommandDefinition(sql, new { id }, cancellationToken: ct));
    }

    public async Task<SaveStatisticTileResult> CreateAsync(
        StatisticTileInput input, Guid actingUserId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        var errors = await ValidateAsync(connection, null, input, ct);
        if (errors.Count > 0) return new SaveStatisticTileResult.ValidationFailed(errors);

        var sql = $"""
            INSERT INTO statistic_tiles
                (title, metric_key, chart_type, period, grouping, scope, scope_user_id, created_by)
            VALUES
                (@Title, @MetricKey, @ChartType, @Period, @Grouping, @Scope, @ScopeUserId, @CreatedBy)
            RETURNING {TileColumns}
            """;
        var tile = await connection.QuerySingleAsync<StatisticTile>(new CommandDefinition(sql, new
        {
            input.Title,
            input.MetricKey,
            input.ChartType,
            input.Period,
            input.Grouping,
            input.Scope,
            ScopeUserId = NormalizeScopeUser(input),
            CreatedBy = actingUserId,
        }, cancellationToken: ct));
        return new SaveStatisticTileResult.Created(tile);
    }

    public async Task<SaveStatisticTileResult> UpdateAsync(
        Guid id, StatisticTileInput input, Guid actingUserId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        var exists = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM statistic_tiles WHERE id = @id", new { id }, cancellationToken: ct));
        if (exists is null) return new SaveStatisticTileResult.NotFound();

        var errors = await ValidateAsync(connection, id, input, ct);
        if (errors.Count > 0) return new SaveStatisticTileResult.ValidationFailed(errors);

        var sql = $"""
            UPDATE statistic_tiles SET
                title = @Title,
                metric_key = @MetricKey,
                chart_type = @ChartType,
                period = @Period,
                grouping = @Grouping,
                scope = @Scope,
                scope_user_id = @ScopeUserId,
                updated_utc = now()
            WHERE id = @id
            RETURNING {TileColumns}
            """;
        var tile = await connection.QuerySingleAsync<StatisticTile>(new CommandDefinition(sql, new
        {
            id,
            input.Title,
            input.MetricKey,
            input.ChartType,
            input.Period,
            input.Grouping,
            input.Scope,
            ScopeUserId = NormalizeScopeUser(input),
        }, cancellationToken: ct));
        _ = actingUserId;
        return new SaveStatisticTileResult.Updated(tile);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM statistic_tiles WHERE id = @id", new { id }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<IReadOnlyList<Guid>> GetAssignmentsAsync(Guid tileId, CancellationToken ct = default)
    {
        const string sql = "SELECT assigned_user_id FROM statistic_tile_assignments WHERE tile_id = @tileId ORDER BY assigned_user_id";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<Guid>(
            new CommandDefinition(sql, new { tileId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<bool> SetAssignmentsAsync(
        Guid tileId, IReadOnlyCollection<Guid> userIds, Guid actingUserId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var exists = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM statistic_tiles WHERE id = @tileId FOR UPDATE",
            new { tileId }, tx, cancellationToken: ct));
        if (exists is null)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        // Keep only Agent/Admin targets — statistics is agent/admin territory,
        // and a tile assigned to a Customer would never render anywhere.
        var distinct = userIds.Distinct().ToArray();
        var validIds = distinct.Length == 0
            ? Array.Empty<Guid>()
            : (await connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM users WHERE id = ANY(@ids) AND role_name IN ('Agent','Admin')",
                new { ids = distinct }, tx, cancellationToken: ct))).ToArray();

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM statistic_tile_assignments WHERE tile_id = @tileId",
            new { tileId }, tx, cancellationToken: ct));

        if (validIds.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO statistic_tile_assignments (tile_id, assigned_user_id, assigned_by)
                VALUES (@tileId, @userId, @actingUserId)
                """,
                validIds.Select(uid => new { tileId, userId = uid, actingUserId }),
                tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return true;
    }

    // ---- read surface -----------------------------------------------------

    public async Task<IReadOnlyList<StatisticTileWithLayout>> GetAssignedForViewerAsync(
        Guid viewerId, CancellationToken ct = default)
    {
        // Assigned tiles LEFT JOINed with the viewer's layout. Tiles without a
        // layout row sort last (NULLS LAST) and fall back to medium/visible.
        const string sql = """
            SELECT  t.id          AS Id,
                    t.title       AS Title,
                    t.metric_key  AS MetricKey,
                    t.chart_type  AS ChartType,
                    t.period      AS Period,
                    t.grouping    AS Grouping,
                    t.scope       AS Scope,
                    t.scope_user_id AS ScopeUserId,
                    t.created_by  AS CreatedBy,
                    t.created_utc AS CreatedUtc,
                    t.updated_utc AS UpdatedUtc,
                    l.position    AS Position,
                    l.size        AS Size,
                    l.hidden      AS Hidden,
                    su.email      AS ScopeUserEmail
            FROM statistic_tile_assignments a
            JOIN statistic_tiles t ON t.id = a.tile_id
            LEFT JOIN statistic_tile_layout l ON l.tile_id = t.id AND l.user_id = @viewerId
            LEFT JOIN users su ON su.id = t.scope_user_id
            WHERE a.assigned_user_id = @viewerId
            ORDER BY l.position ASC NULLS LAST, t.created_utc ASC, t.id ASC
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<AssignedRow>(
            new CommandDefinition(sql, new { viewerId }, cancellationToken: ct));
        var index = 0;
        return rows.Select(r => new StatisticTileWithLayout(
            r.ToTile(),
            r.Position ?? index++,
            r.Size ?? StatisticTileSizes.Medium,
            r.Hidden ?? false,
            r.ScopeUserEmail)).ToList();
    }

    private sealed class AssignedRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = "";
        public string MetricKey { get; set; } = "";
        public string ChartType { get; set; } = "";
        public string Period { get; set; } = "";
        public string Grouping { get; set; } = "";
        public string Scope { get; set; } = "";
        public Guid? ScopeUserId { get; set; }
        public Guid? CreatedBy { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int? Position { get; set; }
        public string? Size { get; set; }
        public bool? Hidden { get; set; }
        public string? ScopeUserEmail { get; set; }

        public StatisticTile ToTile() => new()
        {
            Id = Id, Title = Title, MetricKey = MetricKey, ChartType = ChartType,
            Period = Period, Grouping = Grouping, Scope = Scope, ScopeUserId = ScopeUserId,
            CreatedBy = CreatedBy, CreatedUtc = CreatedUtc, UpdatedUtc = UpdatedUtc,
        };
    }

    public async Task SaveLayoutForViewerAsync(
        Guid viewerId, IReadOnlyList<StatisticLayoutEntry> layout, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        // Only tiles actually assigned to the viewer may carry a layout row —
        // this both keeps FK integrity and stops a viewer persisting layout
        // for tiles they were never given.
        var assigned = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT tile_id FROM statistic_tile_assignments WHERE assigned_user_id = @viewerId",
            new { viewerId }, tx, cancellationToken: ct))).ToHashSet();

        var seen = new HashSet<Guid>();
        var rows = new List<(Guid TileId, int Position, string Size, bool Hidden)>();
        var pos = 0;
        foreach (var entry in layout)
        {
            if (!Guid.TryParse(entry.TileId, out var tileId)) continue;
            if (!assigned.Contains(tileId) || !seen.Add(tileId)) continue;
            var size = StatisticTileSizes.IsKnown(entry.Size) ? entry.Size! : StatisticTileSizes.Medium;
            rows.Add((tileId, pos++, size, entry.Hidden));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM statistic_tile_layout WHERE user_id = @viewerId",
            new { viewerId }, tx, cancellationToken: ct));

        if (rows.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO statistic_tile_layout (user_id, tile_id, position, size, hidden)
                VALUES (@viewerId, @TileId, @Position, @Size, @Hidden)
                """,
                rows.Select(r => new { viewerId, r.TileId, r.Position, r.Size, r.Hidden }),
                tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task<bool> IsTileAssignedToAsync(Guid tileId, Guid viewerId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT EXISTS(
                SELECT 1 FROM statistic_tile_assignments
                WHERE tile_id = @tileId AND assigned_user_id = @viewerId)
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(sql, new { tileId, viewerId }, cancellationToken: ct));
    }

    // ---- helpers ----------------------------------------------------------

    private static Guid? NormalizeScopeUser(StatisticTileInput input) =>
        string.Equals(input.Scope, StatisticScopes.User, StringComparison.Ordinal) ? input.ScopeUserId : null;

    /// Validates a tile definition against the catalogue. Returns the list of
    /// human-readable errors (empty = valid).
    private static async Task<IReadOnlyList<string>> ValidateAsync(
        NpgsqlConnection connection, Guid? _, StatisticTileInput input, CancellationToken ct)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(input.Title))
            errors.Add("Title is required.");
        else if (input.Title.Trim().Length > 120)
            errors.Add("Title must be 120 characters or fewer.");

        var metric = StatisticsCatalogue.Find(input.MetricKey);
        if (metric is null)
        {
            errors.Add("Unknown metric.");
        }
        else
        {
            if (!metric.ChartTypes.Contains(input.ChartType))
                errors.Add("Chart type is not valid for this metric.");
            if (!metric.Groupings.Contains(input.Grouping))
                errors.Add("Grouping is not valid for this metric.");
        }

        if (!StatisticPeriods.IsKnown(input.Period))
            errors.Add("Unknown period.");
        if (!StatisticScopes.IsKnown(input.Scope))
            errors.Add("Unknown scope.");

        if (string.Equals(input.Scope, StatisticScopes.User, StringComparison.Ordinal))
        {
            if (input.ScopeUserId is null)
            {
                errors.Add("A technician must be selected for a single-user tile.");
            }
            else
            {
                var ok = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM users WHERE id = @id AND role_name IN ('Agent','Admin'))",
                    new { id = input.ScopeUserId }, cancellationToken: ct));
                if (!ok) errors.Add("The selected technician does not exist.");
            }
        }

        return errors;
    }
}
