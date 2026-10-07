import { formatDateTime } from "@/lib/dateFormat";
import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import {
  Area,
  AreaChart,
  CartesianGrid,
  Line,
  LineChart,
  ReferenceArea,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import { Flag, Gauge, Loader2, Plus, X } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { apiErrorMessage } from "@/lib/api";
import { perfApi, type CategoryScore, type Overview, type PeriodQuery } from "@/lib/perf-api";
import { cn } from "@/lib/utils";
import { useTabQuery } from "./usePerfQuery";
import {
  ChartTooltipBox,
  Dot,
  EmptyState,
  InfoTip,
  Legend,
  Panel,
  Pill,
  StatTile,
} from "./PerfUi";
import { axisTick, gridStroke, usePalette } from "./perfChart";
import { FindingsList } from "./FindingsList";
import { TONE, fmtCount, fmtMs, fmtPct, fmtTime, vitalTone, type Tone } from "./perfFormat";

type TabKey = "overview" | "api" | "database" | "server" | "frontend" | "background" | "settings";

const CATEGORY_TAB: Record<CategoryScore["key"], TabKey> = {
  hosting: "server",
  network: "frontend",
  code: "api",
  database: "database",
  maintenance: "database",
  frontend: "frontend",
  background: "background",
};

export function OverviewTab({
  period,
  autoRefresh,
  onNavigate,
  serverNow,
}: {
  period: PeriodQuery;
  autoRefresh: boolean;
  onNavigate: (tab: TabKey) => void;
  serverNow: () => number;
}) {
  const q = useTabQuery(["overview", period], () => perfApi.overview(period), autoRefresh ? 30_000 : false);
  const [category, setCategory] = React.useState<CategoryScore["key"] | null>(null);

  if (q.isLoading) {
    return (
      <div className="grid gap-3 md:grid-cols-4">
        {Array.from({ length: 8 }).map((_, i) => <Skeleton key={i} className="h-24 w-full" />)}
      </div>
    );
  }
  if (q.isError || !q.data) {
    return <p className="text-sm text-muted-foreground">{apiErrorMessage(q.error) ?? "Could not load the overview."}</p>;
  }
  const d = q.data;
  const findings = category ? d.findings.filter((f) => f.category === category) : d.findings;
  const noData = d.kpis.requests === 0 && d.findings.length === 0 && d.timeline.length === 0;

  return (
    <div className={cn("flex flex-col gap-5 transition-opacity", q.isFetching && q.isPlaceholderData && "opacity-70")}>
      {noData ? (
        <Panel>
          <EmptyState
            icon={<Gauge className="h-8 w-8" />}
            title="No measurements in this period yet"
            body={
              <>
                The monitor writes one batch per minute. With monitoring on (Basic) the first numbers appear within a minute
                or two of traffic; start <span className="font-medium text-foreground">Diagnose</span> for per-query detail.
              </>
            }
          />
        </Panel>
      ) : null}

      <CategoryGrid categories={d.categories} active={category} onSelect={(k) => setCategory((c) => (c === k ? null : k))} onOpen={(k) => onNavigate(CATEGORY_TAB[k])} />

      <KpiRow overview={d} />

      <div className="grid gap-5 xl:grid-cols-[minmax(0,1.6fr)_minmax(0,1fr)]">
        <Panel
          title={category ? `Top bottlenecks — ${d.categories.find((c) => c.key === category)?.label}` : "Top bottlenecks"}
          info="Sorted by impact: extra waiting time per occurrence × how often it happened in this period. That is why a 50 ms query that runs 100,000 times can rank above a 3-second one that ran twice. Expand a finding for the evidence, the likely cause, what to do and where in the code to look."
          actions={category ? (
            <button type="button" onClick={() => setCategory(null)} className="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground">
              <X className="h-3 w-3" /> All categories
            </button>
          ) : undefined}
        >
          <FindingsList findings={findings} limit={5} />
        </Panel>
        <div className="flex flex-col gap-5">
          <BudgetsPanel overview={d} />
          <MarkersPanel overview={d} serverNow={serverNow} />
        </div>
      </div>

      <TimelinePanel overview={d} />

      {d.gaps.length > 0 ? (
        <p className="text-xs text-muted-foreground">Some sections could not be read: {d.gaps.join(", ")}.</p>
      ) : null}
    </div>
  );
}

function CategoryGrid({
  categories,
  active,
  onSelect,
  onOpen,
}: {
  categories: CategoryScore[];
  active: CategoryScore["key"] | null;
  onSelect: (k: CategoryScore["key"]) => void;
  onOpen: (k: CategoryScore["key"]) => void;
}) {
  return (
    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4 2xl:grid-cols-7">
      {categories.map((c) => {
        const tone = c.status as Tone;
        return (
          <div
            key={c.key}
            className={cn(
              "glass-card group flex flex-col gap-2 rounded-lg border bg-glass px-3.5 py-3 transition-colors",
              active === c.key ? "border-primary/60" : "border-glass-strong",
            )}
          >
            <button type="button" onClick={() => onSelect(c.key)} className="flex flex-col gap-2 text-left" aria-pressed={active === c.key}>
              <div className="flex items-center justify-between gap-2">
                <span className="truncate text-xs font-medium text-muted-foreground">{c.label}</span>
                <Dot tone={tone} />
              </div>
              <div className="flex items-baseline gap-1.5">
                <span className={cn("font-display text-2xl font-semibold tabular-nums", TONE[tone].text)}>
                  {c.score < 0 ? "—" : c.score}
                </span>
                {c.score >= 0 ? <span className="text-xs text-muted-foreground">/ 100</span> : null}
              </div>
              <p className="line-clamp-2 min-h-8 text-xs leading-snug text-muted-foreground">{c.summary}</p>
            </button>
            <div className="mt-auto flex items-center justify-between gap-2">
              <InfoTip text={c.question} />
              <button type="button" onClick={() => onOpen(c.key)} className="text-[11px] text-primary opacity-80 hover:underline group-hover:opacity-100">
                Details →
              </button>
            </div>
          </div>
        );
      })}
    </div>
  );
}

function KpiRow({ overview }: { overview: Overview }) {
  const k = overview.kpis;
  return (
    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
      <StatTile
        label="API p95"
        value={fmtMs(k.p95)}
        sub={`p50 ${fmtMs(k.p50)} · p99 ${fmtMs(k.p99)}`}
        tone={k.requests === 0 ? "nodata" : k.p95 > 1000 ? "critical" : k.p95 > 400 ? "warning" : "good"}
        info="95% of API requests were faster than this (measured on the server, so without network time). p50 is the typical request, p99 the slowest 1%."
      />
      <StatTile
        label="Throughput"
        value={`${fmtCount(k.perMinute)}/min`}
        sub={`${fmtCount(k.requests)} requests · peak ${k.activeUsersPeak} users/min`}
        info="API requests per minute, averaged over the period. Peak users = most distinct signed-in users in one minute."
      />
      <StatTile
        label="Errors"
        value={fmtPct(k.errorRate5xx, 2)}
        sub={`5xx server errors · 4xx ${fmtPct(k.errorRate4xx, 1)}`}
        tone={k.requests === 0 ? "nodata" : k.errorRate5xx > 1 ? "critical" : k.errorRate5xx > 0.1 ? "warning" : "good"}
        info="Share of API requests that failed on the server (5xx). 4xx responses (not found, forbidden, rate limited) are usually client-side and shown separately."
      />
      <StatTile
        label="Time in database"
        value={fmtPct(k.dbSharePct)}
        sub={`external APIs ${fmtPct(k.extSharePct)}`}
        info="Share of all server time spent waiting on PostgreSQL. High values point at queries (see the Database tab); the rest is our own code, the request pipeline and external APIs."
      />
      <StatTile
        label="Host CPU"
        value={k.hostCpuPct === null ? "—" : fmtPct(k.hostCpuPct)}
        sub={k.stealPct === null ? "host metrics need Linux" : `steal ${fmtPct(k.stealPct, 1)} · app ${fmtPct(k.appCpuPct ?? 0)}`}
        tone={k.hostCpuPct === null ? "nodata" : (k.stealPct ?? 0) > 5 || k.hostCpuPct > 85 ? "warning" : "good"}
        info="Average CPU use of the whole server. Steal = CPU time the hosting provider gave to other customers; above a few percent the VPS is oversold."
      />
      <StatTile
        label="Free memory (lowest)"
        value={k.memAvailablePct === null ? "—" : fmtPct(k.memAvailablePct)}
        tone={k.memAvailablePct === null ? "nodata" : k.memAvailablePct < 10 ? "critical" : k.memAvailablePct < 20 ? "warning" : "good"}
        info="The lowest share of RAM that was still available in the period. Below ~10% the server starts swapping and PostgreSQL caches less."
      />
      <StatTile
        label="Page load (LCP p75)"
        value={fmtMs(k.lcpP75)}
        sub={k.lcpP75 === null ? "no browser data yet" : k.inpP75 === null ? "interaction (INP) not measured yet" : `interaction (INP) ${fmtMs(k.inpP75)}`}
        tone={vitalTone("lcp", k.lcpP75)}
        info="Measured in real agents' browsers: how long until the main content is visible (Largest Contentful Paint). Google's 'good' line is 2.5 s. INP = how fast the page reacts to a click."
      />
      <StatTile
        label="Network share"
        value={k.networkSharePct === null ? "—" : fmtPct(k.networkSharePct)}
        sub="of API time seen in the browser"
        tone={k.networkSharePct === null ? "nodata" : k.networkSharePct > 50 ? "warning" : "good"}
        info="How much of an API call's time, as the browser saw it, was spent outside the server (connection, latency, download). High = the users' connection or distance is the bottleneck, not the server."
      />
    </div>
  );
}

function BudgetsPanel({ overview }: { overview: Overview }) {
  if (overview.budgets.length === 0) return null;
  return (
    <Panel
      title="Performance budgets"
      info="Targets for the key flows (Settings tab → Budgets). The bar shows p95 against its budget; past the marker the flow is over budget."
    >
      <ul className="flex flex-col gap-3">
        {overview.budgets.map((b) => {
          const ratio = b.hasData ? b.p95 / b.targetMs : 0;
          const tone: Tone = !b.hasData ? "nodata" : ratio <= 1 ? "good" : ratio <= 2 ? "warning" : "critical";
          return (
            <li key={`${b.method} ${b.route}`} className="space-y-1">
              <div className="flex items-center justify-between gap-2 text-xs">
                <span className="truncate font-mono text-foreground/90" title={`${b.method} ${b.route}`}>{b.method} {b.route}</span>
                <span className={cn("shrink-0 tabular-nums", TONE[tone].text)}>
                  {b.hasData ? fmtMs(b.p95) : "no data"} <span className="text-muted-foreground">/ {fmtMs(b.targetMs)}</span>
                </span>
              </div>
              <div className="relative h-1.5 rounded-full bg-glass-strong">
                <span className={cn("absolute inset-y-0 left-0 rounded-full", TONE[tone].dot)} style={{ width: `${Math.min(100, (ratio / 2) * 100)}%` }} />
                <span aria-hidden className="absolute inset-y-[-3px] left-1/2 w-px bg-foreground/40" />
              </div>
            </li>
          );
        })}
      </ul>
    </Panel>
  );
}

function MarkersPanel({ overview, serverNow }: { overview: Overview; serverNow: () => number }) {
  const qc = useQueryClient();
  const [label, setLabel] = React.useState("");
  const add = useMutation({
    mutationFn: () => perfApi.addMarker(label.trim()),
    onSuccess: () => {
      setLabel("");
      toast.success("Marker added");
      void qc.invalidateQueries({ queryKey: ["admin", "performance"] });
    },
    onError: (e) => toast.error(apiErrorMessage(e) ?? "Could not add the marker"),
  });
  const remove = useMutation({
    mutationFn: (id: number) => perfApi.deleteMarker(id),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["admin", "performance"] }),
  });
  const now = serverNow();
  const markers = [...overview.markers].reverse().slice(0, 6);

  return (
    <Panel
      title="Markers"
      info="Pins on the timeline: deploys and Diagnose runs are added automatically; add your own ('added index on tickets', 'moved to a bigger VPS') to compare before and after."
    >
      <form
        className="flex gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          if (label.trim()) add.mutate();
        }}
      >
        <input
          value={label}
          maxLength={120}
          onChange={(e) => setLabel(e.target.value)}
          placeholder="What changed? e.g. 'Added index on tickets.queue_id'"
          className="h-8 min-w-0 flex-1 rounded-md border border-glass bg-glass px-2 text-xs text-foreground outline-hidden placeholder:text-muted-foreground focus:border-primary/60"
        />
        <Button size="sm" type="submit" variant="outline" disabled={!label.trim() || add.isPending}>
          {add.isPending ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Plus className="h-3.5 w-3.5" />}
        </Button>
      </form>
      {markers.length > 0 ? (
        <ul className="mt-3 flex flex-col divide-y divide-glass">
          {markers.map((m) => (
            <li key={m.id} className="flex items-center gap-2 py-1.5 text-xs">
              <Flag className={cn("h-3 w-3 shrink-0", m.kind === "deploy" ? "text-primary" : "text-muted-foreground")} />
              <span className="min-w-0 flex-1 truncate text-foreground/90" title={m.label}>{m.label}</span>
              <span className="shrink-0 text-muted-foreground" title={formatDateTime(m.t, undefined, true)}>
                {Math.abs(now - new Date(m.t).getTime()) < 86_400_000 ? fmtTime(m.t) : fmtTime(m.t, true)}
              </span>
              {m.kind === "manual" ? (
                <button type="button" aria-label="Remove marker" onClick={() => remove.mutate(m.id)} className="text-muted-foreground hover:text-foreground">
                  <X className="h-3 w-3" />
                </button>
              ) : null}
            </li>
          ))}
        </ul>
      ) : (
        <p className="mt-3 text-xs text-muted-foreground">No markers in this period.</p>
      )}
    </Panel>
  );
}

