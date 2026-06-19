import * as React from "react";
import { HubConnectionState } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ensureNotificationConnection } from "@/hooks/useNotificationSignalR";

/// Push payload for a new/returned Timesheet comment (matches
/// TimesheetCommentPush on the server). Body is deliberately omitted — the
/// client re-reads the authoritative data from the invalidated queries.
type TimesheetCommentPush = {
  threadId: string;
  ticketNumber: number;
  originContext: string;
  sourceUserEmail: string | null;
};

/// Subscribes to `TimesheetCommentReceived` on the shared per-user
/// notifications hub. On each push it invalidates the timesheet query tree so
/// the Comments inbox, the unread red-dots (sidebar nav, Comments tab, rows)
/// and the review-tab feeds all refresh, and shows a small toast. Mounted once
/// at shell level so the dots update from anywhere in the app.
export function useTimesheetCommentsSignalR() {
  const queryClient = useQueryClient();

  React.useEffect(() => {
    const hub = ensureNotificationConnection();

    const handle = (payload: TimesheetCommentPush) => {
      // Refresh every timesheet surface (inbox, unread-count, feeds).
      queryClient.invalidateQueries({ queryKey: ["timesheet"] });

      const who = payload.sourceUserEmail?.split("@")[0] ?? "A colleague";
      toast.message(`${who} commented on #${payload.ticketNumber}`, {
        description: "Open Timesheet → Comments to reply.",
      });
    };

    hub.on("TimesheetCommentReceived", handle);

    // The notifications hook owns starting the connection, but start
    // defensively in case this hook mounts first.
    if (hub.state === HubConnectionState.Disconnected) {
      hub.start().catch(() => {});
    }

    return () => {
      hub.off("TimesheetCommentReceived", handle);
    };
  }, [queryClient]);
}
