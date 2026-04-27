import * as React from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { toast } from "sonner";
import { Check, Copy, FileDown, GitBranch, GitMerge, PanelRightClose, PanelRightOpen, Pencil, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { ticketApi, contactApi, type Ticket, type TicketFieldUpdate } from "@/lib/ticket-api";
import { agentQueueApi } from "@/lib/api";
import {
  CompanyAlertDialog,
  hasSeenAlertThisSession,
  markAlertSeen,
} from "@/components/CompanyAlertDialog";
import { TicketCompanyAssignmentDialog } from "@/components/TicketCompanyAssignmentDialog";
import { Skeleton } from "@/components/ui/skeleton";
import { RichTextEditor } from "@/components/RichTextEditor";
import { useRecentTicketsStore } from "@/stores/useRecentTicketsStore";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";
import { useViewingTicket } from "@/hooks/usePresence";
import { useTicketRealtime } from "@/hooks/useTicketRealtime";
import { SlaPill } from "@/components/sla/SlaPill";
import { TicketSidePanel } from "./components/TicketSidePanel";
import { TicketTimeline } from "./components/TicketTimeline";
import { PinnedEventsSummary } from "./components/PinnedEventsSummary";
import { AddNoteForm } from "./components/AddNoteForm";
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

  const handleCopy = async () => {
    await navigator.clipboard.writeText(`#${number}`);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  };

  return (
    <button
      type="button"
      onClick={handleCopy}
      title="Click to copy"
      className="group flex items-center gap-1 text-xs font-mono font-medium px-2 py-0.5 rounded border border-white/10 bg-white/[0.05] text-muted-foreground hover:bg-white/[0.08] transition-colors"
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
  onSave,
}: {
  number: number;
  value: string;
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
        <h1 className="text-2xl font-semibold tracking-tight text-foreground leading-tight flex-1 min-w-0 truncate">
          {value}
        </h1>
        <button
          type="button"
          onClick={() => setEditing(true)}
          className="shrink-0 p-1 rounded-md text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-white/[0.06] transition-all"
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
        className="shrink-0 p-1.5 rounded-md text-muted-foreground hover:bg-white/[0.06] transition-colors"
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
          className="w-full flex items-center gap-3 px-4 py-3 rounded-[var(--radius)] border border-white/10 bg-white/[0.03] text-muted-foreground/60 hover:bg-white/[0.06] hover:text-muted-foreground hover:border-white/15 transition-colors text-sm"
        >
          <Pencil className="h-4 w-4 shrink-0" />
          Add a description...
        </button>
      );
    }

    return (
      <div
        className="group relative rounded-[var(--radius)] border border-white/10 bg-white/[0.03] px-4 py-3 cursor-pointer hover:bg-white/[0.06] hover:border-white/15 transition-colors max-h-32 overflow-y-auto"
        onClick={() => setEditing(true)}
        title="Click to edit description"
      >
        <button
          type="button"
          className="absolute top-2 right-2 p-1 rounded-md text-muted-foreground/40 opacity-0 group-hover:opacity-100 hover:text-foreground hover:bg-white/[0.06] transition-all z-10"
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
    <div className="rounded-[var(--radius)] border border-white/10 bg-white/[0.04] p-4 space-y-3">
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
          className="px-3 py-1.5 text-xs rounded-md text-muted-foreground hover:bg-white/[0.06] transition-colors"
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
        className="flex items-center gap-1.5 px-2.5 py-1 text-xs font-medium rounded-md border border-white/10 bg-white/[0.04] text-muted-foreground hover:bg-white/[0.08] hover:text-foreground transition-colors"
        title="Export as PDF"
      >
        <FileDown className="h-3.5 w-3.5" />
        PDF
      </button>
      {open && (
        <div className="absolute right-0 top-full mt-1.5 z-50 w-56 rounded-lg border border-white/10 bg-background/95 backdrop-blur-xl p-3 shadow-[0_8px_30px_-12px_rgba(0,0,0,0.6)]">
          <label className="flex items-center gap-2 text-xs text-foreground/80 cursor-pointer mb-3">
            <input
              type="checkbox"
              checked={includeInternal}
              onChange={(e) => setIncludeInternal(e.target.checked)}
              className="rounded border-white/20 bg-white/[0.06] text-primary focus:ring-primary/50"
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

  React.useEffect(() => {
    if (data?.ticket) {
      addTicket({
        id: data.ticket.id,
        number: data.ticket.number,
        subject: data.ticket.subject,
      });
      useWorkspaceStore.getState().setLastTicket(data.ticket.id);
    }
  }, [data?.ticket, addTicket]);

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
    onSuccess: (updated) => {
      queryClient.setQueryData(["ticket", ticketId], updated);
      queryClient.invalidateQueries({ queryKey: ["tickets"] });
      toast.success("Ticket updated");
    },
    onError: () => toast.error("Failed to update ticket"),
  });

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
    mutationFn: (vars: { companyId: string; linkAsSupplier: boolean }) =>
      ticketApi.assignCompany(ticketId, {
        companyId: vars.companyId,
        linkAsSupplier: vars.linkAsSupplier,
      }),
    onSuccess: (updated) => {
      queryClient.setQueryData(["ticket", ticketId], updated);
      queryClient.invalidateQueries({ queryKey: ["tickets"] });
      toast.success("Company toegewezen");
    },
    onError: () => toast.error("Kon company niet toewijzen"),
  });

  const submitAssignment = React.useCallback(
    async (companyId: string, linkAsSupplier: boolean) => {
      await assignMutation.mutateAsync({ companyId, linkAsSupplier });
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
        requesterEmail={requesterContact?.email ?? null}
        ownMailboxAddresses={ownMailboxAddresses}
        onRequestCompanyAssign={() => setAssignOpen(true)}
        mergedSourceTicketNumbers={mergedSourceTicketNumbers}
        mergedByUserName={mergedByUserName}
        mergedIntoTicketNumber={mergedIntoTicketNumber}
        splitFromTicketNumber={splitFromTicketNumber}
        splitFromUserName={splitFromUserName}
        splitChildren={splitChildren}
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
    </>
  );
}

