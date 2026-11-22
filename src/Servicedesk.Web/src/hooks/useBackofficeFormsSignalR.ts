import * as React from "react";
import { HubConnectionState } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { ensureNotificationConnection } from "@/hooks/useNotificationSignalR";
import { BACKOFFICE_FORMS_KEY, backofficeFormKeys } from "@/lib/backoffice-forms-api";

/// v0.1.54 — `BackofficeFormsChanged` (payload-less) on the shared per-user
/// notifications hub: the server only sends it to Back office forms users.
/// Invalidates the work list, the counts (red nav dot) and open detail
/// dialogs. v0.1.61 — `BackofficePresenceChanged`: a back-office user opened
/// or left a ticket; only the presence query refetches (debounced, since
/// every ticket switch of every back-office user pings). Mounted once at
/// shell level, only for flag holders.
export function useBackofficeFormsSignalR(enabled: boolean) {
  const queryClient = useQueryClient();

  React.useEffect(() => {
    if (!enabled) return;
    const hub = ensureNotificationConnection();
    const handle = () => {
      void queryClient.invalidateQueries({ queryKey: BACKOFFICE_FORMS_KEY });
    };
    let timer: ReturnType<typeof setTimeout> | null = null;
    const presence = () => {
      if (timer) clearTimeout(timer);
      timer = setTimeout(() => {
        void queryClient.invalidateQueries({ queryKey: backofficeFormKeys.presence() });
      }, 400);
    };
    hub.on("BackofficeFormsChanged", handle);
    hub.on("BackofficePresenceChanged", presence);
    if (hub.state === HubConnectionState.Disconnected) {
      hub.start().catch(() => {});
    }
    return () => {
      if (timer) clearTimeout(timer);
      hub.off("BackofficeFormsChanged", handle);
      hub.off("BackofficePresenceChanged", presence);
    };
  }, [enabled, queryClient]);
}
