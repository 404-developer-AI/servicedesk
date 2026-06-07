namespace Servicedesk.Infrastructure.Statistics;

/// CRUD + assignment + per-viewer layout for statistic tiles. The write
/// surface (create/update/delete/assign + ListAll) is for statistics_write
/// users; the read surface (GetAssignedForViewer/SaveLayout) is for
/// statistics_read users. The endpoint layer enforces the flags; this
/// service enforces data integrity (catalogue validation, assignment-scoped
/// layout writes, Agent/Admin-only assignment targets).
public interface IStatisticTileService
{
    // ---- write surface (builder) --------------------------------------
    Task<IReadOnlyList<StatisticTileSummary>> ListAllAsync(CancellationToken ct = default);
    Task<StatisticTile?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<SaveStatisticTileResult> CreateAsync(StatisticTileInput input, Guid actingUserId, CancellationToken ct = default);
    Task<SaveStatisticTileResult> UpdateAsync(Guid id, StatisticTileInput input, Guid actingUserId, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetAssignmentsAsync(Guid tileId, CancellationToken ct = default);
    /// Replaces the assignment set for a tile. Non-Agent/Admin ids are
    /// dropped. Returns false if the tile does not exist.
    Task<bool> SetAssignmentsAsync(Guid tileId, IReadOnlyCollection<Guid> userIds, Guid actingUserId, CancellationToken ct = default);

    // ---- read surface (viewer) ----------------------------------------
    /// Tiles assigned to the viewer, merged with their saved layout and
    /// ordered by position. Hidden tiles are included (flagged) so edit mode
    /// can surface them; the normal render filters them out.
    Task<IReadOnlyList<StatisticTileWithLayout>> GetAssignedForViewerAsync(Guid viewerId, CancellationToken ct = default);

    /// Persists the viewer's layout. Only tile-ids actually assigned to the
    /// viewer are written; anything else in the payload is dropped.
    Task SaveLayoutForViewerAsync(Guid viewerId, IReadOnlyList<StatisticLayoutEntry> layout, CancellationToken ct = default);

    /// True when the viewer may see this tile's data: they are assigned it,
    /// or they hold statistics_write (builders can preview any tile).
    Task<bool> IsTileAssignedToAsync(Guid tileId, Guid viewerId, CancellationToken ct = default);
}
