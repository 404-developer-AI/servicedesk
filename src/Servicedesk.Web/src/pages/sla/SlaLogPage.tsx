import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { Filter, Search, Timer } from "lucide-react";
import { slaApi, taxonomyApi, type SlaLogItem } from "@/lib/api";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";

// Radix Select rejects empty-string values, so each filter uses its own
// sentinel for the "All …" row. The handler strips it back to "" so the
// query params stay unchanged.
const ALL_QUEUES = "__all_queues__";
const ALL_PRIORITIES = "__all_priorities__";
const ALL_STATUSES = "__all_statuses__";

function formatMinutes(m: number | null): string {
  if (m === null) return "—";
  if (m < 60) return `${m}m`;
  const h = m / 60;
  if (h < 24) return `${h.toFixed(1)}h`;
  return `${(h / 24).toFixed(1)}d`;
}

function SlaCell({
  consumed,
  target,
  deadline,
  breached,
}: {
  consumed: number | null;
  target: number | null;
  deadline: string | null;
  breached: boolean;
}) {
  if (target === null) return <span className="text-muted-foreground/40">No policy</span>;

  const deadlineLabel = deadline
    ? new Date(deadline).toLocaleString(undefined, { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" })
    : null;

  if (consumed !== null) {
    const pct = target > 0 ? Math.round((consumed / target) * 100) : 0;
    return (
      <span className={breached ? "text-red-300" : "text-emerald-300"}>
        {formatMinutes(consumed)}
        <span className="text-muted-foreground/60"> / {formatMinutes(target)}</span>
        <span className={`ml-1 text-[10px] ${breached ? "text-red-400/80" : "text-muted-foreground/40"}`}>
          ({pct}%)
        </span>
      </span>
    );
  }

  return (
    <span className="text-muted-foreground/60">
      <span className="text-muted-foreground/40">target </span>
      {formatMinutes(target)}
      {deadlineLabel && (
        <span className="ml-1 text-[10px] text-muted-foreground/30">({deadlineLabel})</span>
      )}
    </span>
  );
}

export function SlaLogPage() {
  const [queueId, setQueueId] = useState<string>("");
  const [priorityId, setPriorityId] = useState<string>("");
  const [statusId, setStatusId] = useState<string>("");
  const [breachedOnly, setBreachedOnly] = useState(false);
  const [fromDate, setFromDate] = useState("");
  const [toDate, setToDate] = useState("");
  const [search, setSearch] = useState("");

  const queues = useQuery({ queryKey: ["taxonomy", "queues"], queryFn: () => taxonomyApi.queues.list() });
  const priorities = useQuery({ queryKey: ["taxonomy", "priorities"], queryFn: () => taxonomyApi.priorities.list() });
  const statuses = useQuery({ queryKey: ["taxonomy", "statuses"], queryFn: () => taxonomyApi.statuses.list() });

  const log = useQuery({
    queryKey: ["sla", "log", queueId, priorityId, statusId, breachedOnly, fromDate, toDate, search],
    queryFn: () =>
      slaApi.log({
        queueId: queueId || undefined,
        priorityId: priorityId || undefined,
        statusId: statusId || undefined,
        breachedOnly: breachedOnly || undefined,
        fromUtc: fromDate ? new Date(fromDate + "T00:00:00Z").toISOString() : undefined,
        toUtc: toDate ? new Date(toDate + "T23:59:59Z").toISOString() : undefined,
        search: search || undefined,
      }),
  });

  return (
    <div className="flex flex-col gap-4">
      <header className="space-y-1">
        <div className="mb-2 text-primary">
          <Timer className="h-6 w-6" />
        </div>
        <h1 className="text-display-md font-semibold text-foreground">SLA log</h1>
        <p className="max-w-2xl text-sm text-muted-foreground">
          Per-ticket timing overview — first-response and resolution against the configured targets.
          Filter by queue, priority, status, breach state, date range or subject.
        </p>
      </header>

      <section className="flex flex-wrap items-end gap-3 rounded-lg border border-glass-strong bg-glass p-4">
        <div className="flex items-center gap-2 text-xs text-muted-foreground"><Filter className="h-3 w-3" /> Filters</div>
        <Select
          value={queueId || ALL_QUEUES}
          onValueChange={(v) => setQueueId(v === ALL_QUEUES ? "" : v)}
        >
          <SelectTrigger className="h-9 min-w-[10rem] text-sm">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL_QUEUES}>All queues</SelectItem>
            {queues.data?.map((q) => (
              <SelectItem key={q.id} value={q.id}>{q.name}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select
          value={priorityId || ALL_PRIORITIES}
          onValueChange={(v) => setPriorityId(v === ALL_PRIORITIES ? "" : v)}
        >
          <SelectTrigger className="h-9 min-w-[10rem] text-sm">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL_PRIORITIES}>All priorities</SelectItem>
            {priorities.data?.map((p) => (
              <SelectItem key={p.id} value={p.id}>{p.name}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select
          value={statusId || ALL_STATUSES}
          onValueChange={(v) => setStatusId(v === ALL_STATUSES ? "" : v)}
        >
          <SelectTrigger className="h-9 min-w-[10rem] text-sm">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL_STATUSES}>All statuses</SelectItem>
            {statuses.data?.map((s) => (
              <SelectItem key={s.id} value={s.id}>{s.name}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          <input type="checkbox" checked={breachedOnly} onChange={(e) => setBreachedOnly(e.target.checked)} />
          Breached only
        </label>
        <label className="space-y-1 text-xs text-muted-foreground">
          From
          <Input type="date" value={fromDate} onChange={(e) => setFromDate(e.target.value)} className="w-36" />
        </label>
        <label className="space-y-1 text-xs text-muted-foreground">
          To
          <Input type="date" value={toDate} onChange={(e) => setToDate(e.target.value)} className="w-36" />
        </label>
        <label className="flex-1 min-w-[200px] space-y-1 text-xs text-muted-foreground">
          Search subject
          <div className="relative">
            <Search className="pointer-events-none absolute left-2 top-1/2 h-3 w-3 -translate-y-1/2 text-muted-foreground/60" />
            <Input value={search} onChange={(e) => setSearch(e.target.value)} className="pl-7" placeholder="e.g. printer" />
          </div>
        </label>
      </section>

      <section className="overflow-x-auto rounded-lg border border-glass-strong">
        {log.isLoading ? (
          <Skeleton className="h-48 w-full" />
        ) : (
          <table className="w-full text-sm">
            <thead className="bg-glass text-xs uppercase tracking-wider text-muted-foreground/60">
              <tr>
                <th className="px-3 py-2 text-left">#</th>
                <th className="px-3 py-2 text-left">Subject</th>
                <th className="px-3 py-2 text-left">Queue</th>
                <th className="px-3 py-2 text-left">Priority</th>
                <th className="px-3 py-2 text-left">Status</th>
                <th className="px-3 py-2 text-left">Created</th>
                <th className="px-3 py-2 text-right">First resp.</th>
                <th className="px-3 py-2 text-right">Resolution</th>
                <th className="px-3 py-2 text-center">Status</th>
              </tr>
            </thead>
            <tbody>
              {log.data?.items.map((row: SlaLogItem) => {
                const hasPolicy = row.firstResponseTargetMinutes !== null || row.resolutionTargetMinutes !== null;
                const anyBreached = row.firstResponseBreached || row.resolutionBreached;

                return (
                  <tr key={row.ticketId} className="border-t border-glass">
                    <td className="px-3 py-2 font-mono text-xs">#{row.number}</td>
                    <td className="px-3 py-2">
                      <Link to="/tickets/$ticketId" params={{ ticketId: row.ticketId }} className="text-foreground hover:underline">
                        {row.subject}
                      </Link>
                    </td>
                    <td className="px-3 py-2 text-muted-foreground">{row.queueName}</td>
                    <td className="px-3 py-2 text-muted-foreground">{row.priorityName}</td>
                    <td className="px-3 py-2 text-muted-foreground">{row.statusName}</td>
                    <td className="px-3 py-2 text-xs text-muted-foreground">
                      {new Date(row.createdUtc).toLocaleString()}
                    </td>
                    <td className="px-3 py-2 text-right text-xs">
                      <SlaCell
                        consumed={row.firstResponseBusinessMinutes}
                        target={row.firstResponseTargetMinutes}
                        deadline={row.firstResponseDeadlineUtc}
                        breached={row.firstResponseBreached}
                      />
                    </td>
                    <td className="px-3 py-2 text-right text-xs">
                      <SlaCell
                        consumed={row.resolutionBusinessMinutes}
                        target={row.resolutionTargetMinutes}
                        deadline={row.resolutionDeadlineUtc}
                        breached={row.resolutionBreached}
                      />
                    </td>
                    <td className="px-3 py-2 text-center">
                      {!hasPolicy ? (
                        <span className="text-xs text-muted-foreground/40">—</span>
                      ) : anyBreached ? (
                        <span className="inline-flex gap-1">
                          {row.firstResponseBreached && <span className="rounded bg-red-500/15 px-1.5 py-0.5 text-xs text-red-300">FR</span>}
                          {row.resolutionBreached && <span className="rounded bg-red-500/15 px-1.5 py-0.5 text-xs text-red-300">Res</span>}
                        </span>
                      ) : row.isPaused ? (
                        <span className="rounded bg-amber-500/15 px-1.5 py-0.5 text-xs text-amber-300">Paused</span>
                      ) : (
                        <span className="rounded bg-emerald-500/15 px-1.5 py-0.5 text-xs text-emerald-300">On track</span>
                      )}
                    </td>
                  </tr>
                );
              })}
              {(log.data?.items.length ?? 0) === 0 && (
                <tr>
                  <td colSpan={9} className="px-3 py-8 text-center text-xs text-muted-foreground">
                    No tickets match these filters.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}
