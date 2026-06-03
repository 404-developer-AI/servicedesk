import * as React from "react";
import { useQueryClient } from "@tanstack/react-query";
import { getConnection } from "./usePresence";

/**
 * Listens for real-time ticket updates via SignalR. When the server
 * broadcasts a change for the ticket being viewed, the React Query
 * cache is invalidated so the UI refreshes automatically.
 */
export function useTicketRealtime(ticketId: string) {
  const queryClient = useQueryClient();

  React.useEffect(() => {
    const hub = getConnection();

    const onTicketUpdated = (updatedTicketId: string) => {
      if (updatedTicketId === ticketId) {
        queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
        queryClient.invalidateQueries({ queryKey: ["sla", "ticket", ticketId] });
      }
    };

    hub.on("TicketUpdated", onTicketUpdated);

    return () => {
      hub.off("TicketUpdated", onTicketUpdated);
    };
  }, [ticketId, queryClient]);
}

/**
 * Listens for any ticket list changes (create, update, field change).
 * Invalidates the ticket list query so all viewers see fresh data.
 */
export function useTicketListRealtime() {
  const queryClient = useQueryClient();

  React.useEffect(() => {
    const hub = getConnection();

    // Coalesce bursts of list-change broadcasts into a single refetch. In a
    // busy install every ticket create/update fires TicketListUpdated; the
    // ticket list is now a single full-load query, so invalidating on every
    // event would refetch the whole list repeatedly and make it flicker. A
    // short trailing debounce keeps the list fresh without thrashing.
    let timer: ReturnType<typeof setTimeout> | null = null;
    const onListUpdated = () => {
      if (timer) clearTimeout(timer);
      timer = setTimeout(() => {
        timer = null;
        queryClient.invalidateQueries({ queryKey: ["tickets"] });
      }, 1500);
    };

    hub.on("TicketListUpdated", onListUpdated);

    return () => {
      hub.off("TicketListUpdated", onListUpdated);
      if (timer) clearTimeout(timer);
    };
  }, [queryClient]);
}
