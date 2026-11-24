import * as React from "react";
import type { QueryClient, UseMutationResult } from "@tanstack/react-query";
import { cn } from "@/lib/utils";
import {
  type Ticket,
  type TicketBody,
  type TicketEvent,
  type TicketEventPin,
  type TicketFieldUpdate,
  type TicketMutationResult,
} from "@/lib/ticket-api";
import type { ChecklistSettings, TicketChecklist } from "@/lib/checklist-api";
import { TicketChecklistBar } from "../components/checklists/TicketChecklistBar";
import { SearchContextBar } from "@/components/SearchContextBar";
import { useWorkspaceStore } from "@/stores/useWorkspaceStore";
import { TicketPresence } from "../components/sidePanel/TicketPresence";
import { TicketTimeline } from "../components/TicketTimeline";
import { isSystemEvent, isSystemEventType } from "../components/timeline/eventConfig";
import { TimelineOlderBar } from "../components/TimelineOlderBar";
import type { TicketTimeline as TicketTimelineState } from "../useTicketTimeline";
import { PortalRegistrationCard } from "@/components/portal/PortalRegistrationCard";
import { PinnedEventsSummary } from "../components/PinnedEventsSummary";
import { TicketTimesheetPanel } from "../components/TicketTimesheetPanel";
import { TicketTimeAlertDialog } from "../components/TicketTimeAlertDialog";
import { AddNoteForm } from "../components/AddNoteForm";
import { buildMailContext } from "../mailContext";
import { useInTicketSearch } from "../components/InTicketSearch";
import { stripBaseTags } from "@/lib/sanitize";
import { TicketFormHost } from "@/components/forms/TicketFormHost";
import type { TicketFormSummary } from "@/lib/forms-api";
import { BackofficeLockBanner } from "../components/BackofficeLockBanner";
import { useCategoryLocked } from "../components/useCategoryLocked";
import { EditableSubject } from "./EditableSubject";
import { SplitBanners } from "./SplitBanners";
import { DescriptionSection } from "./DescriptionSection";
import { TicketSideRail } from "./TicketSideRail";

// Shape of the id an order pill may carry — Adsolut mirror rows are GUIDs.
const ORDER_GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

