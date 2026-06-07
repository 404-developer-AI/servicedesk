namespace Servicedesk.Infrastructure.Statistics;

/// A statistic-tile definition as authored by a statistics_write user.
/// Row-DTO: hydrated by Dapper from `statistic_tiles` with `AS PascalCase`
/// column aliases (project convention). Sealed class with settable props so
/// Dapper hydration stays happy (see dapper_record_struct_null_bug).
public sealed class StatisticTile
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string MetricKey { get; set; } = "";
    public string ChartType { get; set; } = "";
    public string Period { get; set; } = "";
    public string Grouping { get; set; } = "";
    public string Scope { get; set; } = "";
    public Guid? ScopeUserId { get; set; }
    /// CSV of technician ids for scope='users' (multi-technician compare).
    public string? ScopeUserIds { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// Tile + the viewing user's layout state, returned by the read surface.
public sealed record StatisticTileWithLayout(
    StatisticTile Tile,
    int Position,
    string Size,
    bool Hidden,
    string? ScopeUserEmail);

/// Admin/builder projection: tile + its assignment count + the scope target
/// email (for display in the management list).
public sealed record StatisticTileSummary(
    StatisticTile Tile,
    int AssignedCount,
    string? ScopeUserEmail);

/// Input for creating / updating a tile. Validated in the service against
/// the catalogue before any write.
public sealed record StatisticTileInput(
    string Title,
    string MetricKey,
    string ChartType,
    string Period,
    string Grouping,
    string Scope,
    Guid? ScopeUserId,
    IReadOnlyList<Guid>? ScopeUserIds = null);

/// One layout entry posted by the read-agent's edit mode.
public sealed record StatisticLayoutEntry(string TileId, string? Size, bool Hidden);

/// A single labelled value in a computed tile (a bar, or a grouped row).
/// <paramref name="Value2"/> carries a second stacked series when the metric
/// is a two-series comparison (e.g. billable vs non-billable); null otherwise.
public sealed record StatisticDataPoint(string Label, double Value, double? Value2 = null);

/// The computed result of a tile for one viewer. Covers KPI (Total + single
/// point), single-series bar (Points) and two-series stacked bar (Points with
/// Value2 + SeriesLabels). Values are in the metric's unit (hours).
public sealed record StatisticTileData(
    Guid TileId,
    string MetricKey,
    string ChartType,
    string Unit,
    string PeriodLabel,
    double Total,
    IReadOnlyList<StatisticDataPoint> Points,
    DateTime GeneratedUtc,
    IReadOnlyList<string>? SeriesLabels = null);

// ---- result types ------------------------------------------------------

public abstract record SaveStatisticTileResult
{
    public sealed record Created(StatisticTile Tile) : SaveStatisticTileResult;
    public sealed record Updated(StatisticTile Tile) : SaveStatisticTileResult;
    public sealed record NotFound : SaveStatisticTileResult;
    public sealed record ValidationFailed(IReadOnlyList<string> Errors) : SaveStatisticTileResult;
}

public abstract record StatisticTileDataResult
{
    public sealed record Ok(StatisticTileData Data) : StatisticTileDataResult;
    public sealed record NotFound : StatisticTileDataResult;
    /// Viewer is neither assigned the tile nor allowed to manage it.
    public sealed record Forbidden : StatisticTileDataResult;
}
