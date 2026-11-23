import * as React from "react";
import { useMutation, type QueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ticketApi, mergeTicketMutation, type ContactCompanyRole, type TicketDetail } from "@/lib/ticket-api";
import { hasSeenAlertThisSession, markAlertSeen } from "@/components/CompanyAlertDialog";
import { useTicketFlagSettings } from "@/lib/ticketFlags";

export function useCompanyAlertOnOpen({
  ticketId,
  data,
}: {
  ticketId: string;
  data: TicketDetail | undefined;
}) {
  // v0.0.9 — company alert on ticket-open. Fires when the requester's
  // company has alert_on_open=true. Mode 'session' shows once per browser
  // session per ticket (tracked in sessionStorage); mode 'every' shows on
  // every mount/refresh of this page.
  const [alertOpen, setAlertOpen] = React.useState(false);
  const companyAlert = data?.companyAlert ?? null;
  React.useEffect(() => {
    if (!companyAlert || !companyAlert.alertOnOpen) return;
    if (!companyAlert.alertText?.trim()) return;
    if (
      companyAlert.alertOnOpenMode === "session" &&
      hasSeenAlertThisSession(ticketId)
    ) {
      return;
    }
    setAlertOpen(true);
  }, [companyAlert, ticketId]);

  const handleAlertClose = React.useCallback(() => {
    markAlertSeen(ticketId);
    setAlertOpen(false);
  }, [ticketId]);
  return { alertOpen, companyAlert, handleAlertClose };
}

export function useCallbackPrompt({
  ticketId,
  data,
  queryClient,
}: {
  ticketId: string;
  data: TicketDetail | undefined;
  queryClient: QueryClient;
}) {
  // v0.1.17 — call-back prompt on open. Decided once per open of a ticket
  // (from the flag as loaded), so switching call-back on while the ticket is
  // open never pops it; it recurs on every later open while the flag is on.
  const flagSettingsQ = useTicketFlagSettings();
  const [timeAlertBlocking, setTimeAlertBlocking] = React.useState(true);
  const [callbackPrompt, setCallbackPrompt] = React.useState<"pending" | "show" | "done">("pending");
  React.useEffect(() => {
    setCallbackPrompt("pending");
    setTimeAlertBlocking(true);
  }, [ticketId]);
  const loadedTicketId = data?.ticket?.id;
  const loadedIsCallback = data?.ticket?.isCallback;
  React.useEffect(() => {
    if (callbackPrompt !== "pending" || loadedTicketId !== ticketId) return;
    // Off by default since the Call button clears the flag itself.
    if (!flagSettingsQ.isSuccess && !flagSettingsQ.isError) return;
    const enabled = flagSettingsQ.data?.callbackOpenPromptEnabled ?? false;
    setCallbackPrompt(enabled && loadedIsCallback ? "show" : "done");
  }, [callbackPrompt, loadedTicketId, loadedIsCallback, ticketId, flagSettingsQ.isSuccess, flagSettingsQ.isError, flagSettingsQ.data]);
  const callbackOffMutation = useMutation({
    mutationFn: () => ticketApi.update(ticketId, { isCallback: false }),
    onSuccess: (updated) => {
      // v0.1.33 — the response is the ticket core only: merge it in, then
      // refetch the timeline (gate notes / status-change events it added).
      queryClient.setQueryData<TicketDetail>(["ticket", ticketId], (old) => mergeTicketMutation(old, updated));
      void queryClient.invalidateQueries({ queryKey: ["ticket", ticketId], exact: true });
      queryClient.invalidateQueries({ queryKey: ["tickets"] });
      setCallbackPrompt("done");
      toast.success("Call-back turned off");
    },
    onError: () => toast.error("Could not turn off the call-back flag"),
  });
  return { flagSettingsQ, timeAlertBlocking, setTimeAlertBlocking, callbackPrompt, setCallbackPrompt, callbackOffMutation };
}

export function useCompanyAssignment({
  ticketId,
  data,
  queryClient,
}: {
  ticketId: string;
  data: TicketDetail | undefined;
  queryClient: QueryClient;
}) {
  // v0.0.9 ToDo #4 — auto-open the company-assignment dialog when the ticket
  // was created in the awaiting state (supplier-only or multi-secondary
  // resolution). Agents can also reopen the dialog from the sidepanel banner.
  const [assignOpen, setAssignOpen] = React.useState(false);
  const awaiting = data?.ticket?.awaitingCompanyAssignment ?? false;
  React.useEffect(() => {
    if (awaiting) setAssignOpen(true);
  }, [awaiting, ticketId]);

  const assignMutation = useMutation({
    mutationFn: (vars: { companyId: string; newLinkRole: ContactCompanyRole | null }) =>
      ticketApi.assignCompany(ticketId, {
        companyId: vars.companyId,
        newLinkRole: vars.newLinkRole ?? undefined,
      }),
    onSuccess: (updated) => {
      // v0.1.33 — the response is the ticket core only: merge it in, then
      // refetch the timeline (gate notes / status-change events it added).
      queryClient.setQueryData<TicketDetail>(["ticket", ticketId], (old) => mergeTicketMutation(old, updated));
      void queryClient.invalidateQueries({ queryKey: ["ticket", ticketId], exact: true });
      queryClient.invalidateQueries({ queryKey: ["tickets"] });
      toast.success("Company assigned");
    },
    onError: () => toast.error("Could not assign company"),
  });

  const submitAssignment = React.useCallback(
    async (
      companyId: string,
      _companyName: string,
      newLinkRole: ContactCompanyRole | null,
    ) => {
      await assignMutation.mutateAsync({ companyId, newLinkRole });
    },
    [assignMutation],
  );
  return { assignOpen, setAssignOpen, submitAssignment };
}
