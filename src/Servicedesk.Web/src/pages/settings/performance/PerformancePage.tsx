import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Activity,
  ChevronDown,
  Clipboard,
  Download,
  FileJson,
  FileText,
  FlaskConical,
  Gauge,
  Loader2,
  Package,
  RefreshCw,
  Square,
} from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { apiErrorMessage } from "@/lib/api";
import { perfApi, type PerfStatus, type PeriodQuery } from "@/lib/perf-api";
import { cn } from "@/lib/utils";
import { Segmented } from "@/pages/insights/InsightsControls";
import { InfoTip } from "./PerfUi";
import { STATUS_KEY } from "./usePerfQuery";
import { fmtBytes } from "./perfFormat";
import { OverviewTab } from "./OverviewTab";
import { ApiTab } from "./ApiTab";
import { DatabaseTab } from "./DatabaseTab";
import { ServerTab } from "./ServerTab";
import { FrontendTab } from "./FrontendTab";
import { BackgroundTab } from "./BackgroundTab";
import { PerfSettingsTab } from "./PerfSettingsTab";


type TabKey = "overview" | "api" | "database" | "server" | "frontend" | "background" | "settings";

const TABS: { key: TabKey; label: string }[] = [
  { key: "overview", label: "Overview" },
  { key: "api", label: "API" },
  { key: "database", label: "Database" },
  { key: "server", label: "Server / hosting" },
  { key: "frontend", label: "Frontend" },
  { key: "background", label: "Background & realtime" },
  { key: "settings", label: "Settings" },
];

type PresetKey = "15m" | "1h" | "6h" | "24h" | "7d" | "30d" | "custom";

const PRESETS: { value: PresetKey; label: string; ms: number }[] = [
  { value: "15m", label: "15 min", ms: 15 * 60_000 },
  { value: "1h", label: "1 h", ms: 3_600_000 },
  { value: "6h", label: "6 h", ms: 6 * 3_600_000 },
  { value: "24h", label: "24 h", ms: 24 * 3_600_000 },
  { value: "7d", label: "7 d", ms: 7 * 86_400_000 },
  { value: "30d", label: "30 d", ms: 30 * 86_400_000 },
  { value: "custom", label: "Custom", ms: 0 },
];

const TAB_STORAGE = "sd-perf-tab";
const PRESET_STORAGE = "sd-perf-preset";

function readStored<T extends string>(key: string, allowed: readonly T[], fallback: T): T {
  try {
    const v = window.localStorage.getItem(key);
    return v && (allowed as readonly string[]).includes(v) ? (v as T) : fallback;
  } catch {
    return fallback;
  }
}

function store(key: string, value: string) {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // per-viewer convenience only
  }
}

