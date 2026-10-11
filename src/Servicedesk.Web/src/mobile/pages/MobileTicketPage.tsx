import * as React from "react";
import { Link, useRouterState } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import {
  Building2,
  ChevronDown,
  FlaskConical,
  GitMerge,
  Info,
  Loader2,
  Lock,
  Phone,
  PhoneCall,
  StickyNote,
  User,
} from "lucide-react";
import { ticketApi, contactApi, companyApi, userApi, type TicketEvent } from "@/lib/ticket-api";
import { agentQueueApi, taxonomyApi } from "@/lib/api";
import { colorPillStyle } from "@/lib/colorPill";
import { flagColors as resolveFlagColors, useTicketFlagSettings } from "@/lib/ticketFlags";
import { formatDateTimeMedium } from "@/lib/dateFormat";
import { useTheme } from "@/app/ThemeProvider";
import { useTicketRealtime } from "@/hooks/useTicketRealtime";
import { useViewingTicket } from "@/hooks/usePresence";
import { useServerNowMs, useServerTimeZone } from "@/hooks/useServerTime";
import { useTicketTimeline } from "@/pages/tickets/useTicketTimeline";
import {
  CollapsibleBody,
  EVENT_CONFIG,
  EventBody,
  TimelinePreviewProvider,
  isSystemEvent,
} from "@/pages/tickets/components/TicketTimeline";
import { CARD_ACCENT, parseMetadata } from "@/pages/tickets/components/timelineMeta";
import { SlaPill } from "@/components/sla/SlaPill";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { useMobileHeader } from "../mobileHeader";
import { ComposeSheet } from "../components/ComposeSheet";
import type { ComposeKind } from "../compose";
import { emailLocalPart, eventStamp, requesterName } from "../format";

