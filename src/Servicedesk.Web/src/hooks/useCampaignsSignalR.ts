import * as React from "react";
import { HubConnectionState } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { ensureNotificationConnection } from "@/hooks/useNotificationSignalR";
import { CAMPAIGNS_KEY, campaignKeys } from "@/lib/campaigns-api";

/// v0.1.67 — `CampaignChanged(campaignId)` on the shared per-user
/// notifications hub (sent only to Campaigns flag holders): the outbox sent
/// a mail, a round finished, or a colleague changed the campaign. Refetches
/// that campaign (debounced — a round pings once per mail) and the list.
/// Mounted by the Campaigns pages only.
export function useCampaignsSignalR(enabled: boolean) {
  const queryClient = useQueryClient();

  React.useEffect(() => {
    if (!enabled) return;
    const hub = ensureNotificationConnection();
    const pending = new Set<string>();
    let timer: ReturnType<typeof setTimeout> | null = null;
    const handle = (campaignId: string) => {
      pending.add(campaignId);
      if (timer) return;
      timer = setTimeout(() => {
        timer = null;
        for (const id of pending) void queryClient.invalidateQueries({ queryKey: campaignKeys.detail(id) });
        pending.clear();
        void queryClient.invalidateQueries({ queryKey: [...CAMPAIGNS_KEY, "list"] });
      }, 1_000);
    };
    hub.on("CampaignChanged", handle);
    if (hub.state === HubConnectionState.Disconnected) {
      hub.start().catch(() => {});
    }
    return () => {
      if (timer) clearTimeout(timer);
      hub.off("CampaignChanged", handle);
    };
  }, [enabled, queryClient]);
}
