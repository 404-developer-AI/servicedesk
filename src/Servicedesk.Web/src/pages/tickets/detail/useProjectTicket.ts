import * as React from "react";
import { useMutation, useQuery, type QueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ticketApi, ApiError, type TicketDetail, type TicketFieldUpdate } from "@/lib/ticket-api";
import { useProjectOverview } from "../components/projects/TicketProjectPanel";
import { useProjectSettings } from "../components/projects/ProjectPromptDialog";

export function useProjectTicket({
  ticketId,
  queryClient,
  data,
}: {
  ticketId: string;
  queryClient: QueryClient;
  data: TicketDetail | undefined;
}) {
  // v0.0.105 — project tickets. Settings gate the whole surface (badge,
  // rail button, panel, prompt); the server re-enforces them on every
  // project endpoint. The docked project panel behaves like the checklist
  // panel: it replaces the details side panel while open.
  const projectSettingsQ = useProjectSettings();
  const projectsEnabled = projectSettingsQ.data?.enabled ?? false;
  const isProjectTicket = projectsEnabled && !!data?.ticket?.isProject;
  const [projectOpen, setProjectOpen] = React.useState(false);
  React.useEffect(() => {
    setProjectOpen(false);
  }, [ticketId]);
  // The overview feeds both the panel and the close-confirmation below;
  // React Query dedupes with the panel's own subscription on the same key.
  const projectOverviewQ = useProjectOverview(ticketId, isProjectTicket);
  const openLinkedTicketCount = React.useMemo(
    () =>
      (projectOverviewQ.data?.tickets ?? []).filter(
        (t) => t.statusStateCategory !== "Resolved" && t.statusStateCategory !== "Closed",
      ).length,
    [projectOverviewQ.data],
  );

  // First-open link prompt: only probed while it could still apply — the
  // server re-checks every condition and returns [] otherwise.
  const projectPromptQ = useQuery({
    queryKey: ["ticket-project-prompt", ticketId],
    queryFn: () => ticketApi.projectPrompt(ticketId),
    enabled:
      !!data?.ticket &&
      projectsEnabled &&
      (projectSettingsQ.data?.linkPromptEnabled ?? false) &&
      !data.ticket.isProject &&
      !data.ticket.projectTicketId &&
      !data.ticket.projectPromptDismissedUtc,
    staleTime: 0,
  });
  const [projectPromptHidden, setProjectPromptHidden] = React.useState(false);
  React.useEffect(() => {
    setProjectPromptHidden(false);
  }, [ticketId]);
  const promptProjects = projectPromptQ.data?.projects ?? [];
  const linkFromPromptMutation = useMutation({
    mutationFn: (projectTicketId: string) => ticketApi.linkProject(ticketId, projectTicketId),
    onSuccess: (response) => {
      setProjectPromptHidden(true);
      toast.success(`Linked to project #${response.projectNumber}`);
      queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
      queryClient.invalidateQueries({ queryKey: ["ticket", response.projectTicketId] });
    },
    onError: (err) =>
      toast.error(err instanceof ApiError ? `Link failed: ${err.message}` : "Link failed"),
  });
  const declineProjectPrompt = React.useCallback(() => {
    // Hide immediately; the server remembers the "no" so it never re-asks.
    setProjectPromptHidden(true);
    queryClient.setQueryData(["ticket-project-prompt", ticketId], { projects: [] });
    ticketApi.dismissProjectPrompt(ticketId).catch(() => {});
  }, [queryClient, ticketId]);

  // Soft confirmation when the project itself is closed/resolved while
  // linked tickets are still open (no hard block — see ROADMAP decision).
  const [projectClosePending, setProjectClosePending] = React.useState<TicketFieldUpdate | null>(null);
  return {
    projectsEnabled,
    isProjectTicket,
    projectOpen,
    setProjectOpen,
    openLinkedTicketCount,
    projectPromptQ,
    projectPromptHidden,
    promptProjects,
    linkFromPromptMutation,
    declineProjectPrompt,
    projectClosePending,
    setProjectClosePending,
  };
}
