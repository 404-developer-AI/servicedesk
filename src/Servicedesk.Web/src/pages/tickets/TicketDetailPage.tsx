import * as React from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { toast } from "sonner";
import { Check, Copy, Download, FileDown, FolderKanban, GitBranch, ListChecks, PanelRightClose, PanelRightOpen, Pencil, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { formatTicketRef } from "@/lib/ticketRef";
import { useTicketReferencePrefix } from "@/hooks/useTicketReferencePrefix";
import { ticketApi, contactApi, ApiError, type ContactCompanyRole, type GateConfirmation, type StatusGateMatch, type Ticket, type TicketFieldUpdate } from "@/lib/ticket-api";
import { checklistErrorCode, type ChecklistSettings, type TicketChecklist } from "@/lib/checklist-api";
import { useChecklistSettings, useTicketChecklists, summarizeChecklists } from "./components/checklists/useTicketChecklists";
import { TicketChecklistBar } from "./components/checklists/TicketChecklistBar";
import { TicketChecklistPanel } from "./components/checklists/TicketChecklistPanel";
import { ChecklistHeaderButton } from "./components/checklists/ChecklistHeaderButton";
import { ChecklistBlockedDialog, type ChecklistBlocker } from "./components/checklists/ChecklistBlockedDialog";
import { CHECKLIST_CLOSE_BLOCKED_EVENT, type ChecklistCloseBlockedPush } from "@/hooks/useNotificationSignalR";
import { notificationApi } from "@/lib/notification-api";
import { StatusGateDialog } from "@/components/StatusGateDialog";
import { TitleReviewGateDialog } from "@/components/TitleReviewGateDialog";
import { ContactCompanyGateDialog } from "@/components/ContactCompanyGateDialog";
import { agentQueueApi, taxonomyApi } from "@/lib/api";
import {
  CompanyAlertDialog,
  hasSeenAlertThisSession,
  markAlertSeen,
} from "@/components/CompanyAlertDialog";
import { TicketCompanyAssignmentDialog } from "@/components/TicketCompanyAssignmentDialog";
import { TicketTypeBadge } from "@/components/TicketTypeBadge";
import { IsoClassificationActions } from "@/components/IsoClassificationActions";
import { SearchContextBar } from "@/components/SearchContextBar";
import { Skeleton } from "@/components/ui/skeleton";
import { RichTextEditor } from "@/components/RichTextEditor";
import { useRecentTicketsStore } from "@/stores/useRecentTicketsStore";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";
import { useViewingTicket } from "@/hooks/usePresence";
import { useTicketRealtime } from "@/hooks/useTicketRealtime";
import { SlaPill } from "@/components/sla/SlaPill";
import { TicketSidePanel, TicketPresence } from "./components/TicketSidePanel";
import { TicketTimeline, isSystemEvent } from "./components/TicketTimeline";
import { PinnedEventsSummary } from "./components/PinnedEventsSummary";
import { TicketTimesheetPanel } from "./components/TicketTimesheetPanel";
import { TicketTimeAlertDialog } from "./components/TicketTimeAlertDialog";
import { AddNoteForm } from "./components/AddNoteForm";
import { TicketProjectPanel, useProjectOverview } from "./components/projects/TicketProjectPanel";
import { ProjectPromptDialog, useProjectSettings } from "./components/projects/ProjectPromptDialog";
import { ProjectCloseConfirmDialog } from "./components/projects/ProjectCloseConfirmDialog";
import { buildMailContext, flattenQueueMailboxes } from "./mailContext";
import { InTicketSearchProvider, useInTicketSearch } from "./components/InTicketSearch";

type TicketDetailPageProps = {
  ticketId: string;
};


function LoadingSkeleton() {
  return (
    <div className="flex gap-6 pt-3 h-[calc(100vh-0.75rem)] overflow-hidden">
      <div className="flex-1 space-y-4">
        <Skeleton className="h-8 w-2/3" />
        <Skeleton className="h-4 w-1/4" />
        <div className="glass-card p-6 space-y-3">
          <Skeleton className="h-4 w-full" />
          <Skeleton className="h-4 w-5/6" />
          <Skeleton className="h-4 w-4/6" />
        </div>
        <Skeleton className="h-6 w-32 mt-6" />
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-24 w-full" />
      </div>
      <div className="w-[320px] shrink-0 space-y-4">
        <Skeleton className="h-[480px] w-full rounded-[var(--radius)]" />
      </div>
    </div>
  );
}

/* ─── Click-to-copy ticket number ─── */

function TicketNumber({ number }: { number: number }) {
  const [copied, setCopied] = React.useState(false);
  const refPrefix = useTicketReferencePrefix();

  const handleCopy = async () => {
    // Copy the full reference ("Ticket#1234"). The backend parsers for global
    // search, the ticket picker, timesheet links and inbound mail all strip
    // the prefix again, so pasting this form resolves straight back to the
    // ticket without anyone having to delete "Ticket#" by hand.
    await navigator.clipboard.writeText(formatTicketRef(number, refPrefix));
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  };

  return (
    <button
      type="button"
      onClick={handleCopy}
      title="Click to copy"
      className="group flex items-center gap-1 text-xs font-mono font-medium px-2 py-0.5 rounded border border-glass bg-glass text-muted-foreground hover:bg-glass-hover transition-colors"
    >
      #{number}
      {copied ? (
        <Check className="h-3 w-3 text-green-400" />
      ) : (
        <Copy className="h-3 w-3 opacity-0 group-hover:opacity-60 transition-opacity" />
      )}
    </button>
  );
}

/* ─── Editable subject ─── */

function EditableSubject({
  number,
  value,
  ticketTypeId,
  onSave,
}: {
  number: number;
  value: string;
  ticketTypeId?: string | null;
  onSave: (subject: string) => Promise<void>;
}) {
  const [editing, setEditing] = React.useState(false);
  const [draft, setDraft] = React.useState(value);
  const inputRef = React.useRef<HTMLInputElement>(null);

  React.useEffect(() => {
    setDraft(value);
  }, [value]);

  React.useEffect(() => {
    if (editing) inputRef.current?.focus();
  }, [editing]);

  const save = async () => {
    const trimmed = draft.trim();
    if (!trimmed || trimmed === value) {
      setDraft(value);
      setEditing(false);
      return;
    }
    await onSave(trimmed);
    setEditing(false);
  };

  const cancel = () => {
    setDraft(value);
    setEditing(false);
  };

  if (!editing) {
    return (
      <div className="group flex items-center gap-3">
        <TicketNumber number={number} />
        {ticketTypeId && <TicketTypeBadge ticketTypeId={ticketTypeId} />}
        <h1 className="text-2xl font-semibold tracking-tight text-foreground leading-tight flex-1 min-w-0 truncate">
          {value}
        </h1>
        <button
          type="button"
          onClick={() => setEditing(true)}
          className="shrink-0 p-1 rounded-md text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-glass-hover transition-all"
          title="Edit subject"
        >
          <Pencil className="h-3.5 w-3.5" />
        </button>
      </div>
    );
  }

  return (
    <div className="flex items-center gap-3">
      <TicketNumber number={number} />
      {ticketTypeId && <TicketTypeBadge ticketTypeId={ticketTypeId} />}
      <input
        ref={inputRef}
        type="text"
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") save();
          if (e.key === "Escape") cancel();
        }}
        className="flex-1 min-w-0 text-2xl font-semibold tracking-tight text-foreground leading-tight bg-transparent border-b-2 border-primary/60 outline-none py-0.5"
      />
      <button
        type="button"
        onClick={save}
        className="shrink-0 p-1.5 rounded-md text-green-400 hover:bg-green-400/10 transition-colors"
        title="Save"
      >
        <Check className="h-4 w-4" />
      </button>
      <button
        type="button"
        onClick={cancel}
        className="shrink-0 p-1.5 rounded-md text-muted-foreground hover:bg-glass-hover transition-colors"
        title="Cancel"
      >
        <X className="h-4 w-4" />
      </button>
    </div>
  );
}

