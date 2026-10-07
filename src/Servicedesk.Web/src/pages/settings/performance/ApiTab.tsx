import { formatDateTime } from "@/lib/dateFormat";
import * as React from "react";
import { Bar, BarChart, CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { Skeleton } from "@/components/ui/skeleton";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { apiErrorMessage } from "@/lib/api";
import { perfApi, type PeriodQuery, type RouteRow, type SlowRequest } from "@/lib/perf-api";
import { cn } from "@/lib/utils";
import { useTabQuery } from "./usePerfQuery";
import {
  ChartTooltipBox,
  Code,
  DataTable,
  Legend,
  Panel,
  Pill,
  SplitBar,
  SqlBlock,
  StatTile,
  type Column,
} from "./PerfUi";
import { axisTick, gridStroke, splitParts, usePalette, useSplitColors } from "./perfChart";
import { fmtBytes, fmtCount, fmtMs, fmtNum, fmtPct, fmtTime, latencyTone } from "./perfFormat";

export function ApiTab({ period }: { period: PeriodQuery }) {
  const q = useTabQuery(["http", period], () => perfApi.http(period));
  const colors = useSplitColors();
  const [selected, setSelected] = React.useState<RouteRow | null>(null);
  const [showOther, setShowOther] = React.useState(false);

  if (q.isLoading) return <Skeleton className="h-96 w-full" />;
  if (q.isError || !q.data) return <p className="text-sm text-muted-foreground">{apiErrorMessage(q.error) ?? "Could not load the API statistics."}</p>;

  const { routes, total } = q.data;
  const api = routes.filter((r) => r.route.startsWith("/api/"));
  const other = routes.filter((r) => !r.route.startsWith("/api/"));
  const rows = showOther ? routes : api;

  const columns: Column<RouteRow>[] = [
    {
      key: "route",
      header: "Route",
      render: (r) => (
        <div className="flex min-w-0 items-center gap-1.5">
          <span className="w-12 shrink-0 font-mono text-[10.5px] font-semibold text-muted-foreground">{r.method}</span>
          <span className="truncate font-mono text-[11.5px]" title={r.route}>{r.route}</span>
        </div>
      ),
      sortValue: (r) => r.route,
      className: "max-w-[28rem]",
    },
    { key: "count", header: "Requests", align: "right", render: (r) => fmtCount(r.count), sortValue: (r) => r.count },
    { key: "p50", header: "p50", align: "right", render: (r) => fmtMs(r.p50), sortValue: (r) => r.p50 },
    {
      key: "p95",
      header: "p95",
      align: "right",
      info: "95% of requests to this route were faster than this. The colour compares it with the slow-request threshold.",
      render: (r) => <Pill tone={latencyTone(r.p95, 1000)}>{fmtMs(r.p95)}</Pill>,
      sortValue: (r) => r.p95,
    },
    { key: "p99", header: "p99", align: "right", render: (r) => fmtMs(r.p99), sortValue: (r) => r.p99 },
    {
      key: "errors",
      header: "Errors",
      align: "right",
      info: "5xx = server errors; 429 = rejected by the rate limiter.",
      render: (r) => (
        <span className={cn(r.errors5xx > 0 && "text-rose-600 dark:text-rose-300")}>
          {fmtCount(r.errors5xx)}
          {r.count429 > 0 ? <span className="ml-1 text-muted-foreground">({fmtCount(r.count429)} × 429)</span> : null}
        </span>
      ),
      sortValue: (r) => r.errors5xx + r.count429,
    },
    {
      key: "split",
      header: "Where the time goes",
      info: "Average time per request split into database, external APIs, pipeline (rate limiter, sign-in check, CSRF) and our own code.",
      render: (r) => (
        <div className="flex min-w-36 items-center gap-2">
          <SplitBar parts={splitParts(r, colors)} className="flex-1" />
          <span className="w-12 shrink-0 text-right tabular-nums text-muted-foreground">{fmtMs(r.avgMs)}</span>
        </div>
      ),
      sortValue: (r) => r.dbSharePct,
    },
    { key: "q", header: "Queries/req", align: "right", info: "Average number of database queries per request. Dozens per request usually means an N+1 pattern.", render: (r) => fmtNum(r.avgDbQueries), sortValue: (r) => r.avgDbQueries },
    { key: "kb", header: "Avg size", align: "right", info: "Average response size. Large JSON payloads cost serialisation, transfer and parsing time.", render: (r) => fmtBytes(r.avgKb * 1024), sortValue: (r) => r.avgKb },
    { key: "total", header: "Total time", align: "right", info: "Requests × average duration — the total time users waited on this route. Sort by this to find what matters most.", render: (r) => fmtMs(r.totalMs), sortValue: (r) => r.totalMs },
  ];

  return (
    <div className={cn("flex flex-col gap-5", q.isFetching && q.isPlaceholderData && "opacity-70")}>
      {total ? (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <StatTile label="Requests" value={fmtCount(total.count)} sub={`${fmtCount(api.length)} API routes`} />
          <StatTile label="p95 / p99" value={fmtMs(total.p95)} sub={`p99 ${fmtMs(total.p99)} · max ${fmtMs(total.maxMs)}`} tone={latencyTone(total.p95, 1000)} />
          <StatTile label="Server errors" value={fmtCount(total.errors5xx)} sub={`${fmtCount(total.count4xx)} × 4xx · ${fmtCount(total.count429)} × 429`} tone={total.errors5xx > 0 ? "warning" : "good"} />
          <div className="glass-card flex flex-col justify-center gap-2 rounded-lg border border-glass-strong bg-glass px-4 py-3">
            <div className="text-xs text-muted-foreground">Average request</div>
            <SplitBar parts={splitParts(total, colors)} className="h-2.5" />
            <div className="flex flex-wrap gap-x-3 text-[11px] text-muted-foreground">
              <span>DB {fmtPct(total.dbSharePct)}</span>
              <span>Ext {fmtPct(total.extSharePct)}</span>
              <span>Pipeline {fmtPct(total.pipelineSharePct)}</span>
              <span>Code {fmtPct(total.appSharePct)}</span>
            </div>
          </div>
        </div>
      ) : null}

      <Panel
        title="Routes"
        info="Every endpoint by its route template (never the concrete URL, so no ids are stored). Click a route for its latency over time, its queries and its slowest requests."
        actions={
          <div className="flex items-center gap-3">
            <Legend items={splitParts({ avgDbMs: 1, avgExtMs: 1, avgPipelineMs: 1, avgAppMs: 1 }, colors).map((p) => ({ label: p.label, color: p.color }))} />
            {other.length > 0 ? (
              <label className="inline-flex items-center gap-1.5 text-xs text-muted-foreground">
                <input type="checkbox" checked={showOther} onChange={(e) => setShowOther(e.target.checked)} className="accent-primary" />
                Non-API ({other.length})
              </label>
            ) : null}
          </div>
        }
      >
        <DataTable
          columns={columns}
          rows={rows}
          rowKey={(r) => `${r.method} ${r.route}`}
          initialSort={{ key: "total", dir: "desc" }}
          onRowClick={setSelected}
          searchText={(r) => `${r.method} ${r.route}`}
          searchPlaceholder="Filter routes…"
        />
      </Panel>

      <Sheet open={selected !== null} onOpenChange={(o) => !o && setSelected(null)}>
        <SheetContent side="right" className="w-full overflow-y-auto border-glass-strong sm:max-w-3xl">
          {selected ? <RouteDetail period={period} route={selected} /> : null}
        </SheetContent>
      </Sheet>
    </div>
  );
}

function RouteDetail({ period, route }: { period: PeriodQuery; route: RouteRow }) {
  const q = useTabQuery(["route", period, route.method, route.route], () => perfApi.route(period, route.method, route.route));
  const pal = usePalette();
  const colors = useSplitColors();
  const d = q.data;
  const series = (d?.series ?? []).map((p) => ({ ...p, t: new Date(p.t).getTime() }));
  const multiDay = new Date(period.to).getTime() - new Date(period.from).getTime() > 36 * 3_600_000;

  return (
    <div className="flex flex-col gap-5">
      <SheetHeader>
        <SheetTitle className="font-mono text-base">
          <span className="mr-2 text-muted-foreground">{route.method}</span>
          {route.route}
        </SheetTitle>
        <SheetDescription>
          {fmtCount(route.count)} requests · p95 {fmtMs(route.p95)} · {fmtNum(route.avgDbQueries)} queries per request
        </SheetDescription>
      </SheetHeader>

      <div className="space-y-2">
        <SplitBar parts={splitParts(route, colors)} className="h-3" />
        <Legend items={splitParts(route, colors).map((p) => ({ label: `${p.label} ${fmtMs(p.value)}`, color: p.color }))} />
      </div>

      {q.isLoading ? <Skeleton className="h-56 w-full" /> : null}

      {series.length > 0 ? (
        <Panel title="Latency over time" actions={<Legend items={[{ label: "p95", color: pal.slot(0) }, { label: "p50", color: pal.slot(2) }]} />}>
          <div className="h-48">
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={series} margin={{ top: 6, right: 8, left: -8, bottom: 0 }}>
                <CartesianGrid vertical={false} stroke={gridStroke} strokeOpacity={0.6} />
                <XAxis dataKey="t" type="number" scale="time" domain={["dataMin", "dataMax"]} tick={axisTick} tickLine={false}
                  tickFormatter={(v: number) => fmtTime(new Date(v).toISOString(), multiDay)} minTickGap={40} axisLine={{ stroke: gridStroke }} />
                <YAxis tick={axisTick} tickLine={false} axisLine={false} width={56} tickFormatter={(v: number) => fmtMs(v)} />
                <Tooltip
                  content={(props) => {
                    const row = props.payload?.[0]?.payload as (typeof series)[number] | undefined;
                    if (!props.active || !row) return null;
                    return (
                      <ChartTooltipBox
                        title={formatDateTime(row.t, undefined, true)}
                        rows={[
                          { label: "p95", value: fmtMs(row.p95), color: pal.slot(0) },
                          { label: "p50", value: fmtMs(row.p50), color: pal.slot(2) },
                          { label: "Requests", value: fmtCount(row.count) },
                        ]}
                      />
                    );
                  }}
                />
                <Line type="monotone" dataKey="p95" stroke={pal.slot(0)} strokeWidth={2} dot={false} isAnimationActive={false} />
                <Line type="monotone" dataKey="p50" stroke={pal.slot(2)} strokeWidth={2} dot={false} isAnimationActive={false} />
              </LineChart>
            </ResponsiveContainer>
          </div>
        </Panel>
      ) : null}

      {d?.histogram && d.histogram.length > 0 ? (
        <Panel title="Distribution" info="How many requests fell into each duration bucket. A long tail to the right means occasional very slow requests.">
          <div className="h-36">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={d.histogram.map((b) => ({ label: b.le === null ? "> 60 s" : `≤ ${fmtMs(b.le)}`, count: b.count }))} margin={{ top: 4, right: 4, left: -16, bottom: 0 }}>
                <XAxis dataKey="label" tick={axisTick} tickLine={false} axisLine={{ stroke: gridStroke }} interval="preserveStartEnd" />
                <YAxis tick={axisTick} tickLine={false} axisLine={false} width={44} allowDecimals={false} />
                <Tooltip cursor={{ fill: pal.muted }} content={(props) => {
                  const row = props.payload?.[0]?.payload as { label: string; count: number } | undefined;
                  if (!props.active || !row) return null;
                  return <ChartTooltipBox title={row.label} rows={[{ label: "Requests", value: fmtCount(row.count), color: pal.slot(0) }]} />;
                }} />
                <Bar dataKey="count" fill={pal.slot(0)} radius={[4, 4, 0, 0]} isAnimationActive={false} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </Panel>
      ) : null}

      {d ? (
        <Panel title="Queries issued by this route" info="Captured in Diagnose mode: every query shape this route ran, with its calling method. Values are never stored — parameters appear as @name or $n.">
          {d.queries.length === 0 ? (
            <p className="text-xs text-muted-foreground">No per-query capture for this route in the period. Start Diagnose mode to see which queries it runs.</p>
          ) : (
            <ul className="flex flex-col gap-3">
              {d.queries.map((qr) => {
                const mine = qr.sources.find((s) => s.source === `${route.method} ${route.route}`);
                return (
                  <li key={qr.fingerprint} className="space-y-1.5">
                    <div className="flex flex-wrap items-center gap-2 text-xs">
                      <Code>{qr.fingerprint}</Code>
                      {qr.caller ? <span className="text-muted-foreground">in <Code>{qr.caller}</Code></span> : null}
                      <span className="ml-auto tabular-nums text-muted-foreground">
                        {fmtCount(mine?.count ?? qr.count)} × · avg {fmtMs(qr.avgMs)} · p95 {fmtMs(qr.p95)}
                      </span>
                    </div>
                    <SqlBlock sql={qr.sql} maxHeight="7rem" />
                  </li>
                );
              })}
            </ul>
          )}
          {d.nPlusOne.length > 0 ? (
            <div className="mt-4 rounded-md border border-amber-500/30 bg-amber-500/5 p-3 text-xs">
              <div className="mb-1 font-medium text-amber-800 dark:text-amber-300">N+1 pattern detected</div>
              {d.nPlusOne.map((n) => (
                <div key={n.fingerprint} className="text-foreground/90">
                  <Code>{n.fingerprint}</Code> ran up to {n.maxPerRequest}× in one request ({fmtCount(n.requests)} requests affected)
                  {n.caller ? <> — from <Code>{n.caller}</Code></> : null}
                </div>
              ))}
            </div>
          ) : null}
        </Panel>
      ) : null}

      {d ? (
        <Panel title="Slowest requests" info="Individual requests above the slow-request threshold, with their time breakdown. In Diagnose mode the heaviest queries of each request are listed too.">
          {d.slow.length === 0 ? (
            <p className="text-xs text-muted-foreground">No slow requests recorded for this route.</p>
          ) : (
            <SlowList rows={d.slow} />
          )}
        </Panel>
      ) : null}
    </div>
  );
}

export function SlowList({ rows }: { rows: SlowRequest[] }) {
  const colors = useSplitColors();
  return (
    <ul className="flex flex-col divide-y divide-glass">
      {rows.map((s) => {
        const app = Math.max(0, s.totalMs - s.dbMs - s.extMs - s.pipelineMs);
        return (
          <li key={s.id} className="space-y-1.5 py-2 text-xs">
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-semibold tabular-nums text-foreground">{fmtMs(s.totalMs)}</span>
              <span className="text-muted-foreground">{formatDateTime(s.t)}</span>
              <Pill tone={s.status >= 500 ? "critical" : s.status >= 400 ? "warning" : "nodata"}>{s.status}</Pill>
              <span className="ml-auto text-muted-foreground">
                {s.dbCount} queries · {fmtBytes(s.bytes)}{s.gcCount > 0 ? ` · ${s.gcCount} GC` : ""}
              </span>
            </div>
            <SplitBar
              parts={splitParts({ avgDbMs: s.dbMs, avgExtMs: s.extMs, avgPipelineMs: s.pipelineMs, avgAppMs: app }, colors)}
            />
            {s.breakdown?.queries && s.breakdown.queries.length > 0 ? (
              <div className="flex flex-wrap gap-x-3 gap-y-0.5 text-muted-foreground">
                {s.breakdown.queries.slice(0, 5).map((qq) => (
                  <span key={qq.fingerprint}><Code>{qq.fingerprint}</Code> ×{qq.count} · {fmtMs(qq.ms)}</span>
                ))}
              </div>
            ) : null}
          </li>
        );
      })}
    </ul>
  );
}
