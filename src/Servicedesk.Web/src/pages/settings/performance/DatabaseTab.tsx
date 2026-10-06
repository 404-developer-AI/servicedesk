import * as React from "react";
import { useMutation } from "@tanstack/react-query";
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { AlertTriangle, Database, FileSearch, Loader2, RefreshCw } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { apiErrorMessage } from "@/lib/api";
import {
  perfApi,
  type ActivityRow,
  type NPlusOneRow,
  type PerfStatus,
  type PeriodQuery,
  type PgssRow,
  type PlanResponse,
  type QueryRow,
  type TableRow,
} from "@/lib/perf-api";
import { cn } from "@/lib/utils";
import { useTabQuery } from "./usePerfQuery";
import {
  ChartTooltipBox,
  Code,
  DataTable,
  EmptyState,
  InfoTip,
  Legend,
  Panel,
  Pill,
  SqlBlock,
  StatTile,
  SubTabs,
  type Column,
} from "./PerfUi";
import { axisTick, gridStroke, usePalette } from "./perfChart";
import { fmtAgo, fmtBytes, fmtCount, fmtDuration, fmtMs, fmtNum, fmtPct, fmtTime, type Tone } from "./perfFormat";

type Sub = "queries" | "tables" | "live" | "config";

export function DatabaseTab({ period, status }: { period: PeriodQuery; status: PerfStatus | undefined }) {
  const [sub, setSub] = React.useState<Sub>("queries");
  const access = status?.pgAccess;
  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <SubTabs<Sub>
          value={sub}
          onChange={setSub}
          items={[
            { key: "queries", label: "Queries" },
            { key: "tables", label: "Tables & indexes" },
            { key: "live", label: "Live" },
            { key: "config", label: "Config & cache" },
          ]}
        />
        {access?.checked && (!access.pgMonitor || !access.statStatements) ? (
          <div className="flex items-center gap-1.5 text-xs text-amber-800 dark:text-amber-300">
            <AlertTriangle className="h-3.5 w-3.5" />
            {access.statStatementsReason ?? "Limited statistics access — run deploy/update.sh on the host."}
          </div>
        ) : null}
      </div>
      {sub === "queries" ? <QueriesView period={period} /> : null}
      {sub === "tables" ? <TablesView period={period} /> : null}
      {sub === "live" ? <LiveView /> : null}
      {sub === "config" ? <ConfigView period={period} /> : null}
    </div>
  );
}

// -------------------------------------------------------------- queries

