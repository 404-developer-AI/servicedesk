import * as React from "react";
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";

let connection: HubConnection | null = null;

function getConnection(): HubConnection {
  if (!connection) {
    connection = new HubConnectionBuilder()
      .withUrl("/hubs/integrations")
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();
  }
  return connection;
}

/// Subscribe to integration-status push events. Each push from the server
/// is treated as a "stale, refetch" signal — the SPA invalidates its
/// per-integration status query so the existing GET /status endpoint
/// re-runs and the tile / detail page render the fresh payload. Keeping
/// the SignalR contract this thin means we never ship the authorized-
/// subject / email through the broadcast channel.
///
/// Connection failure is non-fatal: the existing 30-second poll on the
/// status query covers the gap so an admin without WebSocket support
/// still sees state changes within the polling window.
export function useIntegrationsSignalR() {
  const queryClient = useQueryClient();

  React.useEffect(() => {
    const hub = getConnection();

    const handleStatusUpdated = (integration: string, _state: string) => {
      // Today only Adsolut is wired. The integration argument lets future
      // connectors invalidate their own query without this hook needing
      // to know about each one — the key shape stays in sync via convention.
      queryClient.invalidateQueries({
        queryKey: ["integrations", integration, "status"],
      });
    };

    // v0.0.26 — sync-tick completion. Same convention as the status push,
    // but invalidates the per-integration sync-state query so the admin
    // panel refreshes its counters + last-sync timestamps without waiting
    // for the next user navigation.
    const handleSyncCompleted = (integration: string) => {
      queryClient.invalidateQueries({
        queryKey: ["integrations", integration, "sync"],
      });
      queryClient.invalidateQueries({
        queryKey: ["integrations", integration, "audit"],
      });
    };

    hub.on("IntegrationStatusUpdated", handleStatusUpdated);
    hub.on("IntegrationSyncCompleted", handleSyncCompleted);

    async function start() {
      if (hub.state === HubConnectionState.Disconnected) {
        try {
          await hub.start();
        } catch {
          // Silent — admin will still see updates via the 30s status poll.
        }
      }
    }
    void start();

    return () => {
      hub.off("IntegrationStatusUpdated", handleStatusUpdated);
      hub.off("IntegrationSyncCompleted", handleSyncCompleted);
    };
  }, [queryClient]);
}

export function getIntegrationsConnection(): HubConnection | null {
  return connection;
}
