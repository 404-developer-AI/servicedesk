import * as React from "react";
import type { UseMutationResult } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  ticketApi,
  ApiError,
  type ContactCompanyRole,
  type GateConfirmation,
  type StatusGateMatch,
  type TicketFieldUpdate,
  type TicketMutationResult,
} from "@/lib/ticket-api";
import { checklistErrorCode, type ChecklistSettings, type TicketChecklist } from "@/lib/checklist-api";
import type { Status } from "@/lib/taxonomy-api";
import type { ChecklistBlockState } from "./useChecklistPanel";


export function useStatusChangeFlow({
  ticketId,
  currentStatusId,
  statuses,
  updateMutation,
  checklistsEnabled,
  checklistSettings,
  checklists,
  setChecklistBlock,
  isProjectTicket,
  openLinkedTicketCount,
  setProjectClosePending,
}: {
  ticketId: string;
  currentStatusId: string | null;
  statuses: Status[] | undefined;
  updateMutation: UseMutationResult<TicketMutationResult, Error, TicketFieldUpdate>;
  checklistsEnabled: boolean;
  checklistSettings: ChecklistSettings | null;
  checklists: TicketChecklist[];
  setChecklistBlock: React.Dispatch<React.SetStateAction<ChecklistBlockState>>;
  isProjectTicket: boolean;
  openLinkedTicketCount: number;
  setProjectClosePending: React.Dispatch<React.SetStateAction<TicketFieldUpdate | null>>;
}) {
  // v0.0.42 — status-change gate orchestration. The dialog walks through
  // matching gates one-by-one; only after every gate has been confirmed
  // does the actual PATCH fire with the collected answers. Cancelling any
  // gate aborts the entire status change (TaxonomySelect re-renders from
  // server state on the next tick so the dropdown snaps back on its own).
  const [gateQueue, setGateQueue] = React.useState<StatusGateMatch[]>([]);
  const [gateConfirmations, setGateConfirmations] = React.useState<GateConfirmation[]>([]);
  const [gatePendingFields, setGatePendingFields] = React.useState<TicketFieldUpdate | null>(null);
  const [gateTargetStatusId, setGateTargetStatusId] = React.useState<string | null>(null);


  const readChecklistBlock = React.useCallback((err: unknown): boolean => {
    if (!(err instanceof ApiError) || err.status !== 409 || checklistErrorCode(err) !== "checklist_incomplete") return false;
    const body = err.body as { checklists?: Array<{ checklistId: string; name: string; openRequired: number }> } | null;
    const blockers = (body?.checklists ?? []).map((b) => ({ checklistId: b.checklistId, name: b.name, openRequired: b.openRequired }));
    setChecklistBlock({ blockers, statusName: null });
    return true;
  }, [setChecklistBlock]);

  // The status-change tail (checklist pre-check → gate probe → PATCH),
  // shared by the direct path and the project close-confirmation below.
  const runStatusChange = React.useCallback(async (fields: TicketFieldUpdate) => {
    // Callers only route status changes here, but narrow the type for TS
    // (and fall through safely if a non-status update ever lands here).
    if (!fields.statusId) {
      await updateMutation.mutateAsync(fields);
      return;
    }
    // v0.0.103 — checklist close block, client-side pre-check from the
    // loaded checklists so the agent gets the dialog without a round-trip.
    // The server enforces the same rule (409 checklist_incomplete), which
    // the catch below also renders — this is only the fast path.
    if (checklistsEnabled && checklistSettings) {
      const target = statuses?.find((s) => s.id === fields.statusId);
      if (target && checklistSettings.blockingStateCategories.includes(target.stateCategory)) {
        const blockers = checklists
          .filter((c) => c.blockClose && c.completedUtc === null)
          .map((c) => ({ checklistId: c.id, name: c.name, openRequired: c.requiredTotal - c.requiredDone }));
        if (blockers.length > 0) {
          setChecklistBlock({ blockers, statusName: target.name });
          return;
        }
      }
    }
    let matches: StatusGateMatch[];
    try {
      const probe = await ticketApi.listStatusGates(ticketId, fields.statusId);
      matches = probe.items;
    } catch {
      // Network/auth failure on the probe must not block the user; fall
      // through to the PATCH and let the server return 409 if a gate
      // actually applies. The PATCH then surfaces the gate via ApiError.
      try {
        await updateMutation.mutateAsync(fields);
      } catch (err) {
        if (!readChecklistBlock(err)) throw err;
      }
      return;
    }
    if (matches.length === 0) {
      try {
        await updateMutation.mutateAsync(fields);
      } catch (err) {
        if (!readChecklistBlock(err)) throw err;
      }
      return;
    }
    setGateQueue(matches);
    setGateConfirmations([]);
    setGatePendingFields(fields);
    setGateTargetStatusId(fields.statusId ?? null);
  }, [ticketId, updateMutation, checklistsEnabled, checklistSettings, checklists, statuses, readChecklistBlock, setChecklistBlock]);

  const handleSidePanelUpdate = React.useCallback(async (fields: TicketFieldUpdate) => {
    // Non-status updates and same-value status PATCHes skip the gate
    // probe entirely — listStatusGates would return [] anyway and the
    // extra round-trip would only slow the common case.
    if (!fields.statusId || fields.statusId === currentStatusId) {
      await updateMutation.mutateAsync(fields);
      return;
    }
    // v0.0.105 — closing a project with open linked tickets gets a soft
    // confirmation first (no hard block; the linked tickets are untouched).
    if (isProjectTicket && openLinkedTicketCount > 0) {
      const target = statuses?.find((s) => s.id === fields.statusId);
      if (target && (target.stateCategory === "Resolved" || target.stateCategory === "Closed")) {
        setProjectClosePending(fields);
        return;
      }
    }
    await runStatusChange(fields);
  }, [currentStatusId, updateMutation, isProjectTicket, openLinkedTicketCount, statuses, runStatusChange, setProjectClosePending]);

  const closeGateDialog = React.useCallback(() => {
    setGateQueue([]);
    setGateConfirmations([]);
    setGatePendingFields(null);
    setGateTargetStatusId(null);
  }, []);

  // Shared "advance one gate" path used by both dialog kinds. Pops the
  // head of the queue, appends the supplied confirmation to the running
  // list, and either opens the next gate or fires the actual PATCH.
  // State teardown runs whether the mutation succeeds or fails so a
  // failed PATCH doesn't strand the dialog open.
  const advanceGate = React.useCallback(async (confirmation: GateConfirmation) => {
    if (gateQueue.length === 0 || !gatePendingFields) {
      closeGateDialog();
      return;
    }
    const nextConfirmations = [...gateConfirmations, confirmation];
    const rest = gateQueue.slice(1);
    if (rest.length > 0) {
      setGateQueue(rest);
      setGateConfirmations(nextConfirmations);
      return;
    }
    const fields = gatePendingFields;
    closeGateDialog();
    try {
      await updateMutation.mutateAsync({ ...fields, gateConfirmations: nextConfirmations });
    } catch (err) {
      // updateMutation.onError already surfaced the toast — except for the
      // checklist close block, which gets its dialog here.
      readChecklistBlock(err);
    }
  }, [gateQueue, gateConfirmations, gatePendingFields, updateMutation, closeGateDialog, readChecklistBlock]);

  const onPromptGateConfirm = React.useCallback((answers: Record<string, string>) => {
    const head = gateQueue[0];
    if (!head) return;
    advanceGate({ triggerId: head.triggerId, answers });
  }, [gateQueue, advanceGate]);

  const onContactCompanyGateConfirm = React.useCallback(
    ({ companyId, role }: { companyId: string; role: ContactCompanyRole }) => {
      const head = gateQueue[0];
      if (!head) return;
      advanceGate({ triggerId: head.triggerId, companyId, role });
    },
    [gateQueue, advanceGate],
  );

  const onGateCancel = React.useCallback(() => {
    // Use the target status' stateCategory to phrase the toast — closing
    // a ticket is the prototypical case so "Ticket not closed" reads
    // naturally; everything else falls back to a generic message.
    const targetStatus = statuses?.find((s) => s.id === gateTargetStatusId);
    const closedish = targetStatus?.stateCategory === "Closed";
    closeGateDialog();
    toast(closedish ? "Ticket not closed" : "Status not changed");
  }, [gateTargetStatusId, statuses, closeGateDialog]);
  return {
    gateQueue,
    runStatusChange,
    handleSidePanelUpdate,
    onPromptGateConfirm,
    onContactCompanyGateConfirm,
    onGateCancel,
  };
}