/* ─── Editable description ─── */

function EditableDescription({
  html,
  text,
  onSave,
}: {
  html: string | null;
  text: string;
  onSave: (bodyHtml: string, bodyText: string) => Promise<void>;
}) {
  const [editing, setEditing] = React.useState(false);
  const [draftHtml, setDraftHtml] = React.useState(html ?? text);

  React.useEffect(() => {
    setDraftHtml(html ?? text);
  }, [html, text]);

  const save = async () => {
    const div = document.createElement("div");
    div.innerHTML = draftHtml;
    const plainText = div.textContent ?? "";
    await onSave(draftHtml, plainText);
    setEditing(false);
  };

  const cancel = () => {
    setDraftHtml(html ?? text);
    setEditing(false);
  };

  const isEmpty = !html && !text.trim();

  if (!editing) {
    if (isEmpty) {
      return (
        <button
          type="button"
          onClick={() => setEditing(true)}
          className="w-full flex items-center gap-3 px-4 py-3 rounded-[var(--radius)] border border-glass bg-glass text-muted-foreground/60 hover:bg-glass-hover hover:text-muted-foreground hover:border-glass-strong transition-colors text-sm"
        >
          <Pencil className="h-4 w-4 shrink-0" />
          Add a description...
        </button>
      );
    }

    return (
      <div
        className="group relative rounded-[var(--radius)] border border-glass bg-glass px-4 py-3 cursor-pointer hover:bg-glass-hover hover:border-glass-strong transition-colors max-h-32 overflow-y-auto"
        onClick={() => setEditing(true)}
        title="Click to edit description"
      >
        <button
          type="button"
          className="absolute top-2 right-2 p-1 rounded-md text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-glass-hover transition-all z-10"
        >
          <Pencil className="h-3.5 w-3.5" />
        </button>
        <RichTextEditor
          content={html ?? text}
          editable={false}
          minHeight="0px"
          className="border-none bg-transparent !rounded-none"
        />
      </div>
    );
  }

  return (
    <div className="rounded-[var(--radius)] border border-glass bg-glass p-4 space-y-3">
      <RichTextEditor
        content={draftHtml}
        onChange={setDraftHtml}
        placeholder="Describe the issue..."
        minHeight="100px"
      />
      <div className="flex items-center justify-between">
        <button
          type="button"
          onClick={cancel}
          className="px-3 py-1.5 text-xs rounded-md text-muted-foreground hover:bg-glass-hover transition-colors"
        >
          Cancel
        </button>
        <button
          type="button"
          onClick={save}
          className="px-3 py-1.5 text-xs rounded-md bg-primary text-white hover:bg-primary/90 transition-colors"
        >
          Save
        </button>
      </div>
    </div>
  );
}

