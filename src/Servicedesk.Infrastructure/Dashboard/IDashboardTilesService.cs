namespace Servicedesk.Infrastructure.Dashboard;

/// Read/write access to the per-user `user_dashboard_tiles` junction
/// table. Reads return the enabled-tile set so the dashboard page can
/// decide which widgets to render; writes replace the set atomically
/// after validating every id against <see cref="DashboardTileIds.All"/>.
public interface IDashboardTilesService
{
    /// Returns the enabled tile-ids for one user. Empty array when no
    /// tiles have been activated. Order is not guaranteed.
    Task<IReadOnlyList<string>> GetForUserAsync(Guid userId, CancellationToken ct = default);

    /// Replaces the enabled-tile set for one user. Unknown ids cause
    /// <see cref="SetDashboardTilesResult.UnknownTileIds"/> and no
    /// rows are written. Customer rows are rejected to keep the
    /// per-tile flow Agent/Admin-only.
    Task<SetDashboardTilesResult> SetForUserAsync(
        Guid userId,
        IReadOnlyCollection<string> tileIds,
        Guid actingAdminId,
        CancellationToken ct = default);
}

public abstract record SetDashboardTilesResult
{
    public sealed record Updated(IReadOnlyList<string> TileIds) : SetDashboardTilesResult;
    public sealed record UserNotFound : SetDashboardTilesResult;
    public sealed record CustomerNotAllowed : SetDashboardTilesResult;
    public sealed record UnknownTileIds(IReadOnlyList<string> Ids) : SetDashboardTilesResult;
}