/// `/m/tickets/:id` — one ticket, one column: header facts, a collapsible
/// details block, the description and the timeline newest-first. Reading is
/// the same data and authorization as desktop (GET /api/tickets/{id} 404s
/// without queue access). The only writes are an internal note and a logged
/// call, through the shared endpoint.
export function MobileTicketPage({ ticketId }: { ticketId: string }) {
  useViewingTicket(ticketId);
  useTicketRealtime(ticketId);
  const detail = useQuery({ queryKey: ["ticket", ticketId], queryFn: () => ticketApi.get(ticketId) });
  const timeline = useTicketTimeline(ticketId, detail.data);
  const t = detail.data?.ticket;
  useMobileHeader(t ? `#${t.number}` : "Ticket", "/m");

  const statuses = useQuery({ queryKey: ["statuses"], queryFn: taxonomyApi.statuses.list, staleTime: 300_000 });
  const priorities = useQuery({ queryKey: ["priorities"], queryFn: taxonomyApi.priorities.list, staleTime: 300_000 });
  const queues = useQuery({ queryKey: ["accessible-queues"], queryFn: agentQueueApi.list, staleTime: 60_000 });
  const contact = useQuery({
    queryKey: ["contact", t?.requesterContactId ?? null],
    queryFn: () => contactApi.get(t!.requesterContactId),
    enabled: !!t?.requesterContactId,
    staleTime: 300_000,
  });
  const company = useQuery({
    queryKey: ["company", t?.companyId ?? null],
    queryFn: () => companyApi.get(t!.companyId!),
    enabled: !!t?.companyId,
    staleTime: 300_000,
  });
  const flagSettings = useTicketFlagSettings();
  const colors = resolveFlagColors(flagSettings.data);
  const zone = useServerTimeZone();

  const [composeOpen, setComposeOpen] = React.useState(false);
  const [composeKind, setComposeKind] = React.useState<ComposeKind>("note");
  const openCompose = (k: ComposeKind) => {
    setComposeKind(k);
    setComposeOpen(true);
  };

  // Deep link to one event (#event-123, e.g. from a mention): load back to
  // it, then scroll it into view — once per hash, so opening a second
  // mention on the same ticket scrolls again.
  const hash = useRouterState({ select: (st) => st.location.hash });
  const scrolledRef = React.useRef<string | null>(null);
  const { ensureLoaded } = timeline;
  React.useEffect(() => {
    if (!detail.data || scrolledRef.current === hash) return;
    const m = /^#?event-(\d+)$/.exec(hash);
    if (!m) return;
    scrolledRef.current = hash;
    const id = Number(m[1]);
    void ensureLoaded(id).then((ok) => {
      if (!ok) return;
      requestAnimationFrame(() => document.getElementById(`m-event-${id}`)?.scrollIntoView({ block: "center" }));
    });
  }, [detail.data, ensureLoaded, hash]);

  if (detail.isLoading) {
    return (
      <div className="space-y-3">
        <Skeleton className="h-40 w-full rounded-xl" />
        <Skeleton className="h-24 w-full rounded-xl" />
        <Skeleton className="h-24 w-full rounded-xl" />
      </div>
    );
  }
  if (detail.isError || !detail.data || !t) {
    return (
      <p className="py-10 text-center text-sm text-muted-foreground">
        This ticket does not exist or you do not have access to it.
      </p>
    );
  }

  const d = detail.data;
  const status = statuses.data?.find((s) => s.id === t.statusId);
  const priority = priorities.data?.find((p) => p.id === t.priorityId);
  const queue = queues.data?.find((q) => q.id === t.queueId);
  const c = contact.data;
  const phone = c?.mobilePhone || c?.phone || "";
  const newestFirst = [...timeline.events].reverse();

  return (
    <TimelinePreviewProvider>
      {/* pb-16 keeps the last timeline item clear of the floating actions. */}
      <div className="space-y-3 pb-16">
        {t.mergedIntoTicketId && (
          <Link
            to="/m/tickets/$ticketId"
            params={{ ticketId: t.mergedIntoTicketId }}
            className="glass-card flex items-center gap-2 px-4 py-3 text-[13px]"
          >
            <GitMerge className="h-4 w-4 text-primary" />
            Merged into <span className="font-semibold">#{d.mergedIntoTicketNumber}</span>
          </Link>
        )}

        <TicketHeaderCard
          subject={t.subject}
          statusName={status?.name}
          statusColor={status?.color}
          priorityName={priority?.name}
          priorityColor={priority?.color}
          priorityIsDefault={priority?.isDefault ?? true}
          isCallback={t.isCallback}
          isResearch={t.isResearch}
          isProject={t.isProject}
          colors={colors}
        />

        <section className="glass-card divide-y divide-glass overflow-hidden">
          <FactRow icon={<User className="h-4 w-4" />} label="Requester">
            <span className="block truncate">{c ? requesterName(c.firstName, c.lastName, c.email) : "…"}</span>
            {c?.email && <span className="block truncate text-[12px] text-muted-foreground">{c.email}</span>}
          </FactRow>
          {phone && (
            <FactRow icon={<Phone className="h-4 w-4" />} label="Phone">
              <a href={`tel:${phone.replace(/[^\d+]/g, "")}`} className="font-medium text-primary">{phone}</a>
            </FactRow>
          )}
          {t.companyId && (
            <FactRow icon={<Building2 className="h-4 w-4" />} label="Company">
              <span className="block truncate">{company.data?.company?.name ?? d.companyAlert?.companyName ?? "…"}</span>
            </FactRow>
          )}
          <DetailsDisclosure>
            <FactRow label="Queue">{queue?.name ?? "—"}</FactRow>
            <FactRow label="Assignee">
              <AssigneeName userId={t.assigneeUserId} />
            </FactRow>
            <FactRow label="Created">{formatDateTimeMedium(t.createdUtc, zone)}</FactRow>
            <FactRow label="Updated">{formatDateTimeMedium(t.updatedUtc, zone)}</FactRow>
            <FactRow label="SLA">
              <SlaPill ticketId={t.id} />
            </FactRow>
          </DetailsDisclosure>
        </section>

        {d.companyAlert?.alertOnOpen && d.companyAlert.alertText && (
          <div className="flex gap-2 rounded-xl border border-amber-500/40 bg-amber-500/10 px-4 py-3 text-[13px]">
            <Info className="mt-0.5 h-4 w-4 shrink-0 text-amber-600" />
            <span className="whitespace-pre-wrap">{d.companyAlert.alertText}</span>
          </div>
        )}

        {(d.body.bodyHtml || d.body.bodyText) && (
          <section className="glass-card px-4 py-3">
            <h2 className="mb-2 text-[11px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">Description</h2>
            <CollapsibleBody html={d.body.bodyHtml} text={d.body.bodyText} />
          </section>
        )}

        <section className="space-y-2.5 pt-1">
          <h2 className="px-1 text-[11px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">Timeline</h2>
          {newestFirst.length === 0 ? (
            <p className="px-1 text-sm text-muted-foreground">No activity yet.</p>
          ) : (
            <ol className="space-y-2.5">
              {newestFirst.map((e) => (
                <li key={e.id} id={`m-event-${e.id}`}>
                  <MobileTimelineEvent event={e} />
                </li>
              ))}
            </ol>
          )}
          {timeline.hasOlder && (
            <button
              type="button"
              onClick={() => void timeline.loadOlder()}
              disabled={timeline.loadingOlder}
              className="flex h-12 w-full items-center justify-center gap-2 rounded-xl border border-glass bg-glass text-[14px] font-medium text-foreground disabled:opacity-60"
            >
              {timeline.loadingOlder && <Loader2 className="h-4 w-4 animate-spin" />}
              Load older activity
            </button>
          )}
        </section>
      </div>

      {/* Thumb-zone actions. Both open the same sheet; the sheet can switch. */}
      <div className="fixed inset-x-0 bottom-[calc(4.75rem+env(safe-area-inset-bottom))] z-20 flex justify-end gap-2 px-4 pointer-events-none">
        <button
          type="button"
          onClick={() => openCompose("call")}
          className={cn(
            "sd-m-fab pointer-events-auto inline-flex h-12 items-center gap-2 rounded-full border px-4 text-[14px] font-semibold shadow-lg",
            t.isCallback ? "border-transparent text-white" : "border-glass bg-background text-foreground",
          )}
          style={t.isCallback ? { backgroundColor: colors.callback } : undefined}
        >
          <Phone className="h-4 w-4" />
          Log call
        </button>
        <button
          type="button"
          onClick={() => openCompose("note")}
          className="sd-m-fab pointer-events-auto inline-flex h-12 items-center gap-2 rounded-full bg-primary px-5 text-[14px] font-semibold text-primary-foreground shadow-lg"
        >
          <StickyNote className="h-4 w-4" />
          Note
        </button>
      </div>

      <ComposeSheet
        ticketId={t.id}
        open={composeOpen}
        kind={composeKind}
        onKindChange={setComposeKind}
        onOpenChange={setComposeOpen}
        callbackHint={t.isCallback && (flagSettings.data?.callbackClearOnCall ?? true)}
      />
    </TimelinePreviewProvider>
  );
}

