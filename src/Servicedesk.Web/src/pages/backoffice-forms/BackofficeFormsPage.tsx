import * as React from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useNavigate, useRouterState } from "@tanstack/react-router";
import { CheckCircle2, ClipboardList, Inbox, Layers, Search, X } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { AgentPicker } from "@/components/AgentPicker";
import { CompanyPicker } from "@/components/CompanyPicker";
import { ViewTabs } from "@/components/ViewTabs";
import { useDebounced } from "@/hooks/useDebounced";
import { useServerTimeZone } from "@/hooks/useServerTime";
import { formatDateTime } from "@/lib/dateFormat";
import { formatNumber, formatTimeAgo } from "@/lib/format";
import {
  backofficeFormKeys,
  backofficeFormsApi,
  type BackofficeFormItem,
  type BackofficeFormTab,
  type BackofficePresenceMap,
} from "@/lib/backoffice-forms-api";
import { useTicketOverlayStore } from "@/stores/useTicketOverlayStore";
import { BackofficeFormDialog } from "./BackofficeFormDialog";
import { BackofficeStatusChip } from "./BackofficeStatusChip";
import { presenceName } from "./backofficeLabels";

const PAGE_SIZE = 50;

const EMPTY_TEXT: Record<BackofficeFormTab, string> = {
  Open: "Nothing waiting. New forms appear here as soon as a technician submits one.",
  Handled: "No handled forms yet.",
  All: "No back office forms yet. Mark a form template with “Send to back office” to start.",
};

/// v0.1.54 — the Back office forms work list: every form filled from a
/// template marked "Send to back office"; flag-gated route and API.
/// v0.1.61 — the work surface: a row opens its ticket in the overlay (which
/// marks the form "In progress" for everyone else through live presence).
/// Open is split into "Waiting for pickup" (counts: badge, red dot) and
/// "Waiting for customer" (ticket Pending — setting-driven, never counted).
/// `?form=<id>` opens one form (search, notifications).
const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function formIdFrom(search: string): string | null {
  const id = new URLSearchParams(search).get("form");
  return id && UUID_RE.test(id) ? id : null;
}

