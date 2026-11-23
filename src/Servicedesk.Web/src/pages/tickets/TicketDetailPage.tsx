import * as React from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { ticketApi, contactApi, ApiError, mergeTicketMutation, type TicketDetail, type TicketFieldUpdate } from "@/lib/ticket-api";
import { checklistErrorCode } from "@/lib/checklist-api";
import { ChecklistBlockedDialog } from "./components/checklists/ChecklistBlockedDialog";
import { StatusGateDialog } from "@/components/StatusGateDialog";
import { TitleReviewGateDialog } from "@/components/TitleReviewGateDialog";
import { ContactCompanyGateDialog } from "@/components/ContactCompanyGateDialog";
import { agentQueueApi } from "@/lib/queue-access-api";
import { taxonomyApi } from "@/lib/taxonomy-api";
import { CompanyAlertDialog } from "@/components/CompanyAlertDialog";
import { TicketCompanyAssignmentDialog } from "@/components/TicketCompanyAssignmentDialog";
import { useRecentTicketsStore } from "@/stores/useRecentTicketsStore";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";
import { useViewingTicket } from "@/hooks/usePresence";
import { useTicketRealtime } from "@/hooks/useTicketRealtime";
import { useTicketTimeline } from "./useTicketTimeline";
import { ProjectPromptDialog } from "./components/projects/ProjectPromptDialog";
import { CallbackPromptDialog } from "./components/CallbackPromptDialog";
import { flagColors } from "@/lib/ticketFlags";
import { ProjectCloseConfirmDialog } from "./components/projects/ProjectCloseConfirmDialog";
import { flattenQueueMailboxes } from "./mailContext";
import { InTicketSearchProvider } from "./components/InTicketSearch";
import type { TicketFormSummary } from "@/lib/forms-api";
import { LoadingSkeleton } from "./detail/TicketHeaderParts";
import { TicketDetailBody } from "./detail/TicketDetailBody";
import { useChecklistPanel } from "./detail/useChecklistPanel";
import { useProjectTicket } from "./detail/useProjectTicket";
import { useTimelineDeepLinks } from "./detail/useTimelineDeepLinks";
import { useTitleReviewGate } from "./detail/useTitleReviewGate";
import { useStatusChangeFlow } from "./detail/useStatusChangeFlow";
import { useCompanyAlertOnOpen, useCallbackPrompt, useCompanyAssignment } from "./detail/useOpenPrompts";

const EMPTY_TAGGED_ORDERS: { id: string; label: string }[] = [];
const EMPTY_TICKET_FORMS: TicketFormSummary[] = [];

type TicketDetailPageProps = {
  ticketId: string;
  /// v0.1.61 — rendered inside the ticket overlay: fills its container
  /// instead of the viewport, and never shows the back-office soft lock
  /// (the overlay *is* the back-office way of working).
  embedded?: boolean;
};

/* ─── Main page ─── */

export function TicketDetailPage(props: TicketDetailPageProps) {
  return (
    <InTicketSearchProvider>
      <TicketDetailPageInner {...props} />
    </InTicketSearchProvider>
  );
}

