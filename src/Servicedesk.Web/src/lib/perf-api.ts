// v0.1.24 — Performance monitoring (Settings → Performance, admin only).
// Typed client for /api/admin/performance/*. Every number arrives already
// aggregated by the server; percentiles are computed server-side from the
// stored histograms.

import { ApiError } from "@/lib/api";
import { csrfHeader } from "@/lib/csrf";

export type PerfLevel = "off" | "basic" | "diagnose";
export type Severity = "info" | "warning" | "critical";
export type CategoryKey = "hosting" | "network" | "code" | "database" | "maintenance" | "frontend" | "background";

export type Collectors = {
  http: boolean;
  database: boolean;
  postgres: boolean;
  runtime: boolean;
  host: boolean;
  frontend: boolean;
  signalR: boolean;
  workers: boolean;
  external: boolean;
};

export type PgAccess = {
  checked: boolean;
  serverVersionNum: number;
  serverVersion: string;
  pgMonitor: boolean;
  statStatements: boolean;
  statStatementsReason: string | null;
};

export type PerfStatus = {
  level: PerfLevel;
  baseLevel: "off" | "basic";
  diagnoseUntilUtc: string | null;
  serverUtc: string;
  collectors: Collectors;
  overhead: { msPerRequest: number; cpuPct: number; requests: number };
  storageBytes: number;
  hostSupported: boolean;
  pgAccess: PgAccess;
  appVersion: string;
};

export type Evidence = { label: string; value: string };
export type CodeRef = { kind: string; value: string; sql: string | null; caller: string | null };

export type Finding = {
  key: string;
  category: CategoryKey;
  severity: Severity;
  title: string;
  explanation: string;
  evidence: Evidence[];
  cause: string;
  action: string;
  codeRefs: CodeRef[];
  impactMs: number;
  impactLabel: string;
};

export type CategoryScore = {
  key: CategoryKey;
  label: string;
  question: string;
  score: number;
  status: "good" | "warning" | "critical" | "nodata";
  summary: string;
  findingCount: number;
};

export type Budget = {
  method: string;
  route: string;
  targetMs: number;
  count: number;
  p95: number;
  ok: boolean;
  hasData: boolean;
};

export type TimelinePoint = {
  t: string;
  count: number;
  errors: number;
  p50: number;
  p95: number;
  dbMs: number;
  extMs: number;
  avgMs: number;
  users: number;
};

export type WorkerRun = {
  t: string;
  worker: string;
  durationMs: number;
  success: boolean;
  items: number;
  error: string | null;
};

export type Marker = { id: number; t: string; kind: "deploy" | "manual" | "diagnose"; label: string; createdBy: string | null };

export type PeriodInfo = { from: string; to: string; usesHourly: boolean; stepSeconds: number };

export type Overview = {
  period: PeriodInfo;
  kpis: {
    requests: number;
    perMinute: number;
    p50: number;
    p95: number;
    p99: number;
    errorRate5xx: number;
    errorRate4xx: number;
    dbSharePct: number;
    extSharePct: number;
    appCpuPct: number | null;
    hostCpuPct: number | null;
    stealPct: number | null;
    memAvailablePct: number | null;
    lcpP75: number | null;
    inpP75: number | null;
    networkSharePct: number | null;
    activeUsersPeak: number;
    diagnoseMinutes: number;
  };
  categories: CategoryScore[];
  findings: Finding[];
  budgets: Budget[];
  timeline: TimelinePoint[];
  workerRuns: WorkerRun[];
  markers: Marker[];
  versions: { version: string; firstUtc: string; lastUtc: string; minutes: number }[];
  gaps: string[];
};

export type RouteRow = {
  method: string;
  route: string;
  count: number;
  errors5xx: number;
  count4xx: number;
  count429: number;
  p50: number;
  p95: number;
  p99: number;
  maxMs: number;
  avgMs: number;
  avgDbMs: number;
  avgDbQueries: number;
  avgExtMs: number;
  avgPipelineMs: number;
  avgAppMs: number;
  avgKb: number;
  p95Kb: number;
  totalMs: number;
  dbSharePct: number;
  extSharePct: number;
  pipelineSharePct: number;
  appSharePct: number;
  users: number;
};