/* ─── Export PDF button ─── */

function ExportPdfButton({ ticketId }: { ticketId: string }) {
  const [open, setOpen] = React.useState(false);
  const [includeInternal, setIncludeInternal] = React.useState(false);
  const ref = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [open]);

  const handleExport = () => {
    const url = ticketApi.exportPdf(ticketId, !includeInternal);
    window.open(url, "_blank");
    setOpen(false);
  };

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        onClick={() => setOpen(!open)}
        className="flex items-center gap-1.5 px-2.5 py-1 text-xs font-medium rounded-md border border-glass bg-glass text-muted-foreground hover:bg-glass-hover hover:text-foreground transition-colors"
        title="Export as PDF"
      >
        <FileDown className="h-3.5 w-3.5" />
        PDF
      </button>
      {open && (
        <div className="absolute right-0 top-full mt-1.5 z-50 w-56 rounded-lg border border-glass bg-background/95 backdrop-blur-xl p-3 shadow-[0_8px_30px_-12px_rgba(0,0,0,0.6)]">
          <label className="flex items-center gap-2 text-xs text-foreground/80 cursor-pointer mb-3">
            <input
              type="checkbox"
              checked={includeInternal}
              onChange={(e) => setIncludeInternal(e.target.checked)}
              className="rounded border-glass-strong bg-glass-strong text-primary focus:ring-primary/50"
            />
            Include internal events
          </label>
          <button
            type="button"
            onClick={handleExport}
            className="w-full flex items-center justify-center gap-2 px-3 py-1.5 text-xs font-medium rounded-md bg-primary text-white hover:bg-primary/90 transition-colors"
          >
            <FileDown className="h-3.5 w-3.5" />
            Export PDF
          </button>
        </div>
      )}
    </div>
  );
}

/* ─── Main page ─── */

export function TicketDetailPage(props: TicketDetailPageProps) {
  return (
    <InTicketSearchProvider>
      <TicketDetailPageInner {...props} />
    </InTicketSearchProvider>
  );
}