function TicketHeaderCard({
  subject,
  statusName,
  statusColor,
  priorityName,
  priorityColor,
  priorityIsDefault,
  isCallback,
  isResearch,
  isProject,
  colors,
}: {
  subject: string;
  statusName?: string;
  statusColor?: string;
  priorityName?: string;
  priorityColor?: string;
  priorityIsDefault: boolean;
  isCallback: boolean;
  isResearch: boolean;
  isProject: boolean;
  colors: { callback: string; research: string };
}) {
  const { family, mode } = useTheme();
  return (
    <section className="glass-card px-4 py-4">
      <div className="flex flex-wrap items-center gap-1.5">
        {statusName && (
          <span className="rounded-full border px-2.5 py-0.5 text-[12px] font-semibold" style={colorPillStyle(statusColor || "#6b7280", { family, mode })}>
            {statusName}
          </span>
        )}
        {priorityName && !priorityIsDefault && (
          <span className="rounded-full border px-2.5 py-0.5 text-[12px] font-semibold" style={colorPillStyle(priorityColor || "#6b7280", { family, mode })}>
            {priorityName}
          </span>
        )}
        {isCallback && (
          <span className="inline-flex items-center gap-1 rounded-full border px-2.5 py-0.5 text-[12px] font-semibold" style={colorPillStyle(colors.callback, { family, mode })}>
            <PhoneCall className="h-3 w-3" /> Call back
          </span>
        )}
        {isResearch && (
          <span className="inline-flex items-center gap-1 rounded-full border px-2.5 py-0.5 text-[12px] font-semibold" style={colorPillStyle(colors.research, { family, mode })}>
            <FlaskConical className="h-3 w-3" /> Research
          </span>
        )}
        {isProject && (
          <span className="rounded-full border border-glass bg-glass px-2.5 py-0.5 text-[12px] font-semibold text-muted-foreground">Project</span>
        )}
      </div>
      <h2 className="mt-2.5 font-display text-[20px] font-semibold leading-snug tracking-tight break-words">
        {subject || "(no subject)"}
      </h2>
    </section>
  );
}

