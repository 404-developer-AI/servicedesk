import * as React from "react";
import { HubConnectionState } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { getConnection } from "./usePresence";

/// v0.0.35 commit H — listens for manager-scoped timesheet broadcasts.
/// Joins the `timesheet-managers` SignalR group on the existing
/// /hubs/presence connection (the server method is a no-op for the
/// non-manager case; the actual data endpoint still gates by flag, so the
/// join is harmless for browsers that mis-mount this hook).
///
/// Mount once in <c>TimesheetPage</c> when the user has the manager flag.
/// On every <c>TimesheetEntriesChanged</c> push: invalidate the manager
/// entries + month queries so Tab 2 / Tab 3 refetch without the manager
/// having to do anything.
export function useTimesheetManagerRealtime(enabled: boolean) {
  const queryClient = useQueryClient();

  React.useEffect(() => {
    if (!enabled) return;
    const hub = getConnection();

    const onChanged = () => {
      queryClient.invalidateQueries({ queryKey: ["timesheet", "manager", "entries"] });
      queryClient.invalidateQueries({ queryKey: ["timesheet", "manager", "month"] });
    };

    hub.on("TimesheetEntriesChanged", onChanged);

    async function joinGroup() {
      if (hub.state === HubConnectionState.Connected) {
        await hub.invoke("JoinTimesheetManagers").catch(() => {});
      }
    }
    // First mount might race the hub-start in usePresenceConnection; retry
    // on reconnected so we end up in the group either way.
    void joinGroup();
    hub.onreconnected(() => {
      void hub.invoke("JoinTimesheetManagers").catch(() => {});
    });

    return () => {
      hub.off("TimesheetEntriesChanged", onChanged);
    };
  }, [enabled, queryClient]);
}

/// Per-ticket listener for the TicketTimesheetPanel. Piggybacks on the
/// existing `ticket:{id}` group that <c>useViewingTicket</c> already joins
/// when the ticket-detail page mounts — no extra plumbing on the server.
/// On `TicketTimesheetUpdated` for THIS ticket id: invalidate the panel's
/// query so the row-list refetches.
export function useTicketTimesheetRealtime(ticketId: string) {
  const queryClient = useQueryClient();

  React.useEffect(() => {
    const hub = getConnection();

    const onUpdated = (updatedTicketId: string) => {
      if (updatedTicketId !== ticketId) return;
      queryClient.invalidateQueries({ queryKey: ["timesheet", "ticket", ticketId] });
    };

    hub.on("TicketTimesheetUpdated", onUpdated);

    return () => {
      hub.off("TicketTimesheetUpdated", onUpdated);
    };
  }, [ticketId, queryClient]);
}
