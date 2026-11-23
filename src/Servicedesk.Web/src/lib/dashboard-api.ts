import { request } from "@/lib/http";

export type AgentActivityTicket = {
  id: string;
  number: number;
  subject: string;
  statusName: string;
  statusColor: string;
  statusStateCategory: string;
  /// True when the viewer has no queue access to this ticket — subject,
  /// number and status are blank and the row renders as a non-clickable
  /// "Restricted ticket" placeholder.
  restricted?: boolean;
};
/// "idle" — no Telavox call (default for users without a linked
/// extension). "ringing" — phone is alerting. "answered" — agent
/// picked up. Drives the indicator on the AgentActivity tile (green
/// dot vs. red phone).
export type AgentCallState = "idle" | "ringing" | "answered";
export type AgentActivity = {
  userId: string;
  email: string;
  roleName: string;
  online: boolean;
  callState: AgentCallState;
  viewing: AgentActivityTicket | null;
  recent: AgentActivityTicket[];
};
export type AgentActivitySnapshot = {
  agents: AgentActivity[];
};
export const dashboardApi = {
  agentActivity: () =>
    request<AgentActivitySnapshot>("GET", "/api/dashboard/agent-activity"),
};
// ---- Recent tickets (v0.0.42) — server-side per-user sidebar list ----

export type RecentTicketEntry = {
  id: string;
  number: number;
  subject: string;
  statusColor?: string;
  statusName?: string;
  statusStateCategory?: string;
};
export const recentTicketsApi = {
  list: () => request<{ items: RecentTicketEntry[] }>("GET", "/api/me/recent-tickets/"),
  add: (ticketId: string) =>
    request<void>("POST", `/api/me/recent-tickets/${ticketId}`),
  remove: (ticketId: string) =>
    request<void>("DELETE", `/api/me/recent-tickets/${ticketId}`),
  /// v0.1.4 — one call empties the list (was one DELETE per ticket).
  clear: () => request<void>("DELETE", "/api/me/recent-tickets/"),
  reorder: (ticketIds: string[]) =>
    request<void>("PUT", "/api/me/recent-tickets/order", { ticketIds }),
};
// ---- User dashboard layout (v0.0.42) ----

/// One row in the user's saved dashboard layout. Mirrors the backend
/// DashboardTilePref record.
export type DashboardLayoutEntry = {
  tileId: string;
  size: "small" | "medium" | "wide" | "full";
};
export const userDashboardLayoutApi = {
  /// Replace the signed-in user's full layout (order + size per tile).
  /// Returns the persisted layout for cache reconciliation.
  save: (tiles: DashboardLayoutEntry[]) =>
    request<{ tiles: DashboardLayoutEntry[] }>(
      "PUT",
      "/api/me/dashboard/layout",
      { tiles },
    ),
};
