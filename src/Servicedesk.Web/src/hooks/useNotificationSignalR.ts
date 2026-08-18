import * as React from "react";
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { useNavigate } from "@tanstack/react-router";
import type { UserNotification } from "@/lib/notification-api";
import { notificationApi } from "@/lib/notification-api";
import { hydrateRecentTicketsFromServer } from "@/stores/useRecentTicketsStore";

let connection: HubConnection | null = null;

function getConnection(): HubConnection {
  if (!connection) {
    connection = new HubConnectionBuilder()
      .withUrl("/hubs/notifications")
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();
  }
  return connection;
}

/// Minimal push-payload shape (matches UserNotificationPush on the server).
/// v0.0.103 — "a trigger tried to resolve/close this ticket but a
/// checklist blocked it". On the ticket page this becomes the same dialog
/// as a manual change (via a window event the page listens to); anywhere
/// else a toast with an "Open ticket" action. The bell row is created
/// server-side; we only refresh the pending list here.
export type ChecklistCloseBlockedPush = {
  notificationId: string;
  ticketId: string;
  ticketNumber: number;
  ticketSubject: string;
  triggerName: string;
  targetStatusName: string;
  checklists: { checklistId: string; name: string; openRequired: number }[];
  eventId: number;
  createdUtc: string;
};

export const CHECKLIST_CLOSE_BLOCKED_EVENT = "sd:checklist-close-blocked";

type NotificationPush = {
  id: string;
  ticketId: string;
  ticketNumber: number;
  ticketSubject: string;
  sourceUserEmail: string | null;
  eventId: number;
  eventType: string;
  previewText: string;
  createdUtc: string;
};

/// Server-push for the security-activity health subsystem. Fired only on
/// upward severity transitions (Ok→Warning / Warning→Critical) by the
/// backend monitor, so a sustained attack doesn't spam the toast. Mirrors
/// `SecurityAlertPush` on the server.
type SecurityAlertPush = {
  severity: "Warning" | "Critical";
  subsystem: string;
  summary: string;
  incidentId: number | null;
  createdUtc: string;
};

