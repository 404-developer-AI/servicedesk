import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, Clock, AlertCircle, Ban, Check, Layers } from "lucide-react";
import { cn } from "@/lib/utils";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import { useTicketTimesheetRealtime } from "@/hooks/useTimesheetRealtime";
import {
  timesheetTicketApi,
  formatHHMM,
  formatDuration,
  type TimesheetEntry,
} from "@/lib/timesheet-api";
import { timeAlertQueryKey } from "./TicketTimeAlertDialog";

type Props = {
  ticketId: string;
  /// The ticket's current queue — part of the alert query key so the
  /// remaining-time badge re-resolves when the queue changes.
  queueId: string | null | undefined;
};

/// v0.0.35-F — collapsible panel that lists every timesheet entry linked
/// to this ticket (across all agents). Collapsed by default so it does
/// not push the activity feed down on tickets that have no entries yet.
export function TicketTimesheetPanel({ ticketId, queueId }: Props) {
  const [open, setOpen] = React.useState(false);

  // v0.0.35 commit H — live-refresh when another agent saves time against
  // this ticket. Piggybacks on the ticket:{id} SignalR group that the
  // ticket-detail page already joined via useViewingTicket.
  useTicketTimesheetRealtime(ticketId);

  const { data, isLoading, isError } = useQuery({
    queryKey: ["timesheet", "ticket", ticketId],
    queryFn: () => timesheetTicketApi.list(ticketId),
    staleTime: 30_000,
  });

  // v0.0.87 — hour-limit snapshot (shares the cache with the warning dialog).
  // Only used to surface "time remaining before the limit" in the header.
  const { data: alert } = useQuery({
    queryKey: timeAlertQueryKey(ticketId, queueId),
    queryFn: () => timesheetTicketApi.timeAlert(ticketId),
    staleTime: 15_000,
  });

  const items = data?.items ?? [];
  const totalMinutes = data?.totalMinutes ?? 0;
  const count = items.length;

  const byTask = React.useMemo(() => {
    const map = new Map<
      string,
      { taskId: string; name: string; isAbsence: boolean; minutes: number }
    >();
    for (const e of items) {
      const prev = map.get(e.taskId);
      if (prev) {
        prev.minutes += e.minutes;
      } else {
        map.set(e.taskId, {
          taskId: e.taskId,
          name: e.taskName,
          isAbsence: e.taskIsAbsence,
          minutes: e.minutes,
        });
      }
    }
    return [...map.values()].sort((a, b) => b.minutes - a.minutes);
  }, [items]);

  return (
    <div className="glass-panel overflow-hidden">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="w-full flex items-center gap-3 px-3 py-2 glass-hover focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        aria-expanded={open}
      >
        <Clock className="h-4 w-4 shrink-0 text-violet-300/80" />
        <span className="text-xs uppercase tracking-wider text-muted-foreground shrink-0">
          Time logged
        </span>
        <div className="flex min-w-0 flex-1 items-center gap-1.5 justify-end">
          {isLoading ? (
            <span className="text-xs text-muted-foreground/60">Loading…</span>
          ) : isError ? (
            <span className="inline-flex items-center gap-1 text-xs text-amber-300/80">
              <AlertCircle className="h-3.5 w-3.5" />
              Failed to load
            </span>
          ) : count === 0 ? (
            <span className="text-xs text-muted-foreground/60">
              No entries yet
            </span>
          ) : (
            <>
              {/* Per-task breakdown. Collapses to a single "N tasks" pill
                  (with a hover tooltip listing each task) when there isn't
                  room to show every badge, so nothing silently disappears.
                  The total + remaining stay pinned (shrink-0). */}
              <TaskBreakdownBadges byTask={byTask} />
              <span className="inline-flex shrink-0 items-center rounded-md border border-violet-400/30 bg-violet-400/10 px-2 py-0.5 text-xs font-medium text-violet-200">
                {formatDuration(totalMinutes)}
              </span>
            </>
          )}
          {alert?.enabled && <RemainingPill alert={alert} />}
          {alert?.trackingDisabled && <TrackingDisabledPill />}
          <ChevronDown
            className={cn(
              "h-4 w-4 shrink-0 text-muted-foreground/60 transition-transform",
              open && "rotate-180",
            )}
          />
        </div>
      </button>

      {open && (
        <div className="border-t border-glass">
          {isLoading && (
            <div className="px-3 py-3 text-xs text-muted-foreground">
              Loading time entries…
            </div>
          )}
          {isError && (
            <div className="px-3 py-3 text-xs text-amber-300/80">
              Could not load time entries for this ticket.
            </div>
          )}
          {!isLoading && !isError && count === 0 && (
            <div className="px-3 py-3 text-xs text-muted-foreground/70">
              No timesheet entries linked to this ticket.
            </div>
          )}
          {!isLoading && !isError && count > 0 && (
            <EntriesGrid items={items} totalMinutes={totalMinutes} />
          )}
        </div>
      )}
    </div>
  );
}

