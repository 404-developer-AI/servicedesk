import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Archive, BellRing, Gauge, Loader2, Radar, SlidersHorizontal, Target, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { CollapsibleSettingsCard } from "@/components/settings/CollapsibleSettingsCard";
import { apiErrorMessage } from "@/lib/api";
import { perfApi, type PerfStatus } from "@/lib/perf-api";
import { Panel } from "./PerfUi";
import { PERF_KEY } from "./usePerfQuery";

const k = (key: string, label: string) => ({ key: `Performance.${key}`, label });

const COLLECTORS = [
  k("Collectors.Http.Enabled", "HTTP / API requests (+ Server-Timing header)"),
  k("Collectors.Database.Enabled", "Database time from the app (per-query capture in Diagnose)"),
  k("Collectors.Postgres.Enabled", "PostgreSQL statistics snapshots"),
  k("Collectors.Runtime.Enabled", ".NET runtime (CPU, memory, GC, thread pool, pools)"),
  k("Collectors.Host.Enabled", "Linux host (CPU steal, load, memory, disk)"),
  k("Collectors.Frontend.Enabled", "Real-user monitoring in agents' browsers"),
  k("Collectors.SignalR.Enabled", "SignalR realtime traffic"),
  k("Collectors.Workers.Enabled", "Background-worker runs"),
  k("Collectors.External.Enabled", "Outgoing HTTP calls (Graph, Adsolut, …)"),
];

const CAPTURE = [
  k("Rum.SamplePercent", "Browser sampling in Basic mode (% of page loads)"),
  k("SlowRequestThresholdMs", "Slow-request threshold (ms)"),
  k("SlowQueryThresholdMs", "Slow-query threshold (ms)"),
  k("NPlusOneThreshold", "N+1: same query per request at least"),
  k("ServerTimingEnabled", "Send Server-Timing header to staff"),
  k("PgSnapshotIntervalMinutes", "PostgreSQL snapshot interval, Basic (minutes)"),
  k("PgSnapshotDiagnoseIntervalMinutes", "PostgreSQL snapshot interval, Diagnose (minutes)"),
  k("DiagnoseDefaultMinutes", "Default Diagnose duration (minutes)"),
  k("DiagnoseMaxMinutes", "Longest Diagnose run (minutes)"),
];

const RETENTION = [
  k("Retention.MinuteDays", "Per-minute metrics (days)"),
  k("Retention.HourDays", "Hourly rollups (days)"),
  k("Retention.EventDays", "Slow requests, N+1 and worker runs (days)"),
  k("Retention.PgSnapshotDays", "PostgreSQL statement/database snapshots (days)"),
  k("Retention.TableSnapshotDays", "Table-size snapshots (days)"),
];

const ALERTS = [
  k("Alerts.Enabled", "Raise a Health incident for critical findings"),
  k("Alerts.CooldownHours", "Minimum hours between alerts for the same finding"),
];

const THRESHOLDS = [
  k("Findings.RouteP95Ms", "Slow route: p95 above (ms)"),
  k("Findings.RouteMinRequests", "Ignore routes with fewer requests than"),
  k("Findings.DbSharePct", "Database-bound route: DB share above (%)"),
  k("Findings.AppSharePct", "Code-bound route: own-code share above (%)"),
  k("Findings.ExtSharePct", "External-API-bound route: share above (%)"),
  k("Findings.NPlusOneMinRequests", "N+1 reported from this many requests"),
  k("Findings.SlowQueryTopN", "Heaviest queries listed (top N)"),
  k("Findings.SeqScanMinRows", "Seq-scan check: tables from (rows)"),
  k("Findings.SeqScanRatio", "Seq-scan check: seq scans × index scans above"),
  k("Findings.DeadTuplePct", "Dead rows above (% of live rows)"),
  k("Findings.VacuumStaleDays", "Not vacuumed for (days)"),
  k("Findings.CacheHitPct", "Buffer cache hit ratio below (%)"),
  k("Findings.TempMb", "Temp-file spill above (MB)"),
  k("Findings.IdleInTxSeconds", "Idle in transaction above (seconds)"),
  k("Findings.PoolWaitPct", "Connection-pool waits in more than (% of samples)"),
  k("Findings.CpuStealPct", "CPU steal above (%)"),
  k("Findings.CpuPct", "Host CPU above (%)"),
  k("Findings.LoadPctOfCores", "Load above (% of cores)"),
  k("Findings.MemAvailablePct", "Available memory below (%)"),
  k("Findings.DiskAwaitMs", "Disk latency above (ms)"),
  k("Findings.DiskUtilPct", "Disk utilisation above (%)"),
  k("Findings.DiskFreePct", "Free disk space below (%)"),
  k("Findings.ThreadPoolQueuePct", "Thread-pool queue in more than (% of samples)"),
  k("Findings.GcPausePct", "GC pause above (%)"),
  k("Findings.HeapGrowthPct", "Heap growth above (%)"),
  k("Findings.NetworkSharePct", "Network share of API time above (%)"),
  k("Findings.LcpMs", "LCP p75 above (ms)"),
  k("Findings.InpMs", "INP p75 above (ms)"),
  k("Findings.ClsMilli", "CLS p75 above (÷ 1000)"),
  k("Findings.LongTaskMs", "Long task from (ms)"),
  k("Findings.LongTasksPerView", "Long tasks per view above"),
  k("Findings.ApiCallsPerScreen", "API calls per screen above"),
  k("Findings.ResponseKb", "Response size p95 above (KB)"),
  k("Findings.WorkerOverlapPct", "API slower during a worker by more than (%)"),
  k("Findings.RegressionPct", "Slower after a version change by more than (%)"),
  k("Findings.ExternalErrorPct", "External API errors above (%)"),
];