function QueriesView({ period }: { period: PeriodQuery }) {
  const q = useTabQuery(["queries", period], () => perfApi.queries(period));
  const [plan, setPlan] = React.useState<{ source: "pgss" | "app"; id: string; title: string } | null>(null);

  if (q.isLoading) return <Skeleton className="h-96 w-full" />;
  if (q.isError || !q.data) return <p className="text-sm text-muted-foreground">{apiErrorMessage(q.error) ?? "Could not load query statistics."}</p>;
  const d = q.data;

  const appColumns: Column<QueryRow>[] = [
    {
      key: "sql",
      header: "Query",
      render: (r) => (
        <div className="min-w-0 max-w-xl space-y-1">
          <div className="truncate font-mono text-[11px] text-foreground/90" title={r.sql}>{r.sql}</div>
          <div className="flex flex-wrap items-center gap-1.5 text-[11px] text-muted-foreground">
            <Code>{r.fingerprint}</Code>
            {r.caller ? <span>in <Code>{r.caller}</Code></span> : null}
            {r.sources[0] ? <span>· from {r.sources[0].source}</span> : null}
          </div>
        </div>
      ),
      sortValue: (r) => r.sql,
    },
    { key: "count", header: "Calls", align: "right", render: (r) => fmtCount(r.count), sortValue: (r) => r.count },
    {
      key: "avg",
      header: "Avg",
      align: "right",
      render: (r) => <Pill tone={r.avgMs > d.slowQueryMs ? "critical" : r.avgMs > d.slowQueryMs / 4 ? "warning" : "good"}>{fmtMs(r.avgMs)}</Pill>,
      sortValue: (r) => r.avgMs,
    },
    { key: "p95", header: "p95", align: "right", render: (r) => fmtMs(r.p95), sortValue: (r) => r.p95 },
    { key: "total", header: "Total", align: "right", info: "Calls × average: the database time this query shape cost in total.", render: (r) => fmtDuration(r.totalMs), sortValue: (r) => r.totalMs },
    {
      key: "plan",
      header: "",
      render: (r) => (
        <Button size="sm" variant="ghost" className="h-7 px-2 text-xs" onClick={(e) => { e.stopPropagation(); setPlan({ source: "app", id: r.fingerprint, title: r.caller ?? r.fingerprint }); }}>
          <FileSearch className="mr-1 h-3.5 w-3.5" /> Plan
        </Button>
      ),
    },
  ];

  const pgColumns: Column<PgssRow>[] = [
    {
      key: "query",
      header: "Statement",
      render: (r) => <div className="max-w-xl truncate font-mono text-[11px] text-foreground/90" title={r.query}>{r.query || "(text not available)"}</div>,
      sortValue: (r) => r.query,
    },
    { key: "calls", header: "Calls", align: "right", render: (r) => fmtCount(r.calls), sortValue: (r) => r.calls },
    { key: "mean", header: "Mean", align: "right", render: (r) => <Pill tone={r.meanMs > d.slowQueryMs ? "critical" : r.meanMs > d.slowQueryMs / 4 ? "warning" : "good"}>{fmtMs(r.meanMs)}</Pill>, sortValue: (r) => r.meanMs },
    { key: "total", header: "Total", align: "right", render: (r) => fmtDuration(r.totalMs), sortValue: (r) => r.totalMs },
    { key: "rows", header: "Rows", align: "right", render: (r) => fmtCount(r.rows), sortValue: (r) => r.rows },
    {
      key: "hit",
      header: "Cache hit",
      align: "right",
      info: "Share of the pages this statement read that were already in memory. Low = it reads from disk.",
      render: (r) => <span className={cn(r.hitPct < 95 && "text-amber-700 dark:text-amber-300")}>{fmtPct(r.hitPct)}</span>,
      sortValue: (r) => r.hitPct,
    },
    {
      key: "temp",
      header: "Temp",
      align: "right",
      info: "Data written to temporary files because a sort or hash did not fit in work_mem.",
      render: (r) => (r.tempWritten > 0 ? fmtBytes(r.tempWritten * 8192) : "—"),
      sortValue: (r) => r.tempWritten,
    },
    {
      key: "plan",
      header: "",
      render: (r) => (
        <Button size="sm" variant="ghost" className="h-7 px-2 text-xs" onClick={(e) => { e.stopPropagation(); setPlan({ source: "pgss", id: r.queryId, title: `queryid ${r.queryId}` }); }}>
          <FileSearch className="mr-1 h-3.5 w-3.5" /> Plan
        </Button>
      ),
    },
  ];

  const nColumns: Column<NPlusOneRow>[] = [
    { key: "route", header: "Route", render: (r) => <span className="font-mono text-[11px]">{r.route}</span>, sortValue: (r) => r.route },
    { key: "fp", header: "Query", render: (r) => <div className="max-w-md space-y-0.5"><div className="truncate font-mono text-[11px]" title={r.sql ?? ""}>{r.sql ?? r.fingerprint}</div>{r.caller ? <div className="text-[11px] text-muted-foreground">in <Code>{r.caller}</Code></div> : null}</div> },
    { key: "req", header: "Requests", align: "right", render: (r) => fmtCount(r.requests), sortValue: (r) => r.requests },
    { key: "max", header: "Max/request", align: "right", render: (r) => <Pill tone={r.maxPerRequest >= 50 ? "critical" : "warning"}>{r.maxPerRequest}×</Pill>, sortValue: (r) => r.maxPerRequest },
    { key: "exec", header: "Executions", align: "right", render: (r) => fmtCount(r.executions), sortValue: (r) => r.executions },
  ];

  const totalDb = d.bySource.reduce((n, s) => n + s.totalMs, 0);

  return (
    <div className={cn("flex flex-col gap-5", q.isFetching && q.isPlaceholderData && "opacity-70")}>
      {d.bySource.length > 0 ? (
        <Panel title="Database time by source" info="Where the database time comes from: user requests, each background worker, or other (startup, hosted services). Measured in every mode.">
          <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-4">
            {d.bySource.slice(0, 8).map((s) => (
              <div key={s.source} className="rounded-md border border-glass bg-glass px-3 py-2">
                <div className="truncate text-xs text-muted-foreground" title={s.source}>{s.source}</div>
                <div className="mt-0.5 flex items-baseline justify-between gap-2">
                  <span className="text-sm font-semibold tabular-nums text-foreground">{fmtDuration(s.totalMs)}</span>
                  <span className="text-[11px] tabular-nums text-muted-foreground">{totalDb > 0 ? fmtPct((100 * s.totalMs) / totalDb) : "—"} · {fmtCount(s.count)} q</span>
                </div>
              </div>
            ))}
          </div>
        </Panel>
      ) : null}

      <Panel
        title="Queries measured by the app"
        info="Every query shape the app ran, with the method that issued it — captured in Diagnose mode. Sorted by total time: a cheap query that runs very often can cost more than a slow rare one. 'Plan' shows PostgreSQL's plan without running the query."
      >
        {d.app.length === 0 ? (
          <EmptyState
            icon={<Database className="h-7 w-7" />}
            title={d.diagnoseActive ? "Collecting… queries appear within a minute" : "Per-query capture runs in Diagnose mode"}
            body="Basic mode measures database time per request and per worker. Start Diagnose mode (top bar) for a period of normal work to see every query shape, its calling code and N+1 patterns."
          />
        ) : (
          <DataTable columns={appColumns} rows={d.app} rowKey={(r) => r.fingerprint} initialSort={{ key: "total", dir: "desc" }}
            searchText={(r) => `${r.sql} ${r.caller ?? ""} ${r.fingerprint}`} searchPlaceholder="Filter by SQL, method or table…" />
        )}
      </Panel>

      {d.nPlusOne.length > 0 ? (
        <Panel title="N+1 suspects" info="The same query shape ran many times inside one request — typically a loop that loads related data one item at a time. One batched query (WHERE id = ANY(@ids)) usually replaces all of them.">
          <DataTable columns={nColumns} rows={d.nPlusOne} rowKey={(r) => `${r.route}|${r.fingerprint}`} initialSort={{ key: "exec", dir: "desc" }} />
        </Panel>
      ) : null}

      <Panel
        title="PostgreSQL statement statistics"
        info="From pg_stat_statements: every statement the database executed in the period (app, workers, maintenance), computed as the difference between two snapshots. Constants appear as $1, $2."
      >
        {!d.access.statStatements ? (
          <EmptyState icon={<AlertTriangle className="h-7 w-7" />} title="pg_stat_statements is not available"
            body={d.access.statStatementsReason ?? "Run deploy/update.sh on the host to enable it."} />
        ) : d.pgss.length === 0 ? (
          <p className="text-xs text-muted-foreground">No snapshots in this period yet (taken every few minutes).</p>
        ) : (
          <DataTable columns={pgColumns} rows={d.pgss} rowKey={(r) => r.queryId} initialSort={{ key: "total", dir: "desc" }}
            searchText={(r) => r.query} searchPlaceholder="Filter statements…" />
        )}
      </Panel>

      <PlanDialog plan={plan} onClose={() => setPlan(null)} />
    </div>
  );
}

