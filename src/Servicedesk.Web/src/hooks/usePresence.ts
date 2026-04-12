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

function getConnection(): HubConnection {
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
      // Re-sync recent tickets and request full state after reconnect
      const ids = recentRef.current.map((t) => t.id);
      await hub.invoke("SyncRecent", ids);
      await hub.invoke("RequestFullSync");
    });

    async function start() {
      if (hub.state === HubConnectionState.Disconnected) {
        try {
          await hub.start();
          // Initial sync
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
 * Automatically stops viewing on unmount.
 */
export function useViewingTicket(ticketId: string) {
  React.useEffect(() => {
    const hub = getConnection();

    async function startViewing() {
      if (hub.state === HubConnectionState.Connected) {
        await hub.invoke("StartViewing", ticketId).catch(() => {});
      }
    }

    // If connected, start immediately. Otherwise wait for connection.
    startViewing();

    // Also handle reconnects — re-announce viewing
    const onReconnected = async () => {
      await hub.invoke("StartViewing", ticketId).catch(() => {});
    };
    hub.onreconnected(onReconnected);

    return () => {
      if (hub.state === HubConnectionState.Connected) {
        hub.invoke("StopViewing").catch(() => {});
      }
    };
  }, [ticketId]);
}