export function TicketDetailBody({
  ticketId, embedded, ticket, body, events, timeline, eventTypeCounts, latestMailReceived, pinnedEventsOutsidePage,
  serverTaggedOrders, ticketForms, pinnedEvents, pinnedEventIds, updateMutation, queryClient,
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
  portalRegistration,
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
  contactNotLinkedWarning,
  onTimeAlertBlockingChange,
}: {
  ticketId: string;
  embedded: boolean;
  ticket: Ticket;
  body: TicketBody;
  events: TicketEvent[];
  timeline: TicketTimelineState;
  eventTypeCounts?: Record<string, number>;
  latestMailReceived: TicketEvent | null;
  pinnedEventsOutsidePage: TicketEvent[];
  serverTaggedOrders: { id: string; label: string }[];
  ticketForms: TicketFormSummary[];
  pinnedEvents: TicketEventPin[];
  pinnedEventIds: Set<number>;
  updateMutation: UseMutationResult<TicketMutationResult, Error, TicketFieldUpdate>;
  queryClient: QueryClient;
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
  /// v0.1.0 — non-null only on a portal-registration system ticket.
  portalRegistration: { userId: string; status: string; email: string; displayName: string } | null;
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
  contactNotLinkedWarning?: boolean;
  /// v0.1.17 — forwarded from the hour-limit dialog so the page can hold
  /// the call-back prompt until that dialog is handled.
  onTimeAlertBlockingChange: (blocking: boolean) => void;
}) {
  const { matchesEvent, mode, query, registerScope } = useInTicketSearch();

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

  // v0.1.33 — counted over the whole timeline (server per-type counts), not
  // just the loaded page.
  const systemEventCount = React.useMemo(
    () =>
      eventTypeCounts
        ? Object.entries(eventTypeCounts).reduce((n, [type, c]) => (isSystemEventType(type) ? n + c : n), 0)
        : events.filter(isSystemEvent).length,
    [events, eventTypeCounts],
  );

  // Orders tagged via "::" pills anywhere in this ticket (description or any
  // article/note body). Derived from the already-fetched HTML — the pill is
  // the source of truth for order↔ticket links (ticket_order_links is dead
  // by design). GUID-checked so a hand-crafted data-order-id in an inbound
  // mail body can never smuggle an arbitrary string into an /api/orders/{id}
  // fetch. First-seen order wins; duplicates collapse to one row.
  const taggedOrders = React.useMemo(() => {
    const seen = new Map<string, string>();
    const htmls: string[] = [];
    if (body?.bodyHtml) htmls.push(body.bodyHtml);
    for (const e of events) if (e.bodyHtml) htmls.push(e.bodyHtml);
    const parser = new DOMParser();
    for (const html of htmls) {
      parser
        .parseFromString(stripBaseTags(html), "text/html")
        .querySelectorAll("[data-order-id]")
        .forEach((el) => {
          const id = el.getAttribute("data-order-id")?.toLowerCase();
          if (!id || !ORDER_GUID_RE.test(id) || seen.has(id)) return;
          seen.set(id, el.getAttribute("data-label") ?? el.textContent ?? "");
        });
    }
    // v0.1.33 — pills in events that aren't loaded come from the server.
    for (const o of serverTaggedOrders) {
      if (ORDER_GUID_RE.test(o.id) && !seen.has(o.id)) seen.set(o.id, o.label);
    }
    return Array.from(seen, ([id, label]) => ({ id, label }));
  }, [body, events, serverTaggedOrders]);

  // The composer's reply / quote source is the newest inbound mail, which
  // may be older than the loaded page — the server sends it separately.
  const mailContextEvents = React.useMemo(
    () => (latestMailReceived ? [latestMailReceived, ...events] : events),
    [latestMailReceived, events],
  );
  // Pinned-summary previews resolve against loaded + out-of-page pinned events.
  const pinnedLookupEvents = React.useMemo(
    () => (pinnedEventsOutsidePage.length > 0 ? [...pinnedEventsOutsidePage, ...events] : events),
    [pinnedEventsOutsidePage, events],
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

  // v0.1.61 — soft lock: a ticket in a "Locked" category is worked on via
  // Back office forms. On the normal page the side-panel fields are greyed
  // (notes stay open) until the agent chooses "Edit anyway" for this visit —
  // in the ticket overlay too (its banner adds Mark as handled). UI only —
  // the server never refuses.
  const { locked: categoryLocked, name: lockedCategoryName } = useCategoryLocked(ticket?.categoryId);
  const [editAnyway, setEditAnyway] = React.useState(false);
  React.useEffect(() => setEditAnyway(false), [ticketId]);
  const softLocked = categoryLocked && !editAnyway;

  return (
    <div className={cn("flex gap-6 pt-3 overflow-hidden", embedded ? "h-full" : "h-[calc(100vh-0.75rem)]")}>
      <TicketFormHost ticketId={ticketId} ticketNumber={ticket?.number ?? null} />
      {/* Left column — header + description static, activity scrolls, reply pinned bottom */}
      <div className="flex flex-col flex-1 min-w-0 min-h-0 overflow-hidden">
        <SearchContextBar ticketId={ticketId} />
        {softLocked && (
          <BackofficeLockBanner
            ticketId={ticketId}
            ticketNumber={ticket.number}
            subject={ticket.subject}
            categoryName={lockedCategoryName}
            forms={ticketForms ?? []}
            embedded={embedded}
            onEditAnyway={() => setEditAnyway(true)}
          />
        )}
        {/* Static: ticket number + subject on one line, full width — action pills
            live on the Description label row below so long subjects stay visible */}
        <div className="shrink-0 pb-4">
          <EditableSubject
            ticketId={ticketId}
            queueId={ticket.queueId}
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
        <DescriptionSection
          ticketId={ticketId}
          ticket={ticket}
          body={body}
          updateMutation={updateMutation}
          descriptionAttachments={descriptionAttachments}
          projectsEnabled={projectsEnabled}
          projectTicketNumber={projectTicketNumber}
          checklistsEnabled={checklistsEnabled}
          checklistSettings={checklistSettings}
          checklists={checklists}
          activeChecklistId={activeChecklistId}
          onOpenChecklist={onOpenChecklist}
        />

        {/* Pinned events summary */}
        {pinnedEvents.length > 0 && (
          <div className="shrink-0 pb-3">
            <PinnedEventsSummary
              ticketId={ticketId}
              pinnedEvents={pinnedEvents}
              events={pinnedLookupEvents}
              ensureEventLoaded={timeline.ensureLoaded}
            />
          </div>
        )}

        {/* v0.0.35-F — time-logged expand panel */}
        <div className="shrink-0 pb-3">
          <TicketTimesheetPanel
            ticketId={ticketId}
            queueId={ticket.queueId}
            ticketNumber={ticket.number}
            ticketSubject={ticket.subject}
          />
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

        {/* v0.1.0 — portal registration approve/reject card (only on the
            system ticket a registration created). */}
        {portalRegistration ? <PortalRegistrationCard ticketId={ticketId} /> : null}

        {/* v0.0.87 — per-ticket hour-limit warning (self-gating: only opens
            when the feature is on and the ticket is over its limit). The queue
            drives re-evaluation on open and on queue change. */}
        <TicketTimeAlertDialog
          ticketId={ticketId}
          queueId={ticket.queueId}
          onBlockingChange={onTimeAlertBlockingChange}
        />

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
          {timeline.hasOlder && (
            <TimelineOlderBar
              loading={timeline.loadingOlder}
              searching={!!query.trim()}
              onLoadOlder={timeline.loadOlder}
              onLoadAll={timeline.loadAll}
            />
          )}
          <TicketTimeline ticketId={ticketId} ticketNumber={ticket.number} events={visibleEvents} pinnedEventIds={pinnedEventIds} />
          {mode === "filter" && query.trim() && visibleEvents.length === 0 && (
            <div className="py-6 text-center text-sm text-muted-foreground">
              No events match "{query}".
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
                mailContext={buildMailContext(ticket, mailContextEvents, requesterEmail, ownMailboxAddresses)}
                onSubmitted={() => {
                  queryClient.invalidateQueries({ queryKey: ["ticket", ticketId] });
                }}
              />
            )}
            <TicketPresence ticketId={ticketId} />
          </div>
        </div>
      </div>

      {/* Right column — toggle rail + animated side panel (see TicketSideRail). */}
      <TicketSideRail
        ticketId={ticketId}
        ticket={ticket}
        queryClient={queryClient}
        checklistsEnabled={checklistsEnabled}
        checklistSettings={checklistSettings}
        checklists={checklists}
        checklistOpen={checklistOpen}
        onChecklistOpenChange={onChecklistOpenChange}
        activeChecklistId={activeChecklistId}
        onActiveChecklistChange={onActiveChecklistChange}
        projectsEnabled={projectsEnabled}
        projectOpen={projectOpen}
        onProjectOpenChange={onProjectOpenChange}
        sidePanelExpanded={sidePanelExpanded}
        setSidePanelExpanded={setSidePanelExpanded}
        sidePanelPinned={sidePanelPinned}
        onTogglePin={() => setSidePanelPinned(!sidePanelPinned)}
        showSystemEvents={showSystemEvents}
        onToggleSystemEvents={() => setShowSystemEvents((v) => !v)}
        systemEventCount={systemEventCount}
        onSidePanelUpdate={onSidePanelUpdate}
        onRequestCompanyAssign={onRequestCompanyAssign}
        mergedIntoTicketNumber={mergedIntoTicketNumber}
        mergedSourceTicketNumbers={mergedSourceTicketNumbers}
        mergedByUserName={mergedByUserName}
        parentTicketNumber={parentTicketNumber}
        parentLinkedByUserName={parentLinkedByUserName}
        childTickets={childTickets}
        projectTicketNumber={projectTicketNumber}
        projectLinkedByUserName={projectLinkedByUserName}
        projectLinkedTicketCount={projectLinkedTicketCount}
        contactNotLinkedWarning={contactNotLinkedWarning}
        taggedOrders={taggedOrders}
        ticketForms={ticketForms}
        softLocked={softLocked}
      />
    </div>
  );
}