type TaskAgg = {
  taskId: string;
  name: string;
  isAbsence: boolean;
  minutes: number;
};

function TaskPill({ t }: { t: TaskAgg }) {
  return (
    <span
      className={cn(
        "inline-flex max-w-[10rem] shrink-0 items-center gap-1.5 rounded-md border px-2 py-0.5 text-[11px]",
        t.isAbsence
          ? "border-amber-400/30 bg-amber-400/10 text-amber-200"
          : "border-glass bg-glass text-foreground/80",
      )}
      title={`${t.name} — ${formatDuration(t.minutes)}`}
    >
      <span className="truncate text-muted-foreground/80">{t.name}</span>
      <span className="font-mono font-medium tabular-nums shrink-0">
        {formatDuration(t.minutes)}
      </span>
    </span>
  );
}

/// Per-task breakdown for the collapsed header. Measures the full set of
/// badges against the space actually available and, when they don't all fit,
/// collapses them into a single "N tasks" pill whose tooltip lists every task
/// and its duration — so a crowded ticket never silently drops a badge.
function TaskBreakdownBadges({ byTask }: { byTask: TaskAgg[] }) {
  const containerRef = React.useRef<HTMLDivElement>(null);
  const measureRef = React.useRef<HTMLDivElement>(null);
  const [collapsed, setCollapsed] = React.useState(false);

  React.useLayoutEffect(() => {
    const container = containerRef.current;
    const measure = measureRef.current;
    if (!container || !measure) return;
    // The measurer renders the full breakdown off-layout, so its width is the
    // natural width regardless of the collapsed state — the decision can never
    // oscillate. +1 absorbs sub-pixel rounding.
    const check = () =>
      setCollapsed(measure.offsetWidth > container.clientWidth + 1);
    check();
    const ro = new ResizeObserver(check);
    ro.observe(container);
    return () => ro.disconnect();
  }, [byTask]);

  const count = byTask.length;

  return (
    <div
      ref={containerRef}
      className="relative flex min-w-0 flex-1 items-center justify-end gap-1.5 overflow-hidden"
    >
      {/* Off-layout natural-width measurer (never visible). */}
      <div
        ref={measureRef}
        aria-hidden
        className="pointer-events-none absolute right-0 flex items-center gap-1.5 opacity-0"
      >
        {byTask.map((t) => (
          <TaskPill key={t.taskId} t={t} />
        ))}
      </div>

      {collapsed ? (
        <TooltipProvider delayDuration={150}>
          <Tooltip>
            <TooltipTrigger asChild>
              <span className="inline-flex shrink-0 cursor-default items-center gap-1.5 rounded-md border border-glass bg-glass px-2 py-0.5 text-[11px] text-foreground/80">
                <Layers className="h-3 w-3 text-muted-foreground/80" />
                {count} {count === 1 ? "task" : "tasks"}
              </span>
            </TooltipTrigger>
            <TooltipContent className="max-w-xs border border-glass bg-glass-strong text-foreground">
              <div className="flex flex-col gap-1">
                {byTask.map((t) => (
                  <div
                    key={t.taskId}
                    className="flex items-center justify-between gap-4 text-[11px]"
                  >
                    <span className={cn(t.isAbsence && "text-amber-200")}>
                      {t.name}
                    </span>
                    <span className="font-mono tabular-nums text-muted-foreground/90">
                      {formatDuration(t.minutes)}
                    </span>
                  </div>
                ))}
              </div>
            </TooltipContent>
          </Tooltip>
        </TooltipProvider>
      ) : (
        byTask.map((t) => <TaskPill key={t.taskId} t={t} />)
      )}
    </div>
  );
}