/** yyyy-MM-ddTHH:mm in local time, for datetime-local inputs. */
function toLocalInput(ms: number): string {
  const d = new Date(ms);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export function PerformancePage() {
  const qc = useQueryClient();
  const [tab, setTab] = React.useState<TabKey>(() => readStored(TAB_STORAGE, TABS.map((t) => t.key), "overview"));
  const [preset, setPreset] = React.useState<PresetKey>(() => readStored(PRESET_STORAGE, PRESETS.map((p) => p.value), "24h"));
  const [autoRefresh, setAutoRefresh] = React.useState(true);
  const [tick, setTick] = React.useState(0);
  const [customFrom, setCustomFrom] = React.useState(() => toLocalInput(Date.now() - 86_400_000));
  const [customTo, setCustomTo] = React.useState(() => toLocalInput(Date.now()));

  const status = useQuery({
    queryKey: STATUS_KEY,
    queryFn: async () => ({ status: await perfApi.status(), fetchedAt: Date.now() }),
    refetchInterval: 30_000,
  });

  // The server clock is the source of truth: periods are anchored on the
  // server's "now", never on the browser clock.
  const clockOffset = status.data ? new Date(status.data.status.serverUtc).getTime() - status.data.fetchedAt : 0;
  const serverNow = React.useCallback(() => Date.now() + clockOffset, [clockOffset]);

  React.useEffect(() => {
    if (!autoRefresh || preset === "custom") return;
    const id = window.setInterval(() => setTick((t) => t + 1), 30_000);
    return () => window.clearInterval(id);
  }, [autoRefresh, preset]);

  const period: PeriodQuery | null = React.useMemo(() => {
    if (preset === "custom") {
      const f = new Date(customFrom).getTime();
      const t = new Date(customTo).getTime();
      if (!Number.isFinite(f) || !Number.isFinite(t) || f >= t) return null;
      return { from: new Date(f).toISOString(), to: new Date(t).toISOString() };
    }
    if (!status.data) return null;
    // Round to the minute: data is flushed per minute, and a stable key
    // keeps every tab from refetching on each render.
    const now = Math.floor(serverNow() / 60_000) * 60_000 + 60_000;
    const span = PRESETS.find((p) => p.value === preset)!.ms;
    return { from: new Date(now - span).toISOString(), to: new Date(now).toISOString() };
    // `tick` deliberately re-anchors the window while auto-refresh runs.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [preset, customFrom, customTo, tick, status.data != null, serverNow]);

  const changeTab = (k: TabKey) => {
    setTab(k);
    store(TAB_STORAGE, k);
  };

  const s = status.data?.status;

  return (
    <div className="flex flex-col gap-5">
      <header className="flex flex-wrap items-start justify-between gap-4">
        <div className="space-y-2">
          <div className="mb-2 text-primary">
            <Gauge className="h-6 w-6" />
          </div>
          <h1 className="text-display-md font-semibold text-foreground">Performance</h1>
          <p className="max-w-2xl text-sm text-muted-foreground">
            Where the application is slow, and why: server, network, code, database, maintenance, browser or background
            jobs. Every finding points to the route, query or worker to look at — export it as a report for Claude Code.
          </p>
        </div>
        {s ? <OverheadChip status={s} /> : null}
      </header>

      <Toolbar
        status={s}
        serverNow={serverNow}
        preset={preset}
        onPreset={(p) => {
          setPreset(p);
          store(PRESET_STORAGE, p);
        }}
        customFrom={customFrom}
        customTo={customTo}
        onCustomFrom={setCustomFrom}
        onCustomTo={setCustomTo}
        autoRefresh={autoRefresh}
        onAutoRefresh={setAutoRefresh}
        onRefresh={() => {
          setTick((t) => t + 1);
          void qc.invalidateQueries({ queryKey: ["admin", "performance"] });
        }}
        period={period}
      />

      <nav role="tablist" aria-label="Performance sections" className="flex gap-1 overflow-x-auto border-b border-glass">
        {TABS.map((t) => (
          <button
            key={t.key}
            type="button"
            role="tab"
            aria-selected={tab === t.key}
            onClick={() => changeTab(t.key)}
            className={cn(
              "-mb-px whitespace-nowrap border-b-2 px-3 py-2 text-sm transition-colors",
              tab === t.key
                ? "border-primary font-medium text-foreground"
                : "border-transparent text-muted-foreground hover:text-foreground",
            )}
          >
            {t.label}
          </button>
        ))}
      </nav>

      {s?.baseLevel === "off" && tab !== "settings" ? (
        <div className="glass-card rounded-lg border border-glass-strong bg-glass px-4 py-3 text-sm text-muted-foreground">
          Monitoring is switched <span className="font-medium text-foreground">off</span> — nothing new is being measured.
          Switch it to <span className="font-medium text-foreground">Basic</span> above to start collecting; the history below stays available.
        </div>
      ) : null}

      {!period ? (
        <p className="text-sm text-muted-foreground">Choose a valid period.</p>
      ) : tab === "overview" ? (
        <OverviewTab period={period} autoRefresh={autoRefresh} onNavigate={changeTab} serverNow={serverNow} />
      ) : tab === "api" ? (
        <ApiTab period={period} />
      ) : tab === "database" ? (
        <DatabaseTab period={period} status={s} />
      ) : tab === "server" ? (
        <ServerTab period={period} status={s} />
      ) : tab === "frontend" ? (
        <FrontendTab period={period} />
      ) : tab === "background" ? (
        <BackgroundTab period={period} />
      ) : (
        <PerfSettingsTab status={s} />
      )}
    </div>
  );
}

function OverheadChip({ status }: { status: PerfStatus }) {
  return (
    <div className="glass-card flex items-center gap-3 rounded-lg border border-glass-strong bg-glass px-3 py-2 text-xs text-muted-foreground">
      <span className="inline-flex items-center gap-1.5">
        <Activity className="h-3.5 w-3.5 text-primary" />
        Monitoring cost
        <InfoTip text="What the collectors themselves cost over the last 15 minutes, measured by the collectors: time added per request and share of the app's CPU. If this is ever noticeable, switch off a collector or leave Diagnose mode." />
      </span>
      <span className="tabular-nums text-foreground">{status.overhead.msPerRequest.toFixed(3)} ms/request</span>
      <span aria-hidden className="h-3 w-px bg-glass-strong" />
      <span className="tabular-nums text-foreground">{status.overhead.cpuPct.toFixed(2)}% CPU</span>
      <span aria-hidden className="h-3 w-px bg-glass-strong" />
      <span className="tabular-nums" title="Disk space used by the monitor's own tables">{fmtBytes(status.storageBytes)} stored</span>
    </div>
  );
}

function Toolbar({
  status,
  serverNow,
  preset,
  onPreset,
  customFrom,
  customTo,
  onCustomFrom,
  onCustomTo,
  autoRefresh,
  onAutoRefresh,
  onRefresh,
  period,
}: {
  status: PerfStatus | undefined;
  serverNow: () => number;
  preset: PresetKey;
  onPreset: (p: PresetKey) => void;
  customFrom: string;
  customTo: string;
  onCustomFrom: (v: string) => void;
  onCustomTo: (v: string) => void;
  autoRefresh: boolean;
  onAutoRefresh: (v: boolean) => void;
  onRefresh: () => void;
  period: PeriodQuery | null;
}) {
  return (
    <div className="glass-card sticky top-0 z-10 flex flex-wrap items-center gap-3 rounded-lg border border-glass-strong bg-glass px-3 py-2.5">
      <LevelControl status={status} serverNow={serverNow} />
      <span aria-hidden className="hidden h-6 w-px bg-glass-strong md:block" />
      <Segmented ariaLabel="Period" value={preset} options={PRESETS} onChange={(v) => onPreset(v as PresetKey)} />
      {preset === "custom" ? (
        <div className="flex items-center gap-1.5">
          <input type="datetime-local" aria-label="From" value={customFrom} onChange={(e) => onCustomFrom(e.target.value)}
            className="h-8 rounded-md border border-glass bg-glass px-2 text-xs text-foreground outline-hidden focus:border-primary/60" />
          <span className="text-xs text-muted-foreground">–</span>
          <input type="datetime-local" aria-label="To" value={customTo} onChange={(e) => onCustomTo(e.target.value)}
            className="h-8 rounded-md border border-glass bg-glass px-2 text-xs text-foreground outline-hidden focus:border-primary/60" />
        </div>
      ) : null}
      <div className="ml-auto flex items-center gap-2">
        <label className="inline-flex cursor-pointer items-center gap-1.5 text-xs text-muted-foreground">
          <input type="checkbox" checked={autoRefresh} onChange={(e) => onAutoRefresh(e.target.checked)} className="accent-primary" />
          Auto-refresh
        </label>
        <Button size="sm" variant="outline" onClick={onRefresh} aria-label="Refresh now" title="Refresh now">
          <RefreshCw className="h-3.5 w-3.5" />
        </Button>
        <ExportMenu period={period} />
      </div>
    </div>
  );
}

function LevelControl({ status, serverNow }: { status: PerfStatus | undefined; serverNow: () => number }) {
  const qc = useQueryClient();
  const [now, setNow] = React.useState(() => serverNow());
  React.useEffect(() => {
    const id = window.setInterval(() => setNow(serverNow()), 1000);
    return () => window.clearInterval(id);
  }, [serverNow]);

  const refresh = () => void qc.invalidateQueries({ queryKey: ["admin", "performance"] });
  const setLevel = useMutation({
    mutationFn: (level: "off" | "basic") => perfApi.saveSettings({ "Performance.Level": level }),
    onSuccess: (_d, level) => {
      toast.success(level === "off" ? "Monitoring switched off" : "Monitoring switched on (Basic)");
      refresh();
    },
    onError: (e) => toast.error(apiErrorMessage(e) ?? "Could not change the level"),
  });
  const start = useMutation({
    mutationFn: (minutes: number) => perfApi.startDiagnose(minutes),
    onSuccess: () => {
      toast.success("Diagnose mode started");
      refresh();
    },
    onError: (e) => toast.error(apiErrorMessage(e) ?? "Could not start Diagnose mode"),
  });
  const stop = useMutation({
    mutationFn: () => perfApi.stopDiagnose(),
    onSuccess: () => {
      toast.success("Diagnose mode stopped");
      refresh();
    },
  });

  if (!status) return <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" />;

  const until = status.diagnoseUntilUtc ? new Date(status.diagnoseUntilUtc).getTime() : 0;
  const remainingMs = until - now;
  const diagnosing = status.level === "diagnose" && remainingMs > 0;

  return (
    <div className="flex items-center gap-2">
      <span className="inline-flex items-center gap-1 text-xs text-muted-foreground">
        Monitoring
        <InfoTip text="Off: nothing is measured. Basic: per-minute aggregates of every layer, well under 1% overhead — safe to leave on. Diagnose: Basic plus per-query capture, N+1 detection, calling code and 100% browser sampling (a few % overhead); it always switches itself off after the chosen time." />
      </span>
      <Segmented
        ariaLabel="Monitoring level"
        value={status.baseLevel}
        options={[
          { value: "off", label: "Off" },
          { value: "basic", label: "Basic" },
        ]}
        onChange={(v) => setLevel.mutate(v as "off" | "basic")}
      />
      {diagnosing ? (
        <div className="flex items-center gap-2 rounded-md border border-primary/30 bg-primary/10 px-2 py-1">
          <FlaskConical className="h-3.5 w-3.5 text-primary" />
          <span className="text-xs font-medium tabular-nums text-foreground" aria-live="polite">
            Diagnose · {formatRemaining(remainingMs)} left
          </span>
          <button type="button" onClick={() => stop.mutate()} title="Stop Diagnose mode" aria-label="Stop Diagnose mode"
            className="rounded p-0.5 text-primary hover:bg-primary/15">
            <Square className="h-3 w-3 fill-current" />
          </button>
        </div>
      ) : (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button size="sm" variant="outline" disabled={status.baseLevel === "off" || start.isPending}>
              <FlaskConical className="mr-1.5 h-3.5 w-3.5" />
              Diagnose
              <ChevronDown className="ml-1 h-3 w-3" />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="start">
            <DropdownMenuLabel className="text-xs font-normal text-muted-foreground">Deep capture for…</DropdownMenuLabel>
            {[15, 30, 60, 120, 240].map((m) => (
              <DropdownMenuItem key={m} onClick={() => start.mutate(m)}>
                {m < 60 ? `${m} minutes` : `${m / 60} hour${m > 60 ? "s" : ""}`}
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      )}
    </div>
  );
}

function formatRemaining(ms: number): string {
  const total = Math.max(0, Math.round(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return `${h} h ${m} min`;
  if (m >= 5) return `${m} min`;
  return `${m}:${String(s).padStart(2, "0")}`;
}

function ExportMenu({ period }: { period: PeriodQuery | null }) {
  const [compare, setCompare] = React.useState(false);
  const [plans, setPlans] = React.useState(true);
  const [busy, setBusy] = React.useState(false);

  const run = async (fn: () => Promise<void>) => {
    if (!period) return;
    setBusy(true);
    try {
      await fn();
    } catch (e) {
      toast.error(apiErrorMessage(e) ?? "Export failed");
    } finally {
      setBusy(false);
    }
  };

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button size="sm" disabled={!period || busy}>
          {busy ? <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" /> : <Download className="mr-1.5 h-3.5 w-3.5" />}
          Export report
          <ChevronDown className="ml-1 h-3 w-3" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-72">
        <DropdownMenuLabel className="text-xs font-normal leading-relaxed text-muted-foreground">
          A compact report for Claude Code: findings with the routes, queries and workers to look at. No personal data,
          ids or query values are included. Every export is recorded in the audit log.
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuItem onClick={() => run(() => perfApi.download(period!, "zip", compare, plans))}>
          <Package className="mr-2 h-4 w-4" /> ZIP (report + data + query plans)
        </DropdownMenuItem>
        <DropdownMenuItem onClick={() => run(() => perfApi.download(period!, "md", compare, false))}>
          <FileText className="mr-2 h-4 w-4" /> Markdown report
        </DropdownMenuItem>
        <DropdownMenuItem onClick={() => run(() => perfApi.download(period!, "json", compare, false))}>
          <FileJson className="mr-2 h-4 w-4" /> JSON data
        </DropdownMenuItem>
        <DropdownMenuItem
          onClick={() =>
            run(async () => {
              const md = await perfApi.markdown(period!, compare);
              await navigator.clipboard.writeText(md);
              toast.success("Report copied — paste it into Claude Code");
            })
          }
        >
          <Clipboard className="mr-2 h-4 w-4" /> Copy Markdown
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuCheckboxItem checked={compare} onCheckedChange={(v) => setCompare(v === true)} onSelect={(e) => e.preventDefault()}>
          Compare with the previous period
        </DropdownMenuCheckboxItem>
        <DropdownMenuCheckboxItem checked={plans} onCheckedChange={(v) => setPlans(v === true)} onSelect={(e) => e.preventDefault()}>
          Include query plans (ZIP)
        </DropdownMenuCheckboxItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
