import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, Clock, AlertCircle, Check } from "lucide-react";
import { cn } from "@/lib/utils";
import { useTicketTimesheetRealtime } from "@/hooks/useTimesheetRealtime";
import {
  timesheetTicketApi,
  formatHHMM,
  formatDuration,
  type TimesheetEntry,
} from "@/lib/timesheet-api";

type Props = {
  ticketId: string;
};

/// v0.0.35-F — collapsible panel that lists every timesheet entry linked
/// to this ticket (across all agents). Collapsed by default so it does
/// not push the activity feed down on tickets that have no entries yet.
export function TicketTimesheetPanel({ ticketId }: Props) {
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
              {byTask.map((t) => (
                <span
                  key={t.taskId}
                  className={cn(
                    "inline-flex max-w-[10rem] items-center gap-1.5 rounded-md border px-2 py-0.5 text-[11px]",
                    t.isAbsence
                      ? "border-amber-400/30 bg-amber-400/10 text-amber-200"
                      : "border-white/10 bg-white/[0.04] text-foreground/80",
                  )}
                  title={`${t.name} — ${formatDuration(t.minutes)}`}
                >
                  <span className="truncate text-muted-foreground/80">
                    {t.name}
                  </span>
                  <span className="font-mono font-medium tabular-nums shrink-0">
                    {formatDuration(t.minutes)}
                  </span>
                </span>
              ))}
              <span className="inline-flex shrink-0 items-center rounded-md border border-violet-400/30 bg-violet-400/10 px-2 py-0.5 text-xs font-medium text-violet-200">
                {formatDuration(totalMinutes)}
              </span>
            </>
          )}
          <ChevronDown
            className={cn(
              "h-4 w-4 shrink-0 text-muted-foreground/60 transition-transform",
              open && "rotate-180",
            )}
          />
        </div>
      </button>

      {open && (
        <div className="border-t border-white/10">
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
              className="border-t border-white/[0.05] hover:bg-white/[0.02]"
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
                      : "border-white/10 bg-white/[0.04] text-muted-foreground",
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
          <tr className="border-t border-white/10">
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
      className="inline-flex items-center rounded-md border border-white/[0.08] bg-white/[0.02] px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground/70"
      title="Not billed yet"
    >
      —
    </span>
  );
}