/// v0.0.87 — shows how much time may still be logged on this ticket before
/// its effective limit is reached, or by how much it is already over. Server
/// computes the limit; this only renders the snapshot.
function RemainingPill({
  alert,
}: {
  alert: { exceeded: boolean; remainingMinutes: number; limitMinutes: number };
}) {
  const over = alert.exceeded || alert.remainingMinutes < 0;
  const magnitude = Math.abs(alert.remainingMinutes);
  return (
    <span
      className={cn(
        "inline-flex shrink-0 items-center gap-1 rounded-md border px-2 py-0.5 text-[11px] font-medium",
        over
          ? "border-amber-400/40 bg-amber-400/10 text-amber-200"
          : "border-emerald-400/30 bg-emerald-400/10 text-emerald-200",
      )}
      title={`Limit ${formatDuration(alert.limitMinutes)}`}
    >
      {over ? (
        <>
          <AlertCircle className="h-3 w-3" />
          {formatDuration(magnitude)} over limit
        </>
      ) : (
        <>{formatDuration(magnitude)} left</>
      )}
    </span>
  );
}

/// v0.0.88 — shown in place of the remaining-time pill once an agent has
/// disabled hour tracking for this ticket. The alert never fires here, so the
/// pill makes the (otherwise invisible) disabled state explicit.
function TrackingDisabledPill() {
  return (
    <span
      className="inline-flex shrink-0 items-center gap-1 rounded-md border border-muted-foreground/20 bg-muted-foreground/10 px-2 py-0.5 text-[11px] font-medium text-muted-foreground"
      title="Hour tracking has been disabled for this ticket"
    >
      <Ban className="h-3 w-3" />
      Tracking off
    </span>
  );
}

function EntriesGrid({
  items,
  totalMinutes,
}: {
  items: TimesheetEntry[];
  totalMinutes: number;
}) {
  return (
    <div className="max-h-[320px] overflow-auto">
      <table className="w-full min-w-[800px] text-xs">
        <thead className="sticky top-0 bg-background/95 backdrop-blur-sm">
          <tr className="text-left text-muted-foreground">
            <th className="px-3 py-2 font-medium">Agent</th>
            <th className="px-3 py-2 font-medium">Date</th>
            <th className="px-3 py-2 font-medium">Start</th>
            <th className="px-3 py-2 font-medium">End</th>
            <th className="px-3 py-2 font-medium">Task</th>
            <th className="px-3 py-2 font-medium">Description</th>
            <th className="px-3 py-2 font-medium">Billed</th>
            <th className="px-3 py-2 font-medium text-right">Duration</th>
          </tr>
        </thead>
        <tbody>
          {items.map((entry) => (
            <tr
              key={entry.id}
              className="border-t border-glass hover:bg-glass-hover"
            >
              <td className="px-3 py-1.5 text-muted-foreground/90">
                {entry.userEmail || "—"}
              </td>
              <td className="px-3 py-1.5 font-mono text-muted-foreground">
                {entry.entryDate.slice(0, 10)}
              </td>
              <td className="px-3 py-1.5 font-mono">
                {formatHHMM(entry.startMinutes)}
              </td>
              <td className="px-3 py-1.5 font-mono">
                {formatHHMM(entry.endMinutes)}
              </td>
              <td className="px-3 py-1.5">
                <span
                  className={cn(
                    "inline-flex items-center rounded-md border px-1.5 py-0.5 text-[10px] font-medium",
                    entry.taskIsAbsence
                      ? "border-amber-400/30 bg-amber-400/10 text-amber-200"
                      : "border-glass bg-glass text-muted-foreground",
                  )}
                >
                  {entry.taskName}
                </span>
              </td>
              <td className="px-3 py-1.5 max-w-[280px] truncate text-foreground/80">
                {entry.description}
              </td>
              <td className="px-3 py-1.5">
                <BilledPill invoiced={entry.invoiced} />
              </td>
              <td className="px-3 py-1.5 text-right font-mono tabular-nums">
                {formatDuration(entry.minutes)}
              </td>
            </tr>
          ))}
        </tbody>
        <tfoot className="sticky bottom-0 bg-background/95 backdrop-blur-sm">
          <tr className="border-t border-glass">
            <td
              colSpan={7}
              className="px-3 py-2 text-right text-muted-foreground"
            >
              Total
            </td>
            <td className="px-3 py-2 text-right font-mono font-medium text-violet-200">
              {formatDuration(totalMinutes)}
            </td>
          </tr>
        </tfoot>
      </table>
    </div>
  );
}

function BilledPill({ invoiced }: { invoiced: boolean }) {
  if (invoiced) {
    return (
      <span
        className="inline-flex items-center gap-1 rounded-md border border-emerald-400/30 bg-emerald-400/10 px-1.5 py-0.5 text-[10px] font-medium text-emerald-200"
        title="Billed"
      >
        <Check className="h-3 w-3" />
        Billed
      </span>
    );
  }
  return (
    <span
      className="inline-flex items-center rounded-md border border-glass-strong bg-glass px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground/70"
      title="Not billed yet"
    >
      —
    </span>
  );
}
