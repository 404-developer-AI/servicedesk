import * as React from "react";
import type { QueryClient } from "@tanstack/react-query";
import type { ChecklistSettings } from "@/lib/checklist-api";
import { CHECKLIST_CLOSE_BLOCKED_EVENT, type ChecklistCloseBlockedPush } from "@/hooks/useNotificationSignalR";
import { notificationApi } from "@/lib/notification-api";
import { useChecklistSettings, useTicketChecklists } from "../components/checklists/useTicketChecklists";
import type { ChecklistBlocker } from "../components/checklists/ChecklistBlockedDialog";

/// The close-block dialog state: which checklists block, for which status
/// (and, when a trigger was refused, which trigger).
export type ChecklistBlockState = { blockers: ChecklistBlocker[]; statusName: string | null; triggerName?: string | null } | null;

export function useChecklistPanel({
  ticketId,
  queryClient,
  ticketLoaded,
}: {
  ticketId: string;
  queryClient: QueryClient;
  ticketLoaded: boolean;
}) {
  // v0.0.103 — ticket checklists. Settings gate the whole surface; the list
  // key sits under ["ticket", id] so realtime pushes refresh it. The docked
  // panel state lives here (not in the body) because the close-block dialog
  // and the ?checklist= deep link both need to open it.
  const checklistSettingsQ = useChecklistSettings();
  const checklistSettings: ChecklistSettings | null = checklistSettingsQ.data ?? null;
  const checklistsEnabled = checklistSettings?.enabled ?? false;
  const { checklists } = useTicketChecklists(ticketId, checklistsEnabled && ticketLoaded);
  const [checklistOpen, setChecklistOpen] = React.useState(false);
  const [activeChecklistId, setActiveChecklistId] = React.useState<string | null>(null);
  const [checklistBlock, setChecklistBlock] = React.useState<ChecklistBlockState>(null);
  // A trigger's set-status action was refused by the close block while this
  // agent has the ticket open: show the same dialog a manual change gets,
  // and mark the matching bell row viewed so it doesn't nag afterwards.
  React.useEffect(() => {
    const onBlocked = (e: Event) => {
      const payload = (e as CustomEvent<ChecklistCloseBlockedPush>).detail;
      if (!payload || payload.ticketId !== ticketId) return;
      setChecklistBlock({
        blockers: payload.checklists.map((c) => ({ checklistId: c.checklistId, name: c.name, openRequired: c.openRequired })),
        statusName: payload.targetStatusName,
        triggerName: payload.triggerName,
      });
      if (payload.notificationId && payload.notificationId !== "00000000-0000-0000-0000-000000000000") {
        notificationApi.markViewed(payload.notificationId).catch(() => {});
        queryClient.invalidateQueries({ queryKey: ["notifications", "pending"] });
      }
    };
    window.addEventListener(CHECKLIST_CLOSE_BLOCKED_EVENT, onBlocked);
    return () => window.removeEventListener(CHECKLIST_CLOSE_BLOCKED_EVENT, onBlocked);
  }, [ticketId, queryClient]);
  React.useEffect(() => {
    // Deep link from global search ("checklist-items" hits): open the panel
    // on that checklist. Read once per ticket.
    const wanted = new URLSearchParams(window.location.search).get("checklist");
    setChecklistOpen(!!wanted);
    setActiveChecklistId(wanted);
    setChecklistBlock(null);
  }, [ticketId]);
  const openChecklistPanel = React.useCallback((checklistId?: string | null) => {
    if (checklistId) setActiveChecklistId(checklistId);
    setChecklistOpen(true);
    setChecklistBlock(null);
  }, []);
  return {
    checklistSettings,
    checklistsEnabled,
    checklists,
    checklistOpen,
    setChecklistOpen,
    activeChecklistId,
    setActiveChecklistId,
    checklistBlock,
    setChecklistBlock,
    openChecklistPanel,
  };
}
