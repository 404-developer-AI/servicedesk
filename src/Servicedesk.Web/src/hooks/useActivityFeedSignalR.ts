import * as React from "react";
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import type { ActivityEntry, ActivityListPage } from "@/lib/api";

let connection: HubConnection | null = null;

function getConnection(): HubConnection {
  if (!connection) {
    connection = new HubConnectionBuilder()
      .withUrl("/hubs/activity")
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();
  }
  return connection;
}

/// Subscribes once to the activity-feed hub and merges new
/// <c>ActivityEvent</c> pushes into the React Query caches used by the
/// dashboard tile (`["activity", "recent"]`) and the admin page
/// (`["activity", "list", …]`). The page caches are queryKey-prefixed so
/// every filter variant gets the same prepend.
export function useActivityFeedSignalR(enabled: boolean) {
  const queryClient = useQueryClient();

  React.useEffect(() => {
    if (!enabled) return;
    const hub = getConnection();

    const onEvent = (entry: ActivityEntry) => {
      // Prepend to the recent-list cache (the dashboard tile).
      queryClient.setQueryData<{ items: ActivityEntry[] } | undefined>(
        ["activity", "recent"],
        (prev) => {
          if (!prev) return prev;
          if (prev.items.some((e) => e.id === entry.id)) return prev;
          return { items: [entry, ...prev.items].slice(0, 100) };
        },
      );

      // Prepend to every cached filter variant on the admin page.
      queryClient
        .getQueryCache()
        .findAll({ queryKey: ["activity", "list"] })
        .forEach((q) => {
          const data = q.state.data as ActivityListPage | undefined;
          if (!data) return;
          if (data.items.some((e) => e.id === entry.id)) return;
          q.setData({
            items: [entry, ...data.items],
            nextCursor: data.nextCursor,
          });
        });
    };

    hub.on("ActivityEvent", onEvent);
    if (hub.state === HubConnectionState.Disconnected) {
      hub.start().catch(() => {});
    }

    return () => {
      hub.off("ActivityEvent", onEvent);
    };
  }, [enabled, queryClient]);
}
