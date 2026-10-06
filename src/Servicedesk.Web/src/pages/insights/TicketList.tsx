import * as React from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { useTheme } from "@/app/ThemeProvider";
import { useServerTime, toServerLocal } from "@/hooks/useServerTime";
import { colorPillStyle } from "@/lib/colorPill";
import { insightsApi, type InsightsReportKind, type ReportParams } from "@/lib/insights-api";
import { cn } from "@/lib/utils";
import { nf } from "./insightsFormat";

const PAGE_SIZES = [10, 25, 50, 100] as const;
const DEFAULT_PAGE_SIZE = 25;
// Per-viewer convenience: remember the chosen page size.
const PAGE_SIZE_KEY = "sd-insights-ticket-page-size";

function readPageSize(): number {
  try {
    const n = Number(window.localStorage.getItem(PAGE_SIZE_KEY));
    return (PAGE_SIZES as readonly number[]).includes(n) ? n : DEFAULT_PAGE_SIZE;
  } catch {
    return DEFAULT_PAGE_SIZE;
  }
}

/// The tickets behind a report's numbers — same period, outcome filter and
/// ticked queues, newest first (by the moment they were counted on). Server-side access scoping applies exactly as
/// for the counts; the ticket number opens the ticket.
export function TicketList({
  kind,
  params,
  queueIds,
  disabled,
}: {
  kind: InsightsReportKind;
  params: ReportParams;
  queueIds: string[];
  disabled: boolean;
}) {
  const theme = useTheme();
  const { time } = useServerTime();
  const offsetMinutes = time?.offsetMinutes ?? 0;

  const [pageSize, setPageSize] = React.useState(readPageSize);
  // The page belongs to one filter: a new period / queue selection / page
  // size starts again at page 1 without an effect-driven reset.
  const filterKey = JSON.stringify([kind, params, queueIds, pageSize]);
  const [pager, setPager] = React.useState({ key: filterKey, page: 1 });
  const page = pager.key === filterKey ? pager.page : 1;

  const query = useQuery({
    queryKey: ["insights", kind, "list", params, queueIds, page, pageSize],
    queryFn: () => insightsApi.tickets(kind, params, queueIds, (page - 1) * pageSize, pageSize),
    placeholderData: keepPreviousData,
    enabled: !disabled,
    staleTime: 30_000,
    retry: false,
  });

  const items = query.data?.items ?? [];
  const total = query.data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const rangeStart = total === 0 ? 0 : (page - 1) * pageSize + 1;
  const rangeEnd = Math.min(page * pageSize, total);

  function goTo(next: number) {
    setPager({ key: filterKey, page: Math.min(Math.max(1, next), totalPages) });
  }

  function changePageSize(next: number) {
    try {
      window.localStorage.setItem(PAGE_SIZE_KEY, String(next));
    } catch {
      // storage unavailable - the choice still applies for this visit
    }
    setPageSize(next);
  }

  return (
    <div className="glass-card overflow-hidden">
      <div className="flex items-baseline justify-between gap-3 px-5 pt-5 pb-3">
        <h2 className="text-base font-semibold text-foreground">Tickets</h2>
        {!disabled && query.data && (
          <span className="text-xs text-muted-foreground">
            {nf.format(total)} ticket{total === 1 ? "" : "s"}
          </span>
        )}
      </div>

      {disabled ? (
        <p className="px-5 pb-5 text-sm text-muted-foreground">All queues are switched off.</p>
      ) : query.isLoading ? (
        <div className="space-y-2 px-5 pb-5">
          {[0, 1, 2].map((i) => (
            <div key={i} className="h-9 animate-pulse rounded-md bg-glass" />
          ))}
        </div>
      ) : query.isError ? (
        <p className="px-5 pb-5 text-sm text-muted-foreground">The ticket list could not be loaded.</p>
      ) : items.length === 0 ? (
        <p className="px-5 pb-5 text-sm text-muted-foreground">No tickets in this period.</p>
      ) : (
        <>
          <div className={cn("overflow-x-auto transition-opacity", query.isPlaceholderData && "opacity-60")}>
            <table className="w-full border-collapse text-sm">
              <thead>
                <tr className="border-b border-glass text-left text-[11px] uppercase tracking-[0.06em] text-muted-foreground">
                  <th scope="col" className="px-5 py-2 font-medium">Ticket</th>
                  <th scope="col" className="px-3 py-2 font-medium">Subject</th>
                  <th scope="col" className="px-3 py-2 font-medium">Requester</th>
                  <th scope="col" className="px-3 py-2 font-medium">Company</th>
                  <th scope="col" className="px-3 py-2 font-medium">Status</th>
                  <th scope="col" className="px-5 py-2 text-right font-medium">
                    {kind === "new-tickets" ? "Created" : "Closed"}
                  </th>
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
                    <td className="max-w-md truncate px-3 py-2 text-foreground" title={t.subject}>
                      {t.subject}
                    </td>
                    <td className="max-w-56 truncate px-3 py-2 text-muted-foreground" title={t.requester}>
                      {t.requester || "—"}
                    </td>
                    <td className="max-w-56 truncate px-3 py-2 text-muted-foreground" title={t.company ?? undefined}>
                      {t.company ?? "—"}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2">
                      <span
                        className="rounded px-2 py-0.5 text-[11px] font-medium"
                        style={colorPillStyle(t.statusColor || "#6b7280", theme)}
                      >
                        {t.statusName}
                      </span>
                    </td>
                    <td className="whitespace-nowrap px-5 py-2 text-right text-xs tabular-nums text-muted-foreground">
                      {time ? toServerLocal(t.momentUtc, offsetMinutes) : "…"}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="flex flex-wrap items-center justify-between gap-3 border-t border-glass px-5 py-2.5 text-xs text-muted-foreground">
            <div className="flex items-center gap-3">
              <span className="tabular-nums">
                Showing {nf.format(rangeStart)}–{nf.format(rangeEnd)} of {nf.format(total)}
              </span>
              <span className="flex items-center gap-1.5">
                <span className="text-[10px] uppercase tracking-wider">Per page</span>
                <Select value={String(pageSize)} onValueChange={(v) => changePageSize(Number(v))}>
                  <SelectTrigger className="h-7 w-17 text-xs">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {PAGE_SIZES.map((n) => (
                      <SelectItem key={n} value={String(n)}>
                        {n}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </span>
            </div>
            <div className="flex items-center gap-1">
              <PagerButton label="First page" onClick={() => goTo(1)} disabled={page <= 1}>
                <ChevronsLeft className="h-3.5 w-3.5" />
              </PagerButton>
              <PagerButton label="Previous page" onClick={() => goTo(page - 1)} disabled={page <= 1}>
                <ChevronLeft className="h-3.5 w-3.5" />
              </PagerButton>
              <span className="min-w-20 text-center font-mono text-foreground/90">
                {page} / {totalPages}
              </span>
              <PagerButton label="Next page" onClick={() => goTo(page + 1)} disabled={page >= totalPages}>
                <ChevronRight className="h-3.5 w-3.5" />
              </PagerButton>
              <PagerButton label="Last page" onClick={() => goTo(totalPages)} disabled={page >= totalPages}>
                <ChevronsRight className="h-3.5 w-3.5" />
              </PagerButton>
            </div>
          </div>
        </>
      )}
    </div>
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
    <Button
      size="sm"
      variant="ghost"
      className="h-7 w-7 p-0"
      onClick={onClick}
      disabled={disabled}
      aria-label={label}
      title={label}
    >
      {children}
    </Button>
  );
}
