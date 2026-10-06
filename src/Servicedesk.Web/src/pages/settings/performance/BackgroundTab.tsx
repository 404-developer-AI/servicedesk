import * as React from "react";
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { Skeleton } from "@/components/ui/skeleton";
import { apiErrorMessage } from "@/lib/api";
import { perfApi, type BackgroundResponse, type PeriodQuery, type SpanRow, type WorkerRun } from "@/lib/perf-api";
import { cn } from "@/lib/utils";
import { useTabQuery } from "./usePerfQuery";
import {
  ChartTooltipBox,
  DataTable,
  Legend,
  Panel,
  Pill,
  type Column,
} from "./PerfUi";
import { axisTick, gridStroke, usePalette } from "./perfChart";
import { fmtCount, fmtDuration, fmtMs, fmtPct, fmtTime, type Tone } from "./perfFormat";

type WorkerSummary = BackgroundResponse["workers"][number];
type ExternalRow = BackgroundResponse["external"][number];

export function BackgroundTab({ period }: { period: PeriodQuery }) {
  const q = useTabQuery(["background", period], () => perfApi.background(period));
  const pal = usePalette();
  if (q.isLoading) return <Skeleton className="h-96 w-full" />;
  if (q.isError || !q.data) return <p className="text-sm text-muted-foreground">{apiErrorMessage(q.error) ?? "Could not load background statistics."}</p>;
  const d = q.data;
  const multiDay = new Date(period.to).getTime() - new Date(period.from).getTime() > 36 * 3_600_000;

  const workerColumns: Column<WorkerSummary>[] = [
    { key: "worker", header: "Worker", render: (w) => <span className="font-mono text-[11.5px]">{w.worker}</span>, sortValue: (w) => w.worker },
    { key: "runs", header: "Runs", align: "right", render: (w) => fmtCount(w.runs), sortValue: (w) => w.runs },
    {
      key: "fail",
      header: "Failures",
      align: "right",
      render: (w) => (w.failures > 0 ? <Pill tone={w.failures > w.runs * 0.2 ? "critical" : "warning"}>{fmtCount(w.failures)}</Pill> : <span className="text-muted-foreground">0</span>),
      sortValue: (w) => w.failures,
    },
    { key: "avg", header: "Avg", align: "right", render: (w) => fmtMs(w.avgMs), sortValue: (w) => w.avgMs },
    { key: "max", header: "Longest", align: "right", render: (w) => fmtMs(w.maxMs), sortValue: (w) => w.maxMs },
    { key: "total", header: "Total busy", align: "right", info: "Sum of all run durations in the period — how much the worker kept the server busy.", render: (w) => fmtDuration(w.totalMs), sortValue: (w) => w.totalMs },
    { key: "items", header: "Items", align: "right", render: (w) => fmtCount(w.items), sortValue: (w) => w.items },
    { key: "last", header: "Last run", align: "right", render: (w) => <span className="text-muted-foreground">{fmtTime(w.lastRunUtc, true)}</span>, sortValue: (w) => w.lastRunUtc },
  ];

  const extColumns: Column<ExternalRow>[] = [
    { key: "host", header: "Host", render: (r) => <span className="font-mono text-[11.5px]">{r.host}</span>, sortValue: (r) => r.host },
    { key: "count", header: "Calls", align: "right", render: (r) => fmtCount(r.count), sortValue: (r) => r.count },
    {
      key: "errors",
      header: "Errors",
      align: "right",
      info: "Failed calls (network errors, 5xx) and throttled ones (429 Too Many Requests).",
      render: (r) => {
        const pct = r.count > 0 ? (100 * r.errors) / r.count : 0;
        const tone: Tone = pct > 5 ? "critical" : pct > 0 ? "warning" : "good";
        return <Pill tone={tone}>{fmtPct(pct)}{r.throttled > 0 ? ` · ${fmtCount(r.throttled)}×429` : ""}</Pill>;
      },
      sortValue: (r) => r.errors,
    },
    { key: "p50", header: "p50", align: "right", render: (r) => fmtMs(r.p50), sortValue: (r) => r.p50 },
    { key: "p95", header: "p95", align: "right", render: (r) => fmtMs(r.p95), sortValue: (r) => r.p95 },
    { key: "total", header: "Total", align: "right", render: (r) => fmtDuration(r.totalMs), sortValue: (r) => r.totalMs },
  ];

  const spanColumns = (nameHeader: string): Column<SpanRow>[] => [
    { key: "name", header: nameHeader, render: (r) => <span className="font-mono text-[11.5px]">{r.detail ? `${r.name}.${r.detail}` : r.name}</span>, sortValue: (r) => `${r.name}.${r.detail}` },
    { key: "count", header: "Calls", align: "right", render: (r) => fmtCount(r.count), sortValue: (r) => r.count },
    { key: "errors", header: "Errors", align: "right", render: (r) => fmtCount(r.errors), sortValue: (r) => r.errors },
    { key: "p50", header: "p50", align: "right", render: (r) => fmtMs(r.p50), sortValue: (r) => r.p50 },
    { key: "p95", header: "p95", align: "right", render: (r) => fmtMs(r.p95), sortValue: (r) => r.p95 },
    { key: "max", header: "Max", align: "right", render: (r) => fmtMs(r.maxMs), sortValue: (r) => r.maxMs },
  ];

  const minutes = Math.max(1, (new Date(period.to).getTime() - new Date(period.from).getTime()) / 60_000);
  const connSeries = d.connectionSeries.map((c) => ({ ...c, ts: new Date(c.t).getTime() }));

  return (
    <div className={cn("flex flex-col gap-5", q.isFetching && q.isPlaceholderData && "opacity-70")}>
      <Panel title="Worker timeline" info="Each bar is one background-worker run (runs under ~25 ms with nothing to do are left out). Long or overlapping bars during office hours are candidates for rescheduling. Hover for details.">
        <Gantt runs={d.runs} period={period} />
      </Panel>

      {d.overlaps.length > 0 ? (
        <Panel title="Do workers slow down the API?" info="API p95 in the minutes a worker was running compared with all other minutes of the period (periods up to 2 days). A clearly higher value during runs means the job competes with users for CPU, connections or locks.">
          <ul className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
            {d.overlaps.map((o) => {
              const worse = o.p95Outside > 0 ? (o.p95During / o.p95Outside - 1) * 100 : 0;
              const tone: Tone = worse > 50 ? "warning" : "good";
              return (
                <li key={o.worker} className="rounded-md border border-glass bg-glass px-3 py-2 text-xs">
                  <div className="flex items-center justify-between gap-2">
                    <span className="truncate font-mono">{o.worker}</span>
                    <Pill tone={tone}>{worse > 0 ? `+${fmtPct(worse)}` : "no impact"}</Pill>
                  </div>
                  <div className="mt-1 text-muted-foreground">
                    p95 {fmtMs(o.p95During)} during runs vs {fmtMs(o.p95Outside)} otherwise · {fmtCount(o.minutesDuring)} min
                  </div>
                </li>
              );
            })}
          </ul>
        </Panel>
      ) : null}

      <Panel title="Workers" info="Every background job (mail polling, integrations sync, retention, …) with its runs in the period.">
        <DataTable columns={workerColumns} rows={d.workers} rowKey={(w) => w.worker} initialSort={{ key: "total", dir: "desc" }} />
      </Panel>

      <Panel title="External APIs" info="Outgoing HTTP calls per host — Microsoft Graph, Adsolut, Tactical RMM, … — from workers and from user requests alike. Throttling (429) means we call too often for the API's quota.">
        <DataTable columns={extColumns} rows={d.external} rowKey={(r) => r.host} initialSort={{ key: "total", dir: "desc" }} />
      </Panel>

      <div className="grid gap-5 xl:grid-cols-2">
        <Panel title="Realtime connections" info="Connected SignalR clients (each open tab holds a few) and open HTTP connections, at their peak per interval."
          actions={<Legend items={[{ label: "SignalR clients", color: pal.slot(0) }, { label: "HTTP connections", color: pal.slot(2) }]} />}>
          {connSeries.length < 2 ? <p className="text-xs text-muted-foreground">No data.</p> : (
            <div className="h-44">
              <ResponsiveContainer width="100%" height="100%">
                <LineChart data={connSeries} margin={{ top: 6, right: 8, left: -8, bottom: 0 }}>
                  <CartesianGrid vertical={false} stroke={gridStroke} strokeOpacity={0.6} />
                  <XAxis dataKey="ts" type="number" scale="time" domain={["dataMin", "dataMax"]} tick={axisTick} tickLine={false}
                    axisLine={{ stroke: gridStroke }} tickFormatter={(v: number) => fmtTime(new Date(v).toISOString(), multiDay)} minTickGap={44} />
                  <YAxis tick={axisTick} tickLine={false} axisLine={false} width={44} allowDecimals={false} />
                  <Tooltip content={(props) => {
                    const row = props.payload?.[0]?.payload as (typeof connSeries)[number] | undefined;
                    if (!props.active || !row) return null;
                    return <ChartTooltipBox title={new Date(row.ts).toLocaleString()} rows={[
                      { label: "SignalR clients", value: fmtCount(row.signalr), color: pal.slot(0) },
                      { label: "HTTP connections", value: fmtCount(row.kestrel), color: pal.slot(2) },
                    ]} />;
                  }} />
                  <Line type="monotone" dataKey="signalr" stroke={pal.slot(0)} strokeWidth={2} dot={false} connectNulls isAnimationActive={false} />
                  <Line type="monotone" dataKey="kestrel" stroke={pal.slot(2)} strokeWidth={2} dot={false} connectNulls isAnimationActive={false} />
                </LineChart>
              </ResponsiveContainer>
            </div>
          )}
        </Panel>
        <Panel title="Broadcasts" info="Messages the server pushed over SignalR, per hub and method, with the target (everyone, a group, a user). Pushes to everyone multiply by the number of open tabs.">
          {d.broadcasts.length === 0 ? <p className="text-xs text-muted-foreground">No broadcasts recorded.</p> : (
            <ul className="divide-y divide-glass text-xs">
              {d.broadcasts.slice(0, 15).map((b) => (
                <li key={`${b.hub}|${b.detail}`} className="flex items-center justify-between gap-3 py-1.5">
                  <span className="truncate font-mono" title={`${b.hub} ${b.detail}`}>{b.hub} · {b.detail}</span>
                  <span className="shrink-0 tabular-nums text-muted-foreground">{fmtCount(b.count)} · {(b.count / minutes).toFixed(1)}/min</span>
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>

      <div className="grid gap-5 xl:grid-cols-2">
        <Panel title="Hub methods" info="Calls from browsers to the server over SignalR (presence, subscriptions). They should be near-instant.">
          <DataTable columns={spanColumns("Hub method")} rows={d.hubs} rowKey={(r) => `${r.name}.${r.detail}`} initialSort={{ key: "count", dir: "desc" }} dense />
        </Panel>
        <Panel title="Global search sources" info="Global search queries every source in parallel and waits for the slowest one. A slow source here slows every search.">
          <DataTable columns={spanColumns("Source")} rows={d.search} rowKey={(r) => r.name} initialSort={{ key: "p95", dir: "desc" }} dense />
        </Panel>
      </div>
    </div>
  );
}

/// One row per worker, one bar per run, positioned on the period's time axis.
function Gantt({ runs, period }: { runs: WorkerRun[]; period: PeriodQuery }) {
  const pal = usePalette();
  const from = new Date(period.from).getTime();
  const to = new Date(period.to).getTime();
  const span = Math.max(1, to - from);
  const workers = React.useMemo(() => [...new Set(runs.map((r) => r.worker))].sort(), [runs]);
  const [hover, setHover] = React.useState<WorkerRun | null>(null);

  if (runs.length === 0) return <p className="text-xs text-muted-foreground">No worker runs recorded in this period (Workers collector off, or only idle polls).</p>;

  const ticks = Array.from({ length: 5 }, (_, i) => from + (span * i) / 4);
  const multiDay = span > 36 * 3_600_000;

  return (
    <div className="flex flex-col gap-1">
      {workers.map((w) => (
        <div key={w} className="grid grid-cols-[10rem_minmax(0,1fr)] items-center gap-3">
          <span className="truncate text-right font-mono text-[11px] text-muted-foreground" title={w}>{w}</span>
          <div className="relative h-5 rounded bg-glass">
            {runs.filter((r) => r.worker === w).map((r, i) => {
              const start = new Date(r.t).getTime();
              const left = ((start - from) / span) * 100;
              const width = Math.max(0.25, (r.durationMs / span) * 100);
              if (left > 100 || left + width < 0) return null;
              return (
                <span
                  key={`${start}-${i}`}
                  onMouseEnter={() => setHover(r)}
                  onMouseLeave={() => setHover((h) => (h === r ? null : h))}
                  className="absolute inset-y-1 rounded-[3px]"
                  style={{
                    left: `${Math.max(0, left)}%`,
                    width: `${Math.min(100 - Math.max(0, left), width)}%`,
                    minWidth: 3,
                    backgroundColor: r.success ? pal.slot(0) : pal.slot(7),
                  }}
                />
              );
            })}
          </div>
        </div>
      ))}
      <div className="grid grid-cols-[10rem_minmax(0,1fr)] gap-3">
        <span />
        <div className="relative h-4 text-[10px] text-muted-foreground">
          {ticks.map((t, i) => (
            <span key={t} className={cn("absolute whitespace-nowrap", i === 0 ? "" : i === 4 ? "-translate-x-full" : "-translate-x-1/2")} style={{ left: `${(i / 4) * 100}%` }}>
              {fmtTime(new Date(t).toISOString(), multiDay)}
            </span>
          ))}
        </div>
      </div>
      <div className="mt-2 flex min-h-5 flex-wrap items-center justify-between gap-2 text-xs">
        <Legend items={[{ label: "Run", color: pal.slot(0) }, { label: "Failed run", color: pal.slot(7) }]} />
        {hover ? (
          <span className="text-muted-foreground">
            <span className="font-mono text-foreground">{hover.worker}</span> · {new Date(hover.t).toLocaleString()} · {fmtMs(hover.durationMs)}
            {hover.items > 0 ? ` · ${fmtCount(hover.items)} items` : ""}{hover.error ? ` · ${hover.error}` : ""}
          </span>
        ) : <span className="text-muted-foreground">Hover a bar for details.</span>}
      </div>
    </div>
  );
}