export type QueryRow = {
  fingerprint: string;
  sql: string;
  caller: string | null;
  count: number;
  errors: number;
  totalMs: number;
  avgMs: number;
  p95: number;
  maxMs: number;
  sources: { source: string; count: number; totalMs: number }[];
};

export type NPlusOneRow = {
  route: string;
  fingerprint: string;
  sql: string | null;
  caller: string | null;
  requests: number;
  executions: number;
  maxPerRequest: number;
};

export type SlowRequest = {
  id: number;
  t: string;
  method: string;
  route: string;
  status: number;
  totalMs: number;
  dbMs: number;
  dbCount: number;
  extMs: number;
  pipelineMs: number;
  gcCount: number;
  bytes: number;
  breakdown: { queries?: { fingerprint: string; count: number; ms: number }[]; nPlusOne?: { fingerprint: string; count: number }[] } | null;
  traceId: string;
};

export type RouteDetail = {
  stats: RouteRow | null;
  histogram: { le: number | null; count: number }[] | null;
  series: { t: string; count: number; errors: number; p50: number; p95: number; p99: number }[];
  queries: QueryRow[];
  nPlusOne: NPlusOneRow[];
  slow: SlowRequest[];
};

export type PgssRow = {
  queryId: string;
  query: string;
  calls: number;
  totalMs: number;
  meanMs: number;
  maxMs: number;
  stddevMs: number;
  rows: number;
  hitPct: number;
  blksRead: number;
  tempWritten: number;
};

export type QueriesResponse = {
  access: PgAccess;
  diagnoseActive: boolean;
  slowQueryMs: number;
  app: QueryRow[];
  pgss: PgssRow[];
  nPlusOne: NPlusOneRow[];
  bySource: { source: string; count: number; errors: number; totalMs: number; avgMs: number; p95: number }[];
};

export type TableRow = {
  name: string;
  rows: number;
  dead: number;
  deadPct: number;
  totalBytes: number;
  tableBytes: number;
  indexBytes: number;
  bloatPct: number | null;
  bloatBytes: number | null;
  seqScan: number;
  idxScan: number;
  periodSeqScan: number | null;
  periodIdxScan: number | null;
  periodSeqTupRead: number | null;
  periodWrites: number | null;
  bytesGrowth: number | null;
  rowsGrowth: number | null;
  modSinceAnalyze: number;
  hitPct: number | null;
  lastVacuum: string | null;
  lastAutovacuum: string | null;
  lastAnalyze: string | null;
  lastAutoanalyze: string | null;
};

export type TablesResponse = {
  tables: TableRow[];
  indexes: { table: string; index: string; scans: number; bytes: number; isUnique: boolean; isPrimary: boolean }[];
  duplicates: { table: string; indexes: string[]; bytes: number }[];
  growth: { name: string; points: { t: string; bytes: number; rows: number }[] }[];
};

export type ActivityRow = {
  pid: number;
  state: string | null;
  waitEventType: string | null;
  waitEvent: string | null;
  backendType: string | null;
  application: string | null;
  queryAgeS: number | null;
  xactAgeS: number | null;
  stateAgeS: number | null;
  blockedBy: number[];
  query: string | null;
};

export type LiveResponse = {
  summary: Record<string, number | null>;
  activity: ActivityRow[];
  locks: { pid: number; lockType: string; mode: string; granted: boolean; relation: string | null; waitingS: number | null }[];
};

export type ConfigResponse = {
  access: PgAccess;
  settings: { name: string; setting: string; unit: string | null; source: string | null }[];
  io: {
    backendType: string;
    object: string;
    context: string;
    reads: number | null;
    readTime: number | null;
    writes: number | null;
    writeTime: number | null;
    hits: number | null;
    evictions: number | null;
    fsyncs: number | null;
  }[];
  current: Record<string, number | null>;
  period: {
    fromUtc: string | null;
    toUtc: string | null;
    cacheHitPct: number | null;
    deltas: Record<string, number | null>;
    idleInTxMaxS: number | null;
    blockedMax: number | null;
    longestActiveMaxS: number | null;
    connTotalMax: number | null;
  };
};

