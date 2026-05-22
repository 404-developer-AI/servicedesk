namespace Servicedesk.Infrastructure.Dashboard;

/// Canonical list of dashboard tile identifiers. The frontend mirrors
/// this in `src/lib/dashboardTiles.ts` — keep both in sync when adding
/// or removing tiles. The values are stored verbatim in
/// `user_dashboard_tiles.tile_id`; validation at the API boundary
/// uses <see cref="IsKnown"/> to reject typos before they reach the DB.
public static class DashboardTileIds
{
    public const string AvgPickup = "avg_pickup";
    public const string SystemHealth = "system_health";
    public const string Integrations = "integrations";
    public const string AgentActivity = "agent_activity";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            AvgPickup,
            SystemHealth,
            Integrations,
            AgentActivity,
        };

    public static bool IsKnown(string tileId) => All.Contains(tileId);
}