/// Mounts the /hubs/notifications connection once per session. On every
/// `NotificationReceived` push: invalidate the pending-list query so the
/// navbar widget re-renders, and surface a sonner toast with a View-action
/// that marks viewed + navigates to the exact event anchor.
///
/// Accepts `toastDurationMs` so the caller (AppShell) can thread the
/// admin-configured `Notifications.PopupDurationSeconds` without this hook
/// having to fetch settings itself.
export function useNotificationSignalR(toastDurationMs: number) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  // Refs so the handler inside the connection callback sees the latest
  // duration + navigate fn without re-subscribing on every render.
  const durationRef = React.useRef(toastDurationMs);
  durationRef.current = toastDurationMs;
  const navigateRef = React.useRef(navigate);
  navigateRef.current = navigate;

  React.useEffect(() => {
    const hub = getConnection();

    const handleNotification = (payload: NotificationPush) => {
      // Invalidate the pending query — the widget will refetch and show
      // the new row. Doing it before the toast keeps the badge count in
      // sync with what the toast is announcing.
      queryClient.invalidateQueries({ queryKey: ["notifications", "pending"] });

      const localPart = payload.sourceUserEmail?.split("@")[0] ?? "agent";
      const kind = payload.eventType === "Note"
        ? "note"
        : payload.eventType === "Comment"
          ? "reply"
          : payload.eventType === "MailSent"
            ? "mail"
            : payload.eventType.toLowerCase();

      toast.message(`@${localPart} tagged you in #${payload.ticketNumber}`, {
        description: payload.previewText || `New ${kind} on "${payload.ticketSubject}"`,
        duration: durationRef.current,
        action: {
          label: "View",
          onClick: () => {
            // Optimistically drop the row from the pending cache so the
            // navbar widget hides it instantly — otherwise the invalidate
            // races the server POST and a stale refetch can bring the row
            // back. Server-side markViewed still fires in the background;
            // on failure the row reappears on the next refetch, which is
            // the conservative fallback.
            queryClient.setQueryData<UserNotification[]>(
              ["notifications", "pending"],
              (old) => old?.filter((n) => n.id !== payload.id) ?? [],
            );
            notificationApi.markViewed(payload.id).catch(() => {});
            void navigateRef.current({
              to: "/tickets/$ticketId",
              params: { ticketId: payload.ticketId },
              hash: `event-${payload.eventId}`,
            });
          },
        },
      });
    };

    hub.on("NotificationReceived", handleNotification);

    const handleChecklistBlocked = (payload: ChecklistCloseBlockedPush) => {
      queryClient.invalidateQueries({ queryKey: ["notifications", "pending"] });
      const onTicket = window.location.pathname === `/tickets/${payload.ticketId}`;
      if (onTicket) {
        // The ticket page owns the dialog; it also marks the bell row viewed.
        window.dispatchEvent(new CustomEvent(CHECKLIST_CLOSE_BLOCKED_EVENT, { detail: payload }));
        return;
      }
      const first = payload.checklists[0];
      const summary = payload.checklists.length === 1 && first
        ? `“${first.name}” still has ${first.openRequired} required item${first.openRequired === 1 ? "" : "s"} open.`
        : `${payload.checklists.length} checklists still have required items open.`;
      toast.warning(`#${payload.ticketNumber} not set to ${payload.targetStatusName}`, {
        description: `Trigger “${payload.triggerName}” was blocked by a checklist. ${summary}`,
        duration: Math.max(durationRef.current, 8000),
        action: {
          label: "Open ticket",
          onClick: () => {
            if (payload.notificationId && payload.notificationId !== "00000000-0000-0000-0000-000000000000") {
              queryClient.setQueryData<UserNotification[]>(
                ["notifications", "pending"],
                (old) => old?.filter((n) => n.id !== payload.notificationId) ?? [],
              );
              notificationApi.markViewed(payload.notificationId).catch(() => {});
            }
            void navigateRef.current({
              to: "/tickets/$ticketId",
              params: { ticketId: payload.ticketId },
              search: first ? ({ checklist: first.checklistId } as never) : undefined,
            });
          },
        },
      });
    };
    hub.on("ChecklistCloseBlocked", handleChecklistBlocked);

    const handleSecurityAlert = (payload: SecurityAlertPush) => {
      // Invalidate the health + incidents queries so the card and pill on
      // /settings/health update when the admin is already on the page.
      queryClient.invalidateQueries({ queryKey: ["admin", "health"] });
      queryClient.invalidateQueries({ queryKey: ["admin", "health", "incidents"] });
      queryClient.invalidateQueries({ queryKey: ["system", "health"] });

      const title = payload.severity === "Critical"
        ? "Critical security activity detected"
        : "Elevated security activity detected";

      const showToast = payload.severity === "Critical" ? toast.error : toast.warning;
      showToast(title, {
        description: payload.summary,
        duration: Math.max(durationRef.current, 15_000),
        action: {
          label: "Review",
          onClick: () => {
            void navigateRef.current({ to: "/settings/health" });
          },
        },
      });
    };

    hub.on("SecurityAlertReceived", handleSecurityAlert);

    // v0.0.42 — recent-tickets sidebar list is server-side and synced
    // across browsers. When another tab/device adds/removes/reorders a
    // ticket, the backend fires this push to the caller's user-group;
    // we rehydrate the local cache so the sidebar reflects the change
    // without a manual refresh.
    const handleRecentTicketsUpdated = () => {
      void hydrateRecentTicketsFromServer();
    };
    hub.on("RecentTicketsUpdated", handleRecentTicketsUpdated);

    async function start() {
      if (hub.state === HubConnectionState.Disconnected) {
        try {
          await hub.start();
        } catch {
          // Connection failure is non-fatal — the widget still renders
          // from the last successful pending-query result. Silent, so a
          // logged-out/session-expired agent doesn't see a red toast.
        }
      }
    }
    void start();

    return () => {
      hub.off("NotificationReceived", handleNotification);
      hub.off("ChecklistCloseBlocked", handleChecklistBlocked);
      hub.off("SecurityAlertReceived", handleSecurityAlert);
      hub.off("RecentTicketsUpdated", handleRecentTicketsUpdated);
    };
  }, [queryClient]);
}

/// Accessor for modules outside the hook (e.g. on logout) to tear down
/// the connection cleanly.
export function getNotificationConnection(): HubConnection | null {
  return connection;
}

/// Get-or-create the shared /hubs/notifications connection. Other hooks
/// (e.g. the Timesheet comments stream) reuse this singleton so the app keeps
/// a single per-user hub connection rather than opening a second one.
export function ensureNotificationConnection(): HubConnection {
  return getConnection();
}

/// Expose the payload type so consumers (toast handlers, tests) can
/// import the shape without duplicating it.
export type { NotificationPush };

/// Narrow helper: after a successful mark-viewed call, other components
/// can call this to refresh the widget without a full re-fetch.
export function prefetchPendingNotifications(
  queryClient: ReturnType<typeof useQueryClient>,
) {
  void queryClient.prefetchQuery({
    queryKey: ["notifications", "pending"],
    queryFn: () => notificationApi.listPending(),
  });
}

/// Allow tests / type-only consumers to see the export.
export type { UserNotification };