export type RuntimePoint = {
  t: string;
  cpuPct: number | null;
  hostCpuPct: number | null;
  stealPct: number | null;
  ioWaitPct: number | null;
  load1: number | null;
  memAvailableMb: number | null;
  memTotalMb: number | null;
  swapUsedMb: number | null;
  diskAwaitMs: number | null;
  diskUtilPct: number | null;
  workingSetMb: number | null;
  gcHeapMb: number | null;
  gcPausePct: number | null;
  tpThreads: number | null;
  tpQueue: number | null;
  poolBusy: number | null;
  poolMax: number | null;
  poolPending: number | null;
  kestrelActive: number | null;
  signalR: number | null;
  exceptions: number | null;
  inFlight: number | null;
  requests: number | null;
  overheadMs: number | null;
};

export type HostResponse = {
  hostSupported: boolean;
  processorCount: number;
  os: string;
  dotnet: string;
  summary: Record<string, number | null>;
  series: RuntimePoint[];
  benchmarks: { id: number; t: string; createdBy: string; result: Record<string, number | string | null> }[];
};

export type Vital = { p75: number; count: number } | null;

export type FrontendResponse = {
  routes: {
    route: string;
    views: number;
    lcp: Vital;
    inp: Vital;
    cls: Vital;
    fcp: Vital;
    ttfb: Vital;
    routeChange: Vital;
    apiCalls: { p75: number; avg: number; max: number } | null;
    apiKb: { p75: number; avg: number } | null;
    longTasks: number;
  }[];
  scripts: { script: string; count: number; totalMs: number; maxMs: number }[];
  api: { route: string; count: number; totalP75: number; serverP75: number; networkP75: number; networkPct: number }[];
  navigation: { metric: string; p75: number | null; count: number }[];
  breakdown: { dimension: string; value: string; count: number }[];
  heap: { t: string; mb: number }[];
  reconnects: number;
  bundle: {
    builtUtc: string | null;
    totalJsBytes: number;
    totalCssBytes: number;
    totalJsGzipBytes: number;
    chunks: { name: string; type: string; bytes: number; gzipBytes: number; isEntry: boolean }[];
  } | null;
};

export type SpanRow = {
  name: string;
  detail: string;
  count: number;
  errors: number;
  avgMs: number;
  p50: number;
  p95: number;
  maxMs: number;
  totalMs: number;
};

export type BackgroundResponse = {
  workers: { worker: string; runs: number; failures: number; avgMs: number; maxMs: number; totalMs: number; items: number; lastRunUtc: string }[];
  runs: WorkerRun[];
  overlaps: { worker: string; minutesDuring: number; requestsDuring: number; p95During: number; p95Outside: number }[];
  external: {
    host: string;
    count: number;
    errors: number;
    throttled: number;
    p50: number;
    p95: number;
    maxMs: number;
    totalMs: number;
    details: { detail: string; count: number }[];
  }[];
  hubs: SpanRow[];
  connections: { hub: string; detail: string; count: number }[];
  broadcasts: { hub: string; detail: string; count: number }[];
  search: SpanRow[];
  connectionSeries: { t: string; signalr: number | null; kestrel: number | null }[];
};

export type PerfSetting = { key: string; value: string; valueType: string; description: string; defaultValue: string };

export type PlanResponse = { ok: boolean; plan: unknown; error: string | null; sql: string | null };

export type EdgeCheckItem = { label: string; value: string; status: "good" | "warning" | "info"; hint: string };

export type PeriodQuery = { from: string; to: string };

