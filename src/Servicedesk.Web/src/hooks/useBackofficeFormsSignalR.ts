import * as React from "react";
import { HubConnectionState } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { ensureNotificationConnection } from "@/hooks/useNotificationSignalR";
import { BACKOFFICE_FORMS_KEY } from "@/lib/backoffice-forms-api";

/// v0.1.54 — `BackofficeFormsChanged` (payload-less) on the shared per-user
/// notifications hub: the server only sends it to Back office forms users.
/// Invalidates the work list, the open count (red nav dot) and open detail
/// dialogs. Mounted once at shell level, only for flag holders.
export function useBackofficeFormsSignalR(enabled: boolean) {
  const queryClient = useQueryClient();

  React.useEffect(() => {
    if (!enabled) return;
    const hub = ensureNotificationConnection();
    const handle = () => {
      void queryClient.invalidateQueries({ queryKey: BACKOFFICE_FORMS_KEY });
    };
    hub.on("BackofficeFormsChanged", handle);
    if (hub.state === HubConnectionState.Disconnected) {
      hub.start().catch(() => {});
    }
    return () => {
      hub.off("BackofficeFormsChanged", handle);
    };
  }, [enabled, queryClient]);
}