function FactRow({ icon, label, children }: { icon?: React.ReactNode; label: string; children: React.ReactNode }) {
  return (
    <div className="flex min-h-12 items-start gap-3 px-4 py-2.5">
      <span className="mt-0.5 flex w-[5.5rem] shrink-0 items-center gap-1.5 text-[12px] font-medium text-muted-foreground">
        {icon}
        {label}
      </span>
      <div className="min-w-0 flex-1 text-[14px]">{children}</div>
    </div>
  );
}

function DetailsDisclosure({ children }: { children: React.ReactNode }) {
  const [open, setOpen] = React.useState(false);
  return (
    <div>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className="flex h-12 w-full items-center justify-between px-4 text-[13px] font-medium text-muted-foreground"
      >
        {open ? "Fewer details" : "More details"}
        <ChevronDown className={cn("h-4 w-4 transition-transform", open && "rotate-180")} />
      </button>
      {open && <div className="divide-y divide-glass border-t border-glass">{children}</div>}
    </div>
  );
}

function AssigneeName({ userId }: { userId: string | null }) {
  const users = useQuery({
    queryKey: ["agents"],
    queryFn: userApi.listAgents,
    staleTime: 300_000,
    enabled: !!userId,
  });
  if (!userId) return <span className="text-muted-foreground">Unassigned</span>;
  const u = users.data?.find((x) => x.id === userId);
  return <span>{u ? emailLocalPart(u.email) : "…"}</span>;
}

/// One timeline entry as a card. Content events (mail, note, call, reply)
/// get the same accent colours as desktop and an amber tint when internal;
/// system events (status change, assignment…) render as a quiet line. The
/// body comes from the desktop EventBody in read-only mode, so every event
/// type renders exactly as on desktop.
function MobileTimelineEvent({ event }: { event: TicketEvent }) {
  const serverNowMs = useServerNowMs();
  const zone = useServerTimeZone();
  const config = EVENT_CONFIG[event.eventType];
  const Icon = config?.icon ?? Info;
  const label = config?.label ?? event.eventType;
  const meta = parseMetadata(event.metadataJson);
  const author =
    (event.authorName ?? "").trim() ||
    (typeof meta.fromName === "string" && meta.fromName) ||
    (typeof meta.from === "string" && meta.from) ||
    (event.authorUserId === null ? "Automatic Trigger" : "");
  const stamp = eventStamp(event.createdUtc, serverNowMs, zone);

  if (isSystemEvent(event)) {
    return (
      <div className="flex items-start gap-2.5 px-1 py-1 text-[13px] text-muted-foreground">
        <span className={cn("mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-full", config?.dotColor ?? "bg-glass-strong")}>
          <Icon className="h-3 w-3 text-white" />
        </span>
        <div className="min-w-0 flex-1">
          <EventBody event={event} readOnly />
          <div className="mt-0.5 text-[11px] text-muted-foreground/70">
            {author ? `${author} · ` : ""}
            {stamp}
          </div>
        </div>
      </div>
    );
  }

  const subject = typeof meta.subject === "string" ? meta.subject : null;
  return (
    <article
      className={cn(
        "glass-card overflow-hidden border-l-2 px-4 py-3",
        event.isInternal ? "sd-internal-note border-l-amber-400/70 bg-amber-500/[0.06]" : CARD_ACCENT[event.eventType] ?? "border-l-glass",
      )}
    >
      <header className="mb-2 flex items-center gap-2">
        <span className={cn("flex h-6 w-6 shrink-0 items-center justify-center rounded-full", config?.dotColor ?? "bg-glass-strong")}>
          <Icon className="h-3.5 w-3.5 text-white" />
        </span>
        <div className="min-w-0 flex-1">
          <div className="truncate text-[13px] font-semibold">{author || label}</div>
          <div className="truncate text-[11px] text-muted-foreground">
            {label}
            {event.isInternal && (
              <span className="ml-1.5 inline-flex items-center gap-0.5 text-amber-700 dark:text-amber-300">
                <Lock className="h-3 w-3" /> Internal
              </span>
            )}
          </div>
        </div>
        <time className="shrink-0 text-[11px] tabular-nums text-muted-foreground">{stamp}</time>
      </header>
      {subject && (event.eventType === "MailReceived" || event.eventType === "MailSent") && (
        <div className="mb-1.5 text-[13px] font-medium text-foreground/90 break-words">{subject}</div>
      )}
      <div className="sd-m-event-body min-w-0 overflow-x-auto text-[14px]">
        <EventBody event={event} readOnly />
      </div>
    </article>
  );
}
