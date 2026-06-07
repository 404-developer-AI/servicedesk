namespace Servicedesk.Infrastructure.Statistics;

/// Computes the data for a statistic tile for a given viewer. The engine is
/// the single place that turns a tile definition (metric + period + grouping
/// + scope) into numbers, applying the scope rules:
///   viewer_self → the viewer's own figures
///   user        → the tile's fixed technician
///   team        → all Agent/Admin users
/// Authorization (may this viewer see this tile at all) is enforced by the
/// endpoint via <see cref="IStatisticTileService.IsTileAssignedToAsync"/>
/// before the engine is called.
public interface IStatisticMetricEngine
{
    /// <paramref name="periodOffset"/> shifts the window by whole period units
    /// (e.g. -1 = previous month for a month tile); 0 = the current period.
    Task<StatisticTileData> ComputeAsync(
        StatisticTile tile, Guid viewerId, int periodOffset = 0, CancellationToken ct = default);
}
