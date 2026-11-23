import { request } from "@/lib/http";

// ---- Activity feed (v0.0.42) ----

/// One row in the agent activity feed. Mirrors the backend
/// ActivityFeedEntry record. EntityId is the source entity's
/// stringified id (ticket UUID, settings key, user id, …); the UI
/// uses entityType to decide whether to render it as a link.
export type ActivityEntry = {
  id: number;
  occurredUtc: string;
  agentId: string;
  agentEmail: string;
  agentRole: string;
  eventType: string;
  entityType: string | null;
  entityId: string | null;
  entityExtra: string | null;
  summary: string;
  metadataJson: string;
  /// Enriched at query time for ticket-typed events. Null for any
  /// other entity type, and null when masked (see ticketRestricted).
  ticketNumber: number | null;
  ticketSubject: string | null;
  /// True when this row references a ticket in a queue the viewer cannot
  /// access. The subject/number/entityId are stripped server-side; the
  /// UI shows a non-clickable "Restricted ticket" placeholder.
  ticketRestricted?: boolean;
};
export type ActivityListQuery = {
  agentId?: string;
  eventType?: string;
  fromUtc?: string;
  toUtc?: string;
  search?: string;
  cursor?: number;
  limit?: number;
};
export type ActivityListPage = {
  items: ActivityEntry[];
  nextCursor: number | null;
};
export const activityApi = {
  recent: (limit = 25) =>
    request<{ items: ActivityEntry[] }>(
      "GET",
      `/api/activity/recent?limit=${limit}`,
    ),
  list: (query: ActivityListQuery = {}) => {
    const params = new URLSearchParams();
    if (query.agentId) params.set("agentId", query.agentId);
    if (query.eventType) params.set("eventType", query.eventType);
    if (query.fromUtc) params.set("fromUtc", query.fromUtc);
    if (query.toUtc) params.set("toUtc", query.toUtc);
    if (query.search) params.set("search", query.search);
    if (query.cursor !== undefined) params.set("cursor", String(query.cursor));
    params.set("limit", String(query.limit ?? 50));
    return request<ActivityListPage>(
      "GET",
      `/api/activity?${params.toString()}`,
    );
  },
};