async function call<T>(method: string, url: string, body?: unknown): Promise<T> {
  const isSafe = method === "GET";
  const res = await fetch(url, {
    method,
    credentials: "include",
    headers: {
      Accept: "application/json",
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      ...(isSafe ? {} : csrfHeader(url)),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    let parsed: unknown = null;
    try {
      const text = await res.text();
      if (text.length > 0) parsed = JSON.parse(text);
    } catch {
      // not JSON
    }
    throw new ApiError(res.status, url, `${url} → ${res.status}`, parsed);
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

const BASE = "/api/admin/performance";

function qs(p: PeriodQuery, extra?: Record<string, string>): string {
  const params = new URLSearchParams({ from: p.from, to: p.to, ...(extra ?? {}) });
  return params.toString();
}

export const perfApi = {
  status: () => call<PerfStatus>("GET", `${BASE}/status`),
  overview: (p: PeriodQuery) => call<Overview>("GET", `${BASE}/overview?${qs(p)}`),
  http: (p: PeriodQuery) => call<{ period: PeriodInfo; total: RouteRow | null; routes: RouteRow[] }>("GET", `${BASE}/http?${qs(p)}`),
  route: (p: PeriodQuery, method: string, route: string) =>
    call<RouteDetail>("GET", `${BASE}/http/route?${qs(p, { method, route })}`),
  queries: (p: PeriodQuery) => call<QueriesResponse>("GET", `${BASE}/db/queries?${qs(p)}`),
  tables: (p: PeriodQuery) => call<TablesResponse>("GET", `${BASE}/db/tables?${qs(p)}`),
  live: () => call<LiveResponse>("GET", `${BASE}/db/live`),
  config: (p: PeriodQuery) => call<ConfigResponse>("GET", `${BASE}/db/config?${qs(p)}`),
  plan: (source: "pgss" | "app", id: string) => call<PlanResponse>("POST", `${BASE}/db/plan`, { source, id }),
  analyze: (table: string) => call<void>("POST", `${BASE}/db/analyze`, { table }),
  host: (p: PeriodQuery) => call<HostResponse>("GET", `${BASE}/host?${qs(p)}`),
  benchmark: () => call<Record<string, number | string | null>>("POST", `${BASE}/benchmark`),
  edgeCheck: () => call<{ items: EdgeCheckItem[] }>("POST", `${BASE}/edge-check`),
  frontend: (p: PeriodQuery) => call<FrontendResponse>("GET", `${BASE}/frontend?${qs(p)}`),
  background: (p: PeriodQuery) => call<BackgroundResponse>("GET", `${BASE}/background?${qs(p)}`),
  addMarker: (label: string) => call<{ id: number }>("POST", `${BASE}/markers`, { label }),
  deleteMarker: (id: number) => call<void>("DELETE", `${BASE}/markers/${encodeURIComponent(String(id))}`),
  settings: () => call<{ settings: PerfSetting[] }>("GET", `${BASE}/settings`),
  saveSettings: (values: Record<string, string>) => call<void>("PUT", `${BASE}/settings`, values),
  startDiagnose: (minutes: number) => call<{ diagnoseUntilUtc: string }>("POST", `${BASE}/diagnose`, { minutes }),
  stopDiagnose: () => call<void>("DELETE", `${BASE}/diagnose`),
  clearData: () => call<void>("DELETE", `${BASE}/data`),

  /** Fetches the export and hands it to the browser as a download. */
  async download(p: PeriodQuery, format: "zip" | "md" | "json", compare: boolean, plans: boolean): Promise<void> {
    const url = `${BASE}/export?${qs(p, { format, compare: String(compare), plans: String(plans) })}`;
    const res = await fetch(url, { credentials: "include" });
    if (!res.ok) throw new ApiError(res.status, url, `${url} → ${res.status}`);
    const blob = await res.blob();
    const disposition = res.headers.get("Content-Disposition") ?? "";
    const match = /filename="?([^";]+)"?/i.exec(disposition);
    const name = match?.[1] ?? `servicedesk-performance.${format}`;
    const href = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = href;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(href), 10_000);
  },

  async markdown(p: PeriodQuery, compare: boolean): Promise<string> {
    const url = `${BASE}/export?${qs(p, { format: "md", compare: String(compare) })}`;
    const res = await fetch(url, { credentials: "include" });
    if (!res.ok) throw new ApiError(res.status, url, `${url} → ${res.status}`);
    return res.text();
  },
};