function PlanDialog({ plan, onClose }: { plan: { source: "pgss" | "app"; id: string; title: string } | null; onClose: () => void }) {
  const run = useMutation({ mutationFn: (p: { source: "pgss" | "app"; id: string }) => perfApi.plan(p.source, p.id) });
  const { mutate, reset } = run;
  React.useEffect(() => {
    if (plan) mutate({ source: plan.source, id: plan.id });
    else reset();
  }, [plan, mutate, reset]);
  const result: PlanResponse | undefined = run.data;

  return (
    <Dialog open={plan !== null} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-h-[85vh] overflow-y-auto border-glass-strong sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>Query plan</DialogTitle>
          <DialogDescription>
            EXPLAIN (GENERIC_PLAN) for <span className="font-mono">{plan?.title}</span> — planned, never executed, in a read-only transaction.
          </DialogDescription>
        </DialogHeader>
        {run.isPending ? (
          <div className="flex items-center gap-2 py-6 text-sm text-muted-foreground"><Loader2 className="h-4 w-4 animate-spin" /> Planning…</div>
        ) : run.isError ? (
          <p className="text-sm text-rose-700 dark:text-rose-300">{apiErrorMessage(run.error) ?? "Planning failed."}</p>
        ) : result ? (
          <div className="flex flex-col gap-3">
            {result.sql ? <SqlBlock sql={result.sql} maxHeight="8rem" /> : null}
            {result.ok ? <PlanTree plan={result.plan} /> : <p className="text-sm text-amber-800 dark:text-amber-300">{result.error}</p>}
          </div>
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

type PlanNode = {
  "Node Type"?: string;
  "Relation Name"?: string;
  "Index Name"?: string;
  "Total Cost"?: number;
  "Plan Rows"?: number;
  "Filter"?: string;
  "Index Cond"?: string;
  "Join Type"?: string;
  "Sort Key"?: string[];
  Plans?: PlanNode[];
};

/// Readable plan tree; sequential scans and sorts are highlighted because
/// they are the usual culprits on large tables.
function PlanTree({ plan }: { plan: unknown }) {
  const root = Array.isArray(plan) ? (plan[0] as { Plan?: PlanNode })?.Plan : undefined;
  if (!root) return <SqlBlock sql={JSON.stringify(plan, null, 2)} />;
  const render = (node: PlanNode, depth: number): React.ReactNode => {
    const type = node["Node Type"] ?? "?";
    const hot = type === "Seq Scan" || type === "Sort" || type.includes("Nested Loop");
    return (
      <div key={`${depth}-${type}-${node["Relation Name"] ?? ""}-${node["Total Cost"]}`} style={{ marginLeft: depth * 16 }} className="py-0.5">
        <div className="flex flex-wrap items-center gap-2 text-xs">
          <span className={cn("font-medium", hot ? "text-amber-800 dark:text-amber-300" : "text-foreground")}>{type}</span>
          {node["Relation Name"] ? <Code>{node["Relation Name"]}</Code> : null}
          {node["Index Name"] ? <span className="text-muted-foreground">using <Code>{node["Index Name"]}</Code></span> : null}
          <span className="text-muted-foreground">cost {fmtNum(node["Total Cost"] ?? 0)} · ~{fmtCount(node["Plan Rows"] ?? 0)} rows</span>
        </div>
        {node["Index Cond"] ? <div className="ml-3 font-mono text-[11px] text-muted-foreground">index: {node["Index Cond"]}</div> : null}
        {node["Filter"] ? <div className="ml-3 font-mono text-[11px] text-muted-foreground">filter: {node["Filter"]}</div> : null}
        {node["Sort Key"] ? <div className="ml-3 font-mono text-[11px] text-muted-foreground">sort: {node["Sort Key"].join(", ")}</div> : null}
        {(node.Plans ?? []).map((c) => render(c, depth + 1))}
      </div>
    );
  };
  return (
    <div className="rounded-md border border-glass bg-glass p-3">
      {render(root, 0)}
      <p className="mt-2 text-[11px] text-muted-foreground">
        Highlighted: sequential scans, sorts and nested loops — fine on small tables, expensive on large ones.
      </p>
    </div>
  );
}

// --------------------------------------------------------------- tables

function TablesView({ period }: { period: PeriodQuery }) {
  const q = useTabQuery(["tables", period], () => perfApi.tables(period));
  const pal = usePalette();
  const analyze = useMutation({
    mutationFn: (table: string) => perfApi.analyze(table),
    onSuccess: (_d, table) => {
      toast.success(`Statistics of ${table} refreshed`);
      void q.refetch();
    },
    onError: (e) => toast.error(apiErrorMessage(e) ?? "ANALYZE failed"),
  });

  if (q.isLoading) return <Skeleton className="h-96 w-full" />;
  if (q.isError || !q.data) return <p className="text-sm text-muted-foreground">{apiErrorMessage(q.error) ?? "Could not load table statistics."}</p>;
  const d = q.data;
  const totalBytes = d.tables.reduce((n, t) => n + t.totalBytes, 0);
  const deadTone = (t: TableRow): Tone => (t.rows + t.dead < 1000 ? "nodata" : t.deadPct > 60 ? "critical" : t.deadPct > 20 ? "warning" : "good");

  const columns: Column<TableRow>[] = [
    { key: "name", header: "Table", render: (t) => <span className="font-mono text-[11.5px]">{t.name}</span>, sortValue: (t) => t.name },
    { key: "rows", header: "Rows", align: "right", render: (t) => fmtCount(t.rows), sortValue: (t) => t.rows },
    { key: "size", header: "Size", align: "right", info: "Table + indexes + TOAST, from the catalog's page counts (refreshed by vacuum/analyze).", render: (t) => fmtBytes(t.totalBytes), sortValue: (t) => t.totalBytes },
    {
      key: "dead",
      header: "Dead rows",
      align: "right",
      info: "Old row versions left behind by updates and deletes until VACUUM cleans them. A high share makes every scan read more pages.",
      render: (t) => <Pill tone={deadTone(t)}>{fmtPct(t.deadPct)}</Pill>,
      sortValue: (t) => t.deadPct,
    },
    {
      key: "bloat",
      header: "Bloat (est.)",
      align: "right",
      info: "Estimated space the table uses beyond what its rows need. A statistical estimate — most useful on larger tables.",
      render: (t) => (t.bloatPct === null ? "—" : <span title={fmtBytes(t.bloatBytes)}>{fmtPct(t.bloatPct)}</span>),
      sortValue: (t) => t.bloatPct ?? -1,
    },
    {
      key: "scans",
      header: "Seq / idx scans",
      align: "right",
      info: "Full-table scans vs index scans in the selected period. Many sequential scans on a big table usually mean a missing index.",
      render: (t) => {
        const seq = t.periodSeqScan ?? null;
        const idx = t.periodIdxScan ?? null;
        const warn = seq !== null && t.rows >= 10_000 && seq > Math.max(1, idx ?? 0) * 10 && seq >= 10;
        return (
          <span className={cn(warn && "text-amber-700 dark:text-amber-300")}>
            {seq === null ? "—" : fmtCount(seq)} / {idx === null ? "—" : fmtCount(idx)}
          </span>
        );
      },
      sortValue: (t) => t.periodSeqScan ?? 0,
    },
    { key: "hit", header: "Cache hit", align: "right", render: (t) => fmtPct(t.hitPct), sortValue: (t) => t.hitPct ?? 101 },
    {
      key: "vacuum",
      header: "Last vacuum",
      align: "right",
      render: (t) => {
        const last = [t.lastVacuum, t.lastAutovacuum].filter(Boolean).sort().pop() ?? null;
        return <span className="text-muted-foreground" title={last ? new Date(last).toLocaleString() : "never"}>{fmtAgo(last, Date.now())}</span>;
      },
      sortValue: (t) => [t.lastVacuum, t.lastAutovacuum].filter(Boolean).sort().pop() ?? "",
    },
    {
      key: "analyze",
      header: "Stats",
      align: "right",
      info: "Rows changed since the planner statistics were last refreshed. 'Analyze' refreshes them now (cheap and safe; it samples the table).",
      render: (t) => {
        const stale = t.rows > 0 && t.modSinceAnalyze > t.rows * 0.2;
        return (
          <div className="flex items-center justify-end gap-1.5">
            <span className={cn("tabular-nums", stale ? "text-amber-700 dark:text-amber-300" : "text-muted-foreground")}>{fmtCount(t.modSinceAnalyze)}</span>
            <Button size="sm" variant="ghost" className="h-6 px-1.5 text-[11px]" disabled={analyze.isPending}
              onClick={(e) => { e.stopPropagation(); analyze.mutate(t.name); }}>
              Analyze
            </Button>
          </div>
        );
      },
      sortValue: (t) => (t.rows > 0 ? t.modSinceAnalyze / t.rows : 0),
    },
  ];

  const growth = d.growth.slice(0, 6);
  const series = buildGrowthSeries(growth);
  const unused = d.indexes.filter((i) => i.scans === 0 && !i.isPrimary && !i.isUnique && i.bytes > 1024 * 1024);

  return (
    <div className={cn("flex flex-col gap-5", q.isFetching && q.isPlaceholderData && "opacity-70")}>
      <div className="grid gap-3 sm:grid-cols-3">
        <StatTile label="Database size (tables)" value={fmtBytes(totalBytes)} sub={`${d.tables.length} tables`} />
        <StatTile label="Unused indexes" value={fmtCount(unused.length)} sub={fmtBytes(unused.reduce((n, i) => n + i.bytes, 0))}
          tone={unused.length > 0 ? "info" : "good"} info="Indexes never used for reads since the statistics were last reset (only those over 1 MB, excluding primary/unique keys). They still cost write time and memory." />
        <StatTile label="Duplicate indexes" value={fmtCount(d.duplicates.length)} tone={d.duplicates.length > 0 ? "warning" : "good"}
          info="Indexes with exactly the same definition on the same table; one of each pair can go." />
      </div>

      <Panel title="Tables" info="Live statistics per table. Sizes are catalog estimates; scan counts are for the selected period.">
        <DataTable columns={columns} rows={d.tables} rowKey={(t) => t.name} initialSort={{ key: "size", dir: "desc" }}
          searchText={(t) => t.name} searchPlaceholder="Filter tables…" dense />
      </Panel>

      {series.data.length > 1 ? (
        <Panel title="Growth of the largest tables" info="Total size per table over (at least) the last 30 days, from the hourly snapshots. Capacity planning: steady growth is expected; a sudden jump is worth a look."
          actions={<Legend items={growth.map((g, i) => ({ label: g.name, color: pal.slot(i) }))} />}>
          <div className="h-56">
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={series.data} margin={{ top: 6, right: 8, left: 0, bottom: 0 }}>
                <CartesianGrid vertical={false} stroke={gridStroke} strokeOpacity={0.6} />
                <XAxis dataKey="t" type="number" scale="time" domain={["dataMin", "dataMax"]} tick={axisTick} tickLine={false}
                  axisLine={{ stroke: gridStroke }} tickFormatter={(v: number) => fmtTime(new Date(v).toISOString(), true)} minTickGap={50} />
                <YAxis tick={axisTick} tickLine={false} axisLine={false} width={64} tickFormatter={(v: number) => fmtBytes(v)} />
                <Tooltip content={(props) => {
                  const row = props.payload?.[0]?.payload as Record<string, number> | undefined;
                  if (!props.active || !row) return null;
                  return <ChartTooltipBox title={new Date(row.t).toLocaleString()} rows={growth.map((g, i) => ({ label: g.name, value: fmtBytes(row[g.name]), color: pal.slot(i) }))} />;
                }} />
                {growth.map((g, i) => (
                  <Line key={g.name} type="monotone" dataKey={g.name} stroke={pal.slot(i)} strokeWidth={2} dot={false} connectNulls isAnimationActive={false} />
                ))}
              </LineChart>
            </ResponsiveContainer>
          </div>
        </Panel>
      ) : null}

      <div className="grid gap-5 lg:grid-cols-2">
        <Panel title="Unused indexes" info="Never scanned since the statistics were last reset. Check over a full business cycle before dropping one.">
          {unused.length === 0 ? <p className="text-xs text-muted-foreground">None over 1 MB.</p> : (
            <ul className="divide-y divide-glass text-xs">
              {unused.slice(0, 20).map((i) => (
                <li key={i.index} className="flex items-center justify-between gap-2 py-1.5">
                  <span className="truncate font-mono" title={i.index}>{i.index}</span>
                  <span className="shrink-0 text-muted-foreground">{i.table} · {fmtBytes(i.bytes)}</span>
                </li>
              ))}
            </ul>
          )}
        </Panel>
        <Panel title="Duplicate indexes">
          {d.duplicates.length === 0 ? <p className="text-xs text-muted-foreground">None found.</p> : (
            <ul className="divide-y divide-glass text-xs">
              {d.duplicates.map((x) => (
                <li key={x.indexes.join()} className="py-1.5">
                  <span className="font-mono">{x.table}</span>: {x.indexes.join(", ")} <span className="text-muted-foreground">({fmtBytes(x.bytes)})</span>
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>
    </div>
  );
}

function buildGrowthSeries(growth: { name: string; points: { t: string; bytes: number }[] }[]) {
  const map = new Map<number, Record<string, number>>();
  for (const g of growth) {
    for (const p of g.points) {
      const t = new Date(p.t).getTime();
      const row = map.get(t) ?? { t };
      row[g.name] = p.bytes;
      map.set(t, row);
    }
  }
  return { data: [...map.values()].sort((a, b) => a.t - b.t) };
}

// ----------------------------------------------------------------- live

function LiveView() {
  const [auto, setAuto] = React.useState(true);
  const q = useTabQuery(["live"], () => perfApi.live(), auto ? 5000 : false);
  if (q.isLoading) return <Skeleton className="h-80 w-full" />;
  if (q.isError || !q.data) return <p className="text-sm text-muted-foreground">{apiErrorMessage(q.error) ?? "Could not read pg_stat_activity."}</p>;
  const d = q.data;
  const s = d.summary;

  const columns: Column<ActivityRow>[] = [
    { key: "pid", header: "PID", render: (r) => <span className="tabular-nums">{r.pid}</span>, sortValue: (r) => r.pid },
    {
      key: "state",
      header: "State",
      render: (r) => {
        const idleTx = r.state?.startsWith("idle in transaction");
        const tone: Tone = r.blockedBy.length > 0 ? "critical" : idleTx ? "warning" : r.state === "active" ? "info" : "nodata";
        return <Pill tone={tone}>{r.blockedBy.length > 0 ? "blocked" : r.state ?? r.backendType ?? "—"}</Pill>;
      },
      sortValue: (r) => r.state ?? "",
    },
    { key: "wait", header: "Waiting on", render: (r) => <span className="text-muted-foreground">{r.waitEventType ? `${r.waitEventType}: ${r.waitEvent}` : "—"}</span> },
    { key: "age", header: "Running for", align: "right", render: (r) => fmtDuration(sessionAge(r) === null ? null : sessionAge(r)! * 1000), sortValue: (r) => sessionAge(r) ?? 0 },
    { key: "tx", header: "Transaction", align: "right", render: (r) => fmtDuration(r.xactAgeS === null ? null : r.xactAgeS * 1000), sortValue: (r) => r.xactAgeS ?? 0 },
    { key: "blocked", header: "Blocked by", render: (r) => (r.blockedBy.length > 0 ? r.blockedBy.join(", ") : "—") },
    { key: "query", header: "Query", render: (r) => <div className="max-w-md truncate font-mono text-[11px] text-foreground/80" title={r.query ?? ""}>{r.query || "—"}</div> },
  ];

  return (
    <div className={cn("flex flex-col gap-5")}>
      <div className="flex items-center justify-between gap-2">
        <p className="text-xs text-muted-foreground">Sessions on this database right now (refreshes every 5 s). Query text is shown with constants removed.</p>
        <div className="flex items-center gap-2">
          <label className="inline-flex items-center gap-1.5 text-xs text-muted-foreground">
            <input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} className="accent-primary" /> Live
          </label>
          <Button size="sm" variant="outline" onClick={() => void q.refetch()} aria-label="Refresh"><RefreshCw className="h-3.5 w-3.5" /></Button>
        </div>
      </div>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <StatTile label="Connections" value={fmtCount(s.conn_total ?? 0)} sub={`${fmtCount(s.conn_active ?? 0)} active · max ${fmtCount(s.max_connections ?? 0)}`}
          info="Server-side sessions on this database. The app's own pool size is shown on the Server tab." />
        <StatTile label="Idle in transaction" value={fmtCount(s.idle_in_tx ?? 0)} sub={s.idle_in_tx_max_s ? `longest ${fmtDuration((s.idle_in_tx_max_s ?? 0) * 1000)}` : "none"}
          tone={(s.idle_in_tx_max_s ?? 0) > 30 ? "warning" : "good"}
          info="A transaction that is open but doing nothing keeps its locks and stops VACUUM from cleaning up. It should be zero most of the time." />
        <StatTile label="Waiting on locks" value={fmtCount(s.waiting_on_lock ?? 0)} sub={`${fmtCount(s.blocked ?? 0)} blocked sessions`}
          tone={(s.blocked ?? 0) > 0 ? "critical" : "good"} info="Sessions that cannot continue because another transaction holds a lock they need — the cause of 'everything hangs for a moment'." />
        <StatTile label="Longest running query" value={fmtDuration((s.longest_active_s ?? 0) * 1000)} tone={(s.longest_active_s ?? 0) > 5 ? "warning" : "good"} />
      </div>
      <Panel title="Sessions">
        <DataTable columns={columns} rows={d.activity} rowKey={(r) => String(r.pid)} initialSort={{ key: "age", dir: "desc" }} dense />
      </Panel>
      {d.locks.length > 0 ? (
        <Panel title="Lock waits" info="Locks that are not granted yet, and the locks held by the sessions blocking them.">
          <ul className="divide-y divide-glass text-xs">
            {d.locks.map((l, i) => (
              <li key={`${l.pid}-${i}`} className="flex flex-wrap items-center gap-2 py-1.5">
                <Pill tone={l.granted ? "nodata" : "critical"}>{l.granted ? "holds" : "waits"}</Pill>
                <span className="tabular-nums">PID {l.pid}</span>
                <span className="text-muted-foreground">{l.mode} on {l.relation ?? l.lockType}</span>
                {l.waitingS ? <span className="ml-auto text-muted-foreground">waiting {fmtDuration(l.waitingS * 1000)}</span> : null}
              </li>
            ))}
          </ul>
        </Panel>
      ) : null}
    </div>
  );
}

/** Active sessions: how long the current query runs; others: time in their state. */
function sessionAge(r: ActivityRow): number | null {
  return r.state === "active" ? r.queryAgeS : r.stateAgeS;
}

// --------------------------------------------------------------- config

const SETTING_HINTS: Record<string, string> = {
  shared_buffers: "PostgreSQL's own page cache. Typically ~25% of RAM on a dedicated server.",
  effective_cache_size: "How much memory the planner assumes the OS caches. Typically 50–75% of RAM.",
  work_mem: "Memory per sort/hash step before spilling to temp files. Raise carefully: it applies per operation, per connection.",
  random_page_cost: "Planner cost of a random disk read. 1.1–1.5 suits SSD/NVMe; 4 is the spinning-disk default.",
  max_connections: "Upper bound on sessions; the app pool must stay well below it.",
  autovacuum_vacuum_scale_factor: "Share of a table that must change before autovacuum runs. Lower it for very large, busy tables.",
};

function ConfigView({ period }: { period: PeriodQuery }) {
  const q = useTabQuery(["config", period], () => perfApi.config(period));
  if (q.isLoading) return <Skeleton className="h-80 w-full" />;
  if (q.isError || !q.data) return <p className="text-sm text-muted-foreground">{apiErrorMessage(q.error) ?? "Could not load the configuration."}</p>;
  const d = q.data;
  const deltas = d.period.deltas;
  const hit = d.period.cacheHitPct;
  const cpTimed = deltas.checkpoints_timed ?? 0;
  const cpReq = deltas.checkpoints_req ?? 0;

  return (
    <div className="flex flex-col gap-5">
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <StatTile label="Cache hit ratio" value={fmtPct(hit, 2)} tone={hit === null ? "nodata" : hit < 99 ? hit < 95 ? "critical" : "warning" : "good"}
          sub={`${fmtCount(deltas.blks_read)} pages read from disk`}
          info="Share of page reads served from PostgreSQL's memory in this period. Below ~99% the database waits on the disk noticeably." />
        <StatTile label="Temp files" value={fmtBytes(deltas.temp_bytes)} sub={`${fmtCount(deltas.temp_files)} files`} tone={(deltas.temp_bytes ?? 0) > 100 * 1024 * 1024 ? "warning" : "good"}
          info="Sorts and hashes that did not fit in work_mem were written to disk." />
        <StatTile label="Checkpoints" value={`${fmtCount(cpTimed)} / ${fmtCount(cpReq)}`} sub="timed / requested"
          tone={cpReq > cpTimed && cpReq > 2 ? "warning" : "good"}
          info="Requested checkpoints happen when WAL fills up before the timer; many of them mean write bursts — consider a larger max_wal_size." />
        <StatTile label="Deadlocks / rollbacks" value={`${fmtCount(deltas.deadlocks)} / ${fmtCount(deltas.xact_rollback)}`}
          tone={(deltas.deadlocks ?? 0) > 0 ? "warning" : "good"} sub={`${fmtCount(deltas.xact_commit)} commits`} />
      </div>

      <div className="grid gap-5 lg:grid-cols-2">
        <Panel title="Relevant settings" info="PostgreSQL settings that most affect performance. Changed on the host (postgresql.conf / update.sh), never from the app.">
          <ul className="divide-y divide-glass text-xs">
            {d.settings.map((s) => (
              <li key={s.name} className="flex items-center justify-between gap-3 py-1.5">
                <span className="inline-flex items-center gap-1.5 font-mono">
                  {s.name}
                  {SETTING_HINTS[s.name] ? <InfoTip text={SETTING_HINTS[s.name]} /> : null}
                </span>
                <span className="tabular-nums text-foreground">{formatSetting(s.setting, s.unit)}</span>
              </li>
            ))}
          </ul>
        </Panel>
        <Panel title="Server" info="Version, size and statistics access of the database.">
          <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-1.5 text-xs">
            <dt className="text-muted-foreground">PostgreSQL</dt><dd>{d.access.serverVersion || "—"}</dd>
            <dt className="text-muted-foreground">Database size</dt><dd>{fmtBytes(d.current.db_size)}</dd>
            <dt className="text-muted-foreground">Transaction ID age</dt><dd>{fmtCount(d.current.xid_age)} <span className="text-muted-foreground">(wraparound at ~2.1 billion)</span></dd>
            <dt className="text-muted-foreground">pg_monitor</dt><dd>{d.access.pgMonitor ? "granted" : "missing — run deploy/update.sh"}</dd>
            <dt className="text-muted-foreground">pg_stat_statements</dt><dd>{d.access.statStatements ? "available" : d.access.statStatementsReason ?? "unavailable"}</dd>
            <dt className="text-muted-foreground">Max idle-in-transaction</dt><dd>{fmtDuration((d.period.idleInTxMaxS ?? 0) * 1000)} in period</dd>
          </dl>
        </Panel>
      </div>

      {d.io.length > 0 ? (
        <Panel title="I/O by backend (pg_stat_io)" info="Reads, writes and cache hits per PostgreSQL process type since the statistics were reset. 'client backend' reads are user queries; high 'evictions' mean shared_buffers is too small for the working set.">
          <DataTable
            columns={[
              { key: "b", header: "Backend", render: (r) => r.backendType, sortValue: (r) => r.backendType },
              { key: "o", header: "Object / context", render: (r) => <span className="text-muted-foreground">{r.object} · {r.context}</span> },
              { key: "r", header: "Reads", align: "right", render: (r) => fmtCount(r.reads), sortValue: (r) => r.reads ?? 0 },
              { key: "h", header: "Hits", align: "right", render: (r) => fmtCount(r.hits), sortValue: (r) => r.hits ?? 0 },
              { key: "w", header: "Writes", align: "right", render: (r) => fmtCount(r.writes), sortValue: (r) => r.writes ?? 0 },
              { key: "e", header: "Evictions", align: "right", render: (r) => fmtCount(r.evictions), sortValue: (r) => r.evictions ?? 0 },
              { key: "f", header: "fsyncs", align: "right", render: (r) => fmtCount(r.fsyncs), sortValue: (r) => r.fsyncs ?? 0 },
            ]}
            rows={d.io}
            rowKey={(r) => `${r.backendType}|${r.object}|${r.context}`}
            initialSort={{ key: "r", dir: "desc" }}
            dense
          />
        </Panel>
      ) : null}
    </div>
  );
}

function formatSetting(value: string, unit: string | null): string {
  const n = Number(value);
  if (unit === "8kB" && Number.isFinite(n)) return fmtBytes(n * 8192);
  if (unit === "kB" && Number.isFinite(n)) return fmtBytes(n * 1024);
  if (unit === "MB" && Number.isFinite(n)) return fmtBytes(n * 1024 * 1024);
  if (unit === "ms" && Number.isFinite(n)) return n < 0 ? "off" : fmtMs(n);
  if (unit === "s" && Number.isFinite(n)) return `${n} s`;
  if (unit === "min" && Number.isFinite(n)) return `${n} min`;
  return value + (unit ? ` ${unit}` : "");
}