function TicketDetailPageInner({ ticketId }: TicketDetailPageProps) {
  const queryClient = useQueryClient();
  const addTicket = useRecentTicketsStore((s) => s.addTicket);
  useViewingTicket(ticketId);
  useTicketRealtime(ticketId);

  const { data, isLoading, isError } = useQuery({
    queryKey: ["ticket", ticketId],
    queryFn: () => ticketApi.get(ticketId),
  });

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
  const checklistSettingsQ = useChecklistSettings();
  const checklistSettings: ChecklistSettings | null = checklistSettingsQ.data ?? null;
  const checklistsEnabled = checklistSettings?.enabled ?? false;
  const { checklists } = useTicketChecklists(ticketId, checklistsEnabled && !!data?.ticket);
  const [checklistOpen, setChecklistOpen] = React.useState(false);
  const [activeChecklistId, setActiveChecklistId] = React.useState<string | null>(null);
  const [checklistBlock, setChecklistBlock] = React.useState<{ blockers: ChecklistBlocker[]; statusName: string | null; triggerName?: string | null } | null>(null);
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

  // v0.0.12 stap 4 — deep-link to a specific event (from mention
  // notifications, mail CTAs, etc.). Runs once events are in the DOM.
  // Scroll + ring-animate pattern copied from PinnedEventsSummary.handleJump.
  React.useEffect(() => {
    if (!data?.events?.length) return;
    const hash = window.location.hash;
    const match = hash.match(/^#event-(\d+)$/);
    if (!match) return;
    const eventId = match[1];
    // requestAnimationFrame waits for the timeline to render before we try
    // to find the anchor — the query may resolve before the DOM settles.
    requestAnimationFrame(() => {
      const el = document.getElementById(`event-${eventId}`);
      if (!el) return;
      el.scrollIntoView({ behavior: "smooth", block: "center" });
      el.classList.add("ring-2", "ring-primary/50", "rounded-lg");
      setTimeout(() => {
        el.classList.remove("ring-2", "ring-primary/50", "rounded-lg");
      }, 2000);
    });
  }, [data?.events]);

  const updateMutation = useMutation({
    mutationFn: (fields: TicketFieldUpdate) => ticketApi.update(ticketId, fields),
    onSuccess: (updated, variables) => {
      queryClient.setQueryData(["ticket", ticketId], updated);
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

  // First-open title-review gate. Probed once the ticket has loaded; a
  // non-null gate blocks the page with a title-review dialog until the
  // agent confirms. One-time per ticket — the server returns { gate: null }
  // afterwards, so this query refetch hides the dialog on its own.
  const openGatesQ = useQuery({
    queryKey: ["ticket-open-gates", ticketId],
    queryFn: () => ticketApi.listOpenGates(ticketId),
    enabled: !!data?.ticket,
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
      queryClient.setQueryData(["ticket", ticketId], updated);
      queryClient.setQueryData(["ticket-open-gates", ticketId], { gate: null });
      queryClient.invalidateQueries({ queryKey: ["tickets"] });
    },
    onError: () => toast.error("Failed to save the title review"),
  });

  // v0.0.42 — status-change gate orchestration. The dialog walks through
  // matching gates one-by-one; only after every gate has been confirmed
  // does the actual PATCH fire with the collected answers. Cancelling any
  // gate aborts the entire status change (TaxonomySelect re-renders from
  // server state on the next tick so the dropdown snaps back on its own).
  const [gateQueue, setGateQueue] = React.useState<StatusGateMatch[]>([]);
  const [gateConfirmations, setGateConfirmations] = React.useState<GateConfirmation[]>([]);
  const [gatePendingFields, setGatePendingFields] = React.useState<TicketFieldUpdate | null>(null);
  const [gateTargetStatusId, setGateTargetStatusId] = React.useState<string | null>(null);

  const currentStatusId = data?.ticket?.statusId ?? null;

  const readChecklistBlock = React.useCallback((err: unknown): boolean => {
    if (!(err instanceof ApiError) || err.status !== 409 || checklistErrorCode(err) !== "checklist_incomplete") return false;
    const body = err.body as { checklists?: Array<{ checklistId: string; name: string; openRequired: number }> } | null;
    const blockers = (body?.checklists ?? []).map((b) => ({ checklistId: b.checklistId, name: b.name, openRequired: b.openRequired }));
    setChecklistBlock({ blockers, statusName: null });
    return true;
  }, []);

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
  }, [ticketId, updateMutation, checklistsEnabled, checklistSettings, checklists, statuses, readChecklistBlock]);

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
  }, [currentStatusId, updateMutation, isProjectTicket, openLinkedTicketCount, statuses, runStatusChange]);

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

  const pinnedEventIds = React.useMemo(
    () => new Set(data?.pinnedEvents?.map((p) => p.eventId) ?? []),
    [data?.pinnedEvents]
  );

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
      queryClient.setQueryData(["ticket", ticketId], updated);
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

  const { ticket, body, events, pinnedEvents } = data;
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

  return (
    <>
      <TicketDetailBody
        ticketId={ticketId}
        ticket={ticket}
        body={body}
        events={events}
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

function TicketDetailBody({
  ticketId, ticket, body, events, pinnedEvents, pinnedEventIds, updateMutation, queryClient,
  onSidePanelUpdate,
  requesterEmail,
  ownMailboxAddresses,
  onRequestCompanyAssign,
  mergedSourceTicketNumbers,
  mergedByUserName,
  mergedIntoTicketNumber,
  splitFromTicketNumber,
  splitFromUserName,
  splitChildren,
  descriptionAttachments,
  parentTicketNumber,
  parentLinkedByUserName,
  childTickets,
  checklistsEnabled,
  checklistSettings,
  checklists,
  checklistOpen,
  onChecklistOpenChange,
  activeChecklistId,
  onActiveChecklistChange,
  onOpenChecklist,
  projectsEnabled,
  projectOpen,
  onProjectOpenChange,
  projectTicketNumber,
  projectLinkedByUserName,
  projectLinkedTicketCount,
}: {
  ticketId: string;
  ticket: any;
  body: any;
  events: any[];
  pinnedEvents: any[];
  pinnedEventIds: Set<number>;
  updateMutation: any;
  queryClient: any;
  /// v0.0.42 — gate-aware wrapper around the side-panel PATCH path.
  /// Intercepts status changes to surface the StatusGateDialog before
  /// the actual mutation fires. Falls through to updateMutation for
  /// every other field.
  onSidePanelUpdate: (fields: TicketFieldUpdate) => Promise<void>;
  requesterEmail: string | null;
  ownMailboxAddresses: string[];
  onRequestCompanyAssign: () => void;
  mergedSourceTicketNumbers: number[];
  mergedByUserName: string | null;
  mergedIntoTicketNumber: string | null;
  splitFromTicketNumber: string | null;
  splitFromUserName: string | null;
  splitChildren: { id: string; number: number }[];
  descriptionAttachments: { id: string; name: string; mimeType: string; size: number; url: string }[];
  parentTicketNumber: string | null;
  parentLinkedByUserName: string | null;
  childTickets: { id: string; number: string }[];
  /// v0.0.103 — checklists (settings-gated). The page owns the panel state.
  checklistsEnabled: boolean;
  checklistSettings: ChecklistSettings | null;
  checklists: TicketChecklist[];
  checklistOpen: boolean;
  onChecklistOpenChange: (open: boolean) => void;
  activeChecklistId: string | null;
  onActiveChecklistChange: (id: string) => void;
  onOpenChecklist: (checklistId?: string | null) => void;
  /// v0.0.105 — project tickets (settings-gated). The page owns the
  /// docked project-panel state, like the checklist panel above.
  projectsEnabled: boolean;
  projectOpen: boolean;
  onProjectOpenChange: (open: boolean) => void;
  projectTicketNumber: string | null;
  projectLinkedByUserName: string | null;
  projectLinkedTicketCount: number;
}) {
  const { matchesEvent, mode, query, registerScope } = useInTicketSearch();
  const checklistSummary = React.useMemo(() => summarizeChecklists(checklists), [checklists]);

  // System/audit events (status, assignment, priority, …) are hidden from
  // the feed by default so the timeline shows only real communication. An
  // agent can reveal them for an audit pass via the side-panel toggle. The
  // choice is local to this ticket view and resets when the agent leaves.
  const [showSystemEvents, setShowSystemEvents] = React.useState(false);
  React.useEffect(() => {
    setShowSystemEvents(false);
  }, [ticketId]);

  const visibleEvents = React.useMemo(() => {
    let list = events;
    if (mode === "filter" && query.trim()) list = list.filter(matchesEvent);
    if (!showSystemEvents) list = list.filter((e) => !isSystemEvent(e));
    return list;
  }, [events, matchesEvent, mode, query, showSystemEvents]);

  const systemEventCount = React.useMemo(
    () => events.filter(isSystemEvent).length,
    [events],
  );

  // Scroll the activity feed to the latest post when an agent opens (or
  // switches to) a ticket. We park a ref on the same scroll container the
  // in-ticket search uses; the callback below feeds both. Logic:
  //   - On every ticketId change we reset a "done" marker.
  //   - On the first render where events.length > 0 for that ticket, we
  //     scroll to scrollHeight. Two follow-up timers catch late layout
  //     from inline images / mail bodies that render after the first
  //     paint and would otherwise leave us above the true bottom.
  // We deliberately do NOT auto-scroll on later events.length growth so
  // a realtime push doesn't yank the agent away from what they're reading.
  const scrollContainerRef = React.useRef<HTMLDivElement | null>(null);
  const initialScrollDoneRef = React.useRef<string | null>(null);
  const setScrollRef = React.useCallback(
    (el: HTMLDivElement | null) => {
      scrollContainerRef.current = el;
      registerScope(el);
    },
    [registerScope],
  );

  React.useEffect(() => {
    if (initialScrollDoneRef.current === ticketId) return;
    if (events.length === 0) return;
    const el = scrollContainerRef.current;
    if (!el) return;

    const scroll = () => {
      const c = scrollContainerRef.current;
      if (c) c.scrollTop = c.scrollHeight;
    };
    scroll();
    const t1 = window.setTimeout(scroll, 80);
    const t2 = window.setTimeout(scroll, 240);
    initialScrollDoneRef.current = ticketId;
    return () => {
      window.clearTimeout(t1);
      window.clearTimeout(t2);
    };
  }, [ticketId, events.length]);

  // Side-panel collapse — per-user pin state lives in the workspace store and
  // applies to *every* ticket the agent opens. The local `expanded` flag is
  // re-seeded from the pin on each ticket switch so a temporary toggle on
  // ticket A does not leak into ticket B.
  const sidePanelPinned = useWorkspaceStore((s) => s.ticketSidePanelPinned);
  const setSidePanelPinned = useWorkspaceStore((s) => s.setTicketSidePanelPinned);
  const [sidePanelExpanded, setSidePanelExpanded] = React.useState(sidePanelPinned);
  React.useEffect(() => {
    setSidePanelExpanded(sidePanelPinned);
  }, [ticketId, sidePanelPinned]);

  return (
    <div className="flex gap-6 pt-3 h-[calc(100vh-0.75rem)] overflow-hidden">
      {/* Left column — header + description static, activity scrolls, reply pinned bottom */}
      <div className="flex flex-col flex-1 min-w-0 min-h-0 overflow-hidden">
        <SearchContextBar ticketId={ticketId} />
        {/* Static: ticket number + subject on one line, full width — action pills
            live on the Description label row below so long subjects stay visible */}
        <div className="shrink-0 pb-4">
          <EditableSubject
            number={ticket.number}
            value={ticket.subject}
            ticketTypeId={ticket.ticketTypeId}
            onSave={async (subject) => {
              await updateMutation.mutateAsync({ subject });
            }}
          />
        </div>

        <SplitBanners
          ticket={ticket}
          splitFromTicketNumber={splitFromTicketNumber}
          splitFromUserName={splitFromUserName}
          splitChildren={splitChildren}
        />

        {/* Static: description */}
        <div className="shrink-0 pb-4">
          <div className="flex items-center gap-2 mb-2">
            <span className="text-xs uppercase tracking-wider text-muted-foreground">
              Description
            </span>
            <span className="rounded px-1.5 py-0.5 text-[10px] font-medium border border-glass bg-glass text-muted-foreground/60">
              Internal
            </span>
            <div className="ml-auto flex items-center gap-3">
              {/* v0.0.105 — project marker: on the project ticket itself, and
                  as a jump-chip on tickets linked to a project. */}
              {projectsEnabled && ticket.isProject && (
                <span
                  className="flex shrink-0 items-center gap-1 rounded-md border border-sky-400/60 bg-sky-100/80 px-2 py-1 text-xs font-medium text-sky-800 dark:border-sky-500/30 dark:bg-sky-500/10 dark:text-sky-200"
                  title="Internal project — customers cannot see this ticket"
                >
                  <FolderKanban className="h-3 w-3" aria-hidden />
                  Project
                </span>
              )}
              {projectsEnabled && !ticket.isProject && ticket.projectTicketId && projectTicketNumber && (
                <Link
                  to="/tickets/$ticketId"
                  params={{ ticketId: ticket.projectTicketId }}
                  className="flex shrink-0 items-center gap-1 rounded-md border border-sky-400/60 bg-sky-100/80 px-2 py-1 text-xs font-medium text-sky-800 transition-colors hover:bg-sky-200/80 dark:border-sky-500/30 dark:bg-sky-500/10 dark:text-sky-200 dark:hover:bg-sky-500/20"
                  title="Part of a project — open the project ticket"
                >
                  <FolderKanban className="h-3 w-3" aria-hidden />
                  Project #{projectTicketNumber}
                </Link>
              )}
              <IsoClassificationActions ticket={ticket} />
              {checklistsEnabled && checklistSettings && (
                <ChecklistHeaderButton
                  ticketId={ticketId}
                  checklists={checklists}
                  maxPerTicket={checklistSettings.maxPerTicket}
                  onOpen={() => onOpenChecklist(activeChecklistId)}
                  onAttached={(c) => onOpenChecklist(c.id)}
                />
              )}
              <SlaPill ticketId={ticket.id} className="shrink-0 justify-end" />
              <ExportPdfButton ticketId={ticketId} />
            </div>
          </div>
          <EditableDescription
            html={body.bodyHtml}
            text={body.bodyText}
            onSave={async (bodyHtml, bodyText) => {
              await updateMutation.mutateAsync({ bodyHtml, bodyText });
            }}
          />
          {descriptionAttachments.length > 0 && (
            <div className="flex flex-wrap gap-2 pt-2">
              {descriptionAttachments.map((a) => (
                <DescriptionAttachmentChip key={a.id} attachment={a} />
              ))}
            </div>
          )}
        </div>

        {/* Pinned events summary */}
        {pinnedEvents.length > 0 && (
          <div className="shrink-0 pb-3">
            <PinnedEventsSummary
              ticketId={ticketId}
              pinnedEvents={pinnedEvents}
              events={events}
            />
          </div>
        )}

        {/* v0.0.35-F — time-logged expand panel */}
        <div className="shrink-0 pb-3">
          <TicketTimesheetPanel ticketId={ticketId} queueId={ticket.queueId} />
        </div>

        {/* v0.0.103 — always-visible checklist progress (one row per
            attached checklist); clicking opens the docked panel. */}
        {checklistsEnabled && checklists.length > 0 && (
          <div className="shrink-0 pb-3">
            <TicketChecklistBar
              checklists={checklists}
              activeChecklistId={checklistOpen ? activeChecklistId : null}
              onOpen={(id) => onOpenChecklist(id)}
            />
          </div>
        )}

        {/* v0.0.87 — per-ticket hour-limit warning (self-gating: only opens
            when the feature is on and the ticket is over its limit). The queue
            drives re-evaluation on open and on queue change. */}
        <TicketTimeAlertDialog ticketId={ticketId} queueId={ticket.queueId} />

        {/* Static: activity divider */}
        <div className="shrink-0 pb-3">
          <div className="flex items-center gap-3">
            <div className="h-px flex-1 bg-glass-strong" />
            <span className="text-xs uppercase tracking-wider text-muted-foreground">
              Activity
            </span>
            <div className="h-px flex-1 bg-glass-strong" />
          </div>
        </div>

        {/* Scrollable: activity timeline + inline compose form. The form
            sits at the bottom of the scroll region so agents can scroll
            past it to re-read earlier posts while typing a reply — a
            static bottom-bar used to obscure the feed below itself. The
            ref is handed to the in-ticket search highlighter so it knows
            which subtree to walk — nothing outside this container gets
            mutated. */}
        <div ref={setScrollRef} className="flex-1 min-h-0 overflow-y-auto pr-1">
          <TicketTimeline ticketId={ticketId} ticketNumber={ticket.number} events={visibleEvents} pinnedEventIds={pinnedEventIds} />
          {mode === "filter" && query.trim() && visibleEvents.length === 0 && (
            <div className="py-6 text-center text-sm text-muted-foreground">
              Geen events matchen "{query}".
            </div>
          )}

          <div className="pt-4 pb-2">
            {ticket.mergedIntoTicketId ? (
              <div className="rounded-md border border-glass bg-glass px-3 py-3 text-xs text-muted-foreground/70 text-center">
                This ticket is closed and merged. Reply on the target ticket
                instead.
              </div>
            ) : (
              <AddNoteForm
                key={ticketId}
                ticketId={ticketId}
                queueId={ticket.queueId}
                statusId={ticket.statusId}
                internalOnly={projectsEnabled && ticket.isProject}
                mailContext={buildMailContext(ticket, events, requesterEmail, ownMailboxAddresses)}
                onSubmitted={() => {
                  queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
                }}
              />
            )}
            <TicketPresence ticketId={ticketId} />
          </div>
        </div>
      </div>

      {/* Right column — toggle rail + animated side panel.
          Both live inside a single shrink-0 wrapper so the parent's gap-6
          stays between the activity feed and this whole assembly. When the
          panel is collapsed, only the rail remains visible. */}
      <div className="flex shrink-0 items-stretch gap-2">
        <div className="flex flex-col items-center gap-1 pt-1">
          <button
            type="button"
            onClick={() => {
              if (checklistOpen || projectOpen) {
                // Switching back from a docked panel: show details.
                onChecklistOpenChange(false);
                onProjectOpenChange(false);
                setSidePanelExpanded(true);
              } else {
                setSidePanelExpanded((v) => !v);
              }
            }}
            title={checklistOpen || projectOpen ? "Show ticket details" : sidePanelExpanded ? "Collapse side panel" : "Expand side panel"}
            aria-label={checklistOpen || projectOpen ? "Show ticket details" : sidePanelExpanded ? "Collapse side panel" : "Expand side panel"}
            className={cn(
              "p-1.5 rounded-md text-muted-foreground/60 hover:text-foreground hover:bg-glass-hover transition-colors",
              !checklistOpen && !projectOpen && sidePanelExpanded && "text-foreground",
            )}
          >
            {sidePanelExpanded && !checklistOpen && !projectOpen
              ? <PanelRightClose className="h-4 w-4" />
              : <PanelRightOpen className="h-4 w-4" />}
          </button>
          {checklistsEnabled && (
            <button
              type="button"
              onClick={() => {
                onProjectOpenChange(false);
                onChecklistOpenChange(!checklistOpen);
              }}
              title={checklistOpen ? "Hide checklist panel" : "Show checklist panel"}
              aria-label={checklistOpen ? "Hide checklist panel" : "Show checklist panel"}
              className={cn(
                "relative p-1.5 rounded-md transition-colors hover:bg-glass-hover",
                checklistOpen
                  ? "text-violet-200 bg-violet-400/15"
                  : checklistSummary.count > 0 && !checklistSummary.allComplete
                    ? "text-amber-300/90 hover:text-amber-200"
                    : "text-muted-foreground/60 hover:text-foreground",
              )}
            >
              <ListChecks className="h-4 w-4" />
              {checklistSummary.count > 0 && !checklistSummary.allComplete && (
                <span className="absolute -right-0.5 -top-0.5 h-2 w-2 rounded-full bg-amber-400 ring-2 ring-background" aria-hidden />
              )}
            </button>
          )}
          {/* v0.0.105 — project panel toggle, only on project tickets. */}
          {projectsEnabled && ticket.isProject && (
            <button
              type="button"
              onClick={() => {
                onChecklistOpenChange(false);
                onProjectOpenChange(!projectOpen);
              }}
              title={projectOpen ? "Hide project panel" : "Show project panel"}
              aria-label={projectOpen ? "Hide project panel" : "Show project panel"}
              className={cn(
                "p-1.5 rounded-md transition-colors hover:bg-glass-hover",
                projectOpen
                  ? "text-sky-200 bg-sky-400/15"
                  : "text-muted-foreground/60 hover:text-foreground",
              )}
            >
              <FolderKanban className="h-4 w-4" />
            </button>
          )}
        </div>
        {checklistsEnabled && checklistOpen && checklistSettings ? (
          <div className="w-[440px] min-h-0 overflow-hidden">
            <TicketChecklistPanel
              ticketId={ticketId}
              checklists={checklists}
              settings={checklistSettings}
              activeChecklistId={activeChecklistId}
              onActiveChange={onActiveChecklistChange}
              onClose={() => {
                onChecklistOpenChange(false);
                setSidePanelExpanded(true);
              }}
              mode="docked"
            />
          </div>
        ) : projectsEnabled && ticket.isProject && projectOpen ? (
          <div className="w-[440px] min-h-0 overflow-hidden">
            <TicketProjectPanel
              ticketId={ticketId}
              ticketNumber={ticket.number}
              onClose={() => {
                onProjectOpenChange(false);
                setSidePanelExpanded(true);
              }}
            />
          </div>
        ) : (
        <div
          className={cn(
            "overflow-hidden transition-[width,opacity] duration-200 ease-out",
            sidePanelExpanded ? "w-[320px] opacity-100" : "w-0 opacity-0",
          )}
          aria-hidden={!sidePanelExpanded}
        >
          <TicketSidePanel
            ticket={ticket}
            onUpdate={onSidePanelUpdate}
            onRequestCompanyAssign={onRequestCompanyAssign}
            pinned={sidePanelPinned}
            onTogglePin={() => setSidePanelPinned(!sidePanelPinned)}
            showSystemEvents={showSystemEvents}
            onToggleSystemEvents={() => setShowSystemEvents((v) => !v)}
            systemEventCount={systemEventCount}
            mergedIntoTicketNumber={mergedIntoTicketNumber}
            mergedSourceTicketNumbers={mergedSourceTicketNumbers}
            mergedByUserName={mergedByUserName}
            parentTicketNumber={parentTicketNumber}
            parentLinkedByUserName={parentLinkedByUserName}
            childTickets={childTickets}
            onUnlinkParent={async () => {
              await ticketApi.unlinkParent(ticketId);
              queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
            }}
            projectsEnabled={projectsEnabled}
            projectTicketNumber={projectTicketNumber}
            projectLinkedByUserName={projectLinkedByUserName}
            projectLinkedTicketCount={projectLinkedTicketCount}
          />
        </div>
        )}
      </div>
    </div>
  );
}

function formatBytes(n: number): string {
  if (!Number.isFinite(n) || n < 0) return "";
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`;
  return `${(n / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

function DescriptionAttachmentChip({
  attachment: a,
}: {
  attachment: { id: string; name: string; mimeType: string; size: number; url: string };
}) {
  return (
    <a
      href={a.url}
      target="_blank"
      rel="noreferrer"
      className="inline-flex items-center gap-2 rounded-md border border-border/60 bg-background/40 px-2.5 py-1.5 text-xs text-foreground/90 hover:border-primary/50 hover:bg-primary/10"
      title={`${a.mimeType} · ${formatBytes(a.size)}`}
    >
      <Download className="h-3.5 w-3.5 text-primary" />
      <span className="max-w-[220px] truncate">{a.name}</span>
      <span className="text-muted-foreground">{formatBytes(a.size)}</span>
    </a>
  );
}

function SplitBanners({
  ticket,
  splitFromTicketNumber,
  splitFromUserName,
  splitChildren,
}: {
  ticket: Ticket;
  splitFromTicketNumber: string | null;
  splitFromUserName: string | null;
  splitChildren: { id: string; number: number }[];
}) {
  const isSplit = !!ticket.splitFromTicketId;
  const hasChildren = splitChildren.length > 0;
  if (!isSplit && !hasChildren) return null;

  return (
    <div className="shrink-0 pb-3 space-y-2">
      {isSplit && ticket.splitFromTicketId && (
        <div className="rounded-md border border-sky-400/30 bg-sky-500/[0.06] px-3 py-2.5 flex items-start gap-2">
          <GitBranch className="h-4 w-4 shrink-0 mt-0.5 text-sky-300/90" />
          <div className="text-sm text-sky-100/90">
            This ticket was split from{" "}
            <Link
              to="/tickets/$ticketId"
              params={{ ticketId: ticket.splitFromTicketId }}
              className="font-medium underline underline-offset-2 hover:text-sky-50"
            >
              #{splitFromTicketNumber ?? "?"}
            </Link>
            {ticket.splitFromUtc && (
              <>
                {" "}on {new Date(ticket.splitFromUtc).toLocaleDateString()}
              </>
            )}
            {splitFromUserName && (
              <>
                {" "}by <span className="text-sky-50/90">{splitFromUserName}</span>
              </>
            )}
            .
          </div>
        </div>
      )}
      {hasChildren && (
        <div className="rounded-md border border-glass bg-glass px-3 py-2 text-xs text-muted-foreground/80 flex items-center gap-2 flex-wrap">
          <GitBranch className="h-3.5 w-3.5 shrink-0 text-sky-300/80" />
          <span>Split into</span>
          {splitChildren.map((child, idx) => (
            <span key={child.id}>
              <Link
                to="/tickets/$ticketId"
                params={{ ticketId: child.id }}
                className="text-foreground/80 font-medium hover:text-foreground hover:underline underline-offset-2"
              >
                #{child.number}
              </Link>
              {idx < splitChildren.length - 1 ? "," : ""}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}
