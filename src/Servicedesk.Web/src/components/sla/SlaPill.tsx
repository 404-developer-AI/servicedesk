import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, CheckCircle2, Clock, PauseCircle } from "lucide-react";
import { slaApi, type TicketSlaState } from "@/lib/api";
import { useServerTime } from "@/hooks/useServerTime";
import { cn } from "@/lib/utils";

type Props = {
  ticketId: string;
  className?: string;
};

function formatDuration(ms: number): string {
  const abs = Math.abs(ms);
  const mins = Math.floor(abs / 60000);
  if (mins < 60) return `${mins}m`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ${mins % 60}m`;
  const days = Math.floor(hours / 24);
  return `${days}d ${hours % 24}h`;
}

function pillClasses(remainingMs: number | null, met: boolean, metLate: boolean, paused: boolean) {
  if (met && metLate) return "border-red-500/30 bg-red-500/10 text-red-200";
  if (met) return "border-emerald-500/30 bg-emerald-500/10 text-emerald-200";
  if (paused) return "border-sky-500/30 bg-sky-500/10 text-sky-200";
  if (remainingMs === null) return "border-white/10 bg-white/[0.04] text-muted-foreground";
  if (remainingMs < 0) return "border-red-500/40 bg-red-500/10 text-red-200";
  if (remainingMs < 15 * 60 * 1000) return "border-amber-500/40 bg-amber-500/10 text-amber-200";
  return "border-emerald-500/20 bg-emerald-500/[0.06] text-emerald-100/80";
}

export function SlaPill({ ticketId, className }: Props) {
  const { time } = useServerTime();
  const { data, isLoading } = useQuery({
    queryKey: ["sla", "ticket", ticketId],
    queryFn: () => slaApi.ticketState(ticketId),
    refetchInterval: 60_000,
  });

  if (isLoading || !data || !time) return null;
  const state = data as TicketSlaState;
  const nowMs = new Date(time.utc).getTime();

  return (
    <div className={cn("flex flex-wrap items-center gap-2 text-xs", className)}>
      <Row label="First response" nowMs={nowMs} deadline={state.firstResponseDeadlineUtc} met={state.firstResponseMetUtc} businessMinutes={state.firstResponseBusinessMinutes} paused={state.isPaused} />
      <Row label="Resolution" nowMs={nowMs} deadline={state.resolutionDeadlineUtc} met={state.resolutionMetUtc} businessMinutes={state.resolutionBusinessMinutes} paused={state.isPaused} />
      {state.isPaused && (
        <span className="flex items-center gap-1 rounded-md border border-sky-500/30 bg-sky-500/10 px-2 py-1 text-sky-200">
          <PauseCircle className="h-3 w-3" /> Paused (waiting)
        </span>
      )}
    </div>
  );
}

function formatBusinessMinutes(m: number): string {
  if (m < 60) return `${m}m`;
  const h = m / 60;
  if (h < 24) return `${h.toFixed(1)}h`;
  return `${(h / 24).toFixed(1)}d`;
}

function Row({
  label,
  nowMs,
  deadline,
  met,
  businessMinutes,
  paused,
}: {
  label: string;
  nowMs: number;
  deadline: string | null;
  met: string | null;
  businessMinutes: number | null;
  paused: boolean;
}) {
  if (!deadline) return null;
  const deadlineMs = new Date(deadline).getTime();
  const remainingMs = deadlineMs - nowMs;
  const isMet = met !== null;
  const metLate = isMet && new Date(met).getTime() > deadlineMs;
  const durationLabel = businessMinutes !== null ? formatBusinessMinutes(businessMinutes) : null;
  return (
    <span
      className={cn(
        "flex items-center gap-1 rounded-md border px-2 py-1",
        pillClasses(isMet || paused ? null : remainingMs, isMet, metLate, paused),
      )}
    >
      {isMet && metLate ? <AlertTriangle className="h-3 w-3" /> : isMet ? <CheckCircle2 className="h-3 w-3" /> : remainingMs < 0 ? <AlertTriangle className="h-3 w-3" /> : <Clock className="h-3 w-3" />}
      <span className="font-medium">{label}:</span>
      {isMet
        ? metLate
          ? `late (${durationLabel ?? "late"})`
          : `met (${durationLabel ?? "met"})`
        : paused
          ? "paused"
          : remainingMs < 0
            ? `${formatDuration(remainingMs)} overdue`
            : `${formatDuration(remainingMs)} left`}
    </span>
  );
}