function TimelinePanel({ overview }: { overview: Overview }) {
  const pal = usePalette();
  const multiDay = new Date(overview.period.to).getTime() - new Date(overview.period.from).getTime() > 36 * 3_600_000;
  const stepMin = overview.period.stepSeconds / 60;
  const data = overview.timeline.map((p) => ({
    t: new Date(p.t).getTime(),
    p95: p.p95,
    p50: p.p50,
    rpm: stepMin > 0 ? p.count / stepMin : p.count,
    errors: p.errors,
  }));
  const runs = overview.workerRuns.slice(0, 120);
  const tickFmt = (v: number) => fmtTime(new Date(v).toISOString(), multiDay);
  const domain: [number, number] = [new Date(overview.period.from).getTime(), new Date(overview.period.to).getTime()];
  const p95Color = pal.slot(0);
  const p50Color = pal.slot(2);
  const rpmColor = pal.slot(6);

  return (
    <Panel
      title="Timeline"
      info="API latency over the period (server-side). Shaded bands are background-worker runs longer than a second; dashed lines are markers (deploys, Diagnose runs, your own notes). If latency rises inside the bands, a background job is slowing users down."
      actions={<Legend items={[{ label: "p95", color: p95Color }, { label: "p50", color: p50Color }, { label: "Worker run", color: pal.mutedStrong }]} />}
    >
      {data.length === 0 ? (
        <p className="py-8 text-center text-sm text-muted-foreground">No API traffic in this period.</p>
      ) : (
        <div className="flex flex-col gap-4">
          <div className="h-64">
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={data} margin={{ top: 8, right: 8, left: -8, bottom: 0 }}>
                <CartesianGrid vertical={false} stroke={gridStroke} strokeOpacity={0.6} />
                <XAxis dataKey="t" type="number" scale="time" domain={domain} tickFormatter={tickFmt} tick={axisTick}
                  tickLine={false} axisLine={{ stroke: gridStroke }} minTickGap={40} tickMargin={8} />
                <YAxis tick={axisTick} tickLine={false} axisLine={false} width={56} tickFormatter={(v: number) => fmtMs(v)} />
                {runs.map((r, i) => {
                  const start = new Date(r.t).getTime();
                  return (
                    <ReferenceArea key={`${r.worker}-${i}`} x1={start} x2={start + Math.max(r.durationMs, 30_000)}
                      fill={pal.mutedStrong} fillOpacity={0.5} strokeOpacity={0} ifOverflow="hidden" />
                  );
                })}
                {overview.markers.map((m) => (
                  <ReferenceLine key={m.id} x={new Date(m.t).getTime()} stroke="hsl(var(--muted-foreground))" strokeDasharray="3 3"
                    ifOverflow="hidden" label={{ value: m.kind === "deploy" ? "deploy" : "●", position: "insideTopLeft", fontSize: 10, fill: "hsl(var(--muted-foreground))" }} />
                ))}
                <Tooltip
                  cursor={{ stroke: "hsl(var(--muted-foreground))", strokeOpacity: 0.4 }}
                  content={(props) => {
                    const row = props.payload?.[0]?.payload as (typeof data)[number] | undefined;
                    if (!props.active || !row) return null;
                    const active = runs.filter((r) => {
                      const s = new Date(r.t).getTime();
                      return row.t >= s - 60_000 && row.t <= s + r.durationMs + 60_000;
                    });
                    return (
                      <ChartTooltipBox
                        title={formatDateTime(row.t, undefined, true)}
                        rows={[
                          { label: "p95", value: fmtMs(row.p95), color: p95Color },
                          { label: "p50", value: fmtMs(row.p50), color: p50Color },
                          { label: "Requests/min", value: fmtCount(row.rpm) },
                          { label: "5xx", value: fmtCount(row.errors) },
                          ...active.slice(0, 3).map((r) => ({ label: `Worker ${r.worker}`, value: fmtMs(r.durationMs) })),
                        ]}
                      />
                    );
                  }}
                />
                <Line type="monotone" dataKey="p95" stroke={p95Color} strokeWidth={2} dot={false} isAnimationActive={false} />
                <Line type="monotone" dataKey="p50" stroke={p50Color} strokeWidth={2} dot={false} isAnimationActive={false} />
              </LineChart>
            </ResponsiveContainer>
          </div>
          <div>
            <div className="mb-1 flex items-center gap-1.5 text-xs text-muted-foreground">
              Requests per minute
              <InfoTip text="Load on the API over the same period. A latency rise without a load rise points at something else: a worker, the host or the database." />
            </div>
            <div className="h-24">
              <ResponsiveContainer width="100%" height="100%">
                <AreaChart data={data} margin={{ top: 4, right: 8, left: -8, bottom: 0 }}>
                  <XAxis dataKey="t" type="number" scale="time" domain={domain} hide />
                  <YAxis tick={axisTick} tickLine={false} axisLine={false} width={56} tickFormatter={(v: number) => fmtCount(v)} />
                  <Tooltip
                    content={(props) => {
                      const row = props.payload?.[0]?.payload as (typeof data)[number] | undefined;
                      if (!props.active || !row) return null;
                      return <ChartTooltipBox title={formatDateTime(row.t, undefined, true)} rows={[{ label: "Requests/min", value: fmtCount(row.rpm), color: rpmColor }]} />;
                    }}
                  />
                  <Area type="monotone" dataKey="rpm" stroke={rpmColor} strokeWidth={2} fill={rpmColor} fillOpacity={0.12} isAnimationActive={false} />
                </AreaChart>
              </ResponsiveContainer>
            </div>
          </div>
          {overview.versions.length > 1 ? (
            <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
              Versions in period:
              {overview.versions.map((v) => (
                <Pill key={v.version} tone="nodata">{v.version}</Pill>
              ))}
            </div>
          ) : null}
        </div>
      )}
    </Panel>
  );
}
