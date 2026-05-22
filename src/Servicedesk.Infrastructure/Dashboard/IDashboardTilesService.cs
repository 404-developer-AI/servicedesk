namespace Servicedesk.Infrastructure.Dashboard;

/// Read/write access to the per-user `user_dashboard_tiles` junction
/// table. Reads return the user's ordered tile preferences (id + size
/// per row); writes either flip individual tiles on/off (the admin
/// flow) or replace the full ordered layout (the user's own edit-mode
/// flow). All writes validate every id against
/// <see cref="DashboardTileIds.All"/> and every size against
/// <see cref="DashboardTileSizes.All"/>.
public interface IDashboardTilesService
{
    /// Returns the user's tile preferences ordered by position
    /// ascending. Empty list when no tiles have been activated.
    Task<IReadOnlyList<DashboardTilePref>> GetForUserAsync(Guid userId, CancellationToken ct = default);

    /// Admin flow — enable/disable the set of tiles for one user
    /// without touching layout. Existing rows keep their position and
    /// size; newly-enabled tiles get appended at the end with the
    /// caller-supplied default size (typically the registry default).
    /// Unknown ids cause <see cref="SetDashboardTilesResult.UnknownTileIds"/>
    /// and no rows are written. Customer rows are rejected to keep the
    /// per-tile flow Agent/Admin-only.
    Task<SetDashboardTilesResult> SetEnabledForUserAsync(
        Guid userId,
        IReadOnlyDictionary<string, string> tileSizes,
        Guid actingAdminId,
        CancellationToken ct = default);

    /// User flow — replace the user's full layout with the supplied
    /// ordered list. Each entry carries its post-edit size. Used by the
    /// dashboard's edit-mode (drag + size-cycle). The caller is the
    /// owner, not an admin; <paramref name="userId"/> is taken from the
    /// session principal.
    Task<SetDashboardTilesResult> SaveLayoutForUserAsync(
        Guid userId,
        IReadOnlyList<DashboardTilePref> layout,
        CancellationToken ct = default);
}

/// One row in the per-user dashboard tile list. Position is the
/// 0-based ordering within the user's grid; size is one of
/// <see cref="DashboardTileSizes.All"/>.
public sealed record DashboardTilePref(string TileId, string Size);

public static class DashboardTileSizes
{
    public const string Small = "small";
    public const string Medium = "medium";
    public const string Wide = "wide";
    public const string Full = "full";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Small, Medium, Wide, Full };

    public static bool IsKnown(string size) => All.Contains(size);
}

public abstract record SetDashboardTilesResult
{
    public sealed record Updated(IReadOnlyList<DashboardTilePref> Tiles) : SetDashboardTilesResult;
    public sealed record UserNotFound : SetDashboardTilesResult;
    public sealed record CustomerNotAllowed : SetDashboardTilesResult;
    public sealed record UnknownTileIds(IReadOnlyList<string> Ids) : SetDashboardTilesResult;
    public sealed record InvalidSize(string Size) : SetDashboardTilesResult;
}
