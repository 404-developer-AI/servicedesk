import { useQuery } from "@tanstack/react-query";
import { ticketChecklistApi, type ChecklistSettings, type TicketChecklist } from "@/lib/checklist-api";

/// Query keys shared by the bar, header button, panel and pop-out. The
/// checklist list key sits under ["ticket", id] on purpose: the existing
/// realtime hook invalidates that prefix on every TicketUpdated push, and
/// every checklist mutation ends with such a push, so all viewers refresh.
export const checklistSettingsKey = ["settings", "checklists"] as const;
export const ticketChecklistsKey = (ticketId: string) => ["ticket", ticketId, "checklists"] as const;

export function useChecklistSettings() {
  return useQuery<ChecklistSettings>({
    queryKey: checklistSettingsKey,
    queryFn: ticketChecklistApi.settings,
    staleTime: 60_000,
  });
}

export function useTicketChecklists(ticketId: string, enabled: boolean) {
  const q = useQuery({
    queryKey: ticketChecklistsKey(ticketId),
    queryFn: () => ticketChecklistApi.list(ticketId),
    enabled,
    staleTime: 15_000,
  });
  const checklists: TicketChecklist[] = q.data?.items ?? [];
  return { ...q, checklists };
}

/// Aggregate over the ticket's checklists for the header/bar chip.
export function summarizeChecklists(checklists: TicketChecklist[]) {
  let requiredTotal = 0;
  let requiredDone = 0;
  let incompleteBlocking = 0;
  for (const c of checklists) {
    requiredTotal += c.requiredTotal;
    requiredDone += c.requiredDone;
    if (c.blockClose && c.completedUtc === null) incompleteBlocking++;
  }
  const allComplete = checklists.length > 0 && checklists.every((c) => c.completedUtc !== null);
  return { count: checklists.length, requiredTotal, requiredDone, incompleteBlocking, allComplete };
}
