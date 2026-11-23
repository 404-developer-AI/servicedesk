import * as React from "react";
import { useMutation, useQuery, type QueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ticketApi, mergeTicketMutation, type TicketDetail } from "@/lib/ticket-api";

export function useTitleReviewGate({
  ticketId,
  queryClient,
  ticketLoaded,
}: {
  ticketId: string;
  queryClient: QueryClient;
  ticketLoaded: boolean;
}) {
  // First-open title-review gate. Probed once the ticket has loaded; a
  // non-null gate blocks the page with a title-review dialog until the
  // agent confirms. One-time per ticket — the server returns { gate: null }
  // afterwards, so this query refetch hides the dialog on its own.
  const openGatesQ = useQuery({
    queryKey: ["ticket-open-gates", ticketId],
    queryFn: () => ticketApi.listOpenGates(ticketId),
    enabled: ticketLoaded,
    staleTime: 0,
  });
  // v0.0.89 — admins can silently dismiss the title-review gate. Unlike a
  // confirm (which marks the title reviewed server-side), a dismiss leaves the
  // server untouched, so we suppress the gate locally for this session; it
  // re-evaluates and recurs the next time the ticket is opened.
  const [titleGateDismissed, setTitleGateDismissed] = React.useState(false);
  React.useEffect(() => {
    setTitleGateDismissed(false);
  }, [ticketId]);
  const openGate = titleGateDismissed ? null : openGatesQ.data?.gate ?? null;
  const confirmOpenGateMutation = useMutation({
    mutationFn: (vars: { triggerId: string; subject: string }) =>
      ticketApi.confirmOpenGate(ticketId, vars.triggerId, vars.subject),
    onSuccess: (updated) => {
      // v0.1.33 — the response is the ticket core only: merge it in, then
      // refetch the timeline (gate notes / status-change events it added).
      queryClient.setQueryData<TicketDetail>(["ticket", ticketId], (old) => mergeTicketMutation(old, updated));
      void queryClient.invalidateQueries({ queryKey: ["ticket", ticketId], exact: true });
      queryClient.setQueryData(["ticket-open-gates", ticketId], { gate: null });
      queryClient.invalidateQueries({ queryKey: ["tickets"] });
    },
    onError: () => toast.error("Failed to save the title review"),
  });
  return { openGatesQ, openGate, setTitleGateDismissed, confirmOpenGateMutation };
}
