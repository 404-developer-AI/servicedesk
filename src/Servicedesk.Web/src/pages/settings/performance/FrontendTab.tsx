import { formatDateTime } from "@/lib/dateFormat";
import { Area, AreaChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { MonitorSmartphone } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { apiErrorMessage } from "@/lib/api";
import { perfApi, type FrontendResponse, type PeriodQuery } from "@/lib/perf-api";
import { cn } from "@/lib/utils";
import { useTabQuery } from "./usePerfQuery";
import {
  ChartTooltipBox,
  DataTable,
  EmptyState,
  Legend,
  Panel,
  Pill,
  SplitBar,
  StatTile,
  type Column,
} from "./PerfUi";
import { axisTick, usePalette } from "./perfChart";
import { fmtBytes, fmtCount, fmtMs, fmtNum, fmtPct, fmtTime, vitalTone } from "./perfFormat";

type RouteRow = FrontendResponse["routes"][number];
type ApiRow = FrontendResponse["api"][number];

const NAV_LABEL: Record<string, string> = {
  nav_dns: "DNS lookup",
  nav_tcp: "TCP connect",
  nav_tls: "TLS handshake",
  nav_ttfb: "Waiting for the server (TTFB)",
  nav_download: "Download",
  nav_dom: "DOM interactive",
  nav_load: "Fully loaded",
};

export function FrontendTab({ period }: { period: PeriodQuery }) {
  const q = useTabQuery(["frontend", period], () => perfApi.frontend(period));
  const pal = usePalette();
  if (q.isLoading) return <Skeleton className="h-96 w-full" />;
  if (q.isError || !q.data) return <p className="text-sm text-muted-foreground">{apiErrorMessage(q.error) ?? "Could not load browser metrics."}</p>;
  const d = q.data;
  const views = d.routes.reduce((n, r) => n + r.views, 0);

  const vital = (metric: "lcp" | "inp" | "cls" | "fcp" | "ttfb", v: { p75: number; count: number } | null) =>
    v === null ? <span className="text-muted-foreground">—</span> : (
      <Pill tone={vitalTone(metric, v.p75)}>{metric === "cls" ? v.p75.toFixed(2) : fmtMs(v.p75)}</Pill>
    );

  const columns: Column<RouteRow>[] = [
    { key: "route", header: "Screen", render: (r) => <span className="font-mono text-[11.5px]">{r.route}</span>, sortValue: (r) => r.route },
    { key: "views", header: "Views", align: "right", render: (r) => fmtCount(r.views), sortValue: (r) => r.views },
    { key: "lcp", header: "LCP", align: "right", info: "Largest Contentful Paint (p75): when the main content became visible on a fresh page load. Good ≤ 2.5 s, poor > 4 s.", render: (r) => vital("lcp", r.lcp), sortValue: (r) => r.lcp?.p75 ?? -1 },
    { key: "inp", header: "INP", align: "right", info: "Interaction to Next Paint (p75): how quickly the page responded to clicks and typing. Good ≤ 200 ms, poor > 500 ms.", render: (r) => vital("inp", r.inp), sortValue: (r) => r.inp?.p75 ?? -1 },
    { key: "cls", header: "CLS", align: "right", info: "Cumulative Layout Shift (p75): how much the layout jumped while loading. Good ≤ 0.1.", render: (r) => vital("cls", r.cls), sortValue: (r) => r.cls?.p75 ?? -1 },
    { key: "route_change", header: "Open time", align: "right", info: "Time from clicking a link inside the app until the new screen was painted (p75).", render: (r) => fmtMs(r.routeChange?.p75), sortValue: (r) => r.routeChange?.p75 ?? -1 },
    { key: "calls", header: "API calls", align: "right", info: "API calls fired while opening the screen (first 10 seconds, p75). Many calls — especially ones waiting on each other — slow the screen down.", render: (r) => (r.apiCalls ? <span className={cn(r.apiCalls.p75 > 15 && "text-amber-700 dark:text-amber-300")}>{fmtNum(r.apiCalls.p75, 0)}</span> : "—"), sortValue: (r) => r.apiCalls?.p75 ?? -1 },
    { key: "kb", header: "Data", align: "right", info: "Data downloaded by those API calls (p75).", render: (r) => (r.apiKb ? fmtBytes(r.apiKb.p75 * 1024) : "—"), sortValue: (r) => r.apiKb?.p75 ?? -1 },
    { key: "lt", header: "Long tasks", align: "right", info: "JavaScript tasks over 50 ms that blocked the page, per view.", render: (r) => (r.views > 0 ? fmtNum(r.longTasks / r.views) : fmtCount(r.longTasks)), sortValue: (r) => (r.views > 0 ? r.longTasks / r.views : r.longTasks) },
  ];

  const apiColumns: Column<ApiRow>[] = [
    { key: "route", header: "API route", render: (r) => <span className="font-mono text-[11.5px]">{r.route}</span>, sortValue: (r) => r.route },
    { key: "count", header: "Calls", align: "right", render: (r) => fmtCount(r.count), sortValue: (r) => r.count },
    { key: "total", header: "Browser p75", align: "right", info: "End-to-end duration as the browser saw it.", render: (r) => fmtMs(r.totalP75), sortValue: (r) => r.totalP75 },
    { key: "server", header: "Server p75", align: "right", info: "The server's own time (Server-Timing header).", render: (r) => fmtMs(r.serverP75), sortValue: (r) => r.serverP75 },
    {
      key: "split",
      header: "Network share",
      info: "Part of the browser time spent outside the server: connection, latency, download. High = the user's connection or distance is the bottleneck. Time the request waited in the browser before it was sent (too many calls at once) is shown apart as Queued.",
      render: (r) => (
        <div className="flex min-w-32 items-center gap-2">
          <SplitBar className="flex-1" parts={[
            { key: "server", label: "Server", value: Math.max(0, 100 - r.networkPct - (r.queuePct ?? 0)), color: pal.slot(0) },
            { key: "queue", label: "Queued", value: r.queuePct ?? 0, color: pal.slot(2) },
            { key: "net", label: "Network", value: r.networkPct, color: pal.slot(1) },
          ]} />
          <span className="w-10 text-right tabular-nums text-muted-foreground">{fmtPct(r.networkPct)}</span>
        </div>
      ),
      sortValue: (r) => r.networkPct,
    },
  ];

  if (views === 0 && d.api.length === 0) {
    return (
      <Panel>
        <EmptyState
          icon={<MonitorSmartphone className="h-8 w-8" />}
          title="No browser measurements in this period"
          body="Real-user metrics come from agents' browsers: a sample of page loads (Settings tab → Frontend sampling; 100% during Diagnose) reports Web Vitals, long tasks and API timings. Make sure the Frontend collector is on, then use the app normally for a few minutes."
        />
      </Panel>
    );
  }

  const breakdown = (dim: string) => d.breakdown.filter((b) => b.dimension === dim).sort((a, b) => b.count - a.count);
  const totalBy = (dim: string) => breakdown(dim).reduce((n, b) => n + b.count, 0);
  const heap = d.heap.map((h) => ({ t: new Date(h.t).getTime(), mb: h.mb }));
  const multiDay = new Date(period.to).getTime() - new Date(period.from).getTime() > 36 * 3_600_000;

  return (
    <div className={cn("flex flex-col gap-5", q.isFetching && q.isPlaceholderData && "opacity-70")}>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <StatTile label="Screen views" value={fmtCount(views)} sub={`${d.routes.length} screens measured`} />
        <StatTile label="SignalR reconnects" value={fmtCount(d.reconnects)} tone={d.reconnects > 20 ? "warning" : "good"}
          info="How often the realtime connection dropped and came back in measured tabs. Many reconnects point at an unstable connection, a proxy timeout or server restarts." />
        <StatTile label="JavaScript bundle" value={d.bundle ? fmtBytes(d.bundle.totalJsGzipBytes) : "—"} sub={d.bundle ? `${fmtBytes(d.bundle.totalJsBytes)} uncompressed` : "manifest not found (dev build)"}
          tone={!d.bundle ? "nodata" : d.bundle.totalJsGzipBytes > 1_500_000 ? "warning" : "good"}
          info="Total size of the app's JavaScript, compressed as browsers download it. Big bundles delay the first load, mostly on slower machines." />
        <StatTile label="Connection types" value={breakdown("connection")[0]?.value ?? "—"}
          sub={breakdown("connection").slice(0, 3).map((b) => `${b.value} ${fmtPct((100 * b.count) / Math.max(1, totalBy("connection")))}`).join(" · ")}
          info="Effective connection type reported by the browser (Chromium). 3G or worse means slow or congested connections." />
      </div>

      <Panel title="Screens" info="Measured in real agents' browsers, per screen (route template). Colours follow Google's Core Web Vitals bands: green good, amber needs improvement, red poor.">
        <DataTable columns={columns} rows={d.routes} rowKey={(r) => r.route} initialSort={{ key: "views", dir: "desc" }} searchText={(r) => r.route} searchPlaceholder="Filter screens…" />
      </Panel>

      <Panel title="API calls: server vs network" info="For each API route, the browser's view of the call split into the server's own time and everything in between (connection, distance, download).">
        <DataTable columns={apiColumns} rows={d.api} rowKey={(r) => r.route} initialSort={{ key: "count", dir: "desc" }} searchText={(r) => r.route} />
      </Panel>

      <div className="grid gap-5 xl:grid-cols-2">
        <Panel title="Scripts blocking the page" info="From Long Animation Frames (Chromium): the JavaScript chunk and function that ran during frames longer than 50 ms. The chunk name maps to a lazily loaded page or library.">
          {d.scripts.length === 0 ? <p className="text-xs text-muted-foreground">No long frames attributed to a script (or the browser does not support it).</p> : (
            <ul className="divide-y divide-glass text-xs">
              {d.scripts.slice(0, 15).map((s) => (
                <li key={s.script} className="flex items-center justify-between gap-3 py-1.5">
                  <span className="truncate font-mono" title={s.script}>{s.script}</span>
                  <span className="shrink-0 tabular-nums text-muted-foreground">{fmtCount(s.count)} × · max {fmtMs(s.maxMs)} · total {fmtMs(s.totalMs)}</span>
                </li>
              ))}
            </ul>
          )}
        </Panel>
        <Panel title="First page load" info="Navigation timing of a fresh page load (p75): from DNS lookup to fully loaded. Large connect/TLS times point at the network; a large TTFB at the server.">
          <ul className="divide-y divide-glass text-xs">
            {d.navigation.map((n) => (
              <li key={n.metric} className="flex items-center justify-between gap-3 py-1.5">
                <span className="text-muted-foreground">{NAV_LABEL[n.metric] ?? n.metric}</span>
                <span className="tabular-nums text-foreground">{fmtMs(n.p75)}</span>
              </li>
            ))}
          </ul>
        </Panel>
      </div>

      <div className="grid gap-5 xl:grid-cols-2">
        <Panel title="JavaScript memory in open tabs" info="Average JS heap of measured tabs (Chromium only). Agents keep the app open all day; a heap that keeps climbing over hours is a memory leak in the frontend.">
          {heap.length < 2 ? <p className="text-xs text-muted-foreground">Not enough samples (reported every minute by sampled Chromium tabs).</p> : (
            <div className="h-40">
              <ResponsiveContainer width="100%" height="100%">
                <AreaChart data={heap} margin={{ top: 4, right: 8, left: -8, bottom: 0 }}>
                  <XAxis dataKey="t" type="number" scale="time" domain={["dataMin", "dataMax"]} tick={axisTick} tickLine={false}
                    tickFormatter={(v: number) => fmtTime(new Date(v).toISOString(), multiDay)} minTickGap={44} />
                  <YAxis tick={axisTick} tickLine={false} axisLine={false} width={56} tickFormatter={(v: number) => `${fmtNum(v, 0)} MB`} />
                  <Tooltip content={(props) => {
                    const row = props.payload?.[0]?.payload as { t: number; mb: number } | undefined;
                    if (!props.active || !row) return null;
                    return <ChartTooltipBox title={formatDateTime(row.t, undefined, true)} rows={[{ label: "JS heap", value: `${fmtNum(row.mb)} MB`, color: pal.slot(0) }]} />;
                  }} />
                  <Area type="monotone" dataKey="mb" stroke={pal.slot(0)} strokeWidth={2} fill={pal.slot(0)} fillOpacity={0.12} isAnimationActive={false} />
                </AreaChart>
              </ResponsiveContainer>
            </div>
          )}
        </Panel>
        <Panel title="Bundle chunks" info="Largest JavaScript/CSS files of the current build (compressed). Rarely used pages and heavy libraries belong in lazily loaded chunks.">
          {!d.bundle ? <p className="text-xs text-muted-foreground">The build manifest (perf-bundle.json) is only produced by a production build.</p> : (
            <>
              <Legend items={[{ label: "Entry chunk", color: pal.slot(1) }, { label: "Lazy chunk / CSS", color: pal.slot(0) }]} />
              <ul className="mt-2 flex flex-col gap-1.5 text-xs">
                {d.bundle.chunks.slice(0, 10).map((c) => {
                  const max = d.bundle!.chunks[0]?.gzipBytes || 1;
                  return (
                    <li key={c.name} className="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-3 gap-y-0.5">
                      <span className="truncate font-mono" title={c.name}>{c.name.replace(/^assets\//, "")}</span>
                      <span className="tabular-nums text-muted-foreground">{fmtBytes(c.gzipBytes)}</span>
                      <span className="col-span-2 h-1.5 rounded-full bg-glass-strong">
                        <span className="block h-full rounded-full" style={{ width: `${(100 * c.gzipBytes) / max}%`, backgroundColor: c.isEntry ? pal.slot(1) : pal.slot(0) }} />
                      </span>
                    </li>
                  );
                })}
              </ul>
            </>
          )}
        </Panel>
      </div>

      <div className="grid gap-3 sm:grid-cols-3">
        {(["device", "connection", "protocol"] as const).map((dim) => (
          <Panel key={dim} title={dim === "device" ? "Devices" : dim === "connection" ? "Connections" : "HTTP protocol"}
            info={dim === "protocol" ? "Protocol of the API calls (sampled). h2 (HTTP/2) or h3 is expected behind nginx." : undefined}>
            {breakdown(dim).length === 0 ? <p className="text-xs text-muted-foreground">No data.</p> : (
              <ul className="flex flex-col gap-1 text-xs">
                {breakdown(dim).map((b) => (
                  <li key={b.value} className="flex items-center justify-between gap-2">
                    <span>{b.value}</span>
                    <span className="tabular-nums text-muted-foreground">{fmtPct((100 * b.count) / Math.max(1, totalBy(dim)))}</span>
                  </li>
                ))}
              </ul>
            )}
          </Panel>
        ))}
      </div>
    </div>
  );
}