export function BackofficeFormsPage() {
  const zone = useServerTimeZone();
  const [tab, setTab] = React.useState<BackofficeFormTab>("Open");
  const [search, setSearch] = React.useState("");
  const [templateId, setTemplateId] = React.useState<string>("");
  const [companyId, setCompanyId] = React.useState<string | null>(null);
  const [submittedBy, setSubmittedBy] = React.useState<string | null>(null);
  const [page, setPage] = React.useState(1);
  const [dialogId, setDialogId] = React.useState<string | null>(() => formIdFrom(window.location.search));
  const location = useRouterState({ select: (s) => s.location });
  const openOverlay = useTicketOverlayStore((s) => s.open);
  React.useEffect(() => {
    const id = formIdFrom(location.searchStr);
    if (id) setDialogId(id);
  }, [location.searchStr]);
  const navigate = useNavigate();
  const closeDialog = () => {
    setDialogId(null);
    if (formIdFrom(window.location.search)) void navigate({ to: "/backoffice-forms", replace: true });
  };
  const q = useDebounced(search, 250);

  React.useEffect(() => setPage(1), [tab, q, templateId, companyId, submittedBy]);

  const params = {
    status: tab, q, templateId: templateId || null, companyId, submittedBy,
    offset: (page - 1) * PAGE_SIZE, limit: PAGE_SIZE,
  };
  const list = useQuery({
    queryKey: backofficeFormKeys.list(params),
    queryFn: () => backofficeFormsApi.list(params),
    placeholderData: keepPreviousData,
    staleTime: 15_000,
  });
  const counts = useQuery({
    queryKey: backofficeFormKeys.counts(),
    queryFn: backofficeFormsApi.counts,
    staleTime: 30_000,
    refetchOnWindowFocus: true,
  });
  const templates = useQuery({
    queryKey: backofficeFormKeys.templates(),
    queryFn: backofficeFormsApi.templates,
    staleTime: 5 * 60_000,
  });
  const presence = useQuery({
    queryKey: backofficeFormKeys.presence(),
    queryFn: backofficeFormsApi.presence,
    staleTime: 60_000,
    refetchInterval: 60_000,
  });

  const total = list.data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const items = list.data?.items ?? [];
  const filtered = !!(q.trim() || templateId || companyId || submittedBy);

  const openTicket = (f: BackofficeFormItem) => openOverlay({
    ticketId: f.ticketId,
    ticketNumber: f.ticketNumber,
    subject: f.ticketSubject,
    source: { path: location.pathname, search: location.searchStr, tab: null },
  });

  const ready = items.filter((i) => i.status === "Open" && !i.waiting);
  const waiting = items.filter((i) => i.status === "Open" && i.waiting);

  return (
    <div className="sd-bo-forms flex min-h-0 w-full flex-1 flex-col gap-5">
      <header className="space-y-1">
        <h1 className="text-display-md font-semibold text-foreground">Back office forms</h1>
        <p className="text-sm text-muted-foreground">
          Forms technicians filled in on tickets for back office follow-up. Open one to work on its ticket — colleagues
          see that you are on it.
        </p>
      </header>

      <div className="flex flex-col gap-3">
        <ViewTabs
          ariaLabel="Status"
          value={tab}
          onChange={(id) => setTab(id as BackofficeFormTab)}
          tabs={[
            { id: "Open", label: "Open", icon: Inbox, count: counts.data?.ready },
            { id: "Handled", label: "Handled", icon: CheckCircle2 },
            { id: "All", label: "All", icon: Layers },
          ]}
        />

        <div className="flex flex-wrap items-center gap-2">
          <div className="relative min-w-56 flex-1 sm:max-w-sm">
            <Search className="absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
            <Input value={search} onChange={(e) => setSearch(e.target.value)}
              placeholder="Search form, answers, ticket # or company…" className="pl-9" />
          </div>
          <select value={templateId} onChange={(e) => setTemplateId(e.target.value)} aria-label="Form"
            className="h-9 rounded-md border border-glass bg-glass px-2.5 text-sm text-foreground focus:outline-hidden focus:ring-1 focus:ring-ring [&_option]:bg-popover [&_option]:text-popover-foreground">
            <option value="">All forms</option>
            {(templates.data ?? []).map((t) => (
              <option key={t.id} value={t.id}>{t.name}{t.isActive ? "" : " (inactive)"}</option>
            ))}
          </select>
          <div className="w-52"><CompanyPicker value={companyId} onChange={setCompanyId} placeholder="Any company" /></div>
          <div className="w-48"><AgentPicker value={submittedBy} onChange={setSubmittedBy} placeholder="Any technician" /></div>
          {filtered && (
            <Button variant="ghost" size="sm" onClick={() => { setSearch(""); setTemplateId(""); setCompanyId(null); setSubmittedBy(null); }}>
              <X className="mr-1 h-3.5 w-3.5" /> Clear
            </Button>
          )}
        </div>
      </div>

      <div className="glass-panel min-h-64 flex-1 overflow-auto">
        {list.isLoading ? (
          <div className="space-y-2 p-4">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-10 w-full" />
          </div>
        ) : list.isError ? (
          <div className="p-6 text-sm text-destructive">Could not load the back office forms. Refresh to try again.</div>
        ) : items.length === 0 ? (
          <div className="flex flex-col items-center justify-center gap-2 p-12 text-center">
            <ClipboardList className="h-6 w-6 text-muted-foreground" />
            <p className="max-w-md text-sm text-muted-foreground">{filtered ? "No forms match these filters." : EMPTY_TEXT[tab]}</p>
          </div>
        ) : (
          <table className="w-full text-sm">
            <thead className="sticky top-0 z-10 bg-popover">
              <tr className="border-b border-glass text-left text-[11px] uppercase tracking-wider text-muted-foreground/70">
                <th className="px-4 py-2.5 font-medium">Form</th>
                <th className="px-3 py-2.5 font-medium">Ticket</th>
                <th className="px-3 py-2.5 font-medium">Company</th>
                <th className="px-4 py-2.5 font-medium">{tab === "Open" ? "" : "Status"}</th>
              </tr>
            </thead>
            {tab === "Open" ? (
              <>
                <Group label="Waiting for pickup" count={ready.length} hint="Ready to be handled" />
                <tbody className="divide-y divide-glass">
                  {ready.map((f) => (
                    <Row key={f.id} item={f} zone={zone} presence={presence.data} onOpen={() => openTicket(f)} onView={() => setDialogId(f.id)} />
                  ))}
                </tbody>
                {waiting.length > 0 && (
                  <>
                    <Group label="Waiting for customer" count={waiting.length} hint="Comes back up when the ticket opens again" muted />
                    <tbody className="divide-y divide-glass">
                      {waiting.map((f) => (
                        <Row key={f.id} item={f} zone={zone} presence={presence.data} muted onOpen={() => openTicket(f)} onView={() => setDialogId(f.id)} />
                      ))}
                    </tbody>
                  </>
                )}
              </>
            ) : (
              <tbody className="divide-y divide-glass">
                {items.map((f) => (
                  <Row key={f.id} item={f} zone={zone} presence={presence.data} showStatus onOpen={() => openTicket(f)} onView={() => setDialogId(f.id)} />
                ))}
              </tbody>
            )}
          </table>
        )}
      </div>

      {total > PAGE_SIZE && (
        <div className="flex items-center justify-between text-xs text-muted-foreground">
          <span>Page {page} of {totalPages} · {formatNumber(total)} forms</span>
          <div className="flex items-center gap-2">
            <Button size="sm" variant="ghost" onClick={() => setPage((p) => Math.max(1, p - 1))} disabled={page <= 1 || list.isFetching}>
              Previous
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setPage((p) => Math.min(totalPages, p + 1))} disabled={page >= totalPages || list.isFetching}>
              Next
            </Button>
          </div>
        </div>
      )}

      <BackofficeFormDialog formId={dialogId} onOpenChange={(o) => { if (!o) closeDialog(); }} />
    </div>
  );
}

