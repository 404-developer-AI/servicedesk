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
    let active = true;

    const onChanged = () => {
      queryClient.invalidateQueries({ queryKey: ["timesheet", "manager", "entries"] });
      queryClient.invalidateQueries({ queryKey: ["timesheet", "manager", "month"] });
    };

    hub.on("TimesheetEntriesChanged", onChanged);

    // Retry until connected (same pattern as AgentActivityTile) — the first
    // mount can race the hub-start in usePresenceConnection, and waiting for
    // a *re*connect alone would leave a hard refresh straight onto the
    // timesheet page out of the group.
    const join = () => {
      if (!active) return;
      if (hub.state === HubConnectionState.Connected) {
        hub.invoke("JoinTimesheetManagers").catch(() => {});
      } else {
        window.setTimeout(join, 500);
      }
    };
    join();

    // Group membership is per-connection, so re-join after a reconnect.
    // SignalR has no offReconnected; the `active` guard neutralises this
    // callback once the page unmounts.
    hub.onreconnected(() => {
      if (active) void hub.invoke("JoinTimesheetManagers").catch(() => {});
    });

    return () => {
      active = false;
      hub.off("TimesheetEntriesChanged", onChanged);
      // Leave the group so pushes stop when the page closes — membership
      // used to linger until disconnect, spamming handler-less broadcasts.
      if (hub.state === HubConnectionState.Connected) {
        hub.invoke("LeaveTimesheetManagers").catch(() => {});
      }
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
