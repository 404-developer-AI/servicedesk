import { request } from "@/lib/http";

/// v0.1.54 — following tickets. A personal watch on a ticket: the sidebar
/// Following block lists them, and the server derives "has news" (anything
/// changed since you last opened it) when the list is read.

export interface FollowedTicket {
  ticketId: string;
  ticketNumber: number;
  subject: string;
  statusName: string;
  statusColor: string;
  statusCategory: string;
  followedUtc: string;
  lastSeenUtc: string;
  lastActivityUtc: string;
  hasActivity: boolean;
}

export interface FollowingResponse {
  count: number;
  activeCount: number;
  items: FollowedTicket[];
}

export const FOLLOWING_KEY = ["following"] as const;

const t = (ticketId: string) => `/api/tickets/${encodeURIComponent(ticketId)}/follow`;

export const followingApi = {
  list: () => request<FollowingResponse>("GET", "/api/following"),
  follow: (ticketId: string) => request<void>("PUT", t(ticketId)),
  unfollow: (ticketId: string) => request<void>("DELETE", t(ticketId)),
  seen: (ticketId: string) => request<void>("POST", `${t(ticketId)}/seen`),
};

/// Optimistic cache edits — the server list is the truth, these only bridge
/// the gap until the refetch lands.
export function withoutTicket(data: FollowingResponse | undefined, ticketId: string): FollowingResponse | undefined {
  if (!data) return data;
  const items = data.items.filter((i) => i.ticketId !== ticketId);
  return { items, count: items.length, activeCount: items.filter((i) => i.hasActivity).length };
}

export function markedSeen(data: FollowingResponse | undefined, ticketId: string): FollowingResponse | undefined {
  if (!data || !data.items.some((i) => i.ticketId === ticketId && i.hasActivity)) return data;
  const items = data.items.map((i) => (i.ticketId === ticketId ? { ...i, hasActivity: false } : i));
  return { ...data, items, activeCount: items.filter((i) => i.hasActivity).length };
}