export function PerfSettingsTab({ status }: { status: PerfStatus | undefined }) {
  return (
    <div className="flex flex-col gap-4">
      <p className="max-w-3xl text-sm text-muted-foreground">
        Changes apply within a few seconds, without a restart. Each collector can be switched off on its own if you ever suspect
        it — the monitoring cost per request is shown at the top of the page.
      </p>
      <CollapsibleSettingsCard category="Performance" icon={<Radar className="h-5 w-5" />} title="Collectors"
        description="What is measured. Every collector is cheap in Basic mode; switching one off removes its section from the dashboard and its rules from the findings."
        keys={COLLECTORS} />
      <CollapsibleSettingsCard category="Performance" icon={<SlidersHorizontal className="h-5 w-5" />} title="Capture & sampling"
        description="Thresholds for what counts as slow, how many browser page loads report, how often PostgreSQL is snapshotted and how long Diagnose mode may run."
        keys={CAPTURE} />
      <BudgetsEditor />
      <CollapsibleSettingsCard category="Performance" icon={<BellRing className="h-5 w-5" />} title="Alerts"
        description="Optional: critical findings (exhausted connection pool, nearly full disk, wraparound risk, OOM kills…) become Health incidents, visible on the Health page and its banner."
        keys={ALERTS} />
      <CollapsibleSettingsCard category="Performance" icon={<Archive className="h-5 w-5" />} title="Retention"
        description="How long measurements are kept. Per-minute data rolls up into hourly rows, so long periods stay fast and small."
        keys={RETENTION} />
      <CollapsibleSettingsCard category="Performance" icon={<Gauge className="h-5 w-5" />} title="Finding thresholds"
        description="The rules behind 'Top bottlenecks'. The defaults follow common guidance (Google's Web Vitals, PostgreSQL rules of thumb); tune them to your installation."
        keys={THRESHOLDS} />
      <DangerZone status={status} />
    </div>
  );
}

function BudgetsEditor() {
  const qc = useQueryClient();
  const settings = useQuery({ queryKey: [...PERF_KEY, "settings"], queryFn: () => perfApi.settings() });
  const current = settings.data?.settings.find((s) => s.key === "Performance.Budgets")?.value ?? "";
  const [draft, setDraft] = React.useState<string | null>(null);
  const value = draft ?? current;
  const save = useMutation({
    mutationFn: () => perfApi.saveSettings({ "Performance.Budgets": value }),
    onSuccess: () => {
      toast.success("Budgets saved");
      setDraft(null);
      void qc.invalidateQueries({ queryKey: PERF_KEY });
    },
    onError: (e) => toast.error(apiErrorMessage(e) ?? "Could not save the budgets"),
  });

  return (
    <Panel
      title={<span className="inline-flex items-center gap-2"><Target className="h-4 w-4 text-primary" /> Performance budgets</span>}
      info="Targets for the key flows (ticket open, ticket list, search…). One per line: METHOD route-template=milliseconds. Route templates are listed on the API tab. The Overview shows each flow's p95 against its budget, and an exceeded budget becomes a finding."
    >
      <textarea
        value={value}
        onChange={(e) => setDraft(e.target.value)}
        rows={5}
        spellCheck={false}
        className="w-full rounded-md border border-glass bg-glass px-3 py-2 font-mono text-xs text-foreground outline-hidden focus:border-primary/60"
        placeholder={"GET /api/tickets/{id:guid}=300\nGET /api/tickets=500"}
      />
      <div className="mt-2 flex justify-end gap-2">
        {draft !== null ? <Button size="sm" variant="ghost" onClick={() => setDraft(null)}>Cancel</Button> : null}
        <Button size="sm" onClick={() => save.mutate()} disabled={draft === null || save.isPending}>
          {save.isPending ? <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" /> : null}
          Save budgets
        </Button>
      </div>
    </Panel>
  );
}

function DangerZone({ status }: { status: PerfStatus | undefined }) {
  const qc = useQueryClient();
  const clear = useMutation({
    mutationFn: () => perfApi.clearData(),
    onSuccess: () => {
      toast.success("All performance measurements were deleted");
      void qc.invalidateQueries({ queryKey: PERF_KEY });
    },
    onError: (e) => toast.error(apiErrorMessage(e) ?? "Could not delete the data"),
  });
  return (
    <section className="rounded-lg border border-rose-500/30 bg-rose-500/5 px-5 py-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-foreground">Delete measurements</h2>
          <p className="text-xs text-muted-foreground">
            Removes every collected metric{status ? ` (${Math.round(status.storageBytes / 1024 / 1024)} MB)` : ""}. Markers and benchmark
            results are kept. The deletion is recorded in the audit log.
          </p>
        </div>
        <Button
          size="sm"
          variant="outline"
          className="border-rose-500/40 text-rose-700 hover:bg-rose-500/10 dark:text-rose-300"
          disabled={clear.isPending}
          onClick={() => {
            if (window.confirm("Delete all performance measurements? This cannot be undone.")) clear.mutate();
          }}
        >
          <Trash2 className="mr-1.5 h-3.5 w-3.5" /> Delete all measurements
        </Button>
      </div>
    </section>
  );
}
