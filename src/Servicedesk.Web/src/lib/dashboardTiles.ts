import type { ComponentType } from "react";
import { LayoutDashboard, ShieldCheck, Plug, UsersRound } from "lucide-react";
import { AvgPickupTile } from "@/components/dashboard/AvgPickupTile";
import { SystemHealthTile } from "@/components/dashboard/SystemHealthTile";
import { IntegrationsHealthTile } from "@/components/dashboard/IntegrationsHealthTile";
import { AgentActivityTile } from "@/components/dashboard/AgentActivityTile";
import type { Role } from "@/lib/roles";

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
  /// Full-width or half-width on lg screens. Half = grid cell, full
  /// = spans both columns (used for the wider Integrations tile).
  span: "half" | "full";
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
    span: "half",
    Component: AvgPickupTile,
  },
  {
    id: "system_health",
    label: "System health",
    description: "Subsystem roll-up with status badges and a link to the health page.",
    minRole: "Admin",
    icon: ShieldCheck,
    span: "half",
    Component: SystemHealthTile,
  },
  {
    id: "agent_activity",
    label: "Agent activity",
    description:
      "Live overview of agents with their actively-viewed and recently-opened tickets. Click an agent to see their ticket list.",
    minRole: "Admin",
    icon: UsersRound,
    span: "full",
    Component: AgentActivityTile,
  },
  {
    id: "integrations",
    label: "Integrations",
    description:
      "Connection + sync status for configured integrations (Adsolut, Telavox, …).",
    minRole: "Admin",
    icon: Plug,
    span: "full",
    Component: IntegrationsHealthTile,
  },
];

const ROLE_RANK: Record<Role, number> = {
  Customer: 0,
  Agent: 1,
  Admin: 2,
};

export function roleSatisfies(actual: Role | undefined, minimum: Role): boolean {
  if (!actual) return false;
  return ROLE_RANK[actual] >= ROLE_RANK[minimum];
}
