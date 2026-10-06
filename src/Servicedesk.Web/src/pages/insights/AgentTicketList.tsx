import * as React from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { ArrowDown, ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { useTheme } from "@/app/ThemeProvider";
import { useServerTime, toServerLocal } from "@/hooks/useServerTime";
import { colorPillStyle } from "@/lib/colorPill";
import {
  insightsApi,
  type AgentActivityTotals,
  type AgentTicketItem,
  type AgentTicketSort,
  type InsightsAgent,
  type OpenedCloseReason,
  type ReportParams,
} from "@/lib/insights-api";
import { cn } from "@/lib/utils";
import { Segmented } from "./InsightsControls";
import { fmtDate, formatDuration, formatMinutes, nf } from "./insightsFormat";

const PAGE_SIZES = [10, 25, 50, 100] as const;
const DEFAULT_PAGE_SIZE = 25;
// Per-viewer convenience: same page-size memory as the other ticket lists.
const PAGE_SIZE_KEY = "sd-insights-ticket-page-size";

function readPageSize(): number {
  try {
    const n = Number(window.localStorage.getItem(PAGE_SIZE_KEY));
    return (PAGE_SIZES as readonly number[]).includes(n) ? n : DEFAULT_PAGE_SIZE;
  } catch {
    return DEFAULT_PAGE_SIZE;
  }
}

/// The tickets behind the Agents overview — one list per agent, switched
/// by the tabs on top, in two views: tickets worked on, and tickets opened
/// and closed again without action (v0.1.14). Server-paged and server-sorted; queue access is
/// applied server-side exactly as for the totals.
export function AgentTicketList({
  agents,
  totals,
  params,
  sort,
  onSort,
  colorFor,
  openedMinSeconds,
}: {
  agents: InsightsAgent[];
  totals: AgentActivityTotals[];
  params: ReportParams;
  sort: AgentTicketSort;
  onSort: (s: AgentTicketSort) => void;
  colorFor: (slot: number) => string;
  openedMinSeconds: number;
}) {
  const [activeId, setActiveId] = React.useState<string | null>(null);
  const [view, setView] = React.useState<ListView>(readView);
  const active = agents.find((a) => a.id === activeId) ?? agents[0];

  function chooseView(v: ListView) {
    try {
      window.localStorage.setItem(VIEW_KEY, v);
    } catch {
      // storage unavailable — the view still switches
    }
    setView(v);
  }

  return (
    <div className="glass-card overflow-hidden">
      <div className="flex flex-wrap items-end justify-between gap-3 px-5 pt-5">
        <div>
          <h2 className="text-base font-semibold text-foreground">
            {view === "worked" ? "Tickets worked on" : "Opened without action"}
          </h2>
          <p className="max-w-3xl text-xs text-muted-foreground">
            {view === "worked"
              ? "Every ticket the agent acted on or logged time on in this period."
              : `Tickets the agent opened and closed again from their recent-tickets list without doing anything on them in between (openings under ${openedMinSeconds}s are left out). Work on the ticket later doesn't remove the line.`}
          </p>
        </div>
        <Segmented
          ariaLabel="List"
          value={view}
          options={[
            { value: "worked", label: "Worked on" },
            { value: "opened", label: "Opened without action" },
          ]}
          onChange={(v) => chooseView(v as ListView)}
        />
      </div>

      {agents.length > 1 && (
        <div role="tablist" aria-label="Agent" className="sd-insights-subtabs mt-3 flex gap-1 overflow-x-auto border-b border-glass px-5">
          {agents.map((a, i) => {
            const on = a.id === active.id;
            return (
              <button
                key={a.id}
                type="button"
                role="tab"
                aria-selected={on}
                onClick={() => setActiveId(a.id)}
                className={cn(
                  "-mb-px inline-flex shrink-0 items-center gap-2 border-b-2 px-3 pb-2 pt-1 text-sm transition-colors",
                  on ? "text-foreground" : "border-transparent text-muted-foreground hover:text-foreground",
                )}
                style={on ? { borderBottomColor: colorFor(a.slot) } : undefined}
              >
                <span className="h-2.5 w-2.5 rounded-[3px]" style={{ backgroundColor: colorFor(a.slot) }} />
                <span className="max-w-56 truncate">{a.name}</span>
                <span className="rounded-full bg-glass px-1.5 text-[11px] tabular-nums text-muted-foreground">
                  {nf.format((view === "worked" ? totals[i]?.tickets : totals[i]?.openedNoAction) ?? 0)}
                </span>
              </button>
            );
          })}
        </div>
      )}

      {active &&
        (view === "worked" ? (
          <AgentTickets key={active.id} agentId={active.id} params={params} sort={sort} onSort={onSort} />
        ) : (
          <AgentOpened key={active.id} agentId={active.id} params={params} />
        ))}
    </div>
  );
}

type ListView = "worked" | "opened";

// Per-viewer convenience: reopen on the last list.
const VIEW_KEY = "sd-insights-agents-list";

function readView(): ListView {
  try {
    return window.localStorage.getItem(VIEW_KEY) === "opened" ? "opened" : "worked";
  } catch {
    return "worked";
  }
}

const CLOSE_REASON_LABEL: Record<OpenedCloseReason, string | null> = {
  removed: null,
  cleared: "list cleared",
  evicted: "auto · list full",
};

/// Page state that resets to 1 whenever the filter changes, plus the
/// remembered page size — shared by both lists.
function usePaging(filter: unknown) {
  const [pageSize, setPageSize] = React.useState(readPageSize);
  const filterKey = JSON.stringify([filter, pageSize]);
  const [pager, setPager] = React.useState({ key: filterKey, page: 1 });
  const page = pager.key === filterKey ? pager.page : 1;

  function changePageSize(next: number) {
    try {
      window.localStorage.setItem(PAGE_SIZE_KEY, String(next));
    } catch {
      // storage unavailable - the choice still applies for this visit
    }
    setPageSize(next);
  }

  return {
    page,
    pageSize,
    changePageSize,
    goTo: (next: number, totalPages: number) =>
      setPager({ key: filterKey, page: Math.min(Math.max(1, next), totalPages) }),
  };
}

function AgentOpened({ agentId, params }: { agentId: string; params: ReportParams }) {
  const theme = useTheme();
  const { time } = useServerTime();
  const offsetMinutes = time?.offsetMinutes ?? 0;
  const { page, pageSize, changePageSize, goTo } = usePaging([agentId, params]);

  const query = useQuery({
    queryKey: ["insights", "agents", "opened", agentId, params, page, pageSize],
    queryFn: () => insightsApi.agentOpened(params, agentId, (page - 1) * pageSize, pageSize),
    placeholderData: keepPreviousData,
    staleTime: 30_000,
    retry: false,
  });

  const items = query.data?.items ?? [];
  const total = query.data?.total ?? 0;

  if (query.isLoading) return <ListSkeleton />;
  if (query.isError) {
    return <p className="p-5 text-sm text-muted-foreground">The list could not be loaded.</p>;
  }
  if (items.length === 0) {
    return (
      <p className="p-5 text-sm text-muted-foreground">
        No tickets opened and closed without action in this period.
      </p>
    );
  }

  const fmt = (utc: string) => (time ? toServerLocal(utc, offsetMinutes) : "…");

  return (
    <>
      <div className={cn("mt-2 overflow-x-auto transition-opacity", query.isPlaceholderData && "opacity-60")}>
        <table className="w-full border-collapse text-sm">
          <thead>
            <tr className="border-b border-glass text-left text-[11px] uppercase tracking-[0.06em] text-muted-foreground">
              <th scope="col" className="px-5 py-2 font-medium">Ticket</th>
              <th scope="col" className="px-3 py-2 font-medium">Subject</th>
              <th scope="col" className="px-3 py-2 font-medium">Company</th>
              <th scope="col" className="px-3 py-2 font-medium" title="The ticket's status today">Status now</th>
              <th scope="col" className="px-3 py-2 font-medium">Opened</th>
              <th scope="col" className="px-3 py-2 font-medium">Closed</th>
              <th scope="col" className="px-5 py-2 text-right font-medium">Open for</th>
            </tr>
          </thead>
          <tbody>
            {items.map((o) => (
              <tr key={o.sessionId} className="border-b border-glass last:border-0 hover:bg-glass-hover">
                <td className="whitespace-nowrap px-5 py-2">
                  <Link
                    to={"/tickets/$ticketId" as never}
                    params={{ ticketId: o.ticketId } as never}
                    className="font-mono text-xs font-medium text-primary hover:underline"
                  >
                    #{o.number}
                  </Link>
                </td>
                <td className="max-w-104 truncate px-3 py-2 text-foreground" title={o.subject}>{o.subject}</td>
                <td className="max-w-48 truncate px-3 py-2 text-muted-foreground" title={o.company ?? undefined}>
                  {o.company ?? "—"}
                </td>
                <td className="whitespace-nowrap px-3 py-2">
                  <span className="rounded px-2 py-0.5 text-[11px] font-medium" style={colorPillStyle(o.statusColor || "#6b7280", theme)}>
                    {o.statusName}
                  </span>
                </td>
                <td className="whitespace-nowrap px-3 py-2 text-xs tabular-nums text-muted-foreground">{fmt(o.openedUtc)}</td>
                <td className="whitespace-nowrap px-3 py-2 text-xs tabular-nums text-muted-foreground">
                  {fmt(o.closedUtc)}
                  {CLOSE_REASON_LABEL[o.closeReason] && (
                    <span className="ml-2 rounded bg-glass px-1.5 py-0.5 text-[10px] uppercase tracking-wide text-muted-foreground">
                      {CLOSE_REASON_LABEL[o.closeReason]}
                    </span>
                  )}
                </td>
                <td className="whitespace-nowrap px-5 py-2 text-right tabular-nums text-foreground">
                  {formatDuration(o.durationSeconds)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <Pager
        page={page}
        pageSize={pageSize}
        total={total}
        onPage={(p) => goTo(p, Math.max(1, Math.ceil(total / pageSize)))}
        onPageSize={changePageSize}
      />
    </>
  );
}

function ListSkeleton() {
  return (
    <div className="space-y-2 p-5">
      {[0, 1, 2].map((i) => <div key={i} className="h-9 animate-pulse rounded-md bg-glass" />)}
    </div>
  );
}

function Pager({
  page,
  pageSize,
  total,
  onPage,
  onPageSize,
}: {
  page: number;
  pageSize: number;
  total: number;
  onPage: (page: number) => void;
  onPageSize: (size: number) => void;
}) {
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const rangeStart = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const rangeEnd = Math.min(page * pageSize, total);
  return (
    <div className="flex flex-wrap items-center justify-between gap-3 border-t border-glass px-5 py-2.5 text-xs text-muted-foreground">
      <div className="flex items-center gap-3">
        <span className="tabular-nums">
          Showing {nf.format(rangeStart)}–{nf.format(rangeEnd)} of {nf.format(total)}
        </span>
        <span className="flex items-center gap-1.5">
          <span className="text-[10px] uppercase tracking-wider">Per page</span>
          <Select value={String(pageSize)} onValueChange={(v) => onPageSize(Number(v))}>
            <SelectTrigger className="h-7 w-17 text-xs">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {PAGE_SIZES.map((n) => (
                <SelectItem key={n} value={String(n)}>{n}</SelectItem>
              ))}
            </SelectContent>
          </Select>
        </span>
      </div>
      <div className="flex items-center gap-1">
        <PagerButton label="First page" onClick={() => onPage(1)} disabled={page <= 1}>
          <ChevronsLeft className="h-3.5 w-3.5" />
        </PagerButton>
        <PagerButton label="Previous page" onClick={() => onPage(page - 1)} disabled={page <= 1}>
          <ChevronLeft className="h-3.5 w-3.5" />
        </PagerButton>
        <span className="min-w-20 text-center font-mono text-foreground/90">{page} / {totalPages}</span>
        <PagerButton label="Next page" onClick={() => onPage(page + 1)} disabled={page >= totalPages}>
          <ChevronRight className="h-3.5 w-3.5" />
        </PagerButton>
        <PagerButton label="Last page" onClick={() => onPage(totalPages)} disabled={page >= totalPages}>
          <ChevronsRight className="h-3.5 w-3.5" />
        </PagerButton>
      </div>
    </div>
  );
}

const SORT_COLUMNS: ReadonlyArray<{ sort: AgentTicketSort; label: string; hint: string }> = [
  { sort: "period", label: "In period", hint: "Time this agent logged on the ticket in the period" },
  { sort: "agent", label: "Agent total", hint: "All time this agent ever logged on the ticket" },
  { sort: "ticket", label: "Ticket total", hint: "All time logged on the ticket, every agent together" },
];

function AgentTickets({
  agentId,
  params,
  sort,
  onSort,
}: {
  agentId: string;
  params: ReportParams;
  sort: AgentTicketSort;
  onSort: (s: AgentTicketSort) => void;
}) {
  const theme = useTheme();
  const { time } = useServerTime();
  const offsetMinutes = time?.offsetMinutes ?? 0;

  // A new period / sort / page size starts again at page 1.
  const { page, pageSize, changePageSize, goTo } = usePaging([agentId, params, sort]);

  const query = useQuery({
    queryKey: ["insights", "agents", "list", agentId, params, sort, page, pageSize],
    queryFn: () => insightsApi.agentTickets(params, agentId, sort, (page - 1) * pageSize, pageSize),
    placeholderData: keepPreviousData,
    staleTime: 30_000,
    retry: false,
  });

  const items = query.data?.items ?? [];
  const total = query.data?.total ?? 0;

  function lastActivity(t: AgentTicketItem): string {
    if (t.lastActionUtc) {
      const local = time ? toServerLocal(t.lastActionUtc, offsetMinutes) : "…";
      // A later time-only day wins over an earlier action.
      if (!t.lastEntryDate || t.lastEntryDate <= t.lastActionUtc.slice(0, 10)) return local;
    }
    return t.lastEntryDate ? fmtDate(t.lastEntryDate, true) : "—";
  }

  if (query.isLoading) return <ListSkeleton />;
  if (query.isError) {
    return <p className="p-5 text-sm text-muted-foreground">The ticket list could not be loaded.</p>;
  }
  if (items.length === 0) {
    return <p className="p-5 text-sm text-muted-foreground">No tickets for this agent in this period.</p>;
  }

  return (
    <>
      <div className={cn("mt-2 overflow-x-auto transition-opacity", query.isPlaceholderData && "opacity-60")}>
        <table className="w-full border-collapse text-sm">
          <thead>
            <tr className="border-b border-glass text-left text-[11px] uppercase tracking-[0.06em] text-muted-foreground">
              <th scope="col" className="px-5 py-2 font-medium">Ticket</th>
              <th scope="col" className="px-3 py-2 font-medium">Subject</th>
              <th scope="col" className="px-3 py-2 font-medium">Company</th>
              <th scope="col" className="px-3 py-2 font-medium">Status</th>
              <SortTh label="Last activity" active={sort === "recent"} onClick={() => onSort("recent")} align="left" />
              {SORT_COLUMNS.map((c) => (
                <SortTh key={c.sort} label={c.label} hint={c.hint} active={sort === c.sort} onClick={() => onSort(c.sort)} />
              ))}
            </tr>
          </thead>
          <tbody>
            {items.map((t) => (
              <tr key={t.id} className="border-b border-glass last:border-0 hover:bg-glass-hover">
                <td className="whitespace-nowrap px-5 py-2">
                  <Link
                    to={"/tickets/$ticketId" as never}
                    params={{ ticketId: t.id } as never}
                    className="font-mono text-xs font-medium text-primary hover:underline"
                  >
                    #{t.number}
                  </Link>
                </td>
                <td className="max-w-104 truncate px-3 py-2 text-foreground" title={t.subject}>{t.subject}</td>
                <td className="max-w-48 truncate px-3 py-2 text-muted-foreground" title={t.company ?? undefined}>
                  {t.company ?? "—"}
                </td>
                <td className="whitespace-nowrap px-3 py-2">
                  <span className="rounded px-2 py-0.5 text-[11px] font-medium" style={colorPillStyle(t.statusColor || "#6b7280", theme)}>
                    {t.statusName}
                  </span>
                </td>
                <td className="whitespace-nowrap px-3 py-2 text-xs tabular-nums text-muted-foreground">{lastActivity(t)}</td>
                <MinutesTd value={t.periodMinutes} strong />
                <MinutesTd value={t.agentMinutes} />
                <MinutesTd value={t.ticketMinutes} muted last />
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <Pager
        page={page}
        pageSize={pageSize}
        total={total}
        onPage={(p) => goTo(p, Math.max(1, Math.ceil(total / pageSize)))}
        onPageSize={changePageSize}
      />
    </>
  );
}

function SortTh({
  label,
  hint,
  active,
  onClick,
  align = "right",
}: {
  label: string;
  hint?: string;
  active: boolean;
  onClick: () => void;
  align?: "left" | "right";
}) {
  return (
    <th
      scope="col"
      aria-sort={active ? "descending" : "none"}
      className={cn("px-3 py-2 font-medium last:pr-5", align === "right" ? "text-right" : "text-left")}
    >
      <button
        type="button"
        onClick={onClick}
        title={hint}
        className={cn(
          "inline-flex items-center gap-1 uppercase tracking-[0.06em] transition-colors hover:text-foreground",
          active && "text-foreground",
        )}
      >
        {label}
        {active && <ArrowDown className="h-3 w-3" />}
      </button>
    </th>
  );
}

function MinutesTd({ value, strong, muted, last }: { value: number; strong?: boolean; muted?: boolean; last?: boolean }) {
  return (
    <td
      className={cn(
        "whitespace-nowrap px-3 py-2 text-right tabular-nums",
        last && "pr-5",
        strong ? "font-medium text-foreground" : muted ? "text-muted-foreground" : "text-foreground/90",
      )}
    >
      {value === 0 ? <span className="text-muted-foreground/50">·</span> : formatMinutes(value)}
    </td>
  );
}

function PagerButton({
  label,
  onClick,
  disabled,
  children,
}: {
  label: string;
  onClick: () => void;
  disabled: boolean;
  children: React.ReactNode;
}) {
  return (
    <Button size="sm" variant="ghost" className="h-7 w-7 p-0" onClick={onClick} disabled={disabled} aria-label={label} title={label}>
      {children}
    </Button>
  );
}