function TicketDetailPageInner({ ticketId, embedded = false }: TicketDetailPageProps) {
  const queryClient = useQueryClient();
  const addTicket = useRecentTicketsStore((s) => s.addTicket);
  useViewingTicket(ticketId);
  useTicketRealtime(ticketId);

  const { data, isLoading, isError } = useQuery({
    queryKey: ["ticket", ticketId],
    queryFn: () => ticketApi.get(ticketId),
  });
  // v0.1.33 — the detail carries the latest page of the timeline; older
  // events load on demand and live in this hook.
  const timeline = useTicketTimeline(ticketId, data);

  // Pull the requester's email so "Send mail → New" can pre-fill the To
  // field. Same query key as TicketSidePanel so the two components share
  // one fetch (React Query auto-dedupes by key + staleTime).
  const requesterContactId = data?.ticket?.requesterContactId ?? null;
  const { data: requesterContact } = useQuery({
    queryKey: ["contact", requesterContactId],
    queryFn: () => contactApi.get(requesterContactId!),
    enabled: !!requesterContactId,
    staleTime: 300_000,
  });

  // All queue mailboxes so "Reply-all" can strip them from To/Cc —
  // a queue mailbox is never a correct recipient on an outbound reply.
  // Same query key as TicketListPage so the fetch is deduped.
  const { data: accessibleQueues } = useQuery({
    queryKey: ["accessible-queues"],
    queryFn: agentQueueApi.list,
    staleTime: 60_000,
  });
  const ownMailboxAddresses = React.useMemo(
    () => flattenQueueMailboxes(accessibleQueues),
    [accessibleQueues],
  );

  // Status taxonomy — shared cache key with TicketSidePanel so this is a
  // no-cost subscribe (no second fetch). Used to snapshot the current
  // status into the Recent list, so the sidebar can colour-code rows
  // by status without re-opening every ticket.
  const { data: statuses } = useQuery({
    queryKey: ["statuses"],
    queryFn: taxonomyApi.statuses.list,
    staleTime: 300_000,
  });
  const currentStatus = React.useMemo(
    () => statuses?.find((s) => s.id === data?.ticket?.statusId),
    [statuses, data?.ticket?.statusId],
  );

  // v0.0.103 — ticket checklists. Settings gate the whole surface; the list
  // key sits under ["ticket", id] so realtime pushes refresh it. The docked
  // panel state lives here (not in the body) because the close-block dialog
  // and the ?checklist= deep link both need to open it.
  const {
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
  } = useChecklistPanel({ ticketId, queryClient, ticketLoaded: !!data?.ticket });

  // v0.0.105 — project tickets. Settings gate the whole surface (badge,
  // rail button, panel, prompt); the server re-enforces them on every
  // project endpoint.
  const {
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
  } = useProjectTicket({ ticketId, queryClient, data });

  React.useEffect(() => {
    if (data?.ticket) {
      addTicket({
        id: data.ticket.id,
        number: data.ticket.number,
        subject: data.ticket.subject,
        statusColor: currentStatus?.color,
        statusName: currentStatus?.name,
        statusStateCategory: currentStatus?.stateCategory,
      });
      useWorkspaceStore.getState().setLastTicket(data.ticket.id);
    }
  }, [data?.ticket, addTicket, currentStatus]);

  useTimelineDeepLinks({ ticketId, timeline });

  const updateMutation = useMutation({
    mutationFn: (fields: TicketFieldUpdate) => ticketApi.update(ticketId, fields),
    onSuccess: (updated, variables) => {
      // v0.1.33 — the response is the ticket core only: merge it in, then
      // refetch the timeline (gate notes / status-change events it added).
      queryClient.setQueryData<TicketDetail>(["ticket", ticketId], (old) => mergeTicketMutation(old, updated));
      void queryClient.invalidateQueries({ queryKey: ["ticket", ticketId], exact: true });
      queryClient.invalidateQueries({ queryKey: ["tickets"] });
      // v0.0.89 — a status-change gate whose chosen option keeps the ticket
      // open returns the unchanged status, so the requested status differs
      // from what came back. Surface that explicitly instead of the generic
      // "Ticket updated".
      const keptOpen =
        !!variables.gateConfirmations?.length &&
        !!variables.statusId &&
        updated.ticket.statusId !== variables.statusId;
      toast.success(keptOpen ? "Ticket kept open" : "Ticket updated");
    },
    onError: (err) => {
      // v0.0.103 — the checklist close block has its own dialog (see
      // handleSidePanelUpdate); a generic toast on top would be noise.
      if (err instanceof ApiError && err.status === 409 && checklistErrorCode(err) === "checklist_incomplete") return;
      toast.error("Failed to update ticket");
    },
  });

  const { openGatesQ, openGate, setTitleGateDismissed, confirmOpenGateMutation } = useTitleReviewGate({
    ticketId,
    queryClient,
    ticketLoaded: !!data?.ticket,
  });

  const currentStatusId = data?.ticket?.statusId ?? null;
  const {
    gateQueue,
    runStatusChange,
    handleSidePanelUpdate,
    onPromptGateConfirm,
    onContactCompanyGateConfirm,
    onGateCancel,
  } = useStatusChangeFlow({
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
  });

  const pinnedEventIds = React.useMemo(
    () => new Set(data?.pinnedEvents?.map((p) => p.eventId) ?? []),
    [data?.pinnedEvents]
  );

  const { alertOpen, companyAlert, handleAlertClose } = useCompanyAlertOnOpen({ ticketId, data });
  const { flagSettingsQ, timeAlertBlocking, setTimeAlertBlocking, callbackPrompt, setCallbackPrompt, callbackOffMutation } =
    useCallbackPrompt({ ticketId, data, queryClient });
  const { assignOpen, setAssignOpen, submitAssignment } = useCompanyAssignment({ ticketId, data, queryClient });

  if (isLoading) {
    return <LoadingSkeleton />;
  }

  if (isError || !data) {
    return (
      <div className="flex flex-col items-center justify-center py-24 gap-3">
        <div className="text-lg font-medium text-foreground/70">
          Ticket not found
        </div>
        <div className="text-sm text-muted-foreground">
          This ticket does not exist or you do not have access.
        </div>
      </div>
    );
  }

  const { ticket, body, pinnedEvents } = data;
  const events = timeline.events;
  const mergedSourceTicketNumbers = data.mergedSourceTicketNumbers ?? [];
  const mergedByUserName = data.mergedByUserName ?? null;
  const mergedIntoTicketNumber = data.mergedIntoTicketNumber ?? null;
  const splitFromTicketNumber = data.splitFromTicketNumber ?? null;
  const splitFromUserName = data.splitFromUserName ?? null;
  const splitChildren = data.splitChildren ?? [];
  const descriptionAttachments = data.descriptionAttachments ?? [];
  const parentTicketNumber = data.parentTicketNumber ?? null;
  const parentLinkedByUserName = data.parentLinkedByUserName ?? null;
  const childTickets = data.childTickets ?? [];
  const projectTicketNumber = data.projectTicketNumber ?? null;
  const projectLinkedByUserName = data.projectLinkedByUserName ?? null;
  const projectLinkedTicketCount = data.projectLinkedTicketCount ?? 0;
  const contactNotLinkedWarning = data.contactNotLinkedWarning;

  const otherOpenDialog =
    !!openGate ||
    openGatesQ.isLoading ||
    projectPromptQ.isLoading ||
    (!projectPromptHidden && promptProjects.length > 0) ||
    alertOpen ||
    assignOpen ||
    timeAlertBlocking;
  const callbackPromptOpen =
    callbackPrompt === "show" && ticket.isCallback && !otherOpenDialog;

  return (
    <>
      <TicketDetailBody
        ticketId={ticketId}
        embedded={embedded}
        ticket={ticket}
        body={body}
        events={events}
        timeline={timeline}
        eventTypeCounts={data.eventTypeCounts}
        latestMailReceived={data.latestMailReceived ?? null}
        pinnedEventsOutsidePage={data.pinnedEventsOutsidePage ?? []}
        serverTaggedOrders={data.taggedOrders ?? EMPTY_TAGGED_ORDERS}
        ticketForms={data.forms ?? EMPTY_TICKET_FORMS}
        pinnedEvents={pinnedEvents}
        pinnedEventIds={pinnedEventIds}
        updateMutation={updateMutation}
        queryClient={queryClient}
        onSidePanelUpdate={handleSidePanelUpdate}
        requesterEmail={requesterContact?.email ?? null}
        ownMailboxAddresses={ownMailboxAddresses}
        onRequestCompanyAssign={() => setAssignOpen(true)}
        mergedSourceTicketNumbers={mergedSourceTicketNumbers}
        mergedByUserName={mergedByUserName}
        mergedIntoTicketNumber={mergedIntoTicketNumber}
        splitFromTicketNumber={splitFromTicketNumber}
        splitFromUserName={splitFromUserName}
        splitChildren={splitChildren}
        descriptionAttachments={descriptionAttachments}
        portalRegistration={data.portalRegistration ?? null}
        parentTicketNumber={parentTicketNumber}
        parentLinkedByUserName={parentLinkedByUserName}
        childTickets={childTickets}
        checklistsEnabled={checklistsEnabled}
        checklistSettings={checklistSettings}
        checklists={checklists}
        checklistOpen={checklistOpen}
        onChecklistOpenChange={setChecklistOpen}
        activeChecklistId={activeChecklistId}
        onActiveChecklistChange={setActiveChecklistId}
        onOpenChecklist={openChecklistPanel}
        projectsEnabled={projectsEnabled}
        projectOpen={projectOpen}
        onProjectOpenChange={setProjectOpen}
        projectTicketNumber={projectTicketNumber}
        projectLinkedByUserName={projectLinkedByUserName}
        projectLinkedTicketCount={projectLinkedTicketCount}
        contactNotLinkedWarning={contactNotLinkedWarning}
        onTimeAlertBlockingChange={setTimeAlertBlocking}
      />
      <ChecklistBlockedDialog
        open={checklistBlock !== null}
        blockers={checklistBlock?.blockers ?? []}
        targetStatusName={checklistBlock?.statusName ?? null}
        triggerName={checklistBlock?.triggerName ?? null}
        onOpenChecklist={(id) => openChecklistPanel(id)}
        onClose={() => setChecklistBlock(null)}
      />
      {companyAlert && (
        <CompanyAlertDialog
          alert={companyAlert}
          open={alertOpen}
          onClose={handleAlertClose}
        />
      )}
      <TicketCompanyAssignmentDialog
        open={assignOpen}
        ticketId={ticketId}
        contactId={ticket.requesterContactId}
        onClose={() => setAssignOpen(false)}
        onAssigned={() => setAssignOpen(false)}
        submit={submitAssignment}
      />
      <StatusGateDialog
        gate={gateQueue[0]?.kind === "prompt_confirm" ? gateQueue[0] : null}
        onConfirm={onPromptGateConfirm}
        onCancel={onGateCancel}
      />
      <ContactCompanyGateDialog
        gate={gateQueue[0]?.kind === "contact_company_link" ? gateQueue[0] : null}
        onConfirm={onContactCompanyGateConfirm}
        onCancel={onGateCancel}
      />
      <TitleReviewGateDialog
        gate={openGate}
        submitting={confirmOpenGateMutation.isPending}
        onConfirm={(subject) =>
          confirmOpenGateMutation.mutate({ triggerId: openGate!.triggerId, subject })
        }
        onDismiss={() => {
          setTitleGateDismissed(true);
          toast.success("Title review dismissed without logging.");
        }}
      />
      {/* v0.0.105 — link-to-project prompt. Held back while the title-review
          gate is open so the two first-open dialogs never stack. */}
      <ProjectPromptDialog
        open={!projectPromptHidden && promptProjects.length > 0 && !openGate}
        projects={promptProjects}
        linking={linkFromPromptMutation.isPending}
        onLink={(projectTicketId) => linkFromPromptMutation.mutate(projectTicketId)}
        onDecline={declineProjectPrompt}
      />
      {/* v0.1.17 — call-back prompt. Last in line: it waits until every
          other on-open dialog (title review, project link, company alert,
          company assignment, hour limit) is closed, so dialogs never stack. */}
      <CallbackPromptDialog
        open={callbackPromptOpen}
        color={flagColors(flagSettingsQ.data).callback}
        submitting={callbackOffMutation.isPending}
        onTurnOff={() => callbackOffMutation.mutate()}
        onKeep={() => setCallbackPrompt("done")}
      />
      <ProjectCloseConfirmDialog
        open={projectClosePending !== null}
        openTicketCount={openLinkedTicketCount}
        targetStatusName={
          statuses?.find((s) => s.id === projectClosePending?.statusId)?.name ?? null
        }
        onConfirm={() => {
          const fields = projectClosePending;
          setProjectClosePending(null);
          if (fields) void runStatusChange(fields);
        }}
        onCancel={() => {
          setProjectClosePending(null);
          toast("Status not changed");
        }}
      />
    </>
  );
}
