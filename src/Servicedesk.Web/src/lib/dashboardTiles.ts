import type { ComponentType } from "react";
import { LayoutDashboard, ShieldCheck, Plug, UsersRound, Activity } from "lucide-react";
import { AvgPickupTile } from "@/components/dashboard/AvgPickupTile";
import { SystemHealthTile } from "@/components/dashboard/SystemHealthTile";
import { IntegrationsHealthTile } from "@/components/dashboard/IntegrationsHealthTile";
import { AgentActivityTile } from "@/components/dashboard/AgentActivityTile";
import { ActivityFeedTile } from "@/components/dashboard/ActivityFeedTile";
import type { Role } from "@/lib/roles";
import type { DashboardTileSize } from "@/auth/authStore";

export type { DashboardTileSize };

/// Column spans on the 4-col `lg` grid. The dashboard switches to 2-col
/// on md and 1-col on sm, where every size collapses appropriately.
export const TILE_SIZE_COLSPAN: Record<DashboardTileSize, number> = {
  small: 1,
  medium: 2,
  wide: 3,
  full: 4,
};

/// Ordered cycle for the size-cycle button in edit mode. Loops back to
/// the first entry after the last so a click always advances by one.
export const TILE_SIZE_CYCLE: readonly DashboardTileSize[] = ["small", "medium", "wide", "full"];

export function nextSize(current: DashboardTileSize): DashboardTileSize {
  const i = TILE_SIZE_CYCLE.indexOf(current);
  return TILE_SIZE_CYCLE[(i + 1) % TILE_SIZE_CYCLE.length];
}

export type DashboardTile = {
  /// Stable identifier. Stored in `user_dashboard_tiles.tile_id` and
  /// mirrored in the backend `DashboardTileIds` allow-list — keep in
  /// sync when adding or removing tiles.
  id: string;
  label: string;
  description: string;
  /// Minimum role required to see the tile. Admin-only tiles are
  /// hidden when granted to an Agent (UI cannot grant them either).
  minRole: Role;
  icon: ComponentType<{ className?: string }>;
  /// Default size used when an admin first enables this tile for a
  /// user. Stored size lives on user_dashboard_tiles and is editable
  /// from the dashboard's edit mode.
  defaultSize: DashboardTileSize;
  /// Subset of sizes this tile actually supports. A table-heavy tile
  /// may exclude `small` because columns become unreadable; the size-
  /// cycle button skips over excluded sizes.
  allowedSizes: readonly DashboardTileSize[];
  Component: ComponentType;
};

export const DASHBOARD_TILES: DashboardTile[] = [
  {
    id: "avg_pickup",
    label: "Average first-response per queue",
    description:
      "Per-queue average first-response time with a 1d/7d/30d window selector.",
    minRole: "Agent",
    icon: LayoutDashboard,
    defaultSize: "medium",
    allowedSizes: ["medium", "wide", "full"],
    Component: AvgPickupTile,
  },
  {
    id: "system_health",
    label: "System health",
    description: "Subsystem roll-up with status badges and a link to the health page.",
    minRole: "Admin",
    icon: ShieldCheck,
    defaultSize: "medium",
    allowedSizes: ["medium", "wide", "full"],
    Component: SystemHealthTile,
  },
  {
    id: "agent_activity",
    label: "Agent activity",
    description:
      "Live overview of agents with their actively-viewed and recently-opened tickets. Click an agent to see their ticket list.",
    minRole: "Admin",
    icon: UsersRound,
    defaultSize: "full",
    allowedSizes: ["medium", "wide", "full"],
    Component: AgentActivityTile,
  },
  {
    id: "activity_feed",
    label: "Activity feed",
    description:
      "Compact log of every agent + admin action across the app. Visible only to users with the activity-feed flag enabled.",
    minRole: "Agent",
    icon: Activity,
    defaultSize: "medium",
    allowedSizes: ["medium", "wide", "full"],
    Component: ActivityFeedTile,
  },
  {
    id: "integrations",
    label: "Integrations",
    description:
      "Connection + sync status for configured integrations (Adsolut, Telavox, …).",
    minRole: "Admin",
    icon: Plug,
    defaultSize: "full",
    allowedSizes: ["medium", "wide", "full"],
    Component: IntegrationsHealthTile,
  },
];

/// Returns the next size in the cycle that is allowed for the given
/// tile. Skips disallowed sizes silently so the button never lands on
/// a width the tile cannot render. Falls back to the current size when
/// only one size is allowed (cycle is a no-op).
export function nextAllowedSize(tile: DashboardTile, current: DashboardTileSize): DashboardTileSize {
  if (tile.allowedSizes.length <= 1) return current;
  const startIndex = TILE_SIZE_CYCLE.indexOf(current);
  for (let i = 1; i <= TILE_SIZE_CYCLE.length; i++) {
    const candidate = TILE_SIZE_CYCLE[(startIndex + i) % TILE_SIZE_CYCLE.length];
    if (tile.allowedSizes.includes(candidate)) return candidate;
  }
  return current;
}

const ROLE_RANK: Record<Role, number> = {
  Customer: 0,
  Agent: 1,
  Admin: 2,
};

export function roleSatisfies(actual: Role | undefined, minimum: Role): boolean {
  if (!actual) return false;
  return ROLE_RANK[actual] >= ROLE_RANK[minimum];
}
