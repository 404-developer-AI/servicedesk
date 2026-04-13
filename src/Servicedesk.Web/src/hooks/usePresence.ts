import * as React from "react";
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from "@microsoft/signalr";
import { usePresenceStore, type PresenceUser } from "@/stores/usePresenceStore";
import { useRecentTicketsStore } from "@/stores/useRecentTicketsStore";

let connection: HubConnection | null = null;

// Module-level pending ticket id so StartViewing fires immediately
// after connection, before SyncRecent — prevents the brief "recent"
// flash that other agents see on page refresh.
let pendingViewTicketId: string | null = null;

export function setPendingView(ticketId: string | null) {
  pendingViewTicketId = ticketId;
}

export function getConnection(): HubConnection {
  if (!connection) {
    connection = new HubConnectionBuilder()
      .withUrl("/hubs/presence")
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();
  }
  return connection;
}

/**
 * Top-level hook that manages the SignalR presence connection.
 * Mount once in AppShell. Syncs recent tickets and listens for
 * presence broadcasts from other users.
 */
export function usePresenceConnection() {
  const setTicketPresence = usePresenceStore((s) => s.setTicketPresence);
  const setFullSync = usePresenceStore((s) => s.setFullSync);
  const recentTickets = useRecentTicketsStore((s) => s.recentTickets);

  // Keep a ref to latest recentTickets so we can sync without re-subscribing
  const recentRef = React.useRef(recentTickets);
  recentRef.current = recentTickets;

  React.useEffect(() => {
    const hub = getConnection();

    hub.on("TicketPresence", (ticketId: string, users: PresenceUser[]) => {
      setTicketPresence(ticketId, users);
    });

    hub.on("FullSync", (data: Record<string, PresenceUser[]>) => {
      setFullSync(data);
    });

    hub.onreconnected(async () => {
      // Re-announce viewing BEFORE syncing recent so the first
      // broadcast already shows "viewing", not "recent".
      if (pendingViewTicketId) {
        await hub.invoke("StartViewing", pendingViewTicketId).catch(() => {});
      }
      const ids = recentRef.current.map((t) => t.id);
      await hub.invoke("SyncRecent", ids);
      await hub.invoke("RequestFullSync");
    });

    async function start() {
      if (hub.state === HubConnectionState.Disconnected) {
        try {
          await hub.start();
          // Announce viewing first so other agents never see a
          // "recent" flash for someone who is actively viewing.
          if (pendingViewTicketId) {
            await hub.invoke("StartViewing", pendingViewTicketId).catch(() => {});
          }
          const ids = recentRef.current.map((t) => t.id);
          await hub.invoke("SyncRecent", ids);
          await hub.invoke("RequestFullSync");
        } catch {
          // Will auto-retry via reconnect policy
        }
      }
    }

    start();

    return () => {
      hub.off("TicketPresence");
      hub.off("FullSync");
      hub.stop();
      connection = null;
    };
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  // Sync recent tickets whenever they change
  React.useEffect(() => {
    const hub = getConnection();
    if (hub.state === HubConnectionState.Connected) {
      const ids = recentTickets.map((t) => t.id);
      hub.invoke("SyncRecent", ids).catch(() => {});
    }
  }, [recentTickets]);
}

/**
 * Call from TicketDetailPage to signal "I'm viewing this ticket".
 * Automatically stops viewing on unmount. Sets a module-level pending
 * view so that on page refresh the initial connection sends
 * StartViewing before SyncRecent — preventing the "recently opened"
 * flash for other agents.
 */
export function useViewingTicket(ticketId: string) {
  React.useEffect(() => {
    const hub = getConnection();

    // Register the pending view so the connection start/reconnect
    // sequence fires StartViewing first.
    setPendingView(ticketId);

    async function startViewing() {
      if (hub.state === HubConnectionState.Connected) {
        await hub.invoke("StartViewing", ticketId).catch(() => {});
      }
      // If not yet connected, usePresenceConnection's start() will
      // pick up pendingViewTicketId and send StartViewing for us.
    }

    startViewing();

    return () => {
      setPendingView(null);
      if (hub.state === HubConnectionState.Connected) {
        hub.invoke("StopViewing").catch(() => {});
      }
    };
  }, [ticketId]);
}