function Group({ label, count, hint, muted = false }: { label: string; count: number; hint: string; muted?: boolean }) {
  return (
    <tbody>
      <tr className="sd-bo-group">
        <td colSpan={4} className="border-y border-glass bg-glass/60 px-4 py-2">
          <span className={muted ? "text-xs font-semibold text-muted-foreground" : "text-xs font-semibold text-foreground"}>{label}</span>
          <span className={muted
            ? "ml-2 rounded-md bg-glass-strong px-1.5 text-[11px] font-semibold tabular-nums text-muted-foreground"
            : "ml-2 rounded-md bg-primary/10 px-1.5 text-[11px] font-semibold tabular-nums text-primary"}>
            {count}
          </span>
          <span className="ml-3 text-[11px] text-muted-foreground/80">{hint}</span>
        </td>
      </tr>
    </tbody>
  );
}

function Row({
  item, zone, presence, onOpen, onView, muted = false, showStatus = false,
}: {
  item: BackofficeFormItem;
  zone: string | null;
  presence: BackofficePresenceMap | undefined;
  onOpen: () => void;
  onView: () => void;
  muted?: boolean;
  showStatus?: boolean;
}) {
  const viewers = item.status === "Open" ? presence?.[item.ticketId] ?? [] : [];
  const inProgressBy = viewers.length > 0 ? presenceName(viewers[0].name) + (viewers.length > 1 ? ` +${viewers.length - 1}` : "") : null;
  return (
    <tr className={muted ? "sd-bo-row cursor-pointer text-muted-foreground transition-colors hover:bg-glass-hover" : "sd-bo-row cursor-pointer transition-colors hover:bg-glass-hover"}
      onClick={onOpen} onKeyDown={(e) => { if (e.key === "Enter") onOpen(); }} tabIndex={0}>
      <td className="px-4 py-2.5">
        <div className="flex items-center gap-2">
          <span className={muted ? "font-medium" : "font-medium text-foreground"}>{item.name}</span>
          {item.reopened && (
            <span className="rounded-md border border-amber-400/45 bg-amber-100/70 px-1.5 py-px text-[10px] font-medium text-amber-900 dark:bg-amber-400/12 dark:text-amber-200">
              Changed after handling
            </span>
          )}
        </div>
        <div className="text-[11px] text-muted-foreground/80" title={formatDateTime(item.submittedUtc, zone)}>
          {item.submittedByName ? presenceName(item.submittedByName) : "unknown"} · {formatTimeAgo(item.submittedUtc, { dateAfterDays: 7 })}
          {item.version > 1 && <span className="tabular-nums"> · v{item.version}</span>}
        </div>
      </td>
      <td className="max-w-80 px-3 py-2.5">
        <div className="truncate">
          <span className="tabular-nums text-xs text-muted-foreground">#{item.ticketNumber}</span>{" "}
          <span className={muted ? "" : "text-foreground"}>{item.ticketSubject}</span>
        </div>
        {item.waiting && item.ticketPendingTillUtc && (
          <div className="text-[11px] text-muted-foreground/80">Pending till {formatDateTime(item.ticketPendingTillUtc, zone)}</div>
        )}
      </td>
      <td className="max-w-48 truncate px-3 py-2.5 text-muted-foreground">{item.companyName ?? "—"}</td>
      <td className="px-4 py-2.5">
        <div className="flex items-center justify-end gap-2">
          {(inProgressBy || showStatus) && (
            <BackofficeStatusChip item={{ status: item.status, handledByName: item.handledByName ? presenceName(item.handledByName) : null, inProgressBy }} />
          )}
          <Button variant="ghost" size="sm" className="h-7 px-2 text-xs" onClick={(e) => { e.stopPropagation(); onView(); }}>
            View form
          </Button>
        </div>
      </td>
    </tr>
  );
}