function TicketDetailBody({
  ticketId, ticket, body, events, pinnedEvents, pinnedEventIds, updateMutation, queryClient,
  requesterEmail,
  ownMailboxAddresses,
  onRequestCompanyAssign,
  mergedSourceTicketNumbers,
  mergedByUserName,
  mergedIntoTicketNumber,
  splitFromTicketNumber,
  splitFromUserName,
  splitChildren,
}: {
  ticketId: string;
  ticket: any;
  body: any;
  events: any[];
  pinnedEvents: any[];
  pinnedEventIds: Set<number>;
  updateMutation: any;
  queryClient: any;
  requesterEmail: string | null;
  ownMailboxAddresses: string[];
  onRequestCompanyAssign: () => void;
  mergedSourceTicketNumbers: number[];
  mergedByUserName: string | null;
  mergedIntoTicketNumber: string | null;
  splitFromTicketNumber: string | null;
  splitFromUserName: string | null;
  splitChildren: { id: string; number: number }[];
}) {
  const { matchesEvent, mode, query, registerScope } = useInTicketSearch();
  const visibleEvents = React.useMemo(() => {
    if (mode !== "filter" || !query.trim()) return events;
    return events.filter(matchesEvent);
  }, [events, matchesEvent, mode, query]);

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
        {/* Static: ticket number + subject on one line, with SLA pills + PDF inline */}
        <div className="shrink-0 pb-4">
          <div className="flex items-start gap-3">
            <div className="flex-1 min-w-0">
              <EditableSubject
                number={ticket.number}
                value={ticket.subject}
                onSave={async (subject) => {
                  await updateMutation.mutateAsync({ subject });
                }}
              />
            </div>
            <SlaPill ticketId={ticket.id} className="shrink-0 justify-end" />
            <ExportPdfButton ticketId={ticketId} />
          </div>
        </div>

        <MergeBanners
          ticket={ticket}
          mergedIntoTicketNumber={mergedIntoTicketNumber}
          mergedSourceTicketNumbers={mergedSourceTicketNumbers}
          mergedByUserName={mergedByUserName}
        />

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
            <span className="rounded px-1.5 py-0.5 text-[10px] font-medium border border-white/10 bg-white/[0.04] text-muted-foreground/60">
              Internal
            </span>
          </div>
          <EditableDescription
            html={body.bodyHtml}
            text={body.bodyText}
            onSave={async (bodyHtml, bodyText) => {
              await updateMutation.mutateAsync({ bodyHtml, bodyText });
            }}
          />
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

        {/* Static: activity divider */}
        <div className="shrink-0 pb-3">
          <div className="flex items-center gap-3">
            <div className="h-px flex-1 bg-white/10" />
            <span className="text-xs uppercase tracking-wider text-muted-foreground">
              Activity
            </span>
            <div className="h-px flex-1 bg-white/10" />
          </div>
        </div>

        {/* Scrollable: activity timeline + inline compose form. The form
            sits at the bottom of the scroll region so agents can scroll
            past it to re-read earlier posts while typing a reply — a
            static bottom-bar used to obscure the feed below itself. The
            ref is handed to the in-ticket search highlighter so it knows
            which subtree to walk — nothing outside this container gets
            mutated. */}
        <div ref={registerScope} className="flex-1 min-h-0 overflow-y-auto pr-1">
          <TicketTimeline ticketId={ticketId} events={visibleEvents} pinnedEventIds={pinnedEventIds} />
          {mode === "filter" && query.trim() && visibleEvents.length === 0 && (
            <div className="py-6 text-center text-sm text-muted-foreground">
              Geen events matchen "{query}".
            </div>
          )}

          <div className="pt-4 pb-2">
            {ticket.mergedIntoTicketId ? (
              <div className="rounded-md border border-white/10 bg-white/[0.02] px-3 py-3 text-xs text-muted-foreground/70 text-center">
                This ticket is closed and merged. Reply on the target ticket
                instead.
              </div>
            ) : (
              <AddNoteForm
                key={ticketId}
                ticketId={ticketId}
                mailContext={buildMailContext(ticket, events, requesterEmail, ownMailboxAddresses)}
                onSubmitted={() => {
                  queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
                }}
              />
            )}
          </div>
        </div>
      </div>

      {/* Right column — toggle rail + animated side panel.
          Both live inside a single shrink-0 wrapper so the parent's gap-6
          stays between the activity feed and this whole assembly. When the
          panel is collapsed, only the rail remains visible. */}
      <div className="flex shrink-0 items-stretch gap-2">
        <div className="flex flex-col items-center pt-1">
          <button
            type="button"
            onClick={() => setSidePanelExpanded((v) => !v)}
            title={sidePanelExpanded ? "Collapse side panel" : "Expand side panel"}
            aria-label={sidePanelExpanded ? "Collapse side panel" : "Expand side panel"}
            className="p-1.5 rounded-md text-muted-foreground/60 hover:text-foreground hover:bg-white/[0.06] transition-colors"
          >
            {sidePanelExpanded
              ? <PanelRightClose className="h-4 w-4" />
              : <PanelRightOpen className="h-4 w-4" />}
          </button>
        </div>
        <div
          className={cn(
            "overflow-hidden transition-[width,opacity] duration-200 ease-out",
            sidePanelExpanded ? "w-[320px] opacity-100" : "w-0 opacity-0",
          )}
          aria-hidden={!sidePanelExpanded}
        >
          <TicketSidePanel
            ticket={ticket}
            onUpdate={async (fields) => { await updateMutation.mutateAsync(fields); }}
            onRequestCompanyAssign={onRequestCompanyAssign}
            pinned={sidePanelPinned}
            onTogglePin={() => setSidePanelPinned(!sidePanelPinned)}
          />
        </div>
      </div>
    </div>
  );
}

