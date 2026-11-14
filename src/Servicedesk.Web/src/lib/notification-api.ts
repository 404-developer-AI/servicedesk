import { request } from "@/lib/http";

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
  eventType: "Note" | "Comment" | "MailSent" | "ChecklistCloseBlocked" | string;
  previewText: string;
  createdUtc: string;
  viewedUtc: string | null;
  ackedUtc: string | null;
  /// v0.1.54 — "mention" | "note_reply" | "note_like" | "trigger_notice" | …
  notificationType?: string;
  /// Set when the notification points at a reply on an internal note.
  replyId?: number | null;
};

/// Where a bell row / toast jumps to on its ticket.
export function notificationHash(n: { eventId: number; replyId?: number | null }): string {
  return n.replyId ? `reply-${n.replyId}` : `event-${n.eventId}`;
}

/// The row's headline: who did what (the ticket number follows separately).
export function notificationHeadline(n: { notificationType?: string; sourceUserEmail: string | null }): string {
  const who = `@${n.sourceUserEmail?.split("@")[0] ?? "agent"}`;
  switch (n.notificationType) {
    case "note_reply": return `${who} replied`;
    case "note_like": return `${who} liked your note`;
    default: return `${who} in`;
  }
}

export type NotificationHistoryCursor = {
  createdUtc: string;
  id: string;
};

export type NotificationHistoryPage = {
  items: UserNotification[];
  nextCursor: NotificationHistoryCursor | null;
};

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
