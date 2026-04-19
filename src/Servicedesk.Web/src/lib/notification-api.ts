import { csrfHeader } from "@/lib/csrf";
import { ApiError } from "@/lib/ticket-api";

/// v0.0.12 stap 4 — client for the @@-mention notification raamwerk.
/// Shape mirrors the backend DTO in `NotificationEndpoints.UserNotificationDto`.
export type UserNotification = {
  id: string;
  ticketId: string;
  ticketNumber: number;
  ticketSubject: string;
  sourceUserId: string | null;
  sourceUserEmail: string | null;
  eventId: number;
  eventType: "Note" | "Comment" | "MailSent" | string;
  previewText: string;
  createdUtc: string;
  viewedUtc: string | null;
  ackedUtc: string | null;
};

export type NotificationHistoryCursor = {
  createdUtc: string;
  id: string;
};

export type NotificationHistoryPage = {
  items: UserNotification[];
  nextCursor: NotificationHistoryCursor | null;
};

async function request<T>(
  method: string,
  url: string,
  body?: unknown,
): Promise<T> {
  const isSafe = method === "GET" || method === "HEAD";
  const res = await fetch(url, {
    method,
    credentials: "include",
    headers: {
      Accept: "application/json",
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      ...(isSafe ? {} : csrfHeader()),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    throw new ApiError(
      res.status,
      url,
      `${url} → ${res.status} ${res.statusText}`,
    );
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const notificationApi = {
  /// Unacked entries for the navbar-widget. Refetched on SignalR push +
  /// window focus.
  listPending: () =>
    request<UserNotification[]>("GET", "/api/notifications/pending"),

  /// Cursor-paginated history for the /profile/mentions page. First call
  /// passes no cursor; subsequent calls pass the `nextCursor` returned
  /// by the previous page.
  listHistory: (cursor?: NotificationHistoryCursor, limit = 50) => {
    const params = new URLSearchParams();
    if (cursor) {
      params.set("cursorUtc", cursor.createdUtc);
      params.set("cursorId", cursor.id);
    }
    params.set("limit", String(limit));
    return request<NotificationHistoryPage>(
      "GET",
      `/api/notifications/history?${params.toString()}`,
    );
  },

  /// Marks viewed (+ acked) — called when the user click-throughs from the
  /// toast or the navbar entry. Idempotent; safe to call twice.
  markViewed: (id: string) =>
    request<void>("POST", `/api/notifications/${id}/view`, {}),

  /// Dismiss without navigating. Used by the per-row X-button in the navbar.
  markAcked: (id: string) =>
    request<void>("POST", `/api/notifications/${id}/ack`, {}),

  /// Bulk-dismiss everything currently unacked for the caller.
  ackAll: () =>
    request<void>("POST", "/api/notifications/ack-all", {}),
};