function MergeBanners({
  ticket,
  mergedIntoTicketNumber,
  mergedSourceTicketNumbers,
  mergedByUserName,
}: {
  ticket: Ticket;
  mergedIntoTicketNumber: string | null;
  mergedSourceTicketNumbers: number[];
  mergedByUserName: string | null;
}) {
  const isMerged = !!ticket.mergedIntoTicketId;
  const hasIncomingMerges = mergedSourceTicketNumbers.length > 0;
  if (!isMerged && !hasIncomingMerges) return null;

  return (
    <div className="shrink-0 pb-3 space-y-2">
      {isMerged && ticket.mergedIntoTicketId && (
        <div className="rounded-md border border-purple-400/30 bg-purple-500/[0.06] px-3 py-2.5 flex items-start gap-2">
          <GitMerge className="h-4 w-4 shrink-0 mt-0.5 text-purple-300/90" />
          <div className="text-sm text-purple-100/90">
            This ticket was merged into{" "}
            <Link
              to="/tickets/$ticketId"
              params={{ ticketId: ticket.mergedIntoTicketId }}
              className="font-medium underline underline-offset-2 hover:text-purple-50"
            >
              #{mergedIntoTicketNumber ?? "?"}
            </Link>
            {ticket.mergedUtc && (
              <>
                {" "}on {new Date(ticket.mergedUtc).toLocaleDateString()}
              </>
            )}
            {mergedByUserName && (
              <>
                {" "}by <span className="text-purple-50/90">{mergedByUserName}</span>
              </>
            )}
            .
          </div>
        </div>
      )}
      {hasIncomingMerges && !isMerged && (
        <div className="rounded-md border border-white/10 bg-white/[0.02] px-3 py-2 text-xs text-muted-foreground/80 flex items-center gap-2 flex-wrap">
          <GitMerge className="h-3.5 w-3.5 shrink-0 text-purple-300/80" />
          <span>Merged from</span>
          {mergedSourceTicketNumbers.map((n, idx) => (
            <span key={n}>
              <span className="text-foreground/80 font-medium">#{n}</span>
              {idx < mergedSourceTicketNumbers.length - 1 ? "," : ""}
            </span>
          ))}
        </div>
      )}
    </div>
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
        <div className="rounded-md border border-white/10 bg-white/[0.02] px-3 py-2 text-xs text-muted-foreground/80 flex items-center gap-2 flex-wrap">
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
