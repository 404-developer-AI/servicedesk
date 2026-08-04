import * as React from "react";
import { useQueryClient } from "@tanstack/react-query";
import { systemApi, type SystemVersion } from "@/lib/api";
import { CLIENT_VERSION_OUTDATED_EVENT } from "@/lib/clientVersion";
import {
  SERVER_RECONNECTED_EVENT,
  handleClientOutdated,
  handleServerVersion,
} from "@/lib/appUpdate";

// Tab switching can fire focus checks rapidly; the server version only
// changes on deploys, so anything more frequent than this is waste.
const FOCUS_THROTTLE_MS = 30_000;

/**
 * Mount once in AppShell. Re-checks the server version on the events that
 * can mean "a deploy just happened" (SignalR reconnect, tab focus) and
 * hands the result to the update module. Shares the ["system","version"]
 * query with the sidebar's version display, so a detected update also
 * refreshes the version shown in the UI.
 */
export function useUpdateCheck() {
  const queryClient = useQueryClient();

  React.useEffect(() => {
    let disposed = false;
    let lastCheckAt = 0;
    let checkOnFocus = true;

    async function check(bypassThrottle: boolean) {
      const now = Date.now();
      if (!bypassThrottle && now - lastCheckAt < FOCUS_THROTTLE_MS) {
        return;
      }
      lastCheckAt = now;
      try {
        const data = await queryClient.fetchQuery<SystemVersion>({
          queryKey: ["system", "version"],
          queryFn: systemApi.version,
          staleTime: 0,
        });
        if (disposed || !data) {
          return;
        }
        checkOnFocus = data.updateCheckOnFocus ?? true;
        handleServerVersion(data.version, data.updateRefreshMode ?? "auto");
      } catch {
        // Transient network failure — the next trigger tries again.
      }
    }

    const onReconnected = () => void check(true);
    const onVisibility = () => {
      if (document.visibilityState === "visible" && checkOnFocus) {
        void check(false);
      }
    };
    const onOutdated = () => handleClientOutdated();

    window.addEventListener(SERVER_RECONNECTED_EVENT, onReconnected);
    window.addEventListener(CLIENT_VERSION_OUTDATED_EVENT, onOutdated);
    document.addEventListener("visibilitychange", onVisibility);

    // Initial run anchors the dev-mode reference version and catches a
    // deploy that happened while this tab was asleep. React Query dedupes
    // it against the sidebar's own version fetch on page load.
    void check(true);

    return () => {
      disposed = true;
      window.removeEventListener(SERVER_RECONNECTED_EVENT, onReconnected);
      window.removeEventListener(CLIENT_VERSION_OUTDATED_EVENT, onOutdated);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [queryClient]);
}
