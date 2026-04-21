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
      hub.off("SecurityAlertReceived", handleSecurityAlert);
    };
  }, [queryClient]);
}

/// Accessor for modules outside the hook (e.g. on logout) to tear down
/// the connection cleanly.
export function getNotificationConnection(): HubConnection | null {
  return connection;
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
